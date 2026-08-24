using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    /// Station identity as the game itself defines it: display name, yard ID,
    /// and the station colour used on in-game signage and job overviews.
    public static class Stations
    {
        private static string? stationJSON;
        private static int cachedTrackVersion = -1;
        private static int cachedRegionalStationCount = -1;

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
            var regionalStations = PassengerRegionalStations();
            if (stationJSON == null || cachedTrackVersion != TrackCatalog.Version
                || cachedRegionalStationCount != regionalStations.Count)
            {
                stationJSON = JsonConvert.SerializeObject(GetStationData(regionalStations));
                cachedTrackVersion = TrackCatalog.Version;
                cachedRegionalStationCount = regionalStations.Count;
            }
            return stationJSON;
        }

        public static void ResetCache()
        {
            stationJSON = null;
            cachedTrackVersion = -1;
            cachedRegionalStationCount = -1;
        }

        private static JArray GetStationData(List<RegionalStation> regionalStations)
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

            // Passenger Jobs regional stations are platform-length sections of
            // otherwise unnamed main-line track. They deliberately have no
            // StationController and their station ID (for example "AM") is not
            // a real TrackID, so neither discovery path above can see them. The
            // passenger mod does expose the live platform controller and its
            // underlying warehouse track; turn those into ordinary routable
            // station entries without taking a compile-time dependency on the
            // optional mod.
            foreach (var station in regionalStations.Where(item => !represented.Contains(item.id)))
            {
                represented.Add(station.id);
                result.Add(new JObject(
                    new JProperty("yardId", station.id),
                    new JProperty("name", "Regional station"),
                    new JProperty("type", "Passenger"),
                    new JProperty("color", "#DCCCFF"),
                    new JProperty("position", station.center.ToLatLon().ToJson()),
                    new JProperty("tracks", new JArray(new JObject(
                        new JProperty("id", RouteDestination.RegionalPrefix + station.id),
                        new JProperty("display", station.id + "-LP"))))));
            }
            return result;
        }

        private sealed class RegionalStation
        {
            public string id = "";
            public World.Position center;
            public List<RailTrack> tracks = new List<RailTrack>();
        }

        public static bool TryRegionalStation(
            string stationId, out List<RailTrack> tracks, out World.Position center)
        {
            var station = PassengerRegionalStations()
                .FirstOrDefault(item => item.id == stationId);
            if (station != null)
            {
                tracks = station.tracks;
                center = station.center;
                return tracks.Count > 0;
            }
            tracks = new List<RailTrack>();
            center = default;
            return false;
        }

        /// Discover Passenger Jobs' live rural platforms through its public
        /// controller API. Reflection keeps Remote Dispatch loadable when that
        /// mod is absent or changes; failure simply leaves the vanilla station
        /// list untouched.
        private static List<RegionalStation> PassengerRegionalStations()
        {
            var result = new List<RegionalStation>();
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType(
                        "PassengerJobs.Platforms.PlatformController", false))
                    .FirstOrDefault(found => found != null);
                var controllers = type?.GetProperty("AllPlatformControllers",
                    BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null) as IEnumerable;
                if (controllers == null)
                    return result;

                foreach (var controller in controllers)
                {
                    var platform = MemberValue(controller, "Platform");
                    if (platform == null || platform.GetType().FullName
                        != "PassengerJobs.Platforms.RuralPlatformWrapper")
                        continue;
                    var id = MemberValue(platform, "Id") as string;
                    var warehouse = MemberValue(platform, "Warehouse");
                    var logicTrack = MemberValue(warehouse, "WarehouseTrack");
                    var trackIdObject = MemberValue(logicTrack, "ID");
                    var fullId = MemberValue(trackIdObject, "FullID") as string
                        ?? trackIdObject?.ToString();
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(fullId))
                        continue;

                    var tracks = TrackCatalog.WithId(fullId!);
                    // The underlying rail can be a very long unnamed main-line
                    // component, so its geometric centre may be kilometres from
                    // the platform. Passenger Jobs puts the controller on the
                    // generated platform itself; convert that shifted Unity
                    // position back to the absolute coordinates used by the map.
                    World.Position? center = null;
                    if (controller is Component component)
                    {
                        var absolute = component.transform.position - WorldMover.currentMove;
                        center = new World.Position(absolute.x, absolute.z);
                    }
                    if (center == null)
                        center = CenterOf(tracks);
                    if (center == null)
                        continue;
                    result.Add(new RegionalStation
                    {
                        id = id!,
                        center = center.Value,
                        tracks = tracks.ToList(),
                    });
                }
            }
            catch (Exception exception)
            {
                Main.DebugLog(() => "Passenger regional station discovery failed: "
                    + exception.Message);
            }
            return result;
        }

        private static object? MemberValue(object? instance, string name)
        {
            if (instance == null)
                return null;
            var type = instance.GetType();
            return type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(instance, null)
                ?? type.GetField(name, BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(instance);
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
        ///
        /// Taken from the resampled point set, which carries absolute world
        /// coordinates, and not from the curve's own points, which come off
        /// Transforms and therefore move with the floating origin. Everything
        /// this mod puts on the map is absolute - the track geometry from this
        /// same source, junctions and cars by subtracting WorldMover.currentMove
        /// - so reading the Transforms put every label out by however far the
        /// world had shifted, kilometres once the player had travelled.
        ///
        /// It also has to be absolute to be cached at all: a position worked out
        /// against the current shift would be wrong the moment the player moved
        /// away from where it was built.
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
