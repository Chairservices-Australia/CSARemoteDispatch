using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    /// Three-position colour light aspects.
    public enum Aspect
    {
        Clear,      // green  - line clear for at least two blocks
        Caution,    // amber  - next block clear, the one beyond is not
        Stop,       // red    - next block occupied
        Unknown,    // no line ahead to read (buffer stop, or not on track)
    }

    /// Block occupancy ahead of a train, and the speed the road ahead will take.
    ///
    /// A block here is one RailTrack: DV has no signalling of its own, and track
    /// is already divided at every junction, which is where a real block would
    /// end anyway.
    public static class Signalling
    {
        /// How far ahead to read, in blocks, and the distance over which the
        /// speed of the road ahead is judged.
        public const int BlocksToRead = 2;
        public const float SpeedLookaheadMeters = 400f;

        /// Standard sign values, so the readout matches the numbers a driver
        /// expects rather than an arbitrary computed figure.
        private static readonly int[] SignValues = { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 120 };

        /// Lateral acceleration allowed through a curve, in m/s^2. Sets how
        /// sharply the derived limit falls off as the radius tightens.
        private const float LateralAcceleration = 0.65f;

        public readonly struct Reading
        {
            public readonly Aspect aspect;
            public readonly int speedLimitKph;
            public readonly string blockAhead;

            public Reading(Aspect aspect, int speedLimitKph, string blockAhead)
            {
                this.aspect = aspect;
                this.speedLimitKph = speedLimitKph;
                this.blockAhead = blockAhead;
            }
        }

        /// Remembered direction of travel, so the aspect does not flip to the
        /// other end of the train the moment it stops.
        private static readonly Dictionary<int, Vector3> lastHeadings = new Dictionary<int, Vector3>();

        /// Read the line ahead of the train the player is in.
        public static Reading ReadForPlayer()
        {
            var car = PlayerManager.Car;
            return car == null ? new Reading(Aspect.Unknown, 0, "") : ReadFor(car);
        }

        /// Read the line ahead of the consist a given car belongs to.
        public static Reading ReadFor(TrainCar car)
        {
            if (car == null)
                return new Reading(Aspect.Unknown, 0, "");

            var trainset = car.trainset;
            var ownTrainsetId = trainset == null ? -1 : trainset.id;
            var heading = HeadingOf(car, trainset, ownTrainsetId);

            // Read from the end of the consist that leads, which when propelling
            // or reversing is not the car the player is sitting in.
            var leadCar = LeadingCar(trainset, car, heading);
            var start = StepFrom(leadCar, heading);
            if (start == null)
                return new Reading(Aspect.Unknown, 0, "");

            var ahead = BlocksAhead(start.Value, BlocksToRead + 1);
            var aspect = AspectFor(start.Value, ahead, ownTrainsetId);
            var speed = SpeedLimitFor(start.Value);
            var blockAhead = ahead.Count > 0 ? DescribeTrack(ahead[0].track) : "";
            return new Reading(aspect, speed, blockAhead);
        }

        /// Direction of travel, from the consist's own motion. Falls back to the
        /// last direction it moved, and only to where a car points if it has not
        /// moved at all: while reversing, the way a locomotive faces is the
        /// opposite of where it is going.
        private static Vector3 HeadingOf(TrainCar car, Trainset? trainset, int trainsetId)
        {
            var velocity = Vector3.zero;
            var source = trainset?.firstCar ?? car;
            if (source != null && source.rb != null)
                velocity = source.rb.velocity;
            velocity.y = 0;

            // A low threshold on purpose: a train easing back at walking pace is
            // still going that way, and must not read the line off its nose.
            if (velocity.sqrMagnitude > 0.0025f)
            {
                var normalized = velocity.normalized;
                if (trainsetId >= 0)
                    lastHeadings[trainsetId] = normalized;
                return normalized;
            }

            if (trainsetId >= 0 && lastHeadings.TryGetValue(trainsetId, out var remembered)
                && remembered.sqrMagnitude > 0.001f)
                return remembered;

            var forward = car.transform.forward;
            forward.y = 0;
            return forward.normalized;
        }

        /// The car at the leading end of the consist for this direction.
        private static TrainCar LeadingCar(Trainset? trainset, TrainCar fallback, Vector3 heading)
        {
            var first = trainset?.firstCar;
            var last = trainset?.lastCar;
            if (first == null || last == null || first == last)
                return fallback;

            var alongConsist = first.transform.position - last.transform.position;
            alongConsist.y = 0;
            return Vector3.Dot(alongConsist, heading) >= 0f ? first : last;
        }

        private static TrackGraph.Step? StepFrom(TrainCar car, Vector3 heading)
        {
            var bogie = car?.Bogies?.FirstOrDefault(b => b != null && b.track != null);
            if (bogie == null)
                return null;

            var track = bogie.track;
            var curve = track.curve;
            if (curve == null || curve.pointCount < 2)
                return new TrackGraph.Step(track, true);

            var along = curve[curve.pointCount - 1].position - curve[0].position;
            var forward = Vector3.Dot(new Vector3(along.x, 0, along.z), heading) >= 0f;
            return new TrackGraph.Step(track, forward);
        }

        /// Walk the line the switches are actually set for, which is where the
        /// train will go, rather than every branch it could take.
        public static List<TrackGraph.Step> BlocksAhead(TrackGraph.Step start, int count)
        {
            var blocks = new List<TrackGraph.Step>();
            var current = start;
            var visited = new HashSet<RailTrack> { start.track };

            for (var i = 0; i < count; i++)
            {
                var branch = current.enteredViaIn
                    ? current.track.GetOutBranch()
                    : current.track.GetInBranch();
                if (branch == null || branch.track == null || !visited.Add(branch.track))
                    break;
                var next = new TrackGraph.Step(branch.track, branch.first);
                blocks.Add(next);
                current = next;
            }
            return blocks;
        }

        private static Aspect AspectFor(TrackGraph.Step current, List<TrackGraph.Step> ahead, int ownTrainsetId)
        {
            // Foreign cars standing on the track the train is already running
            // along are the closest hazard there is, and used to be missed
            // entirely because only subsequent blocks were examined.
            if (IsOccupied(current.track, ownTrainsetId))
                return Aspect.Stop;
            if (ahead.Count == 0)
                return Aspect.Unknown;
            if (IsOccupied(ahead[0].track, ownTrainsetId))
                return Aspect.Stop;
            if (ahead.Count > 1 && IsOccupied(ahead[1].track, ownTrainsetId))
                return Aspect.Caution;
            return Aspect.Clear;
        }

        private static bool IsOccupied(RailTrack track, int ownTrainsetId)
        {
            foreach (var position in Occupancy.AllCarPositions())
            {
                if (position.track != track)
                    continue;
                var trainset = position.car.trainset;
                if (trainset == null || trainset.id != ownTrainsetId)
                    return true;
            }
            return false;
        }

        /// Speed the road ahead will take, derived from how sharply it curves.
        ///
        /// Derail Valley publishes no speed limit anywhere in its API - track
        /// signs carry only yard and track IDs - so this is computed from track
        /// geometry rather than read from the game, and is a recommended speed
        /// for the curve rather than an authored limit.
        public static int SpeedLimitFor(TrackGraph.Step start)
        {
            var tightestRadius = float.MaxValue;
            var travelled = 0f;
            var current = start;
            var visited = new HashSet<RailTrack> { start.track };

            while (travelled < SpeedLookaheadMeters)
            {
                var radius = TightestRadius(current.track);
                if (radius > 0f)
                    tightestRadius = Mathf.Min(tightestRadius, radius);
                travelled += TrackGraph.TrackLength(current.track);

                var branch = current.enteredViaIn
                    ? current.track.GetOutBranch()
                    : current.track.GetInBranch();
                if (branch == null || branch.track == null || !visited.Add(branch.track))
                    break;
                current = new TrackGraph.Step(branch.track, branch.first);
            }

            if (tightestRadius == float.MaxValue)
                return SignValues[SignValues.Length - 1];

            // v = sqrt(a * r), then rounded down to a value that appears on a sign.
            var metersPerSecond = Mathf.Sqrt(LateralAcceleration * tightestRadius);
            var kph = metersPerSecond * 3.6f;
            return RoundDownToSign(kph);
        }

        /// Smallest turning radius on a track, from the heading change between
        /// evenly spaced points: radius = arc length / angle turned.
        private static float TightestRadius(RailTrack track)
        {
            var pointSet = track == null ? null : track.GetKinkedPointSet();
            var points = pointSet?.points;
            if (points == null || points.Length < 3)
                return -1f;

            var tightest = float.MaxValue;
            for (var i = 1; i + 1 < points.Length; i++)
            {
                var previous = points[i - 1].position;
                var here = points[i].position;
                var next = points[i + 1].position;

                var a = new Vector3((float)(here.x - previous.x), 0, (float)(here.z - previous.z));
                var b = new Vector3((float)(next.x - here.x), 0, (float)(next.z - here.z));
                if (a.sqrMagnitude < 0.01f || b.sqrMagnitude < 0.01f)
                    continue;

                var angle = Vector3.Angle(a, b) * Mathf.Deg2Rad;
                if (angle < 0.0005f)
                    continue;   // effectively straight
                var radius = b.magnitude / angle;
                tightest = Mathf.Min(tightest, radius);
            }
            return tightest == float.MaxValue ? -1f : tightest;
        }

        private static int RoundDownToSign(float kph)
        {
            var result = SignValues[0];
            foreach (var value in SignValues)
            {
                if (kph >= value)
                    result = value;
            }
            return result;
        }

        private static string DescribeTrack(RailTrack track)
        {
            var logicTrack = track == null ? null : track.LogicTrack();
            return logicTrack == null ? "" : logicTrack.ID.FullDisplayID;
        }
    }
}
