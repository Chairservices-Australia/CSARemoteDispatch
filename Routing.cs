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

        /// Left-hand running: at a facing junction prefer the branch that departs
        /// to the left of the approach direction, matching Australian practice and
        /// the paired mainlines the DoubleTrack mod lays down.
        public static bool IsLeftBranch(Junction junction, Vector3 approachDirection, RailTrack branchTrack)
        {
            if (branchTrack == null || branchTrack.curve == null || branchTrack.curve.pointCount == 0)
                return false;
            var junctionPosition = junction.position;
            var start = branchTrack.curve[0].position;
            var end = branchTrack.curve.Last().position;
            // Head away from the junction along this branch.
            var outward = Vector3.Distance(start, junctionPosition) <= Vector3.Distance(end, junctionPosition)
                ? end - start
                : start - end;
            outward.y = 0;
            var approach = new Vector3(approachDirection.x, 0, approachDirection.z);
            if (outward.sqrMagnitude < 0.001f || approach.sqrMagnitude < 0.001f)
                return false;
            // The Y sign of the cross product gives the turn direction in Unity's
            // left-handed coordinate system: negative is a turn to the left.
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

            var start = StartStepFor(trainset);
            if (start == null)
            {
                route.status = RouteStatus.Failed;
                route.message = "Could not determine which track the train is on.";
                routes[route.id] = route;
                return route;
            }

            route.priority = PriorityFor(start.Value.track);

            // Search the way the train faces first. If nothing is reachable that
            // way the train can still reverse, so try the opposite direction
            // rather than reporting no route at all.
            var goals = new HashSet<RailTrack> { destination };
            var path = TrackGraph.FindPath(start.Value, goals, extraCost: RightHandPenalty);
            var reversed = false;
            if (path == null)
            {
                var behind = new TrackGraph.Step(start.Value.track, !start.Value.enteredViaIn);
                path = TrackGraph.FindPath(behind, goals, extraCost: RightHandPenalty);
                reversed = path != null;
            }

            if (path == null)
            {
                route.status = RouteStatus.Failed;
                var forwardReach = TrackGraph.CountReachable(start.Value);
                var backwardReach = TrackGraph.CountReachable(
                    new TrackGraph.Step(start.Value.track, !start.Value.enteredViaIn));
                route.message = "No route found to " + DescribeTrack(destination)
                    + " from " + DescribeTrack(start.Value.track)
                    + " (reachable states: " + forwardReach + " ahead, "
                    + backwardReach + " behind).";
                routes[route.id] = route;
                return route;
            }
            route.requiresReverse = reversed;

            route.trackIds = path
                .Select(step => step.track.LogicTrack())
                .Where(track => track != null)
                .Select(track => track.ID.FullID)
                .ToList();
            route.settings = SettingsForPath(path);

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
        public const float LeftTurnPenalty = 250f;

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
            return IsLeftBranch(junction, ApproachDirection(from), to.track) ? 0f : LeftTurnPenalty;
        }

        /// Direction of travel as the train leaves `step`.
        private static Vector3 ApproachDirection(TrackGraph.Step step)
        {
            var curve = step.track.curve;
            if (curve == null || curve.pointCount < 2)
                return Vector3.forward;
            var start = curve[0].position;
            var end = curve.Last().position;
            return step.enteredViaIn ? end - start : start - end;
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
            var prefix = route.requiresReverse ? "Route set (train must reverse). " : "";
            route.message = route.pending.Count > 0
                ? prefix + "Waiting for " + route.pending.Count + " occupied junction(s) to clear."
                : (route.requiresReverse ? "Route set - train must reverse to follow it." : "Route set.");
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

        private static TrackGraph.Step? StartStepFor(Trainset trainset)
        {
            var cars = trainset == null ? null : trainset.cars;
            if (cars == null || cars.Count == 0)
                return null;
            // Prefer a locomotive; otherwise take any car with a bogie on track.
            var car = cars.FirstOrDefault(c => c != null && c.IsLoco) ?? cars.FirstOrDefault(c => c != null);
            if (car == null)
                return null;
            var bogie = car.Bogies?.FirstOrDefault(b => b != null && b.track != null);
            if (bogie == null)
                return null;
            // Which end we leave by follows the direction the train faces.
            var forward = car.transform.forward;
            var curve = bogie.track.curve;
            var enteredViaIn = true;
            if (curve != null && curve.pointCount >= 2)
            {
                var along = curve.Last().position - curve[0].position;
                enteredViaIn = Vector3.Dot(
                    new Vector3(along.x, 0, along.z),
                    new Vector3(forward.x, 0, forward.z)) >= 0f;
            }
            return new TrackGraph.Step(bogie.track, enteredViaIn);
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
            new JProperty("tracks", new JArray(route.trackIds)));

        public static string AllRoutesJson() =>
            new JArray(routes.Values.Select(ToJson)).ToString(Newtonsoft.Json.Formatting.None);
    }
}
