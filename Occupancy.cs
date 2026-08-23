using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    /// Where every car currently is, for switch safety and block detection.
    public static class Occupancy
    {
        /// A junction with a bogie this close is treated as occupied. Throwing a
        /// switch under a car derails it, so this is deliberately generous.
        public const float JunctionClearanceMeters = 20f;

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

        /// True when no car sits close enough to the junction that throwing it
        /// would derail something. `ignoreTrainsetId` excludes the train being
        /// routed, which may legitimately be standing on its own switch.
        public static bool IsJunctionClear(Junction junction, int ignoreTrainsetId = -1)
        {
            if (junction == null)
                return false;
            var junctionPosition = junction.position;
            foreach (var position in AllCarPositions())
            {
                if (ignoreTrainsetId >= 0
                    && position.car.trainset != null
                    && position.car.trainset.id == ignoreTrainsetId)
                    continue;
                if (Vector3.Distance(position.position, junctionPosition) <= JunctionClearanceMeters)
                    return false;
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
    }
}
