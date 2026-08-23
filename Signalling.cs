using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Signals.Common;
using Signals.Game;
using Signals.Game.Controllers;
using Signals.Game.Generation;
using Signals.Game.Railway;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    /// Compact HUD representation of the aspect supplied by DV Signals.
    public enum Aspect
    {
        Clear,              // green          - all three blocks clear
        PreliminaryCaution, // flashing amber - stop aspect is two signals ahead
        Caution,            // steady amber   - next signal is at stop
        Stop,               // red            - next protected block occupied
        Unknown,            // no line ahead to read (buffer stop, or not on track)
    }

    /// DV Signals is authoritative for physical signals, blocks, occupation,
    /// junction paths and reservations. This class only finds the signal ahead
    /// of a train and translates its live aspect for the CSA HUD.
    public static class Signalling
    {
        public const float SpeedLookaheadMeters = 400f;
        public const int DefaultSpeedLimitKph = 30;

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
            public readonly float approachingDistanceMeters;

            public Reading(Aspect aspect, int speedLimitKph, string blockAhead,
                float approachingDistanceMeters = -1f)
            {
                this.aspect = aspect;
                this.speedLimitKph = speedLimitKph;
                this.blockAhead = blockAhead;
                this.approachingDistanceMeters = approachingDistanceMeters;
            }
        }

        /// Remembered direction of travel, so the aspect does not flip to the
        /// other end of the train the moment it stops.
        private static readonly Dictionary<int, Vector3> lastHeadings = new Dictionary<int, Vector3>();

        public static void Reset() => lastHeadings.Clear();

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

            var aspect = Aspect.Unknown;
            var approachingDistance = -1f;
            var blockAhead = "";
            ReadDvSignal(start.Value, leadCar.transform.position,
                out aspect, out approachingDistance, out blockAhead);

            // Prefer the limit the game actually posts; fall back to the figure
            // derived from curvature only for the initial block. SpeedSigns
            // latches either value until the leading end crosses another sign.
            SpeedSigns.ScanIfDue();
            var speed = SpeedSigns.LimitAt(
                ownTrainsetId, leadCar, heading,
                () => Mathf.Max(DefaultSpeedLimitKph, SpeedLimitFor(start.Value)));
            return new Reading(aspect, speed, blockAhead, approachingDistance);
        }

        private static void ReadDvSignal(TrackGraph.Step start, Vector3 position,
            out Aspect aspect, out float distance, out string signalName)
        {
            aspect = Aspect.Unknown;
            distance = -1f;
            signalName = "";
            if (!SignalManager.Running)
                return;

            var controller = NextDvSignalController(start, position);
            var signal = controller?.GetControllerSignal();
            var current = signal?.CurrentAspect;
            if (controller == null || signal == null || current == null)
                return;

            signalName = controller.Name;
            distance = Vector3.Distance(position, controller.Position);
            aspect = NswAspect(current);
        }

        internal static BasicSignalController? NextDvSignalController(
            TrackGraph.Step start, Vector3 position)
        {
            if (!SignalManager.Running)
                return null;
            var direction = start.enteredViaIn ? TrackDirection.Out : TrackDirection.In;
            TrackWalker.GetTracksUntilMainSignal(start.track, direction, out var info);
            var controller = info.Signal;
            if (controller != null && HasPassed(controller, start.track, direction, position))
            {
                // TrackWalker starts at a whole RailTrack and can therefore
                // return a signal at the end already passed by the leading cab.
                TrackWalker.GetTracksUntilMainSignal(
                    start.track, direction, controller, out info);
                controller = info.Signal;
            }
            return controller;
        }

        private static bool HasPassed(BasicSignalController controller, RailTrack currentTrack,
            TrackDirection direction, Vector3 trainPosition)
        {
            var placement = controller.PlacementInfo;
            if (!placement.HasValue || placement.Value.Track != currentTrack)
                return false;
            if (!SpeedSigns.TryProjectOntoTrack(trainPosition, currentTrack,
                    out var trainAlong, out _)
                || !SpeedSigns.TryProjectOntoTrack(controller.Position, currentTrack,
                    out var signalAlong, out _))
                return false;
            const float passedToleranceMeters = 0.75f;
            return direction == TrackDirection.Out
                ? trainAlong > signalAlong + passedToleranceMeters
                : trainAlong < signalAlong - passedToleranceMeters;
        }

        /// Translate what DV Signals is physically displaying, rather than the
        /// aspect's array index. Different controller types contain different
        /// numbers of route, reservation and shunting aspects, so index-based
        /// mapping incorrectly made most of them preliminary caution.
        private static Aspect NswAspect(Signals.Game.Aspects.IAspect current)
        {
            if (current.DisallowPassing)
                return Aspect.Stop;

            var definition = current.GetDefinition();
            var steady = definition.OnLights ?? new SignalLightDefinition[0];
            var blinking = definition.BlinkingLights ?? new SignalLightDefinition[0];

            if (blinking.Any(light => IsAmber(light.Colour)))
                return Aspect.PreliminaryCaution;
            if (steady.Any(light => IsAmber(light.Colour)))
                return Aspect.Caution;
            if (steady.Any(light => IsGreen(light.Colour)))
                return Aspect.Clear;

            // Semaphore/custom packs may not encode their indication as a
            // coloured lamp. Their definition names retain the same semantics.
            var name = definition.gameObject == null ? "" : definition.gameObject.name;
            if (Contains(name, "clear"))
                return Aspect.Clear;
            if (Contains(name, "nextstop") || Contains(name, "expectstop")
                || Contains(name, "restricted"))
                return Aspect.Caution;
            return Aspect.Caution;
        }

        private static bool IsAmber(Color colour) =>
            colour.r > 0.45f && colour.g > 0.25f && colour.b < 0.35f;

        private static bool IsGreen(Color colour) =>
            colour.g > 0.35f && colour.g > colour.r * 1.2f && colour.g > colour.b * 1.2f;

        private static bool Contains(string value, string text) =>
            value.IndexOf(text, System.StringComparison.OrdinalIgnoreCase) >= 0;

        /// DV Signals' default pack includes a three-lamp main controller plus
        /// optional four-lamp variants for entries, exits and combined signals.
        /// NSW mode consistently uses the three-lamp controller for all main
        /// signals while retaining dedicated distant and shunting equipment.
        [HarmonyPatch(typeof(RealisticSignalPlacer), nameof(RealisticSignalPlacer.CreateSignals))]
        private static class NswSignalPackPatch
        {
            private static void Prefix(SignalPack pack)
            {
                if (pack == null || pack.Signal == null)
                    return;
                var main = pack.Signal;
                pack.DivergingSignal = main;
                pack.LeftJunctionSignal = main;
                pack.RightJunctionSignal = main;
                pack.EntrySignal = main;
                pack.ExitSignal = main;
                pack.ExitPassengerSignal = main;
                pack.ExitMainlineSignal = main;
                pack.CombinedSignal = main;
                pack.CombinedLeftJunctionSignal = main;
                pack.CombinedRightJunctionSignal = main;
                pack.OldSignal = main;
                pack.OldDivergingSignal = main;
                pack.OldLeftJunctionSignal = main;
                pack.OldRightJunctionSignal = main;
                pack.OldEntrySignal = main;
                pack.OldExitSignal = main;
                pack.OldExitPassengerSignal = main;
                pack.OldExitMainlineSignal = main;
                pack.OldCombinedSignal = main;
                pack.OldCombinedLeftJunctionSignal = main;
                pack.OldCombinedRightJunctionSignal = main;
            }
        }

        /// Direction the driver has selected, from the reverser, or zero when it
        /// is centred or no locomotive in the consist reports one.
        ///
        /// A standing train has no motion to read, but the reverser still says
        /// which way it is about to go, which is what the road ahead should be
        /// laid and read for.
        public static Vector3 ReverserHeading(Trainset? trainset)
        {
            if (trainset?.cars == null)
                return Vector3.zero;

            foreach (var car in trainset.cars)
            {
                if (car == null)
                    continue;
                var controller = car.GetComponent<DV.RemoteControls.RemoteControllerModule>();
                var reverser = controller?.controlsOverrider?.Reverser;
                if (reverser == null)
                    continue;

                // Centred: no direction selected.
                var value = reverser.Value;
                if (Mathf.Abs(value - 0.5f) < 0.1f)
                    continue;

                var forward = car.transform.forward;
                forward.y = 0;
                if (forward.sqrMagnitude < 0.0001f)
                    continue;
                return (value > 0.5f ? forward : -forward).normalized;
            }
            return Vector3.zero;
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

            // Standing still: the reverser says where it is about to go, which
            // beats both the last direction and the way the cab happens to face.
            var selected = ReverserHeading(trainset);
            if (selected.sqrMagnitude > 0.001f)
                return selected;

            if (trainsetId >= 0 && lastHeadings.TryGetValue(trainsetId, out var remembered)
                && remembered.sqrMagnitude > 0.001f)
                return remembered;

            var forward = car.transform.forward;
            forward.y = 0;
            return forward.normalized;
        }

        /// The car at the leading end of the consist for this direction.
        internal static TrainCar LeadingCar(Trainset? trainset, TrainCar fallback, Vector3 heading)
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
