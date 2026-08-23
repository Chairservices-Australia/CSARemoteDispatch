using DV.PointSet;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using System.Collections;
using System.Threading.Tasks;
using System;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    public static class World
    {
        public readonly struct Position
        {
            public readonly float x;
            public readonly float z;

            public Position(float x, float z)
            {
                this.x = x;
                this.z = z;
            }

            public Position(Vector3 position) : this(position.x, position.z) { }
            public Position(Transform transform) : this(transform.position) { }

            public LatLon ToLatLon() => LatLon.From(this);
        }

        public readonly struct LatLon
        {
            private const int DECIMAL_PLACES = 8; // 1.11 mm
            private const float EARTH_CIRCUMFERENCE = 40e6f;
            private const float DEGREES_PER_METER = 360f / EARTH_CIRCUMFERENCE;

            public readonly float latitude;
            public readonly float longitude;

            public LatLon(float latitude, float longitude)
            {
                this.latitude = (float)Math.Round(latitude, DECIMAL_PLACES);
                this.longitude = (float)Math.Round(longitude, DECIMAL_PLACES);
            }

            public static LatLon From(Position p) => new LatLon(DEGREES_PER_METER * p.z, DEGREES_PER_METER * p.x);

            public JToken ToJson() => new JArray(latitude, longitude);
        }
    }

    public static class RailTracks
    {
        private const float SIMPLIFIED_RESOLUTION = 40f;

        private static IEnumerable<World.LatLon> NormalizeTrackPoints(IEnumerable<World.Position> positions) => positions.Select(p => p.ToLatLon());

        public static IEnumerable<World.Position> GetTrackPoints(RailTrack track, float resolution = SIMPLIFIED_RESOLUTION)
        {
            var pointSet = track.GetKinkedPointSet();
            EquiPointSet simplified = EquiPointSet.ResampleEquidistant(
                pointSet,
                Mathf.Min(resolution, (float)pointSet.span / 3));

            foreach (var pt in simplified.points)
                yield return new World.Position((float)pt.position.x, (float)pt.position.z);
        }

        private static string? trackPointJSON;
        private static int cachedTrackVersion = -1;

        /// Tracks resampled per frame while the map's geometry is built.
        private const int TracksPerFrame = 64;

        private static Task<string>? generation;

        /// The geometry the map is drawn from.
        ///
        /// Producing it resamples the curve of every track in the world, which
        /// takes far longer than one frame is worth: doing it inline froze the
        /// game for as long as it took, every time a page was opened. It is
        /// built a slice at a time instead, and kept until the set of tracks
        /// itself changes. The request waits; the game does not.
        ///
        /// Must be called from the game thread. Callers on an HTTP thread reach
        /// it through Updater.RunOnMainThread and await the task it returns.
        public static Task<string> GetTrackPointJSON()
        {
            if (!WorldStreamingInit.Instance || !WorldStreamingInit.IsLoaded)
                throw new Exception("World not yet loaded");
            TrackCatalog.RefreshIfStale();
            if (trackPointJSON != null && cachedTrackVersion == TrackCatalog.Version)
                return Task.FromResult(trackPointJSON);
            // Several pages opening at once share one build rather than each
            // starting another pass over every track in the world.
            if (generation != null)
                return generation;

            var completion = new TaskCompletionSource<string>();
            generation = completion.Task;
            if (!Updater.RunSliced(GenerateTrackPointCoroutine(completion)))
            {
                generation = null;
                completion.SetException(new Exception("The mod is shutting down."));
            }
            return completion.Task;
        }

        private static IEnumerator GenerateTrackPointCoroutine(
            TaskCompletionSource<string> completion)
        {
            var tracks = TrackCatalog.All;
            var version = TrackCatalog.Version;
            var points = new Dictionary<string, List<JToken>>();
            Exception? failure = null;

            for (var i = 0; i < tracks.Length; i++)
            {
                // No yield inside the try, so this stays a legal iterator; a
                // track that cannot be read must not leave the waiting request
                // hanging for ever.
                try
                {
                    var track = tracks[i];
                    var logicTrack = track == null ? null : track.LogicTrack();
                    if (logicTrack != null)
                    {
                        points[logicTrack.ID.ToString()] = NormalizeTrackPoints(
                            GetTrackPoints(track!)).Select(ll => ll.ToJson()).ToList();
                    }
                }
                catch (Exception e)
                {
                    failure = e;
                    break;
                }
                if ((i + 1) % TracksPerFrame == 0)
                    yield return null;
            }

            // A world reload part way through leaves this holding geometry for
            // tracks that no longer exist, and may already have started a
            // replacement. Answer the request that is waiting, but only publish
            // to the cache if what was built still describes the world.
            if (generation == completion.Task)
                generation = null;
            if (failure != null)
            {
                completion.SetException(failure);
                yield break;
            }
            var json = JsonConvert.SerializeObject(points);
            if (version == TrackCatalog.Version)
            {
                trackPointJSON = json;
                cachedTrackVersion = version;
            }
            completion.SetResult(json);
        }

        public static void ResetCache()
        {
            trackPointJSON = null;
            cachedTrackVersion = -1;
            generation = null;
            TrackCatalog.Invalidate();
            Junctions.ResetCache();
        }
    }

    public static class Junctions
    {
        private static string? junctionPointJSON;
        private static int cachedJunctionCount = -1;

        public static string GetJunctionPointJSON()
        {
            if (!WorldStreamingInit.Instance || !WorldStreamingInit.IsLoaded)
                throw new Exception("World not yet loaded");
            var junctions = RailTrackRegistry.Instance.OrderedJunctions;
            if (junctionPointJSON == null || cachedJunctionCount != junctions.Length)
            {
                junctionPointJSON = JsonConvert.SerializeObject(
                junctions.Select(j =>
                {
                    var moved = j.position - WorldMover.currentMove;
                    return new JObject(
                        new JProperty("position", new World.Position(moved.x, moved.z).ToLatLon().ToJson()),
                        new JProperty("branches", j.outBranches.Select(b => b.track.LogicTrack().ID.ToString()))
                    );
                }));
                cachedJunctionCount = junctions.Length;
            }
            return junctionPointJSON;
        }

        internal static void ResetCache()
        {
            junctionPointJSON = null;
            cachedJunctionCount = -1;
        }

        public static IEnumerable<byte> GetAllJunctionStates()
        {
            if (!WorldStreamingInit.Instance || !WorldStreamingInit.IsLoaded)
                throw new Exception("World not yet loaded");
            return RailTrackRegistry.Instance.OrderedJunctions.Select(j => j.selectedBranch);
        }

        public static string GetJunctionStateJSON()
        {
            return JsonConvert.SerializeObject(GetAllJunctionStates());
        }
    }
}
