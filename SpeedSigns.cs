using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DV.Signs;
using UnityEngine;
using UnityEngine.SceneManagement;

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
            public readonly bool? arrowLeft;

            public Sign(Vector3 position, Vector3 facing, int kph, RailTrack track,
                float trackPosition, bool? arrowLeft)
            {
                this.position = position;
                this.facing = facing;
                this.kph = kph;
                this.track = track;
                this.trackPosition = trackPosition;
                this.arrowLeft = arrowLeft;
            }
        }

        /// Identity of a sign: where it stands, to the metre, plus which entry
        /// on the pole it is.
        ///
        /// Keyed by position rather than by the component carrying it, because
        /// the world streams signs out and back in and the same physical sign
        /// must not register twice. The index separates the stacked limits on a
        /// junction board, which share both a pole and a position.
        private readonly struct SignKey : IEquatable<SignKey>
        {
            private readonly int x;
            private readonly int y;
            private readonly int z;
            private readonly int index;

            public SignKey(Vector3 position, int index)
            {
                x = Mathf.RoundToInt(position.x);
                y = Mathf.RoundToInt(position.y);
                z = Mathf.RoundToInt(position.z);
                this.index = index;
            }

            public bool Equals(SignKey other) =>
                x == other.x && y == other.y && z == other.z && index == other.index;

            public override bool Equals(object? obj) => obj is SignKey other && Equals(other);

            public override int GetHashCode()
            {
                var hash = x;
                hash = (hash * 397) ^ y;
                hash = (hash * 397) ^ z;
                return (hash * 397) ^ index;
            }
        }

        private sealed class TrainState
        {
            public TrainCar leadCar = null!;
            public RailTrack track = null!;
            public float trackPosition;
            public int limit;
        }

        /// Signs stream in and out with the world, so they are accumulated as
        /// they are seen rather than scanned once.
        private static readonly Dictionary<SignKey, Sign> known = new Dictionary<SignKey, Sign>();

        /// The same signs indexed by the track they belong to. A limit is only
        /// ever looked up for the track a train is on, and the flat collection
        /// grows for as long as the session lasts, so it must not be the thing
        /// that gets searched.
        private static readonly Dictionary<RailTrack, List<Sign>> signsByTrack =
            new Dictionary<RailTrack, List<Sign>>();

        /// Signs no track in the current grid is near enough to claim. Held so
        /// a pass does not keep paying to re-project them; cleared whenever the
        /// grid is rebuilt, which is the only thing that can change the answer.
        private static readonly HashSet<SignKey> unplaceable = new HashSet<SignKey>();

        private static readonly Dictionary<int, TrainState> trainStates = new Dictionary<int, TrainState>();
        private static readonly Dictionary<Vector2Int, List<RailTrack>> trackGrid =
            new Dictionary<Vector2Int, List<RailTrack>>();

        private static readonly Queue<Scene> scenesToScan = new Queue<Scene>();
        private static readonly HashSet<int> queuedScenes = new HashSet<int>();

        public static int KnownCount => known.Count;

        /// What discovery made of the sign at this position and entry index:
        /// the track it was put on, or why it was not. Reported by the /signs
        /// endpoint, which exists to answer "why is that limit not showing".
        public static bool TryDescribePlacement(Vector3 position, int index, out string description)
        {
            var key = new SignKey(position, index);
            if (known.TryGetValue(key, out var sign))
            {
                var logicTrack = sign.track == null ? null : sign.track.LogicTrack();
                description = "on " + (logicTrack == null
                        ? "an unnamed track" : logicTrack.ID.FullDisplayID)
                    + " at " + Mathf.RoundToInt(sign.trackPosition) + " m";
                return true;
            }
            if (unplaceable.Contains(key))
            {
                description = "no track near enough, or none facing the right way";
                return false;
            }
            description = "not a speed limit, or not yet scanned";
            return false;
        }

        public static void Reset()
        {
            known.Clear();
            signsByTrack.Clear();
            unplaceable.Clear();
            trainStates.Clear();
            trackGrid.Clear();
            scenesToScan.Clear();
            queuedScenes.Clear();
        }

        /// Arrange to inspect a scene after Unity has finished loading it. The
        /// scene handle prevents the initial enumeration and sceneLoaded from
        /// queuing the same scene twice.
        public static void QueueScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || !queuedScenes.Add(scene.handle))
                return;
            scenesToScan.Enqueue(scene);
        }

        public static void ForgetScene(Scene scene) => queuedScenes.Remove(scene.handle);

        /// Sign discovery, spread across frames as scenes stream in.
        ///
        /// FindObjectsOfType performs its entire world walk synchronously before
        /// returning, so processing its result in slices did not prevent the
        /// several-second hitch caused by the walk itself. Scene loading is the
        /// arrival notification: traverse only that scene's hierarchy, yielding
        /// regularly, and register each SignGeneratorData component in place.
        public static IEnumerator DiscoveryCoroutine()
        {
            while (true)
            {
                if (scenesToScan.Count == 0)
                {
                    yield return null;
                    continue;
                }

                var scene = scenesToScan.Dequeue();
                if (!scene.IsValid() || !scene.isLoaded)
                    continue;

                if (trackGrid.Count == 0)
                {
                    TrackCatalog.RefreshIfStale();
                    var tracks = TrackCatalog.All;
                    for (var i = 0; i < tracks.Length; i++)
                    {
                        AddTrackToGrid(tracks[i]);
                        if ((i + 1) % TracksPerFrame == 0)
                            yield return null;
                    }
                }

                var pending = new Stack<Transform>();
                foreach (var root in scene.GetRootGameObjects())
                    pending.Push(root.transform);

                var visited = 0;
                while (pending.Count > 0)
                {
                    var transform = pending.Pop();
                    if (transform == null)
                        continue;
                    var data = transform.GetComponent<SignGeneratorData>();
                    if (data != null)
                        RegisterSigns(data);
                    for (var i = 0; i < transform.childCount; i++)
                        pending.Push(transform.GetChild(i));
                    if (++visited % SceneObjectsPerFrame == 0)
                        yield return null;
                }
            }
        }

        private static void RegisterSigns(SignGeneratorData data)
        {
            if (data == null || data.signParameters == null)
                return;

            var transform = data.transform;
            var position = transform.position;
            var facing = transform.forward;

            for (var i = 0; i < data.signParameters.Length; i++)
            {
                var parameters = data.signParameters[i];
                if (!IsSpeedLimit(parameters.type))
                    continue;
                if (!TryParseSpeed(parameters.signText, out var kph))
                    continue;

                var key = new SignKey(position, i);
                if (known.ContainsKey(key) || unplaceable.Contains(key))
                    continue;

                if (!NearestTrack(position, facing, NearbyTracks(position),
                    out var track, out var trackPosition))
                {
                    unplaceable.Add(key);
                    continue;
                }

                // Junction boards are generated as speed, arrow, speed, arrow
                // on one pole, so the arrow that qualifies a limit is the entry
                // after it.
                bool? arrowLeft = null;
                if (i + 1 < data.signParameters.Length)
                {
                    var nextType = data.signParameters[i + 1].type;
                    if (nextType == SignType.ArrowLeft)
                        arrowLeft = true;
                    else if (nextType == SignType.ArrowRight)
                        arrowLeft = false;
                }

                var sign = new Sign(position, facing, kph, track, trackPosition, arrowLeft);
                known[key] = sign;
                if (!signsByTrack.TryGetValue(track, out var onTrack))
                    signsByTrack[track] = onTrack = new List<Sign>();
                onTrack.Add(sign);
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
            if (signsByTrack.TryGetValue(bogie.track, out var signs))
            {
                for (var i = 0; i < signs.Count; i++)
                {
                    var sign = signs[i];
                    if (!FacesTrain(sign, flatHeading))
                        continue;

                    var crossed = movement > 0f
                        ? sign.trackPosition > state.trackPosition + CrossingToleranceMeters
                            && sign.trackPosition <= trainPosition + CrossingToleranceMeters
                        : sign.trackPosition < state.trackPosition - CrossingToleranceMeters
                            && sign.trackPosition >= trainPosition - CrossingToleranceMeters;
                    if (!crossed)
                        continue;
                    if (!AppliesToSelectedBranch(sign, movement > 0f))
                        continue;

                    // If more than one sign was crossed between UI updates, the
                    // last one in the direction of travel defines the new block.
                    if (!lastCrossed.HasValue
                        || (movement > 0f && sign.trackPosition > lastCrossed.Value.trackPosition)
                        || (movement < 0f && sign.trackPosition < lastCrossed.Value.trackPosition))
                        lastCrossed = sign;
                }
            }

            state.trackPosition = trainPosition;
            if (lastCrossed.HasValue)
                state.limit = lastCrossed.Value.kph;
            return state.limit;
        }

        private static int? InitialLimit(RailTrack track, float trainPosition, Vector3 heading)
        {
            if (!signsByTrack.TryGetValue(track, out var signs))
                return null;

            var increasing = TrackDirectionAt(track, trainPosition, heading);
            var nearestPassed = float.MaxValue;
            int? result = null;
            for (var i = 0; i < signs.Count; i++)
            {
                var sign = signs[i];
                if (!FacesTrain(sign, heading))
                    continue;
                if (!AppliesToSelectedBranch(sign, increasing))
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

        /// Arrowed limits on a stacked junction board apply only to the road
        /// selected by that arrow. DV creates the pair in out-branch order:
        /// ArrowLeft for branch 0 and ArrowRight for branch 1.
        private static bool AppliesToSelectedBranch(Sign sign, bool increasing)
        {
            if (!sign.arrowLeft.HasValue)
                return true;

            var junction = increasing ? sign.track.outJunction : sign.track.inJunction;
            if (junction == null || junction.inBranch == null
                || junction.inBranch.track != sign.track
                || junction.outBranches == null || junction.outBranches.Count < 2)
                return false;

            var indicatedBranch = sign.arrowLeft.Value ? 0 : 1;
            return junction.selectedBranch == indicatedBranch;
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
        private const float MinimumMovementMeters = 0.05f;
        private const float CrossingToleranceMeters = 0.1f;
        private const int CurveSamples = 32;

        // Work carried by a single frame during a pass.
        private const int TracksPerFrame = 96;
        private const int SceneObjectsPerFrame = 192;

        private static void AddTrackToGrid(RailTrack track)
        {
            var curve = track == null ? null : track.curve;
            if (curve == null || curve.pointCount < 2)
                return;
            var samples = Mathf.Max(1,
                Mathf.CeilToInt(TrackGraph.TrackLength(track!) / TrackGridSampleMeters));
            for (var i = 0; i <= samples; i++)
            {
                var cell = GridCell(curve.GetPointAt((float)i / samples));
                if (!trackGrid.TryGetValue(cell, out var bucket))
                    trackGrid[cell] = bucket = new List<RailTrack>();
                // Consecutive samples land in the same cell far more often than
                // not, so checking the last entry removes nearly all repeats
                // without a set per track.
                if (bucket.Count == 0 || bucket[bucket.Count - 1] != track)
                    bucket.Add(track!);
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
