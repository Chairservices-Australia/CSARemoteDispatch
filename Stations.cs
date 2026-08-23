using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    /// Station identity as the game itself defines it: display name, yard ID,
    /// and the station colour used on in-game signage and job overviews.
    public static class Stations
    {
        private static string? stationJSON;
        private static int cachedTrackVersion = -1;

        /// Building this walks every station's tracks and resamples the geometry
        /// of each one to find the yard centre, which is far too much to repeat
        /// per browser request - a page reload or a second client used to pay
        /// for it again. It is kept until the set of tracks itself changes, so
        /// modded and streamed-in yards still appear.
        public static string GetStationJSON()
        {
            if (!WorldStreamingInit.Instance || !WorldStreamingInit.IsLoaded)
                throw new System.Exception("World not yet loaded");
            TrackCatalog.RefreshIfStale();
            if (stationJSON == null || cachedTrackVersion != TrackCatalog.Version)
            {
                stationJSON = JsonConvert.SerializeObject(GetStationData());
                cachedTrackVersion = TrackCatalog.Version;
            }
            return stationJSON;
        }

        public static void ResetCache()
        {
            stationJSON = null;
            cachedTrackVersion = -1;
        }

        private static JArray GetStationData()
        {
            var stationControllers = (StationController.allStations ?? new List<StationController>())
                .Where(station => station != null && station.StationInfoValid)
                .ToList();

            // One LogicTrack() call per track, not three: it is a lookup through
            // the game's own registry and this runs over every track in the
            // world.
            var tracksByYard = new Dictionary<string, List<RailTrack>>();
            foreach (var track in TrackCatalog.All)
            {
                var logicTrack = track == null ? null : track.LogicTrack();
                if (logicTrack == null)
                    continue;
                var yardId = YardIdOf(logicTrack.ID.FullDisplayID);
                if (string.IsNullOrEmpty(yardId))
                    continue;
                if (!tracksByYard.TryGetValue(yardId, out var group))
                    tracksByYard[yardId] = group = new List<RailTrack>();
                group.Add(track!);
            }

            var result = new JArray();
            var represented = new HashSet<string>();
            foreach (var station in stationControllers)
            {
                var yardId = station.stationInfo.YardID;
                represented.Add(yardId);
                tracksByYard.TryGetValue(yardId, out var discovered);
                var json = StationToJson(station, discovered ?? new List<RailTrack>());
                if (json != null)
                    result.Add(json);
            }

            // A mod can add a yard and named tracks without adding a valid
            // StationController. Give it a synthetic station entry so every
            // routable named platform/track still appears in the router.
            foreach (var group in tracksByYard.Where(pair => !represented.Contains(pair.Key)))
            {
                var center = CenterOf(group.Value);
                if (center == null)
                    continue;
                result.Add(new JObject(
                    new JProperty("yardId", group.Key),
                    new JProperty("name", group.Key + " tracks"),
                    new JProperty("type", "Modded/other"),
                    new JProperty("color", "#888888"),
                    new JProperty("position", center.Value.ToLatLon().ToJson()),
                    new JProperty("tracks", new JArray(TrackIdsOf(group.Value)))));
            }
            return result;
        }

        private static JObject? StationToJson(StationController station, IEnumerable<RailTrack> discovered)
        {
            var info = station.stationInfo;
            var tracks = (station.AllStationTracks ?? new List<RailTrack>())
                .Concat(discovered)
                .Where(track => track != null)
                .Distinct()
                .ToList();
            var center = CenterOf(tracks);
            if (center == null)
                return null;
            return new JObject(
                new JProperty("yardId", info.YardID),
                new JProperty("name", info.Name),
                new JProperty("type", info.Type),
                new JProperty("color", "#" + ColorUtility.ToHtmlStringRGB(info.StationColor)),
                new JProperty("position", center.Value.ToLatLon().ToJson()),
                new JProperty("tracks", new JArray(TrackIdsOf(tracks))));
        }

        /// Yard centre: the mean of the points defining the station's tracks.
        /// Preferred over the station office transform, which sits off to one
        /// side of larger yards and would drag the label away from the track.
        ///
        /// Read straight off each curve rather than from a resampled point set.
        /// Resampling every track of every yard is by far the most expensive
        /// part of building this list, and it buys nothing here: what comes out
        /// is one rough centre for a label, which the curve's own points give
        /// to well within the size of the yard.
        private static World.Position? CenterOf(IEnumerable<RailTrack> tracks)
        {
            double x = 0, z = 0;
            var count = 0;
            foreach (var track in tracks)
            {
                var curve = track == null ? null : track.curve;
                if (curve == null)
                    continue;
                for (var i = 0; i < curve.pointCount; i++)
                {
                    var point = curve[i].position;
                    x += point.x;
                    z += point.z;
                    count++;
                }
            }
            if (count == 0)
                return null;
            return new World.Position((float)(x / count), (float)(z / count));
        }

        /// Tracks belonging to this station, for destination selection.
        /// Carries both IDs: FullID is canonical and used for lookup, while
        /// FullDisplayID is the shorter form the game shows on jobs and signage
        /// ("GF-D5I" rather than "GF-D-05-I"), so the UI can match what the
        /// player sees.
        private static IEnumerable<JObject> TrackIdsOf(IEnumerable<RailTrack> tracks)
        {
            foreach (var track in tracks
                .GroupBy(track => track.LogicTrack().ID.FullID)
                .Select(group => group.First()))
            {
                var logicTrack = track == null ? null : track.LogicTrack();
                if (logicTrack == null)
                    continue;
                yield return new JObject(
                    new JProperty("id", logicTrack.ID.FullID),
                    new JProperty("display", logicTrack.ID.FullDisplayID));
            }
        }

        private static string YardIdOf(string displayId)
        {
            if (string.IsNullOrEmpty(displayId) || displayId[0] == '#')
                return "";
            var separator = displayId.IndexOf('-');
            return separator > 0 ? displayId.Substring(0, separator) : "OTHER";
        }
    }
}
