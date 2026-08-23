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
        public float distanceMeters;
        public JArray divergenceDetail = new JArray();
        public List<RailTrack> pathTracks = new List<RailTrack>();
        public int progressIndex;   // path entries before this are behind the train
        public int releasedUpTo;    // junctions handed back, never re-taken
        public int rerouteCount;
        public float offRouteSince;
        public float wrongWaySince;
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
        /// Sampled with GetPointAt, which follows the curve including its
        /// handles. Stepping between anchor points instead was the fault behind
        /// every wrong turn: a plain bezier has only two anchors, so one step
        /// spans the whole track and the "forty metres out" sample landed at the
        /// far end, hundreds of metres away and around whatever curve lay
        /// between. Both the approach line and the branches were being measured
        /// there, which is why two legs of one turnout could read the same side.
        private static Vector3 PointAlong(RailTrack track, bool fromInEnd, float distance)
        {
            var curve = track == null ? null : track.curve;
            if (curve == null || curve.pointCount < 2)
                return track == null ? Vector3.zero : track.transform.position;

            var length = Mathf.Max(1f, TrackGraph.TrackLength(track!));
            // t runs from the in end to the out end, so approach from the far
            // end by walking t backwards.
            var fraction = Mathf.Clamp01(distance / length);
            var t = fromInEnd ? fraction : 1f - fraction;

            var point = curve.GetPointAt(t);
            return new Vector3(point.x, 0f, point.z);
        }

        /// Which side of the approach a branch lies on.
        ///
        /// Measured as lateral offset a little way along the branch, not as the
        /// direction it initially turns. On double track the two roads run
        /// parallel, so their turn directions differ by a fraction of a degree
        /// and the sign of that difference is noise; how far each sits to the
        /// left or right of the approach line is unambiguous.
        public static bool IsLeftBranch(
            Vector3 junctionPosition, Vector3 approachDirection, RailTrack branch, bool branchAtInEnd) =>
            LateralOffset(junctionPosition, approachDirection, branch, branchAtInEnd) > 0f;

        /// Signed distance a branch sits to the left of the approach line.
        /// Positive is left. Exposed so a wrong verdict can be told from a
        /// measurement that never had any signal in it.
        public static float LateralOffset(
            Vector3 junctionPosition, Vector3 approachDirection, RailTrack branch, bool branchAtInEnd)
        {
            var approach = new Vector3(approachDirection.x, 0, approachDirection.z);
            if (approach.sqrMagnitude < 0.0001f || branch == null)
                return 0f;
            approach = approach.normalized;

            // Unity is left-handed with +X to the right of +Z, so the left-hand
            // normal of a heading (x, z) is (-z, x).
            var leftNormal = new Vector3(-approach.z, 0f, approach.x);

            var sampled = PointAlong(branch, branchAtInEnd, BranchSampleMeters);
            var relative = sampled - new Vector3(junctionPosition.x, 0f, junctionPosition.z);
            return Vector3.Dot(relative, leftNormal);
        }

        /// How far along a branch to look when deciding which side it is on.
        /// Far enough that parallel roads have visibly separated, short enough
        /// that a curve further along does not reverse the answer.
        private const float BranchSampleMeters = 40f;

        /// The position of one end of a track.
        private static Vector3 CurveEnd(RailTrack track, bool atInEnd)
        {
            var curve = track == null ? null : track.curve;
            if (curve == null || curve.pointCount == 0)
                return track == null ? Vector3.zero : track.transform.position;
            var p = curve.GetPointAt(atInEnd ? 0f : 1f);
            return new Vector3(p.x, 0f, p.z);
        }

        /// Direction a train is travelling as it arrives at the junction, taken
        /// over the same distance the branches are sampled over.
        ///
        /// Measuring this from the chord between the track's bezier anchors was
        /// what broke left and right: on curving track those anchors sit a
        /// hundred metres or more apart, so the chord is nowhere near the tangent
        /// at the junction. A few degrees of error there is enough to push both
        /// branches onto the same side, which is exactly what was observed - two
        /// legs of one turnout both reading left, or both right.
        private static Vector3 ApproachAtJunction(TrackGraph.Step from)
        {
            var endIsIn = !from.enteredViaIn;
            var endPoint = CurveEnd(from.track, endIsIn);
            var backPoint = PointAlong(from.track, endIsIn, BranchSampleMeters);
            var direction = endPoint - backPoint;
            direction.y = 0;
            return direction;
        }

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

            // One logical destination can consist of multiple RailTrack objects
            // (and modded layouts commonly duplicate an ID across their physical
            // segments). Treat every matching component as a goal; selecting the
            // first object alone can choose an isolated duplicate and report no
            // route despite the platform being connected.
            var goals = new HashSet<RailTrack>(FindTracks(destinationTrackId));
            if (goals.Count == 0)
                goals.Add(destination);
            List<TrackGraph.Step>? path = null;
            var chosen = candidates[0];
            var shortestLength = float.MaxValue;

            // Every connected track is traversable, including tracks belonging
            // to intermediate stations and yards. The selected destination is
            // the only terminal condition. Choose the physically shortest road
            // from either end of the consist; junction side is diagnostic only
            // and must not force a train onto a longer route.
            foreach (var candidate in candidates)
            {
                var found = TrackGraph.FindPath(candidate, goals);
                if (found == null)
                    continue;
                var length = PathLength(found);
                if (path == null || length < shortestLength)
                {
                    path = found;
                    chosen = candidate;
                    shortestLength = length;
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
            route.distanceMeters = shortestLength;
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

        /// Reports whether a path takes the right-hand option. This no longer
        /// affects route cost: the quickest/shortest connected road wins.
        private static bool IsRightHandChoice(TrackGraph.Step from, TrackGraph.Step to)
        {
            if (!IsFacingChoice(from, out var junction))
                return false;

            var junctionPosition = junction!.position;
            var approach = ApproachAtJunction(from);
            var chosenOffset = LateralOffset(junctionPosition, approach, to.track, to.enteredViaIn);

            // "Left" is relative to the other available roads. At a skewed or
            // curved turnout every branch can lie to the right of the approach
            // centreline, but the road with the greatest signed offset is still
            // the left-hand road. Testing offset > 0 made those junctions appear
            // to have no left option and allowed the rightmost road to win.
            var leftmostOffset = LeftmostOffset(junction, junctionPosition, approach);
            return chosenOffset < leftmostOffset - BranchSideToleranceMeters;
        }

        private const float BranchSideToleranceMeters = 0.25f;

        private static float LeftmostOffset(Junction junction, Vector3 junctionPosition, Vector3 approach)
        {
            var leftmost = float.MinValue;
            foreach (var branch in junction.outBranches)
            {
                if (branch == null || branch.track == null)
                    continue;
                leftmost = Mathf.Max(leftmost,
                    LateralOffset(junctionPosition, approach, branch.track, branch.first));
            }
            return leftmost;
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
                var approach = ApproachAtJunction(from);
                var leftmostOffset = LeftmostOffset(junction, junctionPosition, approach);
                var tookLeft = !IsRightHandChoice(from, to);
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
                    var offset = LateralOffset(junctionPosition, approach, branch.track, branch.first);
                    options.Add(new JObject(
                        new JProperty("track", DescribeTrack(branch.track)),
                        new JProperty("side", offset >= leftmostOffset - BranchSideToleranceMeters
                            ? "left" : "right"),
                        new JProperty("offset", System.Math.Round(offset, 2)),
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
            foreach (var setting in PendingSettings(route))
            {
                if (setting.junction.selectedBranch == setting.branch)
                    continue;
                if (Occupancy.IsJunctionClear(setting.junction, route.trainsetId))
                    setting.junction.Switch(Junction.SwitchMode.NO_SOUND, setting.branch);
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
            foreach (var track in FindTracks(trackId))
                return track;
            return null;
        }

        public static IEnumerable<RailTrack> FindTracks(string trackId)
        {
            foreach (var track in Component.FindObjectsOfType<RailTrack>())
            {
                var logicTrack = track == null ? null : track.LogicTrack();
                if (logicTrack == null)
                    continue;
                if (logicTrack.ID.FullDisplayID == trackId || logicTrack.ID.FullID == trackId)
                    yield return track!;
            }
        }

        /// Re-assert the road ahead. A junction can be thrown by hand, or by
        /// another route that outranked this one, after the road was set; without
        /// this the train would run onto the wrong line with the route still
        /// reporting itself as set.
        private static void Revalidate(TrainRoute route)
        {
            foreach (var setting in PendingSettings(route))
            {
                if (setting.junction.selectedBranch == setting.branch)
                {
                    route.pending.Remove(setting.junction);
                    continue;
                }
                if (Occupancy.IsJunctionClear(setting.junction, route.trainsetId))
                {
                    setting.junction.Switch(Junction.SwitchMode.NO_SOUND, setting.branch);
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
            if (replacement.status != RouteStatus.Failed)
                replacement.message = "Rerouted (" + attempts + "). " + replacement.message;
        }

        /// The settings still to be applied, one per junction.
        ///
        /// A road that passes through the same junction twice - which propelling
        /// and run-round moves do - carries two settings for it, usually wanting
        /// opposite branches. Asserting both every tick throws the switch back
        /// and forth continuously. Only the earliest requirement still ahead of
        /// the train is held; the later one takes over once the train has passed
        /// the first.
        private static IEnumerable<JunctionSetting> PendingSettings(TrainRoute route)
        {
            var chosen = new Dictionary<Junction, JunctionSetting>();
            foreach (var setting in route.settings)
            {
                if (setting.junction == null || setting.pathIndex < route.progressIndex)
                    continue;
                if (chosen.TryGetValue(setting.junction, out var existing)
                    && existing.pathIndex <= setting.pathIndex)
                    continue;
                chosen[setting.junction] = setting;
            }
            return chosen.Values;
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
            if (route.pathTracks.Count < 2)
                return;

            var trainset = Trainset.allSets?.Find(set => set.id == route.trainsetId);
            if (trainset == null)
                return;

            // Motion is the best evidence of where a train is going; before it
            // moves, the reverser states the same intent.
            var lead = trainset.firstCar ?? trainset.cars?.FirstOrDefault(c => c != null);
            var velocity = lead?.rb != null ? lead.rb.velocity : Vector3.zero;
            velocity.y = 0;

            Vector3 heading;
            if (velocity.sqrMagnitude >= 0.25f)
                heading = velocity.normalized;
            else
            {
                heading = Signalling.ReverserHeading(trainset);
                if (heading.sqrMagnitude < 0.001f)
                    return;   // standing with the reverser centred: nothing to check
            }

            // Where the booked road goes from where the train currently is.
            var index = Mathf.Clamp(route.progressIndex, 0, route.pathTracks.Count - 2);
            var here = route.pathTracks[index];
            var next = route.pathTracks[index + 1];
            if (here == null || next == null)
                return;

            var alongRoute = CentreOf(next) - CentreOf(here);
            alongRoute.y = 0;
            if (alongRoute.sqrMagnitude < 0.01f)
                return;

            if (Vector3.Dot(alongRoute.normalized, heading) >= 0f)
            {
                route.wrongWaySince = 0f;
                return;
            }

            // Opposed. Checked every tick rather than once, so a train that
            // changes direction mid-journey has its road re-laid for the new one,
            // which is also what re-sets the junctions ahead to the left for it.
            // A short delay avoids re-laying on the momentary reversal of a
            // shunt or a rollback.
            if (route.wrongWaySince <= 0f)
            {
                route.wrongWaySince = Time.time;
                return;
            }
            if (Time.time - route.wrongWaySince < WrongWayGraceSeconds)
                return;

            route.wrongWaySince = 0f;
            Main.DebugLog(() => $"Route {route.id}: travelling opposite the booked road; re-laying.");
            Reroute(route);
        }

        /// How long a train must be heading against its road before it is
        /// re-laid, so a shunt or a rollback does not trigger one.
        public const float WrongWayGraceSeconds = 2f;

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
                            junction.Switch(Junction.SwitchMode.NO_SOUND, setting.branch);
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
            new JProperty("distanceMeters", System.Math.Round(route.distanceMeters, 1)),
            new JProperty("divergenceDetail", route.divergenceDetail),
            new JProperty("tracks", new JArray(route.trackIds.Skip(route.progressIndex))),
            new JProperty("passedTracks", route.progressIndex));

        public static string AllRoutesJson() =>
            new JArray(routes.Values.Select(ToJson)).ToString(Newtonsoft.Json.Formatting.None);
    }
}
