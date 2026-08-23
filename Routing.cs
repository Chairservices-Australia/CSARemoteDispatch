using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    /// Which way a junction must lie for a route to pass through it.
    public class JunctionSetting
    {
        public Junction junction = null!;
        public byte branch;
        public bool facing;   // true when the route runs stem -> branch here
        public int pathIndex;  // position along the route, for releasing behind the train
    }

    public enum RouteStatus { Active, Pending, Conflict, Failed, Cleared }

    public class TrainRoute
    {
        public string id = "";
        public int trainsetId;
        public string destinationTrackId = "";
        public int priority;                       // higher wins a contested junction
        public RouteStatus status = RouteStatus.Active;
        public string message = "";
        public List<JunctionSetting> settings = new List<JunctionSetting>();
        public List<string> trackIds = new List<string>();
        public readonly HashSet<Junction> pending = new HashSet<Junction>();
        public bool requiresReverse;
        public int leftDivergences;
        public int rightDivergences;
        public JArray divergenceDetail = new JArray();
        public List<RailTrack> pathTracks = new List<RailTrack>();
        public int progressIndex;   // path entries before this are behind the train
        public int releasedUpTo;    // junctions handed back, never re-taken
        public int rerouteCount;
        public float offRouteSince;
        public bool directionVerified;
        public string notice = "";   // survives status updates
    }

    /// Route planning and safe junction setting.
    ///
    /// Junctions are never thrown under a car: anything not clear is deferred and
    /// retried until it is. Each route reserves the junctions it needs, so two
    /// routes cannot fight over the same switch; a train leaving a station
    /// outranks one approaching, so departures are not held by arrivals.
    public static class Routing
    {
        private static readonly Dictionary<string, TrainRoute> routes = new Dictionary<string, TrainRoute>();
        private static readonly Dictionary<Junction, string> reservations = new Dictionary<Junction, string>();
        private static int nextRouteId = 1;

        public const float RetryIntervalSeconds = 1f;

        /// Direction of a track at one of its ends, taken from the segment
        /// nearest that end.
        ///
        /// The chord between a track's two endpoints is useless here: a siding
        /// that curves away from a junction runs almost parallel to the main
        /// line overall, so the chord cannot tell left from right. Only the
        /// local tangent at the junction can.
        private static Vector3 TangentAtEnd(RailTrack track, bool atInEnd, bool leaving)
        {
            var curve = track == null ? null : track.curve;
            if (curve == null || curve.pointCount < 2)
                return Vector3.zero;

            Vector3 outer, inner;
            if (atInEnd)
            {
                outer = curve[0].position;
                inner = curve[1].position;
            }
            else
            {
                outer = curve[curve.pointCount - 1].position;
                inner = curve[curve.pointCount - 2].position;
            }

            // Leaving the end points away from the track; arriving at it points
            // along the direction of travel, which is into the end.
            var direction = leaving ? inner - outer : outer - inner;
            direction.y = 0;
            return direction;
        }

        /// A point on a branch a little way out from the junction it leaves.
        ///
        /// Walks the bezier curve rather than the sampled point set: curve points
        /// are transform positions, the same frame as Junction.position, while
        /// GetKinkedPointSet returns coordinates that are not shifted by the
        /// world mover. Subtracting one from the other leaves the mover offset in
        /// the result, which is far larger than any track geometry and drowns the
        /// answer entirely.
        private static Vector3 PointAlong(RailTrack track, bool fromInEnd, float distance)
        {
            var curve = track == null ? null : track.curve;
            if (curve == null || curve.pointCount == 0)
                return track == null ? Vector3.zero : track.transform.position;

            var startIndex = fromInEnd ? 0 : curve.pointCount - 1;
            var stepDir = fromInEnd ? 1 : -1;
            var origin = curve[startIndex].position;

            var travelled = 0f;
            var previous = origin;
            for (var i = startIndex + stepDir; i >= 0 && i < curve.pointCount; i += stepDir)
            {
                var here = curve[i].position;
                travelled += Vector3.Distance(previous, here);
                previous = here;
                if (travelled >= distance)
                    return new Vector3(here.x, 0f, here.z);
            }

            // Shorter than the sample distance: its far end is the best answer.
            var far = curve[fromInEnd ? curve.pointCount - 1 : 0].position;
            return new Vector3(far.x, 0f, far.z);
        }

        /// Which side of the approach a branch lies on.
        ///
        /// Measured as lateral offset a little way along the branch, not as the
        /// direction it initially turns. On double track the two roads run
        /// parallel, so their turn directions differ by a fraction of a degree
        /// and the sign of that difference is noise; how far each sits to the
        /// left or right of the approach line is unambiguous.
        public static bool IsLeftBranch(
            Vector3 junctionPosition, Vector3 approachDirection, RailTrack branch, bool branchAtInEnd)
        {
            var approach = new Vector3(approachDirection.x, 0, approachDirection.z);
            if (approach.sqrMagnitude < 0.0001f || branch == null)
                return false;
            approach = approach.normalized;

            // Unity is left-handed with +X to the right of +Z, so the left-hand
            // normal of a heading (x, z) is (-z, x).
            var leftNormal = new Vector3(-approach.z, 0f, approach.x);

            var sampled = PointAlong(branch, branchAtInEnd, BranchSampleMeters);
            var relative = sampled - new Vector3(junctionPosition.x, 0f, junctionPosition.z);
            return Vector3.Dot(relative, leftNormal) > 0f;
        }

        /// How far along a branch to look when deciding which side it is on.
        /// Far enough that parallel roads have visibly separated, short enough
        /// that a curve further along does not reverse the answer.
        private const float BranchSampleMeters = 40f;

        /// Derive the junction settings a path requires.
        public static List<JunctionSetting> SettingsForPath(List<TrackGraph.Step> path)
        {
            var settings = new List<JunctionSetting>();
            for (var i = 0; i + 1 < path.Count; i++)
            {
                var from = path[i];
                var to = path[i + 1];
                var junction = from.ExitJunction;
                if (junction == null || junction.outBranches == null)
                    continue;

                // Facing move: we arrive on the stem, so the switch decides which
                // branch we take. Trailing move: we arrive on a branch, and the
                // switch must still point at us or the train runs through it.
                var facing = junction.inBranch != null && junction.inBranch.track == from.track;
                var target = facing ? to.track : from.track;

                var index = -1;
                for (var b = 0; b < junction.outBranches.Count; b++)
                {
                    var branch = junction.outBranches[b];
                    if (branch != null && branch.track == target)
                    {
                        index = b;
                        break;
                    }
                }
                if (index < 0)
                    continue;
                settings.Add(new JunctionSetting
                {
                    junction = junction,
                    branch = (byte)index,
                    facing = facing,
                    pathIndex = i,
                });
            }
            return settings;
        }

        /// True when the train starts on a station track, which gives its route
        /// precedence over trains running towards that station.
        private static int PriorityFor(RailTrack startTrack)
        {
            if (startTrack == null)
                return 0;
            var stations = StationController.allStations;
            if (stations == null)
                return 0;
            foreach (var station in stations)
            {
                var tracks = station == null ? null : station.AllStationTracks;
                if (tracks != null && tracks.Contains(startTrack))
                    return 1;
            }
            return 0;
        }

        public static TrainRoute? GetRoute(string id) =>
            routes.TryGetValue(id, out var route) ? route : null;

        public static IEnumerable<TrainRoute> AllRoutes() => routes.Values;

        public static void ClearRoute(string id)
        {
            if (!routes.TryGetValue(id, out var route))
                return;
            foreach (var junction in reservations.Where(kvp => kvp.Value == id).Select(kvp => kvp.Key).ToList())
                reservations.Remove(junction);
            route.status = RouteStatus.Cleared;
            routes.Remove(id);
            Sessions.AddTag("routes");
        }

        public static void ClearAll()
        {
            routes.Clear();
            reservations.Clear();
            Sessions.AddTag("routes");
        }

        /// Plan and apply a route for a trainset to a destination track.
        public static TrainRoute SetRoute(Trainset trainset, RailTrack destination, string destinationTrackId)
        {
            var route = new TrainRoute
            {
                id = "r" + nextRouteId++,
                trainsetId = trainset.id,
                destinationTrackId = destinationTrackId,
            };

            var candidates = StartCandidates(trainset).ToList();
            if (candidates.Count == 0)
            {
                route.status = RouteStatus.Failed;
                route.message = "Could not determine which track the train is on.";
                routes[route.id] = route;
                return route;
            }

            // The consist occupies several tracks; a road that immediately runs
            // back through the train itself is not usable.
            var occupied = new HashSet<RailTrack>();
            foreach (var position in Occupancy.AllCarPositions())
            {
                var set = position.car.trainset;
                if (set != null && set.id == trainset.id)
                    occupied.Add(position.track);
            }

            var goals = new HashSet<RailTrack> { destination };
            List<TrackGraph.Step>? path = null;
            var chosen = candidates[0];

            // Candidates are ordered with the outward direction of each end
            // first, so a pull is tried before the equivalent propelling move.
            foreach (var candidate in candidates)
            {
                var found = TrackGraph.FindPath(candidate, goals, extraCost: RightHandPenalty);
                if (found == null)
                    continue;
                if (path == null || PathLength(found) < PathLength(path))
                {
                    path = found;
                    chosen = candidate;
                }
            }

            if (path == null)
            {
                var reach = candidates.Select(c => TrackGraph.CountReachable(c)).ToList();
                route.status = RouteStatus.Failed;
                route.message = "No route found to " + DescribeTrack(destination)
                    + " from " + DescribeTrack(candidates[0].track)
                    + " (reachable states: " + string.Join(", ", reach) + ").";
                routes[route.id] = route;
                return route;
            }

            route.priority = PriorityFor(chosen.track);
            // The road leaves from the end of the consist that this path starts
            // at; if that is not the end the locomotive faces, the train is
            // propelling rather than pulling.
            route.requiresReverse = !occupied.Contains(chosen.track) || IsPropelling(trainset, chosen);

            route.pathTracks = path.Select(step => step.track).ToList();
            // FullDisplayID, matching both the /track keys the map draws with and
            // the IDs the game prints on jobs.
            route.trackIds = path
                .Select(step => step.track.LogicTrack())
                .Where(track => track != null)
                .Select(track => track.ID.FullDisplayID)
                .ToList();
            route.settings = SettingsForPath(path);
            CountDivergences(path, route);

            var conflict = FirstConflict(route);
            if (conflict != null)
            {
                route.status = RouteStatus.Conflict;
                route.message = conflict;
                routes[route.id] = route;
                return route;
            }

            foreach (var setting in route.settings)
                reservations[setting.junction] = route.id;

            routes[route.id] = route;
            Apply(route);
            Sessions.AddTag("routes");
            return route;
        }

        /// Cost charged for taking a right-hand branch where a left one exists.
        ///
        /// Larger than any total path length the world can produce, so a road
        /// with fewer right-hand turns always beats one with more no matter how
        /// much longer it is. That gives "always left where possible" without
        /// forbidding the move outright: an outright ban makes the destination
        /// unreachable wherever a left branch dead-ends, and the search then
        /// abandons left-hand running for the whole journey rather than for the
        /// one junction that needed it.
        public const float RightHandCost = 1000000f;

        private static float RightHandPenalty(TrackGraph.Step from, TrackGraph.Step to)
        {
            if (!IsFacingChoice(from, out var junction))
                return 0f;

            var junctionPosition = junction!.position;
            var approach = TangentAtEnd(from.track, atInEnd: !from.enteredViaIn, leaving: false);
            if (IsLeftBranch(junctionPosition, approach, to.track, to.enteredViaIn))
                return 0f;

            // Only charged when there is a left-hand road to take instead.
            foreach (var branch in junction.outBranches)
            {
                if (branch == null || branch.track == null || branch.track == to.track)
                    continue;
                if (IsLeftBranch(junctionPosition, approach, branch.track, branch.first))
                    return RightHandCost;
            }
            return 0f;
        }

        /// True when the route arrives on the stem here and the switch actually
        /// chooses between branches. Trailing moves have no left or right.
        private static bool IsFacingChoice(TrackGraph.Step from, out Junction? junction)
        {
            junction = from.ExitJunction;
            if (junction == null || junction.inBranch == null)
                return false;
            if (junction.inBranch.track != from.track)
                return false;
            return junction.outBranches != null && junction.outBranches.Count >= 2;
        }

        /// Tally which way the route diverges at each facing junction, so
        /// left-hand running can be confirmed from the result rather than
        /// inferred from the map.
        private static void CountDivergences(List<TrackGraph.Step> path, TrainRoute route)
        {
            route.divergenceDetail = new JArray();
            for (var i = 0; i + 1 < path.Count; i++)
            {
                var from = path[i];
                var to = path[i + 1];
                if (!IsFacingChoice(from, out var junction))
                    continue;

                var junctionPosition = junction!.position;
                var approach = TangentAtEnd(from.track, atInEnd: !from.enteredViaIn, leaving: false);
                var tookLeft = IsLeftBranch(junctionPosition, approach, to.track, to.enteredViaIn);
                if (tookLeft)
                    route.leftDivergences++;
                else
                    route.rightDivergences++;

                // Record what the alternatives looked like, so a wrong turn can
                // be told apart from a turn that had no left-hand option.
                var options = new JArray();
                foreach (var branch in junction!.outBranches)
                {
                    if (branch == null || branch.track == null)
                        continue;
                    options.Add(new JObject(
                        new JProperty("track", DescribeTrack(branch.track)),
                        new JProperty("side", IsLeftBranch(junctionPosition, approach, branch.track, branch.first)
                            ? "left" : "right"),
                        new JProperty("taken", branch.track == to.track)));
                }

                route.divergenceDetail.Add(new JObject(
                    new JProperty("from", DescribeTrack(from.track)),
                    new JProperty("to", DescribeTrack(to.track)),
                    new JProperty("took", tookLeft ? "left" : "right"),
                    new JProperty("approach", Vec(approach)),
                    new JProperty("options", options)));
            }
        }

        private static JArray Vec(Vector3 v) => new JArray(
            System.Math.Round(v.x, 2), System.Math.Round(v.z, 2));

        /// A junction already promised to another live route, which needs it set
        /// a different way, cannot be resolved by waiting.
        private static string? FirstConflict(TrainRoute route)
        {
            foreach (var setting in route.settings)
            {
                if (!reservations.TryGetValue(setting.junction, out var ownerId) || ownerId == route.id)
                    continue;
                var owner = GetRoute(ownerId);
                if (owner == null)
                    continue;
                var ownerSetting = owner.settings.FirstOrDefault(s => s.junction == setting.junction);
                if (ownerSetting == null || ownerSetting.branch == setting.branch)
                    continue;
                if (route.priority > owner.priority)
                {
                    // A departure outranks the holder, so take the junction from it.
                    owner.status = RouteStatus.Conflict;
                    owner.message = "Junction released to higher-priority route " + route.id + ".";
                    continue;
                }
                return "Junction reserved by route " + ownerId + " for train " + owner.trainsetId + ".";
            }
            return null;
        }

        /// Set every junction that is currently clear; defer the rest.
        public static void Apply(TrainRoute route)
        {
            route.pending.Clear();
            foreach (var setting in route.settings)
            {
                if (setting.junction == null)
                    continue;
                if (setting.junction.selectedBranch == setting.branch)
                    continue;
                if (Occupancy.IsJunctionClear(setting.junction, route.trainsetId))
                    setting.junction.Switch(Junction.SwitchMode.REGULAR, setting.branch);
                else
                    route.pending.Add(setting.junction);
            }
            UpdateStatus(route);
        }

        private static void UpdateStatus(TrainRoute route)
        {
            // Anything already said about how the road was chosen is kept, since
            // this used to overwrite it and hide notices entirely.
            var notice = route.notice;

            route.status = route.pending.Count > 0 ? RouteStatus.Pending : RouteStatus.Active;
            var divergences = route.leftDivergences + route.rightDivergences > 0
                ? " (" + route.leftDivergences + " left, " + route.rightDivergences + " right)"
                : "";
            var prefix = notice + (route.requiresReverse ? "Route set, train propels (cars lead). " : "");
            route.message = route.pending.Count > 0
                ? prefix + "Waiting for " + route.pending.Count + " occupied junction(s) to clear."
                : notice + (route.requiresReverse
                    ? "Route set" + divergences + " - train propels, cars lead."
                    : "Route set" + divergences + ".");
        }

        /// Advance a route past track the train has already run over, so the
        /// highlighted road shows only what is left and junctions behind the
        /// train are handed back for other routes to use.
        ///
        /// Progress is the rearmost point of the consist still on the route, not
        /// the front, or track would be released out from under the train.
        private static void UpdateProgress(TrainRoute route)
        {
            if (route.pathTracks.Count == 0)
                return;

            var occupied = new HashSet<RailTrack>();
            foreach (var position in Occupancy.AllCarPositions())
            {
                var trainset = position.car.trainset;
                if (trainset != null && trainset.id == route.trainsetId)
                    occupied.Add(position.track);
            }
            if (occupied.Count == 0)
                return;

            // Scan the whole path, not just from the last known point: a train
            // that backs up along its road should light the track behind it
            // again rather than leaving it cleared.
            var rearmost = -1;
            for (var i = 0; i < route.pathTracks.Count; i++)
            {
                if (occupied.Contains(route.pathTracks[i]))
                {
                    rearmost = i;
                    break;
                }
            }
            if (rearmost < 0)
            {
                // Off its road. Give a shunt a moment to step back on before
                // recomputing, so a brief excursion does not trigger a reroute.
                if (route.offRouteSince <= 0f)
                    route.offRouteSince = Time.time;
                else if (Time.time - route.offRouteSince > OffRouteGraceSeconds)
                    Reroute(route);
                return;
            }
            route.offRouteSince = 0f;

            var moved = rearmost != route.progressIndex;
            route.progressIndex = rearmost;

            // Junctions are handed back only as the train clears them for good.
            // Releasing and re-taking them as it shuffles back and forth would
            // let another route claim one mid-manoeuvre.
            if (rearmost > route.releasedUpTo)
            {
                foreach (var setting in route.settings.Where(x => x.pathIndex < rearmost))
                {
                    if (reservations.TryGetValue(setting.junction, out var owner) && owner == route.id)
                        reservations.Remove(setting.junction);
                    route.pending.Remove(setting.junction);
                }
                route.releasedUpTo = rearmost;
            }

            if (route.progressIndex >= route.pathTracks.Count - 1)
            {
                route.message = "Arrived at " + route.destinationTrackId + ".";
                ClearRoute(route.id);
                return;
            }
            if (moved)
                Sessions.AddTag("routes");
        }

        /// How long a train may be off its road before the route is recomputed,
        /// and how many times that may happen before giving up. The delay keeps
        /// a shunt that briefly steps off the path from triggering a reroute.
        public const float OffRouteGraceSeconds = 3f;
        public const int MaxReroutes = 5;

        public static RailTrack? FindTrack(string trackId)
        {
            foreach (var track in Component.FindObjectsOfType<RailTrack>())
            {
                var logicTrack = track == null ? null : track.LogicTrack();
                if (logicTrack == null)
                    continue;
                if (logicTrack.ID.FullDisplayID == trackId || logicTrack.ID.FullID == trackId)
                    return track;
            }
            return null;
        }

        /// Re-assert the road ahead. A junction can be thrown by hand, or by
        /// another route that outranked this one, after the road was set; without
        /// this the train would run onto the wrong line with the route still
        /// reporting itself as set.
        private static void Revalidate(TrainRoute route)
        {
            foreach (var setting in route.settings)
            {
                if (setting.junction == null || setting.pathIndex < route.progressIndex)
                    continue;
                if (setting.junction.selectedBranch == setting.branch)
                {
                    route.pending.Remove(setting.junction);
                    continue;
                }
                if (Occupancy.IsJunctionClear(setting.junction, route.trainsetId))
                {
                    setting.junction.Switch(Junction.SwitchMode.REGULAR, setting.branch);
                    route.pending.Remove(setting.junction);
                }
                else
                {
                    route.pending.Add(setting.junction);
                }
            }
        }

        /// Recompute a road for a train that has left the one it was given.
        private static void Reroute(TrainRoute route)
        {
            if (route.rerouteCount >= MaxReroutes)
            {
                route.status = RouteStatus.Failed;
                route.message = "Train left its road and could not be rerouted.";
                return;
            }

            var trainset = Trainset.allSets?.Find(set => set.id == route.trainsetId);
            var destination = FindTrack(route.destinationTrackId);
            if (trainset == null || destination == null)
            {
                ClearRoute(route.id);
                return;
            }

            var destinationId = route.destinationTrackId;
            var attempts = route.rerouteCount + 1;
            ClearRoute(route.id);

            var replacement = SetRoute(trainset, destination, destinationId);
            replacement.rerouteCount = attempts;
            // The re-laid road was computed from real motion, so its direction is
            // already confirmed and must not trigger another verification pass.
            replacement.directionVerified = true;
            if (replacement.status != RouteStatus.Failed)
                replacement.message = "Rerouted (" + attempts + "). " + replacement.message;
        }

        /// Re-lay a road once the train is actually moving, if it set off the
        /// other way.
        ///
        /// The direction is chosen when the route is booked, from a train that is
        /// usually standing still, so it can only be a guess between the two ends
        /// of the consist. Once there is real motion to read, the guess is
        /// checked against it and the road re-laid from the direction the train
        /// is genuinely travelling, which is also what keeps the junctions ahead
        /// set to the left for that direction.
        private static void VerifyDirection(TrainRoute route)
        {
            if (route.directionVerified || route.pathTracks.Count < 2)
                return;

            var trainset = Trainset.allSets?.Find(set => set.id == route.trainsetId);
            var lead = trainset?.firstCar ?? trainset?.cars?.FirstOrDefault(c => c != null);
            if (lead == null || lead.rb == null)
                return;

            var velocity = lead.rb.velocity;
            velocity.y = 0;

            // Real motion is the best evidence; failing that the reverser is a
            // statement of intent, and is available before the train moves.
            Vector3 heading;
            if (velocity.sqrMagnitude >= 0.25f)
            {
                heading = velocity.normalized;
            }
            else
            {
                heading = Signalling.ReverserHeading(trainset);
                if (heading.sqrMagnitude < 0.001f)
                    return;   // nothing to judge by yet
            }

            // Where the road goes from the train's current position.
            var index = Mathf.Clamp(route.progressIndex, 0, route.pathTracks.Count - 2);
            var here = route.pathTracks[index];
            var next = route.pathTracks[index + 1];
            if (here == null || next == null)
                return;

            var alongRoute = CentreOf(next) - CentreOf(here);
            alongRoute.y = 0;
            if (alongRoute.sqrMagnitude < 0.01f)
                return;

            route.directionVerified = true;
            if (Vector3.Dot(alongRoute.normalized, heading) >= 0f)
                return;   // heading the way the road was laid

            Main.DebugLog(() => $"Route {route.id}: train set off opposite the booked road; re-laying.");
            Reroute(route);
        }

        private static Vector3 CentreOf(RailTrack track)
        {
            var curve = track.curve;
            if (curve == null || curve.pointCount == 0)
                return track.transform.position;
            return (curve[0].position + curve[curve.pointCount - 1].position) * 0.5f;
        }

        /// Retry deferred junctions. Driven by Updater so it shares the mod's
        /// existing coroutine host rather than starting another one.
        public static IEnumerator RetryPendingCoroutine()
        {
            var wait = new WaitForSeconds(RetryIntervalSeconds);
            while (true)
            {
                yield return wait;
                foreach (var route in routes.Values.ToList())
                {
                    VerifyDirection(route);
                    if (!routes.ContainsKey(route.id))
                        continue;   // re-laid during verification
                    UpdateProgress(route);
                    if (!routes.ContainsKey(route.id))
                        continue;   // arrived or rerouted during the update
                    Revalidate(route);
                    if (route.pending.Count == 0)
                    {
                        UpdateStatus(route);
                        continue;
                    }
                    var stillPending = new List<Junction>();
                    foreach (var junction in route.pending)
                    {
                        var setting = route.settings.FirstOrDefault(s => s.junction == junction);
                        if (setting == null)
                            continue;
                        if (Occupancy.IsJunctionClear(junction, route.trainsetId))
                            junction.Switch(Junction.SwitchMode.REGULAR, setting.branch);
                        else
                            stillPending.Add(junction);
                    }
                    var cleared = route.pending.Count != stillPending.Count;
                    route.pending.Clear();
                    foreach (var junction in stillPending)
                        route.pending.Add(junction);
                    UpdateStatus(route);
                    if (cleared)
                        Sessions.AddTag("routes");
                }
            }
        }

        /// Candidate starting points for a route: the outward direction from
        /// each end of the consist.
        ///
        /// Which end leads cannot be read from the locomotive. A loco propelling
        /// its cars faces backwards along the direction of travel, and may sit
        /// anywhere in the consist, so routing from the loco would set the road
        /// out from the wrong end. Both ends are offered and the search picks
        /// whichever actually reaches the destination.
        private static IEnumerable<TrackGraph.Step> StartCandidates(Trainset trainset)
        {
            var cars = trainset?.cars;
            if (cars == null || cars.Count == 0)
                yield break;

            var first = trainset!.firstCar;
            var last = trainset.lastCar;
            var seen = new HashSet<TrackGraph.Step>();

            // The reverser says which way the driver intends to go, so the end
            // it points at is offered first even on a standing train.
            var intent = Signalling.ReverserHeading(trainset);
            var ends = new[] { first, last };
            if (intent.sqrMagnitude > 0.001f && first != null && last != null && first != last)
            {
                var alongConsist = first.transform.position - last.transform.position;
                alongConsist.y = 0;
                if (Vector3.Dot(alongConsist, intent) < 0f)
                    ends = new[] { last, first };
            }

            foreach (var end in ends)
            {
                if (end == null)
                    continue;
                var bogie = end.Bogies?.FirstOrDefault(b => b != null && b.track != null);
                if (bogie == null)
                    continue;

                // Point away from the far end of the consist. With one car there
                // is no "away", so both directions are offered instead.
                var other = end == first ? last : first;
                var away = other == null || other == end
                    ? intent
                    : end.transform.position - other.transform.position;

                foreach (var step in StepsFrom(bogie.track, away))
                {
                    if (seen.Add(step))
                        yield return step;
                }
            }
        }

        /// Steps leaving a track, ordered so the one heading `away` comes first.
        /// A zero `away` yields both directions.
        private static IEnumerable<TrackGraph.Step> StepsFrom(RailTrack track, Vector3 away)
        {
            var forward = new TrackGraph.Step(track, true);
            var backward = new TrackGraph.Step(track, false);

            if (away.sqrMagnitude < 0.0001f)
            {
                yield return forward;
                yield return backward;
                yield break;
            }

            // Exit tangent of each direction, compared against the outward vector.
            var forwardTangent = TangentAtEnd(track, atInEnd: false, leaving: true);
            var flat = new Vector3(away.x, 0, away.z);
            var alignsForward = Vector3.Dot(
                new Vector3(forwardTangent.x, 0, forwardTangent.z), flat) >= 0f;

            if (alignsForward)
            {
                yield return forward;
                yield return backward;
            }
            else
            {
                yield return backward;
                yield return forward;
            }
        }

        private static float PathLength(List<TrackGraph.Step> path)
        {
            var total = 0f;
            foreach (var step in path)
                total += TrackGraph.TrackLength(step.track);
            return total;
        }

        /// True when the road leaves the consist in the direction the leading
        /// locomotive faces away from, meaning the train pushes rather than pulls.
        private static bool IsPropelling(Trainset trainset, TrackGraph.Step start)
        {
            var loco = trainset.cars?.FirstOrDefault(c => c != null && c.IsLoco);
            if (loco == null)
                return false;
            var departure = TangentAtEnd(start.track, atInEnd: !start.enteredViaIn, leaving: true);
            var facing = loco.transform.forward;
            return Vector3.Dot(
                new Vector3(departure.x, 0, departure.z),
                new Vector3(facing.x, 0, facing.z)) < 0f;
        }

        private static string DescribeTrack(RailTrack track)
        {
            var logicTrack = track == null ? null : track.LogicTrack();
            return logicTrack == null ? "an unnamed track" : logicTrack.ID.FullDisplayID;
        }

        public static JObject ToJson(TrainRoute route) => new JObject(
            new JProperty("id", route.id),
            new JProperty("trainsetId", route.trainsetId),
            new JProperty("destinationTrack", route.destinationTrackId),
            new JProperty("status", route.status.ToString()),
            new JProperty("message", route.message),
            new JProperty("priority", route.priority),
            new JProperty("pendingJunctions", route.pending.Count),
            new JProperty("requiresReverse", route.requiresReverse),
            new JProperty("leftDivergences", route.leftDivergences),
            new JProperty("rightDivergences", route.rightDivergences),
            new JProperty("divergenceDetail", route.divergenceDetail),
            new JProperty("tracks", new JArray(route.trackIds.Skip(route.progressIndex))),
            new JProperty("passedTracks", route.progressIndex));

        public static string AllRoutesJson() =>
            new JArray(routes.Values.Select(ToJson)).ToString(Newtonsoft.Json.Formatting.None);
    }
}
