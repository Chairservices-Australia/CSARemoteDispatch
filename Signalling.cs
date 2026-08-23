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

        /// Read the line ahead of the car the player is in.
        public static Reading ReadForPlayer()
        {
            var car = PlayerManager.Car;
            if (car == null)
                return new Reading(Aspect.Unknown, 0, "");

            var start = LeadingStep(car);
            if (start == null)
                return new Reading(Aspect.Unknown, 0, "");

            var ownTrainsetId = car.trainset == null ? -1 : car.trainset.id;
            var ahead = BlocksAhead(start.Value, BlocksToRead + 1);
            var aspect = AspectFor(ahead, ownTrainsetId);
            var speed = SpeedLimitFor(start.Value);
            var blockAhead = ahead.Count > 0 ? DescribeTrack(ahead[0].track) : "";
            return new Reading(aspect, speed, blockAhead);
        }

        /// The step leaving the front of the consist, in the direction it is
        /// travelling. Falls back to the way the car faces when stationary, so a
        /// standing train still reads the line it is pointed at.
        private static TrackGraph.Step? LeadingStep(TrainCar car)
        {
            var bogie = car.Bogies?.FirstOrDefault(b => b != null && b.track != null);
            if (bogie == null)
                return null;

            var velocity = car.rb == null ? Vector3.zero : car.rb.velocity;
            var heading = velocity.sqrMagnitude > 0.25f ? velocity : car.transform.forward;
            heading.y = 0;

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

        private static Aspect AspectFor(List<TrackGraph.Step> ahead, int ownTrainsetId)
        {
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
