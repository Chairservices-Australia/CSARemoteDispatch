using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    /// Where every car currently is, for switch safety and block detection.
    ///
    /// Both questions are asked far more often than cars actually move - the
    /// signalling postfix runs against every signal DV Signals refreshes - so
    /// the answers come from an index rebuilt on demand and shared by every
    /// caller within a frame, rather than by walking the world each time.
    public static class Occupancy
    {
        /// A junction with a bogie this close is treated as occupied. Throwing a
        /// switch under a car derails it, so this is deliberately generous.
        public const float JunctionClearanceMeters = 20f;

        /// How stale an index a display may be drawn from. Callers deciding
        /// whether a switch is safe to throw ask for one built this frame.
        private const float DisplayMaxAgeSeconds = 0.25f;

        /// Wider than the clearance, so the eight neighbouring cells bound any
        /// junction search.
        private const float GridCellMeters = 25f;

        private static readonly Dictionary<RailTrack, HashSet<TrainCar>> carsByTrack =
            new Dictionary<RailTrack, HashSet<TrainCar>>();
        private static readonly Dictionary<Vector2Int, List<CarPosition>> carGrid =
            new Dictionary<Vector2Int, List<CarPosition>>();
        private static readonly HashSet<int> poweredTrainsets = new HashSet<int>();

        // Rebuilding happens often enough that dropping its collections each
        // time is itself a cost; they are recycled rather than reallocated.
        private static readonly Stack<HashSet<TrainCar>> carSetPool =
            new Stack<HashSet<TrainCar>>();
        private static readonly Stack<List<CarPosition>> positionListPool =
            new Stack<List<CarPosition>>();

        private static int indexFrame = -1;
        private static float indexTime = float.NegativeInfinity;

        public readonly struct CarPosition
        {
            public readonly TrainCar car;
            public readonly RailTrack track;
            public readonly Vector3 position;

            public CarPosition(TrainCar car, RailTrack track, Vector3 position)
            {
                this.car = car;
                this.track = track;
                this.position = position;
            }
        }

        public static void Reset()
        {
            Recycle();
            poweredTrainsets.Clear();
            carSetPool.Clear();
            positionListPool.Clear();
            indexFrame = -1;
            indexTime = float.NegativeInfinity;
        }

        public static IEnumerable<CarPosition> AllCarPositions()
        {
            var sets = Trainset.allSets;
            if (sets == null)
                yield break;
            foreach (var set in sets)
            {
                if (set == null || set.cars == null)
                    continue;
                foreach (var car in set.cars)
                {
                    if (car == null || car.Bogies == null)
                        continue;
                    foreach (var bogie in car.Bogies)
                    {
                        if (bogie == null || bogie.track == null || bogie.traveller == null)
                            continue;
                        var p = bogie.traveller.worldPosition;
                        yield return new CarPosition(car, bogie.track, new Vector3((float)p.x, (float)p.y, (float)p.z));
                    }
                }
            }
        }

        /// Which trainsets have a bogie on each track.
        public static Dictionary<RailTrack, HashSet<int>> TrackOccupancy()
        {
            var result = new Dictionary<RailTrack, HashSet<int>>();
            foreach (var position in AllCarPositions())
            {
                var trainsetId = position.car.trainset == null ? -1 : position.car.trainset.id;
                if (!result.TryGetValue(position.track, out var set))
                    result[position.track] = set = new HashSet<int>();
                set.Add(trainsetId);
            }
            return result;
        }

        /// The tracks a single consist stands on.
        ///
        /// Walks that consist rather than every car in the world: a route only
        /// ever asks about its own train, and the world may hold hundreds of
        /// cars belonging to other ones.
        public static void TracksOccupiedBy(Trainset? trainset, HashSet<RailTrack> into)
        {
            into.Clear();
            var cars = trainset?.cars;
            if (cars == null)
                return;
            foreach (var car in cars)
            {
                if (car == null || car.Bogies == null)
                    continue;
                foreach (var bogie in car.Bogies)
                {
                    if (bogie != null && bogie.track != null)
                        into.Add(bogie.track);
                }
            }
        }

        public static void TracksOccupiedBy(int trainsetId, HashSet<RailTrack> into) =>
            TracksOccupiedBy(Trainset.allSets?.Find(set => set != null && set.id == trainsetId), into);

        /// True when no car sits close enough to the junction that throwing it
        /// would derail something. Every consist counts, including the train
        /// whose route requested the switch.
        public static bool IsJunctionClear(Junction junction)
        {
            if (junction == null)
                return false;

            // Switch safety: never decide from an index built on an earlier
            // frame, however recently.
            EnsureIndex(0f);

            var junctionPosition = junction.position;
            var center = GridCell(junctionPosition);
            for (var x = center.x - 1; x <= center.x + 1; x++)
            {
                for (var y = center.y - 1; y <= center.y + 1; y++)
                {
                    if (!carGrid.TryGetValue(new Vector2Int(x, y), out var bucket))
                        continue;
                    for (var i = 0; i < bucket.Count; i++)
                    {
                        var position = bucket[i];
                        if (Vector3.Distance(position.position, junctionPosition) <= JunctionClearanceMeters)
                            return false;
                    }
                }
            }
            return true;
        }

        /// Trainsets occupying any of the given tracks, excluding one.
        public static HashSet<int> TrainsetsOn(IEnumerable<RailTrack> tracks, int ignoreTrainsetId = -1)
        {
            var wanted = new HashSet<RailTrack>(tracks.Where(t => t != null));
            var result = new HashSet<int>();
            foreach (var position in AllCarPositions())
            {
                if (!wanted.Contains(position.track))
                    continue;
                var trainset = position.car.trainset;
                if (trainset == null || trainset.id == ignoreTrainsetId)
                    continue;
                result.Add(trainset.id);
            }
            return result;
        }

        /// Tracks occupied by another consist, separated into any occupancy and
        /// powered-train occupancy. Routing avoids all occupied through tracks,
        /// while an unpowered destination remains usable for a coupling move.
        public static void OccupiedTracksByOthers(
            int ownTrainsetId, HashSet<RailTrack> occupied, HashSet<RailTrack> powered,
            bool requireFresh = false)
        {
            occupied.Clear();
            powered.Clear();
            // Route creation follows coupling/uncoupling closely enough that a
            // quarter-second display cache can still describe the old consist.
            // A route request pays for one exact rebuild; continuous lookahead
            // continues to share the cheaper cached index.
            if (requireFresh)
                Rebuild();
            else
                EnsureIndex(DisplayMaxAgeSeconds);
            foreach (var pair in carsByTrack)
            {
                foreach (var car in pair.Value)
                {
                    var trainset = car.trainset;
                    if (trainset != null && trainset.id == ownTrainsetId)
                        continue;
                    occupied.Add(pair.Key);
                    if (trainset != null && poweredTrainsets.Contains(trainset.id))
                        powered.Add(pair.Key);
                }
            }
        }

        /// True when the selected tracks contain another consist but none of
        /// its occupying vehicles is a locomotive. Used for a permissive
        /// coupling indication without weakening protection against trains.
        ///
        /// Takes a list rather than a sequence: this runs once per signal per
        /// refresh, often enough that an enumerator per call shows up.
        public static bool ContainsOnlyUnpoweredCars(
            List<RailTrack> tracks, int ignoreTrainsetId = -1)
        {
            // Only an indication is drawn from this, so a recent index is good
            // enough and keeps the scan off most frames entirely.
            EnsureIndex(DisplayMaxAgeSeconds);

            var foundCar = false;
            for (var i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                if (track == null || !carsByTrack.TryGetValue(track, out var cars))
                    continue;
                foreach (var car in cars)
                {
                    var trainset = car.trainset;
                    if (ignoreTrainsetId >= 0 && trainset != null && trainset.id == ignoreTrainsetId)
                        continue;
                    foundCar = true;
                    if (car.IsLoco)
                        return false;
                }
            }
            return foundCar;
        }

        /// Rebuild the index unless one already answers to the requested
        /// freshness. An index built this frame always does.
        private static void EnsureIndex(float maxAgeSeconds)
        {
            if (indexFrame == Time.frameCount)
                return;
            if (maxAgeSeconds > 0f && Time.time - indexTime < maxAgeSeconds)
                return;
            Rebuild();
        }

        private static void Rebuild()
        {
            indexFrame = Time.frameCount;
            indexTime = Time.time;
            Recycle();
            poweredTrainsets.Clear();

            var sets = Trainset.allSets;
            if (sets == null)
                return;

            // Deliberately not written over AllCarPositions: this is the one
            // caller hot enough that the iterator itself is worth avoiding.
            foreach (var set in sets)
            {
                if (set == null || set.cars == null)
                    continue;
                foreach (var car in set.cars)
                {
                    if (car == null || car.Bogies == null)
                        continue;
                    if (car.IsLoco)
                        poweredTrainsets.Add(set.id);
                    foreach (var bogie in car.Bogies)
                    {
                        if (bogie == null || bogie.track == null || bogie.traveller == null)
                            continue;
                        var p = bogie.traveller.worldPosition;
                        var position = new Vector3((float)p.x, (float)p.y, (float)p.z);

                        if (!carsByTrack.TryGetValue(bogie.track, out var cars))
                            carsByTrack[bogie.track] = cars = RentSet();
                        cars.Add(car);

                        var cell = GridCell(position);
                        if (!carGrid.TryGetValue(cell, out var bucket))
                            carGrid[cell] = bucket = RentList();
                        bucket.Add(new CarPosition(car, bogie.track, position));
                    }
                }
            }
        }

        private static void Recycle()
        {
            foreach (var cars in carsByTrack.Values)
            {
                cars.Clear();
                carSetPool.Push(cars);
            }
            carsByTrack.Clear();

            foreach (var bucket in carGrid.Values)
            {
                bucket.Clear();
                positionListPool.Push(bucket);
            }
            carGrid.Clear();
        }

        private static HashSet<TrainCar> RentSet() =>
            carSetPool.Count > 0 ? carSetPool.Pop() : new HashSet<TrainCar>();

        private static List<CarPosition> RentList() =>
            positionListPool.Count > 0 ? positionListPool.Pop() : new List<CarPosition>();

        private static Vector2Int GridCell(Vector3 position) => new Vector2Int(
            Mathf.FloorToInt(position.x / GridCellMeters),
            Mathf.FloorToInt(position.z / GridCellMeters));
    }
}
