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
        public static string GetStationJSON()
        {
            if (!WorldStreamingInit.Instance || !WorldStreamingInit.IsLoaded)
                throw new System.Exception("World not yet loaded");
            // Do not cache: modded tracks and streamed station content can be
            // registered after the first browser request.
            return JsonConvert.SerializeObject(GetStationData());
        }

        private static JArray GetStationData()
        {
            var stationControllers = (StationController.allStations ?? new List<StationController>())
                .Where(station => station != null && station.StationInfoValid)
                .ToList();

            var allTracks = Component.FindObjectsOfType<RailTrack>()
                .Where(track => track != null && track.LogicTrack() != null)
                .ToList();
            var tracksByYard = allTracks
                .GroupBy(track => YardIdOf(track.LogicTrack().ID.FullDisplayID))
                .Where(group => !string.IsNullOrEmpty(group.Key))
                .ToDictionary(group => group.Key, group => group.ToList());

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

        /// Yard centre: the mean of every sampled point on the station's tracks.
        /// Preferred over the station office transform, which sits off to one
        /// side of larger yards and would drag the label away from the track.
        private static World.Position? CenterOf(IEnumerable<RailTrack> tracks)
        {
            double x = 0, z = 0;
            var count = 0;
            foreach (var track in tracks)
            {
                if (track == null)
                    continue;
                foreach (var point in RailTracks.GetTrackPoints(track))
                {
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
