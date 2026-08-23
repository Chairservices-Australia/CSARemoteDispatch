using DV.PointSet;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
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

        public static Dictionary<RailTrack, IEnumerable<World.LatLon>> GetNormalizedTrackCoordinates() =>
            GetAllTrackPoints().ToDictionary(kvp => kvp.Key, kvp => NormalizeTrackPoints(kvp.Value));

        public static Dictionary<RailTrack, IEnumerable<World.Position>> GetAllTrackPoints(float resolution = SIMPLIFIED_RESOLUTION)
        {
            if (!WorldStreamingInit.Instance || !WorldStreamingInit.IsLoaded)
                throw new Exception("World not yet loaded");
            var tracks = Component.FindObjectsOfType<RailTrack>();
            Main.DebugLog(() => $"Found {tracks.Length} RailTracks.");
            return tracks.ToDictionary(track => track, track => GetTrackPoints(track, resolution));
        }

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
        private static int cachedTrackCount = -1;

        private static string GenerateTrackPointJSON(RailTrack[] tracks)
        {
            return JsonConvert.SerializeObject(
                tracks.ToDictionary(
                    track => track.LogicTrack().ID,
                    track => NormalizeTrackPoints(GetTrackPoints(track)).Select(ll => ll.ToJson())));
        }

        public static Task<string> GetTrackPointJSON()
        {
            if (!WorldStreamingInit.Instance || !WorldStreamingInit.IsLoaded)
                throw new Exception("World not yet loaded");
            var tracks = Component.FindObjectsOfType<RailTrack>();
            if (trackPointJSON == null || cachedTrackCount != tracks.Length)
            {
                trackPointJSON = GenerateTrackPointJSON(tracks);
                cachedTrackCount = tracks.Length;
            }
            return Task.FromResult(trackPointJSON);
        }

        public static void ResetCache()
        {
            trackPointJSON = null;
            cachedTrackCount = -1;
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
