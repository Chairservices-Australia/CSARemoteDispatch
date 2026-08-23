using System.Collections.Generic;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    /// The world's rail tracks, and how to find one by ID.
    ///
    /// Every lookup used to be a Component.FindObjectsOfType&lt;RailTrack&gt;(),
    /// which walks the whole scene and is one of the most expensive calls Unity
    /// offers. Routing, the station list, the map's track geometry and sign
    /// discovery all did it, several of them on every browser request, and a
    /// route request that named eight stops did it nine times over. The result
    /// was a hitch whenever a page loaded or a road was laid.
    ///
    /// The rail network does not change while the world is loaded, so the scan
    /// happens once and the answer is kept. What can change is what has streamed
    /// in - a mod may register track after the first scan - so a lookup that
    /// misses rescans, no more often than RescanSeconds, and everything that
    /// caches a derived answer hangs it off Version.
    public static class TrackCatalog
    {
        /// The least time between scans forced by a lookup that found nothing.
        /// Without it, one request for a track that genuinely does not exist
        /// would put a world scan behind every lookup that followed.
        private const float RescanSeconds = 10f;

        private static readonly RailTrack[] NoTracks = new RailTrack[0];
        private static readonly List<RailTrack> NoMatches = new List<RailTrack>();

        private static RailTrack[] tracks = NoTracks;
        private static readonly Dictionary<string, List<RailTrack>> tracksById =
            new Dictionary<string, List<RailTrack>>();
        private static bool built;
        private static float lastScanTime = float.NegativeInfinity;

        /// Bumped whenever the scan produced a different set of tracks. Anything
        /// derived from the network - the map's geometry, the station list -
        /// caches against this rather than rebuilding on a timer.
        public static int Version { get; private set; }

        /// Every track in the world.
        public static RailTrack[] All
        {
            get
            {
                EnsureBuilt();
                return tracks;
            }
        }

        /// Tracks carrying an ID, in either the canonical "GF-D-05-I" form or
        /// the shorter "GF-D5I" the game prints on jobs and signage. One ID can
        /// name several objects: modded layouts duplicate an ID across the
        /// physical segments of one road.
        public static IReadOnlyList<RailTrack> WithId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return NoMatches;
            EnsureBuilt();
            if (tracksById.TryGetValue(id, out var found))
                return found;

            // A miss can mean track has streamed in since the scan. Rescanning
            // is worth one attempt, but not once per lookup.
            if (Time.time - lastScanTime < RescanSeconds)
                return NoMatches;
            Rebuild();
            return tracksById.TryGetValue(id, out found) ? found : NoMatches;
        }

        /// Pick up track registered since the last scan, if it has been long
        /// enough to be worth looking. For callers that produce a whole view of
        /// the network and would otherwise never notice a modded yard.
        public static void RefreshIfStale()
        {
            if (!built || Time.time - lastScanTime >= RescanSeconds)
                Rebuild();
        }

        /// Drop everything, so the next use scans afresh. Called when the world
        /// is unloaded, where the tracks held here are about to be destroyed.
        public static void Invalidate()
        {
            tracks = NoTracks;
            tracksById.Clear();
            built = false;
            lastScanTime = float.NegativeInfinity;
            Version++;
        }

        private static void EnsureBuilt()
        {
            if (!built)
                Rebuild();
        }

        private static void Rebuild()
        {
            built = true;
            lastScanTime = Time.time;

            var found = Component.FindObjectsOfType<RailTrack>();
            // The count is what says the network changed. Comparing the arrays
            // themselves would be at the mercy of the order Unity happens to
            // return them in, and a version that moved for no reason would throw
            // away the very caches this exists to keep - the map's geometry and
            // the station list are both rebuilt from scratch when it moves.
            var changed = found.Length != tracks.Length;

            tracks = found;
            tracksById.Clear();
            foreach (var track in found)
            {
                var logicTrack = track == null ? null : track.LogicTrack();
                if (logicTrack == null)
                    continue;
                Index(logicTrack.ID.FullID, track!);
                Index(logicTrack.ID.FullDisplayID, track!);
            }
            if (changed)
                Version++;
            Main.DebugLog(() => $"Track catalogue rebuilt: {found.Length} tracks, "
                + $"{tracksById.Count} IDs, version {Version}.");
        }

        private static void Index(string id, RailTrack track)
        {
            if (string.IsNullOrEmpty(id))
                return;
            if (!tracksById.TryGetValue(id, out var list))
                tracksById[id] = list = new List<RailTrack>(1);
            // FullID and FullDisplayID coincide for some tracks, which would
            // otherwise enter the same object twice under the same key.
            if (!list.Contains(track))
                list.Add(track);
        }
    }
}
