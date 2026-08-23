using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DV.Signs;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    /// The speed limits Derail Valley actually posts, read from the signs it
    /// places rather than derived from track geometry.
    ///
    /// There is no speed limit anywhere in the game's API, but every trackside
    /// sign carries a SignParameters entry naming its type and the text printed
    /// on it, so a SpeedLimit sign states its own value.
    public static class SpeedSigns
    {
        public readonly struct Sign
        {
            public readonly Vector3 position;
            public readonly Vector3 facing;
            public readonly int kph;
            public readonly RailTrack track;
            public readonly float trackPosition;

            public Sign(Vector3 position, Vector3 facing, int kph, RailTrack track, float trackPosition)
            {
                this.position = position;
                this.facing = facing;
                this.kph = kph;
                this.track = track;
                this.trackPosition = trackPosition;
            }
        }

        /// Signs stream in and out with the world, so they are accumulated as
        /// they are seen rather than scanned once, keyed by rounded position so
        /// the same sign is not stored twice.
        private sealed class TrainState
        {
            public TrainCar leadCar = null!;
            public RailTrack track = null!;
            public float trackPosition;
            public int limit;
        }

        private static readonly Dictionary<Vector3Int, Sign> known = new Dictionary<Vector3Int, Sign>();
        private static readonly Dictionary<int, TrainState> trainStates = new Dictionary<int, TrainState>();
        private static readonly Dictionary<Vector2Int, List<RailTrack>> trackGrid =
            new Dictionary<Vector2Int, List<RailTrack>>();
        private static float nextScanTime;
        private static float nextTrackGridTime;

        public const float RescanSeconds = 5f;

        public static int KnownCount => known.Count;

        public static void Reset()
        {
            known.Clear();
            trainStates.Clear();
            trackGrid.Clear();
            nextScanTime = 0f;
            nextTrackGridTime = 0f;
        }

        public static void ScanIfDue()
        {
            if (Time.time < nextScanTime)
                return;
            nextScanTime = Time.time + RescanSeconds;
            Scan();
        }

        public static void Scan()
        {
            if (trackGrid.Count == 0 || Time.time >= nextTrackGridTime)
            {
                BuildTrackGrid(UnityEngine.Object.FindObjectsOfType<RailTrack>());
                nextTrackGridTime = Time.time + TrackGridRefreshSeconds;
            }
            foreach (var data in UnityEngine.Object.FindObjectsOfType<SignGeneratorData>())
            {
                if (data == null || data.signParameters == null)
                    continue;
                foreach (var parameters in data.signParameters)
                {
                    if (!IsSpeedLimit(parameters.type))
                        continue;
                    if (!TryParseSpeed(parameters.signText, out var kph))
                        continue;

                    var transform = data.transform;
                    var position = transform.position;
                    var key = new Vector3Int(
                        Mathf.RoundToInt(position.x),
                        Mathf.RoundToInt(position.y),
                        Mathf.RoundToInt(position.z));
                    if (known.ContainsKey(key))
                        continue;
                    if (!NearestTrack(position, transform.forward, NearbyTracks(position),
                        out var track, out var trackPosition))
                        continue;
                    known[key] = new Sign(position, transform.forward, kph, track, trackPosition);
                }
            }
        }

        private static bool IsSpeedLimit(SignType type) =>
            type == SignType.SpeedLimit
            || type == SignType.SpeedLimitOld
            || type == SignType.SpeedLimitYellow
            || type == SignType.SpeedLimitYellowOld;

        /// Sign text is the limit in tens of km/h on DV's signs ("6" is 60), but
        /// accept a plain speed too in case a sign spells it out.
        private static bool TryParseSpeed(string text, out int kph)
        {
            kph = 0;
            if (string.IsNullOrEmpty(text))
                return false;
            var trimmed = new string(text.Where(char.IsDigit).ToArray());
            if (trimmed.Length == 0)
                return false;
            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return false;
            kph = value <= 15 ? value * 10 : value;
            return kph > 0 && kph <= 200;
        }

        /// The limit in force for a train travelling `heading`.
        ///
        /// Signs divide the line into zones: a limit applies from the sign that
        /// posts it until the next one, so the governing sign is the nearest one
        /// already passed. Each sign is bound to its nearest physical track, so
        /// neighbouring station roads cannot change the limit. Once selected,
        /// the value persists across track boundaries until another sign is
        /// actually passed.
        public static int LimitAt(int trainsetId, TrainCar? leadCar, Vector3 heading,
            Func<int> initialLimit)
        {
            trainStates.TryGetValue(trainsetId, out var state);
            if (leadCar == null)
                return state?.limit ?? initialLimit();
            var bogie = leadCar.Bogies?.FirstOrDefault(b => b != null && b.track != null);
            if (bogie == null)
                return state?.limit ?? initialLimit();

            var position = leadCar.transform.position;
            var flatHeading = new Vector3(heading.x, 0, heading.z);
            if (flatHeading.sqrMagnitude < 0.0001f || known.Count == 0)
                return state?.limit ?? initialLimit();
            flatHeading = flatHeading.normalized;

            if (!ProjectOntoTrack(position, bogie.track, out var trainPosition, out _))
                return state?.limit ?? initialLimit();

            if (state == null)
            {
                state = new TrainState
                {
                    leadCar = leadCar,
                    track = bogie.track,
                    trackPosition = trainPosition,
                    limit = InitialLimit(bogie.track, trainPosition, flatHeading) ?? initialLimit(),
                };
                if (trainsetId >= 0)
                    trainStates[trainsetId] = state;
                return state.limit;
            }

            // Entering another RailTrack does not define a new speed block. Keep
            // the current limit and establish a new crossing baseline there.
            if (state.leadCar != leadCar || state.track != bogie.track)
            {
                state.leadCar = leadCar;
                state.track = bogie.track;
                state.trackPosition = trainPosition;
                return state.limit;
            }

            var movement = trainPosition - state.trackPosition;
            if (Mathf.Abs(movement) < MinimumMovementMeters)
                return state.limit;

            Sign? lastCrossed = null;
            foreach (var sign in known.Values)
            {
                if (sign.track != bogie.track || !FacesTrain(sign, flatHeading))
                    continue;

                var crossed = movement > 0f
                    ? sign.trackPosition > state.trackPosition + CrossingToleranceMeters
                        && sign.trackPosition <= trainPosition + CrossingToleranceMeters
                    : sign.trackPosition < state.trackPosition - CrossingToleranceMeters
                        && sign.trackPosition >= trainPosition - CrossingToleranceMeters;
                if (!crossed)
                    continue;

                // If more than one sign was crossed between UI updates, the last
                // one in the direction of travel defines the new block.
                if (!lastCrossed.HasValue
                    || (movement > 0f && sign.trackPosition > lastCrossed.Value.trackPosition)
                    || (movement < 0f && sign.trackPosition < lastCrossed.Value.trackPosition))
                    lastCrossed = sign;
            }

            state.trackPosition = trainPosition;
            if (lastCrossed.HasValue)
                state.limit = lastCrossed.Value.kph;
            return state.limit;
        }

        private static int? InitialLimit(RailTrack track, float trainPosition, Vector3 heading)
        {
            var increasing = TrackDirectionAt(track, trainPosition, heading);
            var nearestPassed = float.MaxValue;
            int? result = null;
            foreach (var sign in known.Values)
            {
                if (sign.track != track || !FacesTrain(sign, heading))
                    continue;
                var passed = increasing
                    ? trainPosition - sign.trackPosition
                    : sign.trackPosition - trainPosition;
                if (passed < 0f || passed >= nearestPassed)
                    continue;
                nearestPassed = passed;
                result = sign.kph;
            }
            return result;
        }

        private static bool FacesTrain(Sign sign, Vector3 heading)
        {
            var facing = new Vector3(sign.facing.x, 0f, sign.facing.z);
            return facing.sqrMagnitude < 0.0001f
                || Vector3.Dot(facing.normalized, heading) <= 0.3f;
        }

        private const float TrackAssignmentMeters = 6f;
        private const float TrackGridCellMeters = 50f;
        private const float TrackGridSampleMeters = 25f;
        private const float TrackGridRefreshSeconds = 30f;
        private const float MinimumMovementMeters = 0.05f;
        private const float CrossingToleranceMeters = 0.1f;
        private const int CurveSamples = 32;

        private static void BuildTrackGrid(IEnumerable<RailTrack> tracks)
        {
            trackGrid.Clear();
            foreach (var track in tracks)
            {
                var curve = track == null ? null : track.curve;
                if (curve == null || curve.pointCount < 2)
                    continue;
                var samples = Mathf.Max(1,
                    Mathf.CeilToInt(TrackGraph.TrackLength(track!) / TrackGridSampleMeters));
                var cells = new HashSet<Vector2Int>();
                for (var i = 0; i <= samples; i++)
                    cells.Add(GridCell(curve.GetPointAt((float)i / samples)));
                foreach (var cell in cells)
                {
                    if (!trackGrid.TryGetValue(cell, out var bucket))
                        trackGrid[cell] = bucket = new List<RailTrack>();
                    bucket.Add(track!);
                }
            }
        }

        private static IEnumerable<RailTrack> NearbyTracks(Vector3 position)
        {
            var center = GridCell(position);
            var seen = new HashSet<RailTrack>();
            for (var x = center.x - 1; x <= center.x + 1; x++)
            for (var y = center.y - 1; y <= center.y + 1; y++)
            {
                if (!trackGrid.TryGetValue(new Vector2Int(x, y), out var bucket))
                    continue;
                foreach (var track in bucket)
                    if (track != null && seen.Add(track))
                        yield return track;
            }
        }

        private static Vector2Int GridCell(Vector3 position) => new Vector2Int(
            Mathf.FloorToInt(position.x / TrackGridCellMeters),
            Mathf.FloorToInt(position.z / TrackGridCellMeters));

        private static bool NearestTrack(Vector3 position, Vector3 signFacing, IEnumerable<RailTrack> tracks,
            out RailTrack nearest, out float trackPosition)
        {
            nearest = null!;
            trackPosition = 0f;
            var best = TrackAssignmentMeters;
            foreach (var track in tracks)
            {
                if (!ProjectOntoTrack(position, track, out var along, out var distance) || distance >= best)
                    continue;
                var tangent = TangentAt(track, along);
                var facing = new Vector3(signFacing.x, 0f, signFacing.z);
                if (tangent.sqrMagnitude < 0.0001f || facing.sqrMagnitude < 0.0001f
                    || Mathf.Abs(Vector3.Dot(tangent.normalized, facing.normalized)) < MinimumTrackAlignment)
                    continue;
                best = distance;
                nearest = track;
                trackPosition = along;
            }
            return nearest != null;
        }

        private static bool ProjectOntoTrack(Vector3 position, RailTrack track,
            out float along, out float distance)
        {
            along = 0f;
            distance = float.MaxValue;
            var curve = track == null ? null : track.curve;
            if (curve == null || curve.pointCount < 2)
                return false;

            var flatPosition = new Vector3(position.x, 0f, position.z);
            var previous = curve.GetPointAt(0f);
            previous.y = 0f;
            var travelled = 0f;
            for (var i = 1; i <= CurveSamples; i++)
            {
                var current = curve.GetPointAt((float)i / CurveSamples);
                current.y = 0f;
                var segment = current - previous;
                var length = segment.magnitude;
                if (length > 0.001f)
                {
                    var fraction = Mathf.Clamp01(Vector3.Dot(flatPosition - previous, segment) / (length * length));
                    var projected = previous + segment * fraction;
                    var candidate = Vector3.Distance(flatPosition, projected);
                    if (candidate < distance)
                    {
                        distance = candidate;
                        along = travelled + length * fraction;
                    }
                }
                travelled += length;
                previous = current;
            }
            return distance < float.MaxValue;
        }

        internal static bool TryProjectOntoTrack(Vector3 position, RailTrack track,
            out float along, out float distance) =>
            ProjectOntoTrack(position, track, out along, out distance);

        private static bool TrackDirectionAt(RailTrack track, float along, Vector3 heading)
            => Vector3.Dot(TangentAt(track, along), heading) >= 0f;

        private static Vector3 TangentAt(RailTrack track, float along)
        {
            var length = Mathf.Max(1f, TrackGraph.TrackLength(track));
            var t = Mathf.Clamp01(along / length);
            var delta = 1f / CurveSamples;
            var before = track.curve.GetPointAt(Mathf.Clamp01(t - delta));
            var after = track.curve.GetPointAt(Mathf.Clamp01(t + delta));
            var tangent = after - before;
            tangent.y = 0f;
            return tangent;
        }

        private const float MinimumTrackAlignment = 0.75f;
    }
}
