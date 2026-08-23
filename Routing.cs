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
        public List<RailTrack> pathTracks = new List<RailTrack>();
        public int progressIndex;   // path entries before this are behind the train
        public int releasedUpTo;    // junctions handed back, never re-taken
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

        /// Left-hand running: at a facing junction prefer the branch that departs
        /// to the left of the approach direction, matching Australian practice
        /// and the paired mainlines the DoubleTrack mod lays down.
        ///
        /// Unity is left-handed with +X to the right of +Z, so the Y component of
        /// approach x outward is negative for a turn to the left.
        public static bool IsLeftBranch(Vector3 approachDirection, Vector3 outwardDirection)
        {
            var approach = new Vector3(approachDirection.x, 0, approachDirection.z);
            var outward = new Vector3(outwardDirection.x, 0, outwardDirection.z);
            if (approach.sqrMagnitude < 0.0001f || outward.sqrMagnitude < 0.0001f)
                return false;
            return Vector3.Cross(approach.normalized, outward.normalized).y < 0f;
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

        /// Left-hand running is expressed as a cost penalty in the search: a
        /// right-hand branch costs extra, so an equal-length left road always
        /// wins, while a much shorter right-hand route is still available.
        /// Tunable from the mod settings.
        public static float LeftTurnPenalty => Main.settings.leftHandBias;

        /// Penalty applied when a transition diverges to the right at a facing
        /// junction. Trailing moves are not penalised: the switch does not choose
        /// the direction there, so there is no left or right to prefer.
        private static float RightHandPenalty(TrackGraph.Step from, TrackGraph.Step to)
        {
            var junction = from.ExitJunction;
            if (junction == null || junction.inBranch == null)
                return 0f;
            var facing = junction.inBranch.track == from.track;
            if (!facing || junction.outBranches == null || junction.outBranches.Count < 2)
                return 0f;

            // Arriving at the end of `from` that meets the junction, and leaving
            // along `to` from the end that meets the same junction.
            var approach = TangentAtEnd(from.track, atInEnd: !from.enteredViaIn, leaving: false);
            var outward = TangentAtEnd(to.track, atInEnd: to.enteredViaIn, leaving: true);
            return IsLeftBranch(approach, outward) ? 0f : LeftTurnPenalty;
        }

        /// Tally which way the route diverges at each facing junction, so
        /// left-hand running can be confirmed from the result rather than
        /// inferred from the map.
        private static void CountDivergences(List<TrackGraph.Step> path, TrainRoute route)
        {
            for (var i = 0; i + 1 < path.Count; i++)
            {
                var from = path[i];
                var to = path[i + 1];
                var junction = from.ExitJunction;
                if (junction == null || junction.inBranch == null)
                    continue;
                if (junction.inBranch.track != from.track)
                    continue;   // trailing move: no choice was made here
                if (junction.outBranches == null || junction.outBranches.Count < 2)
                    continue;

                var approach = TangentAtEnd(from.track, atInEnd: !from.enteredViaIn, leaving: false);
                var outward = TangentAtEnd(to.track, atInEnd: to.enteredViaIn, leaving: true);
                if (IsLeftBranch(approach, outward))
                    route.leftDivergences++;
                else
                    route.rightDivergences++;
            }
        }

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
            route.status = route.pending.Count > 0 ? RouteStatus.Pending : RouteStatus.Active;
            var divergences = route.leftDivergences + route.rightDivergences > 0
                ? " (" + route.leftDivergences + " left, " + route.rightDivergences + " right)"
                : "";
            var prefix = route.requiresReverse ? "Route set, train propels (cars lead). " : "";
            route.message = route.pending.Count > 0
                ? prefix + "Waiting for " + route.pending.Count + " occupied junction(s) to clear."
                : (route.requiresReverse
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
                return;   // off its road entirely; leave the route as it stands

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
                    UpdateProgress(route);
                    if (route.pending.Count == 0)
                        continue;
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

            foreach (var end in new[] { first, last })
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
                    ? Vector3.zero
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
            new JProperty("tracks", new JArray(route.trackIds.Skip(route.progressIndex))),
            new JProperty("passedTracks", route.progressIndex));

        public static string AllRoutesJson() =>
            new JArray(routes.Values.Select(ToJson)).ToString(Newtonsoft.Json.Formatting.None);
    }
}
