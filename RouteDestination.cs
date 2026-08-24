using System.Collections.Generic;
using System.Linq;

namespace DvMod.RemoteDispatch
{
    /// A place a road can be told to call at.
    ///
    /// Two kinds share one address space, so a route stop, a URL segment and a
    /// multiplayer packet field can each carry either without knowing which it
    /// holds: a named track ("GF-D5I"), or a junction picked off the map
    /// ("J-482"). Junctions are addressable because a train is often wanted at
    /// the throat of a yard rather than in one of its roads - the driver shunts
    /// the rest by hand from there - and no yard throat carries a track ID that
    /// could be selected instead.
    public static class RouteDestination
    {
        public const string JunctionPrefix = "J-";
        public const string RegionalPrefix = "REG-";

        public static bool IsRegional(string id) =>
            !string.IsNullOrEmpty(id) && id.StartsWith(RegionalPrefix);

        private static string RegionalStationId(string id) =>
            id.Substring(RegionalPrefix.Length);

        /// How many places one road may call at, and what separates them in the
        /// single string that carries a whole itinerary over HTTP and over the
        /// multiplayer link. Track IDs are letters, digits and hyphens, so the
        /// bar never appears inside one.
        /// A shunting job that collects cuts from several roads returns to the
        /// loading road between each, so its itinerary is longer than the
        /// number of places it visits. Twelve covers those and still leaves the
        /// joined form well inside the 256 characters a route request carries.
        public const int MaxStops = 12;
        public const char StopSeparator = '|';

        public static bool IsJunction(string id) => TryJunctionIndex(id, out _);

        public static bool TryJunctionIndex(string id, out int index)
        {
            index = -1;
            if (string.IsNullOrEmpty(id) || !id.StartsWith(JunctionPrefix))
                return false;
            return int.TryParse(id.Substring(JunctionPrefix.Length), out index) && index >= 0;
        }

        public static Junction? FindJunction(string id)
        {
            if (!TryJunctionIndex(id, out var index))
                return null;
            var junctions = RailTrackRegistry.Instance?.OrderedJunctions;
            return junctions != null && index < junctions.Length ? junctions[index] : null;
        }

        /// Every track a road may finish on to count as having arrived here.
        ///
        /// A named destination can be several RailTrack objects sharing one ID,
        /// and modded layouts commonly duplicate an ID across physical segments;
        /// every match is a goal, since picking one can choose an isolated
        /// duplicate and report no route despite the platform being connected.
        /// A junction is every rail that meets it, because arriving on any of
        /// them puts the train at the junction, which is what was asked for.
        public static HashSet<RailTrack> Goals(string id)
        {
            var goals = new HashSet<RailTrack>();
            if (IsRegional(id))
            {
                if (Stations.TryRegionalStation(RegionalStationId(id), out var tracks, out _))
                    foreach (var track in tracks)
                        goals.Add(track);
                return goals;
            }
            var junction = FindJunction(id);
            if (junction != null)
            {
                if (junction.inBranch != null && junction.inBranch.track != null)
                    goals.Add(junction.inBranch.track);
                var branches = junction.outBranches;
                if (branches != null)
                {
                    for (var i = 0; i < branches.Count; i++)
                    {
                        if (branches[i] != null && branches[i].track != null)
                            goals.Add(branches[i].track);
                    }
                }
                return goals;
            }
            foreach (var track in Routing.FindTracks(id))
                goals.Add(track);
            return goals;
        }

        /// Whether this destination exists in the loaded world.
        public static bool Exists(string id) => Goals(id).Count > 0;

        /// The first of these that is not in the loaded world, or null when all
        /// of them are.
        public static string? FirstMissing(IEnumerable<string> ids)
        {
            var wanted = new List<string>();
            foreach (var id in ids)
            {
                if (IsJunction(id))
                {
                    if (FindJunction(id) == null)
                        return id;
                    continue;
                }
                if (IsRegional(id))
                {
                    if (!Stations.TryRegionalStation(
                        RegionalStationId(id), out _, out _))
                        return id;
                    continue;
                }
                wanted.Add(id);
            }
            if (wanted.Count == 0)
                return null;

            return wanted.FirstOrDefault(id => TrackCatalog.WithId(id).Count == 0);
        }

        public static string Describe(string id) =>
            string.IsNullOrEmpty(id) ? "nowhere"
            : IsJunction(id) ? "junction " + id
            : IsRegional(id) ? "regional station " + RegionalStationId(id)
            : id;

        /// Split an itinerary carried as one string, dropping blanks and
        /// consecutive repeats: calling twice at the same place in a row is a
        /// leg of no length, which would arrive the moment it was planned.
        public static List<string> SplitStops(string joined)
        {
            var stops = new List<string>();
            if (string.IsNullOrEmpty(joined))
                return stops;
            foreach (var part in joined.Split(StopSeparator))
            {
                var stop = part.Trim();
                if (stop.Length == 0 || (stops.Count > 0 && stops[stops.Count - 1] == stop))
                    continue;
                stops.Add(stop);
                if (stops.Count == MaxStops)
                    break;
            }
            return stops;
        }

        public static string JoinStops(IEnumerable<string> stops) =>
            string.Join(StopSeparator.ToString(), stops.ToArray());
    }
}
