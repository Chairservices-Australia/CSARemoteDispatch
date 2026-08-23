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

        public static string GetStationJSON()
        {
            if (stationJSON != null)
                return stationJSON;
            if (!WorldStreamingInit.Instance || !WorldStreamingInit.IsLoaded)
                throw new System.Exception("World not yet loaded");
            stationJSON = JsonConvert.SerializeObject(GetStationData());
            return stationJSON;
        }

        private static JArray GetStationData()
        {
            var stations = StationController.allStations;
            if (stations == null)
                return new JArray();
            return new JArray(stations
                .Where(station => station != null && station.StationInfoValid)
                .Select(StationToJson)
                .Where(json => json != null));
        }

        private static JObject? StationToJson(StationController station)
        {
            var info = station.stationInfo;
            var center = CenterOf(station);
            if (center == null)
                return null;
            return new JObject(
                new JProperty("yardId", info.YardID),
                new JProperty("name", info.Name),
                new JProperty("type", info.Type),
                new JProperty("color", "#" + ColorUtility.ToHtmlStringRGB(info.StationColor)),
                new JProperty("position", center.Value.ToLatLon().ToJson()),
                new JProperty("tracks", new JArray(TrackIdsOf(station))));
        }

        /// Yard centre: the mean of every sampled point on the station's tracks.
        /// Preferred over the station office transform, which sits off to one
        /// side of larger yards and would drag the label away from the track.
        private static World.Position? CenterOf(StationController station)
        {
            var tracks = station.AllStationTracks;
            if (tracks == null || tracks.Count == 0)
                return null;

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

        /// Track IDs belonging to this station, for destination selection.
        private static IEnumerable<string> TrackIdsOf(StationController station)
        {
            var tracks = station.AllStationTracks;
            if (tracks == null)
                yield break;
            foreach (var track in tracks)
            {
                var logicTrack = track == null ? null : track.LogicTrack();
                if (logicTrack != null)
                    yield return logicTrack.ID.FullID;
            }
        }
    }
}
