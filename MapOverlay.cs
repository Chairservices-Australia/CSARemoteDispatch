using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    /// A picture drawn under the railway on the map.
    ///
    /// Derail Valley is not a real place, so there is no aerial photography of
    /// it, and the game's own map is a schematic rather than a picture of the
    /// ground. The terrain that would let one be drawn cannot be read: the
    /// distant meshes are not marked readable, and the detailed terrain exists
    /// only where the player has been. What can be done is to show a picture
    /// somebody has already made - a community map, a render, the game's own
    /// map sheet - lined up with the world by its corners.
    ///
    /// The corners are in the same absolute world metres the track geometry
    /// uses, so a picture aligned to the rails stays aligned however far the
    /// player travels.
    public static class MapOverlay
    {
        /// A picture is held in memory to be served repeatedly, so there has to
        /// be a limit on what will be taken on.
        private const long MaxBytes = 64L * 1024 * 1024;

        private static byte[]? cached;
        private static string cachedPath = "";
        private static long cachedStamp;
        private static string cachedType = ContentType.Png;

        public static class ContentType
        {
            public const string Png = "image/png";
            public const string Jpeg = "image/jpeg";
            public const string Webp = "image/webp";
        }

        /// Where the picture sits and how it should be drawn, for the page.
        /// Also reports where the railway actually is, so the corners can be
        /// lined up against something rather than guessed at.
        public static string InfoJson()
        {
            var settings = Main.settings;
            var path = settings.mapImagePath ?? "";
            var present = path.Length > 0 && File.Exists(path);

            var info = new JObject
            {
                ["enabled"] = settings.showMapImage && present,
                ["configured"] = path.Length > 0,
                ["present"] = present,
                ["opacity"] = Math.Round(Mathf.Clamp01(settings.mapImageOpacity), 2),
                ["bounds"] = Corners(
                    settings.mapImageMinX, settings.mapImageMinZ,
                    settings.mapImageMaxX, settings.mapImageMaxZ),
            };
            if (path.Length > 0 && !present)
                info["error"] = "No file at \"" + path + "\".";

            var rails = RailBounds();
            if (rails != null)
                info["railBounds"] = rails;
            return info.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// The two corners Leaflet wants, south-west then north-east, in the
        /// same latitude and longitude the track geometry is drawn in.
        private static JArray Corners(float minX, float minZ, float maxX, float maxZ) =>
            new JArray(
                new World.Position(Mathf.Min(minX, maxX), Mathf.Min(minZ, maxZ)).ToLatLon().ToJson(),
                new World.Position(Mathf.Max(minX, maxX), Mathf.Max(minZ, maxZ)).ToLatLon().ToJson());

        /// The box the rails occupy, for lining a picture up against. Taken from
        /// the same catalogue the map is drawn from, so it needs no scan of its
        /// own.
        private static JObject? RailBounds()
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var track in TrackCatalog.All)
            {
                var curve = track == null ? null : track.curve;
                if (curve == null)
                    continue;
                for (var i = 0; i < curve.pointCount; i++)
                {
                    var point = curve[i].position;
                    minX = Mathf.Min(minX, point.x);
                    maxX = Mathf.Max(maxX, point.x);
                    minZ = Mathf.Min(minZ, point.z);
                    maxZ = Mathf.Max(maxZ, point.z);
                }
            }
            if (minX > maxX)
                return null;

            // Curve points come off Transforms, which move with the floating
            // origin; the map is drawn in absolute metres. See Stations.
            var shift = WorldMover.currentMove;
            return new JObject
            {
                ["minX"] = Math.Round(minX - shift.x, 1),
                ["maxX"] = Math.Round(maxX - shift.x, 1),
                ["minZ"] = Math.Round(minZ - shift.z, 1),
                ["maxZ"] = Math.Round(maxZ - shift.z, 1),
            };
        }

        /// The picture itself. Only ever the one file named in the settings, so
        /// a request cannot ask for anything else on the disk.
        public static bool TryRead(out byte[] bytes, out string contentType)
        {
            bytes = new byte[0];
            contentType = ContentType.Png;

            var path = Main.settings.mapImagePath ?? "";
            if (path.Length == 0 || !File.Exists(path))
                return false;

            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxBytes)
                {
                    Main.mod?.Logger.Warning(
                        $"Map picture \"{path}\" is {info.Length / (1024 * 1024)} MB; "
                        + $"the limit is {MaxBytes / (1024 * 1024)} MB.");
                    return false;
                }

                var stamp = info.LastWriteTimeUtc.Ticks;
                if (cached == null || cachedPath != path || cachedStamp != stamp)
                {
                    cached = File.ReadAllBytes(path);
                    cachedPath = path;
                    cachedStamp = stamp;
                    cachedType = TypeFor(Path.GetExtension(path));
                }
                bytes = cached;
                contentType = cachedType;
                return true;
            }
            catch (Exception e)
            {
                Main.mod?.Logger.Warning($"Could not read map picture \"{path}\": {e.Message}");
                return false;
            }
        }

        public static void ResetCache()
        {
            cached = null;
            cachedPath = "";
            cachedStamp = 0;
        }

        private static string TypeFor(string extension)
        {
            switch ((extension ?? "").ToLowerInvariant())
            {
            case ".jpg":
            case ".jpeg":
                return ContentType.Jpeg;
            case ".webp":
                return ContentType.Webp;
            default:
                return ContentType.Png;
            }
        }
    }
}
