using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Signals.Game;
using Signals.Game.Railway;
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

    public enum RouteStatus { Active, Pending, Conflict, Failed, Cleared, AwaitingReversal }

    /// A crossing claimed by a train on approach: how near it stood when last
    /// measured, and when that was.
    public readonly struct ApproachLock
    {
        public readonly float distance;
        public readonly float since;

        public ApproachLock(float distance, float since)
        {
            this.distance = distance;
            this.since = since;
        }
    }

    public class TrainRoute
    {
        public string id = "";
        public int trainsetId;
        public string destinationTrackId = "";

        /// Every place this road calls at, in order, and which of them it is
        /// running to now. A road with one destination is a one-stop list, so
        /// the same machinery carries both; destinationTrackId is always the
        /// stop in hand, which is what the rest of the class plans towards.
        public List<string> stops = new List<string>();
        public int stopIndex;

        public int priority;                       // tie-breaker for equally close approaches
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
        public List<TrackGraph.Step> pathSteps = new List<TrackGraph.Step>();
        public Signals.Game.Signal? reservedSignal;
        public bool allocationApplied;
        public bool waitingForSignal;
        public int progressIndex;   // path entries before this are behind the train
        public int frontIndex;      // the furthest path entry the train has reached
        public int releasedUpTo;    // junctions handed back, never re-taken
        public int rerouteCount;
        public float offRouteSince;
        public float wrongWaySince;
        public string notice = "";   // survives status updates
        public float nextObstructionRerouteTime;
        public string obstructedTrackId = "";

        // A road that changes direction partway. The train runs out along the
        // first leg, stands clear, then draws back over the same junction onto
        // the second. Only one leg is ever the live path: the machinery that
        // tracks progress and releases junctions behind the train assumes a
        // road is travelled once, in one direction, which a single leg is.
        public bool hasReverseLeg;
        public bool onReverseLeg;
        public List<TrackGraph.Step> reverseSteps = new List<TrackGraph.Step>();
        public List<RailTrack> reverseTracks = new List<RailTrack>();
        public List<string> reverseTrackIds = new List<string>();
        public Junction? reversalJunction;
        public string reversalTrackId = "";
        public string reversalSignalName = "";
        public float runOutMeters;

        /// How far along the road the allocation currently extends, as a path
        /// index. Everything before this has its junctions set and reserved;
        /// everything from here on belongs to someone else for now. Advances as
        /// the train in front clears.
        public int allocatedUpTo = int.MaxValue;
        public string heldShortOf = "";

        /// True while the road stops short of a crossing that is being kept
        /// neutral rather than one another road holds, so the two can be told
        /// apart in what the train is shown.
        public bool heldForApproach;

        /// Crossings this road has come close enough to claim outright, each
        /// with how near its train stood when last measured and when.
        ///
        /// A locked junction is not taken by a nearer train: once a train is
        /// committed to a crossing the road in front of it does not change
        /// under it, and two trains cannot trade the crossing back and forth as
        /// their distances cross. The lock is given back as the train passes.
        ///
        /// Kept per crossing rather than per road, because a road can hold more
        /// than one: sharing a single measurement between them let each reset
        /// the other's clock, so the check for a train that has stopped closing
        /// could never fire and a parked train held its crossings for ever.
        public readonly Dictionary<Junction, ApproachLock> approachLocked =
            new Dictionary<Junction, ApproachLock>();

        /// The contested crossing this road is waiting to approach, if any, with
        /// the distance and the time it began waiting. Only ever one, because
        /// the allocation stops at the first crossing that is held.
        public Junction? approachHeld;
        public float approachHeldSince;
        public float approachHeldDistance;

        /// Forget the leg just travelled, keeping the road's identity and the
        /// stops it has yet to call at, so the next leg is planned into this
        /// same entry instead of appearing as a new road on every page.
        public void ResetLeg()
        {
            settings = new List<JunctionSetting>();
            trackIds = new List<string>();
            pathTracks = new List<RailTrack>();
            pathSteps = new List<TrackGraph.Step>();
            divergenceDetail = new JArray();
            pending.Clear();
            approachLocked.Clear();
            approachHeld = null;
            requiresReverse = false;
            leftDivergences = 0;
            rightDivergences = 0;
            distanceMeters = 0f;
            allocationApplied = false;
            waitingForSignal = false;
            progressIndex = 0;
            frontIndex = 0;
            releasedUpTo = 0;
            rerouteCount = 0;
            offRouteSince = 0f;
            wrongWaySince = 0f;
            notice = "";
            nextObstructionRerouteTime = 0f;
            obstructedTrackId = "";
            hasReverseLeg = false;
            onReverseLeg = false;
            reverseSteps = new List<TrackGraph.Step>();
            reverseTracks = new List<RailTrack>();
            reverseTrackIds = new List<string>();
            reversalJunction = null;
            reversalTrackId = "";
            reversalSignalName = "";
            runOutMeters = 0f;
            allocatedUpTo = int.MaxValue;
            heldShortOf = "";
            heldForApproach = false;
        }

        /// The player whose request laid this road, in a session. Empty in
        /// single player, where there is only one.
        public string requestedBy = "";

        /// Which road this is, counting from the first laid this session, and
        /// which of the page's colours draws it. Both are decided here rather
        /// than by each page, so every player sees the same road in the same
        /// colour - a client's list is a copy of this one, and a colour worked
        /// out from a position in that list would differ between players as
        /// their lists changed.
        public int sequence;
        public int colorIndex;
    }

    /// Route planning and safe junction setting.
    ///
    /// Junctions are never thrown under a car: anything not clear is deferred and
    /// retried until it is. Each route reserves the junctions it needs, so two
    /// routes cannot fight over the same switch. A crossing both want set the
    /// other way is left as it lies until one of their trains is on approach;
    /// that train then locks it and keeps it until it has passed. Which train
    /// that is comes from the shortest remaining approach, with route priority
    /// only breaking a near tie.
    ///
    /// A road may call at several places in turn. Only the leg in hand is ever
    /// planned: the next is laid from wherever the train actually stands when it
    /// arrives, which is also the only position the one after it can be planned
    /// from.
    public static class Routing
    {
        private static readonly Dictionary<string, TrainRoute> routes = new Dictionary<string, TrainRoute>();
        private static readonly Dictionary<Junction, string> reservations = new Dictionary<Junction, string>();
        private static int nextRouteId = 1;

        /// Reused by DistanceToJunction, which is asked several times a tick
        /// and filled the same way every time. Safe to share: nothing it calls
        /// re-enters it, and the contents do not outlive the call.
        private static readonly HashSet<RailTrack> distanceScratch = new HashSet<RailTrack>();

        public const float RetryIntervalSeconds = 1f;
        public const float JunctionPriorityHysteresisMeters = 25f;

        /// How close a train must come before a contested crossing is set and
        /// locked to it. Beyond this the crossing is left as it lies, so a
        /// switch is never thrown for a train that is still kilometres off and
        /// two roads do not trade one while both are a long way away.
        public const float ApproachLockMeters = 400f;

        /// How long a road may sit at a crossing it is first in line for, no
        /// closer than when it arrived, before the crossing is given to it
        /// anyway.
        ///
        /// A train held short of a crossing may already be as close as its road
        /// lets it get - stopped at the signal protecting it, which can stand
        /// further out than the approach distance. Waiting for an approach that
        /// can never happen would hold both roads for ever.
        public const float ApproachHoldTimeoutSeconds = 20f;

        /// How long a train that has claimed a crossing may go without getting
        /// any nearer to it before another train may have it. A road in front of
        /// a moving train is never altered; one in front of a train that has
        /// stopped and stayed stopped is not held against the whole railway.
        public const float ApproachLockStaleSeconds = 60f;

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
        /// How many distinct colours the page draws roads in.
        public const int RouteColorCount = 10;

        /// The lowest colour no live road is using.
        ///
        /// Recycled rather than simply counting up, so clearing a road frees its
        /// colour for the next one. Only a handful are ever live at once, so in
        /// practice no two roads on screen share a colour, and the numbering
        /// carries the order they were set in.
        private static int NextFreeColorIndex()
        {
            var taken = new HashSet<int>(routes.Values.Select(route => route.colorIndex));
            for (var i = 0; i < RouteColorCount; i++)
            {
                if (!taken.Contains(i))
                    return i;
            }
            return routes.Count % RouteColorCount;
        }

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
                var logic = startTrack.LogicTrack();
                var display = logic == null ? "" : logic.ID.FullDisplayID;
                var separator = display.IndexOf('-');
                var yard = separator > 0 ? display.Substring(0, separator) : "";
                if (station != null && station.StationInfoValid
                    && yard == station.stationInfo.YardID)
                    return 1;
            }
            return 0;
        }

        /// The consist a route belongs to.
        ///
        /// A plain loop rather than List.Find: the predicate closes over the ID,
        /// so every call allocated a delegate, and the route machinery asks this
        /// several times a second for every road it is holding.
        public static Trainset? FindTrainset(int trainsetId)
        {
            var sets = Trainset.allSets;
            if (sets == null)
                return null;
            for (var i = 0; i < sets.Count; i++)
            {
                var set = sets[i];
                if (set != null && set.id == trainsetId)
                    return set;
            }
            return null;
        }

        public static TrainRoute? GetRoute(string id) =>
            routes.TryGetValue(id, out var route) ? route : null;

        public static IEnumerable<TrainRoute> AllRoutes() => routes.Values;

        public static void ClearRoute(string id)
        {
            if (RouteNetwork.RequestClear(id))
                return;

            if (!routes.TryGetValue(id, out var route))
                return;
            ReleaseAllocation(route);
            route.status = RouteStatus.Cleared;
            routes.Remove(id);
            Sessions.AddTag("routes");
        }

        public static void ClearAll()
        {
            foreach (var route in routes.Values.ToList())
                ReleaseAllocation(route);
            routes.Clear();
            reservations.Clear();
            lastPublishedJson = "";
            Sessions.AddTag("routes");
        }

        /// Plan and allocate a road on behalf of a session participant. The
        /// host owns the shared allocation table, but the requesting player
        /// still owns the action and is identified on the resulting route.
        public static void SetRouteAsAuthority(
            int trainsetId, string trainCarGuid, string destinationTrackId, string requestedBy)
        {
            // Trainset.id is runtime-local and can differ between multiplayer
            // instances. A car GUID identifies the same consist on the host;
            // retain the numeric ID only as a compatibility fallback.
            var car = string.IsNullOrEmpty(trainCarGuid)
                ? null : TrainCarRegistry.Instance.GetTrainCarByCarGuid(trainCarGuid);
            var trainset = car?.trainset
                ?? FindTrainset(trainsetId);
            // One field carries the whole itinerary, so a client's multi-stop
            // request survives the trip to the host unchanged.
            var stops = RouteDestination.SplitStops(destinationTrackId);
            if (trainset == null || stops.Count == 0)
            {
                Main.DebugLog(() => $"Route request from {requestedBy} for train {trainsetId} "
                    + $"to {destinationTrackId} could not be resolved.");
                return;
            }
            var route = SetRoute(trainset, stops);
            route.requestedBy = requestedBy;
        }

        /// Plan and apply a road that calls at each of these places in turn.
        ///
        /// Only the leg in hand is laid now. The stops after it are held until
        /// the train arrives, because a leg can only be planned from where the
        /// train will actually stand when it starts, and that is not known until
        /// the leg before it has been run.
        public static TrainRoute SetRoute(
            Trainset trainset, IList<string> stops, bool allowReversal = true, int startStop = 0)
        {
            var sequence = nextRouteId++;
            var route = new TrainRoute
            {
                id = "r" + sequence,
                trainsetId = trainset.id,
                sequence = sequence,
                colorIndex = NextFreeColorIndex(),
                stops = RouteDestination.SplitStops(RouteDestination.JoinStops(stops)),
            };
            if (route.stops.Count == 0)
            {
                route.status = RouteStatus.Failed;
                route.message = "No destination was given.";
                routes[route.id] = route;
                return route;
            }
            route.stopIndex = Mathf.Clamp(startStop, 0, route.stops.Count - 1);
            route.destinationTrackId = route.stops[route.stopIndex];

            // Clients submit their own request, while the host performs the one
            // shared allocation so independently refreshing pages cannot fight
            // over a turnout. The host broadcasts the resulting route table.
            if (RouteNetwork.RequestRoute(trainset,
                RouteDestination.JoinStops(route.stops.Skip(route.stopIndex))))
            {
                route.status = RouteStatus.Pending;
                route.message = "Planning route...";
                return route;
            }

            // Planned before anything is given up. Planning reads the world -
            // where the train is and what is in the way - not the route table,
            // so it costs nothing to find out whether the new road exists first.
            PlanLeg(route, trainset, allowReversal);

            if (route.status == RouteStatus.Failed)
            {
                // A road that will not plan takes nothing away. Amending an
                // itinerary and getting it wrong used to release the working
                // road before discovering the new one was impossible, leaving
                // the train with no road at all at the moment the dispatcher
                // could least afford it. The failed attempt is still returned
                // so the page can say why it failed, and is only put in the
                // table when the train had no road to lose - which is the case
                // Reroute arrives in, having already cleared the old one.
                if (!routes.Values.Any(existing => existing.trainsetId == trainset.id))
                {
                    routes[route.id] = route;
                    Sessions.AddTag("routes");
                }
                return route;
            }

            // A train works one road at a time. Booking another used to leave
            // both in the table competing for the same junctions - one reported
            // as held short of its own train's other road - with the stale one
            // rerouting every second as the train followed the other until it
            // gave up. Setting a road for a train that has one replaces it.
            ReplaceRoadsFor(trainset.id);

            routes[route.id] = route;
            TryActivate(route);
            Sessions.AddTag("routes");
            return route;
        }

        /// Give up any road already booked for this train, junctions and signal
        /// included, so the new one is not held short by the old.
        private static void ReplaceRoadsFor(int trainsetId)
        {
            foreach (var existing in routes.Values.Where(r => r.trainsetId == trainsetId).ToList())
            {
                ReleaseAllocation(existing);
                existing.status = RouteStatus.Cleared;
                routes.Remove(existing.id);
                Sessions.AddTag("routes");
            }
        }

        /// Lay a road for the stop in hand into a route that already has its
        /// identity and its list of stops. Returns false with the reason left on
        /// the route when no road exists; whether the route is in the table is
        /// the caller's business, since a replanned leg is already there.
        private static bool PlanLeg(TrainRoute route, Trainset trainset, bool allowReversal)
        {
            var destinationTrackId = route.destinationTrackId;
            var candidates = StartCandidates(trainset).ToList();
            if (candidates.Count == 0)
            {
                route.status = RouteStatus.Failed;
                route.message = "Could not determine which track the train is on.";
                return false;
            }

            // The consist occupies several tracks; a road that immediately runs
            // back through the train itself is not usable.
            var occupied = new HashSet<RailTrack>();
            Occupancy.TracksOccupiedBy(trainset, occupied);

            // A destination is a set of tracks, not one: a named platform can be
            // several RailTrack objects sharing an ID, and a junction stop is
            // every rail that meets it. See RouteDestination.
            var goals = RouteDestination.Goals(destinationTrackId);
            if (goals.Count == 0)
            {
                route.status = RouteStatus.Failed;
                route.message = "There is no "
                    + RouteDestination.Describe(destinationTrackId) + " in the loaded world.";
                return false;
            }
            // Which way the driver has asked to go. Held for the candidate
            // loop below, which weighs it against distance.
            var intent = IntendedHeading(trainset);

            var occupiedByOthers = new HashSet<RailTrack>();
            var poweredByOthers = new HashSet<RailTrack>();
            Occupancy.OccupiedTracksByOthers(
                trainset.id, occupiedByOthers, poweredByOthers, requireFresh: true);
            var reversalStartClear = !occupied.Overlaps(occupiedByOthers);
            if (!reversalStartClear)
            {
                // With detached cars sharing the current track, only the first
                // candidate aligned with the driver's reverser intent is safe.
                // Considering the opposite end would itself be an unmodelled
                // reverse move through those cars, even without PlanReversal.
                // The reverser itself, not IntendedHeading: past loose cars an
                // explicit statement is wanted, and a train drifting is not one.
                var stated = Signalling.ReverserHeading(trainset);
                if (stated.sqrMagnitude < 0.001f)
                {
                    route.status = RouteStatus.Failed;
                    route.message = "Detached cars share the train's track. Set the reverser toward the clear end before routing.";
                    return false;
                }
                candidates = new List<TrackGraph.Step> { candidates[0] };
            }
            bool IsBlocked(TrackGraph.Step step) =>
                occupiedByOthers.Contains(step.track)
                && (!goals.Contains(step.track) || poweredByOthers.Contains(step.track));
            List<TrackGraph.Step>? path = null;
            var chosen = candidates[0];
            var shortestLength = float.MaxValue;
            var bestRouteCost = float.MaxValue;
            var chosenAgreesWithIntent = false;

            // Every connected track is traversable, including tracks belonging
            // to intermediate stations and yards. The selected destination is
            // the only terminal condition. Distance remains the main cost, with
            // a small right-hand penalty so parallel and near-equivalent roads
            // settle onto the left without sending trains on large detours.
            // Direction is deliberately not another distance penalty: if a
            // through road exists out of the end the reverser points toward, it
            // wins before distance is considered. A long forward loop is still
            // easier to drive than propelling the whole train over a shorter
            // road, and no finite penalty can express that rule reliably.
            foreach (var candidate in candidates)
            {
                var found = TrackGraph.FindPath(candidate, goals,
                    extraCost: LeftHandRunningPenalty, isBlocked: IsBlocked);
                if (found == null)
                    continue;
                var length = PathLength(found);
                var routeCost = length + CountRightHandChoices(found) * RightHandCostMeters;
                var agrees = AgreesWithIntent(candidate, intent);
                if (path == null
                    || (agrees && !chosenAgreesWithIntent)
                    || (agrees == chosenAgreesWithIntent && (routeCost < bestRouteCost
                        || (Mathf.Approximately(routeCost, bestRouteCost)
                            && length < shortestLength))))
                {
                    path = found;
                    chosen = candidate;
                    shortestLength = length;
                    bestRouteCost = routeCost;
                    chosenAgreesWithIntent = agrees;
                }
            }

            // Where no single-direction road exists - or where one exists but
            // is far enough round to be worse than stopping and changing ends -
            // look for a road that runs out and sets back.
            // Reversing is unsafe when a newly detached cut shares any track
            // component under the train. The graph carries track and direction,
            // not positions along one track, so allowing the start state would
            // otherwise make cars behind the consist invisible to the reverse
            // leg and favour an impossible "shortest" route.
            // Nothing a reversal can produce costs less than the penalty for
            // changing ends, so a direct road already inside that is the answer
            // and the search below - which is the most expensive thing this mod
            // does - is not worth starting.
            // A reversal is only an alternative when no through road leaves in
            // the requested direction. Once a forward through road exists, it
            // remains preferable even when a draw-forward-and-set-back move or
            // a backwards departure would be shorter.
            var reversal = allowReversal && reversalStartClear
                    && !chosenAgreesWithIntent
                    && bestRouteCost > ReversalPenaltyMeters
                ? PlanReversal(
                    candidates, goals, ConsistLength(trainset), IsBlocked, bestRouteCost, intent)
                : null;
            var useReversal = reversal != null && (path == null || reversal.Cost < bestRouteCost);

            if (path == null && !useReversal)
            {
                var reach = candidates.Select(c => TrackGraph.CountReachable(c)).ToList();
                route.status = RouteStatus.Failed;
                route.message = "No route found to "
                    + RouteDestination.Describe(destinationTrackId)
                    + " from " + DescribeTrack(candidates[0].track)
                    + " (reachable states: " + string.Join(", ", reach) + ").";
                return false;
            }

            if (useReversal)
            {
                var plan = reversal!;
                chosen = plan.start;
                // Only the outbound leg is laid now. The rest is set once the
                // train stands clear, because the junction it sets back over has
                // to lie the other way for the second leg and cannot do both.
                path = plan.outbound;
                shortestLength = plan.outboundMeters + plan.inboundMeters;

                route.hasReverseLeg = true;
                route.reverseSteps = plan.inbound;
                route.reverseTracks = plan.inbound.Select(step => step.track).ToList();
                route.reverseTrackIds = TrackIdsOf(plan.inbound);
                route.runOutMeters = plan.runOutMeters;
                route.reversalTrackId = DescribeTrack(plan.inbound[0].track);
                // The turnout to stand clear of is the one the train came
                // through to reach the place it stops.
                route.reversalJunction = plan.outbound.Count >= 2
                    ? plan.outbound[plan.outbound.Count - 2].ExitJunction
                    : null;
                route.reversalSignalName = SignalBeforeReversal(plan, trainset);
            }

            if (path == null)
            {
                // Unreachable: either a direct road was found or a reversal plan
                // supplied one. Stated rather than asserted so a future change
                // to the two branches above cannot fall through silently.
                route.status = RouteStatus.Failed;
                route.message = "Could not lay a road to "
                    + RouteDestination.Describe(destinationTrackId) + ".";
                return false;
            }

            route.priority = PriorityFor(chosen.track);
            route.distanceMeters = shortestLength;
            // The road leaves from the end of the consist that this path starts
            // at; if that is not the end the locomotive faces, the train is
            // propelling rather than pulling.
            route.requiresReverse = !occupied.Contains(chosen.track) || IsPropelling(trainset, chosen);

            route.pathTracks = path.Select(step => step.track).ToList();
            route.pathSteps = path;
            // FullDisplayID, matching both the /track keys the map draws with and
            // the IDs the game prints on jobs.
            route.trackIds = TrackIdsOf(path);
            route.settings = SettingsForPath(path);
            CountDivergences(path, route);
            route.status = RouteStatus.Active;
            return true;
        }

        /// The longest run-out considered when looking for somewhere to change
        /// ends, and how many candidate places are searched from. Both bound
        /// what would otherwise be a search from every state in the network.
        private const float MaxRunOutMeters = 3000f;
        private const int MaxReversalProbes = 24;

        /// What a reversal costs, as a distance equivalent, when weighed against
        /// a road that never changes direction. Stopping, walking to the other
        /// end and setting back takes real time, so a somewhat longer through
        /// road is still the better move.
        private const float ReversalPenaltyMeters = 800f;

        /// A reversal plan should itself begin in the commanded direction when
        /// possible. This only ranks fallback reversing moves; it is never used
        /// to let a backwards departure beat a forward through road.
        private const float WrongEndPenaltyMeters = ReversalPenaltyMeters;

        /// Which way the driver means to go: the reverser where it is set, and
        /// the train's own motion only where it is not.
        ///
        /// The reverser comes first because it is a statement of intent, while
        /// motion is only evidence of one - a train easing back down a grade
        /// with the reverser forward is not asking to be sent the other way.
        /// Both the planner and the direction check read this one measure, so
        /// the road that gets laid is not one the direction check will turn
        /// round and re-lay a moment later.
        private static Vector3 IntendedHeading(Trainset? trainset)
        {
            var reverser = Signalling.ReverserHeading(trainset);
            if (reverser.sqrMagnitude > 0.001f)
                return reverser.normalized;

            var lead = trainset?.firstCar ?? trainset?.cars?.FirstOrDefault(c => c != null);
            var velocity = lead?.rb != null ? lead.rb.velocity : Vector3.zero;
            velocity.y = 0;
            return velocity.sqrMagnitude >= 0.25f ? velocity.normalized : Vector3.zero;
        }

        /// The direction a train travels while running over this step: the chord
        /// of the track from the end it comes on at to the end it leaves by.
        ///
        /// Taken from the two ends rather than a tangent, so there is no end-of-
        /// curve convention to get the wrong way round: flipping the step is by
        /// definition the same track entered from the other end.
        private static Vector3 DepartureDirection(TrackGraph.Step step)
        {
            if (step.track == null)
                return Vector3.zero;
            var direction = TrackGraph.EndPosition(step)
                - TrackGraph.EndPosition(TrackGraph.Flip(step));
            direction.y = 0;
            return direction;
        }

        /// Whether leaving on this step sends the train the way it has been
        /// asked to go. With no intent expressed - reverser centred and standing
        /// still - every road agrees, and distance decides as it always did.
        private static bool AgreesWithIntent(TrackGraph.Step candidate, Vector3 intent)
        {
            if (intent.sqrMagnitude < 0.001f)
                return true;
            var departure = DepartureDirection(candidate);
            if (departure.sqrMagnitude < 0.0001f)
                return true;
            return Vector3.Dot(departure.normalized, intent) > 0f;
        }

        private sealed class ReversalPlan
        {
            public TrackGraph.Step start;
            public List<TrackGraph.Step> outbound = new List<TrackGraph.Step>();
            public List<TrackGraph.Step> inbound = new List<TrackGraph.Step>();
            public float outboundMeters;
            public float inboundMeters;
            public float runOutMeters;

            public float startPenalty;

            public float Cost =>
                outboundMeters + inboundMeters + ReversalPenaltyMeters + startPenalty;
        }

        /// Plan a road that runs out one way, changes ends, and draws back.
        ///
        /// A train standing in a shed or siding whose destination lies back past
        /// the turnout it must leave by cannot get there in one movement:
        /// leaving commits it to the far side of that turnout. What a driver
        /// does is pull out far enough to stand clear, then set back over it
        /// onto the other road, and that is what this looks for.
        /// `ceiling` is what a plan has to beat to be worth having: the cost of
        /// the direct road, or infinity when there is none.
        private static ReversalPlan? PlanReversal(
            List<TrackGraph.Step> candidates, HashSet<RailTrack> goals, float consistLength,
            System.Func<TrackGraph.Step, bool> isBlocked, float ceiling, Vector3 intent)
        {
            // One sweep out of the destination says which states can still reach
            // it, so the thousands of places a train might stand can be filtered
            // before paying for a search from any of them.
            var reaching = TrackGraph.StatesReaching(goals);
            if (reaching.Count == 0)
                return null;

            ReversalPlan? best = null;
            foreach (var candidate in candidates)
            {
                var startPenalty =
                    AgreesWithIntent(candidate, intent) ? 0f : WrongEndPenaltyMeters;
                var exploration = TrackGraph.Explore(
                    candidate, MaxRunOutMeters, extraCost: LeftHandRunningPenalty,
                    isBlocked: isBlocked);

                // The train has to stand entirely beyond the turnout it will set
                // back over, so a run-out shorter than the consist is no use
                // however convenient the track. Measured from the start of the
                // road rather than from the train's nose, which is the
                // conservative direction to be wrong in.
                var viable = exploration.cost
                    .Where(entry => entry.Value >= consistLength
                        && !entry.Key.Equals(candidate)
                        && reaching.Contains(TrackGraph.Flip(entry.Key)))
                    .OrderBy(entry => entry.Value + GoalDistance(TrackGraph.Flip(entry.Key), goals))
                    .Take(MaxReversalProbes)
                    .ToList();

                foreach (var entry in viable)
                {
                    // The run out is already known, and the straight line from
                    // where it ends to the destination cannot be longer than the
                    // rails that cover it. That makes a floor on what this probe
                    // could possibly cost, which is worth having because the
                    // search that would confirm it is a road search across the
                    // whole network. Most probes cannot beat the road already in
                    // hand and are dropped here without one.
                    var outbound = exploration.PathTo(entry.Key);
                    if (outbound.Count == 0)
                        continue;
                    var outboundMeters = PathLength(outbound);
                    // Still a floor with the penalty in it: the straight line
                    // to the goal cannot exceed the rails that cover it, and the
                    // penalty is charged in full on both sides of the comparison.
                    var floor = outboundMeters
                        + GoalDistance(TrackGraph.Flip(entry.Key), goals)
                        + ReversalPenaltyMeters + startPenalty;
                    if (floor >= Mathf.Min(ceiling, best == null ? float.PositiveInfinity : best.Cost))
                        continue;

                    var inbound = TrackGraph.FindPath(
                        TrackGraph.Flip(entry.Key), goals,
                        extraCost: LeftHandRunningPenalty, isBlocked: isBlocked);
                    if (inbound == null || inbound.Count == 0)
                        continue;

                    var plan = new ReversalPlan
                    {
                        start = candidate,
                        outbound = outbound,
                        inbound = inbound,
                        outboundMeters = outboundMeters,
                        inboundMeters = PathLength(inbound),
                        runOutMeters = entry.Value,
                        startPenalty = startPenalty,
                    };
                    if (best == null || plan.Cost < best.Cost)
                        best = plan;
                }
            }
            return best;
        }

        /// Straight-line distance from where a step ends to the nearest goal,
        /// used only to try the most promising reversal points first.
        private static float GoalDistance(TrackGraph.Step step, HashSet<RailTrack> goals)
        {
            var from = TrackGraph.EndPosition(step);
            var best = float.MaxValue;
            foreach (var goal in goals)
            {
                var curve = goal == null ? null : goal.curve;
                if (curve == null || curve.pointCount == 0)
                    continue;
                best = Mathf.Min(best, Vector3.Distance(from, curve[0].position));
            }
            return best == float.MaxValue ? 0f : best;
        }

        private static float ConsistLength(Trainset trainset)
        {
            var cars = trainset?.cars;
            if (cars == null || cars.Count == 0)
                return DefaultCarLengthMeters;
            var total = 0f;
            foreach (var car in cars)
                total += car?.logicCar?.length ?? DefaultCarLengthMeters;
            return Mathf.Max(DefaultCarLengthMeters, total);
        }

        private const float DefaultCarLengthMeters = 20f;

        /// The signal standing between the train and the place it changes ends,
        /// so the instruction can name the one the driver has to pass.
        private static string SignalBeforeReversal(ReversalPlan plan, Trainset trainset)
        {
            var car = trainset?.firstCar ?? trainset?.cars?.FirstOrDefault(c => c != null);
            if (car == null)
                return "";
            var controller = Signalling.NextDvSignalController(plan.start, car.transform.position);
            return controller == null ? "" : controller.Name;
        }

        private static List<string> TrackIdsOf(List<TrackGraph.Step> path) => path
            .Select(step => step.track.LogicTrack())
            .Where(track => track != null)
            .Select(track => track.ID.FullDisplayID)
            .ToList();

        /// True once the whole consist stands beyond the reversal point and off
        /// the turnout it has to set back over.
        private static bool ConsistClearOfReversal(TrainRoute route)
        {
            var occupied = new HashSet<RailTrack>();
            Occupancy.TracksOccupiedBy(route.trainsetId, occupied);
            if (occupied.Count == 0)
                return false;

            // Clearing the actual turnout is authoritative. A long consist can
            // span several RailTrack components beyond it, so requiring every
            // bogie to occupy the final planned component leaves valid moves
            // stuck in AwaitingReversal forever.
            if (route.reversalJunction != null)
                return Occupancy.IsJunctionClear(route.reversalJunction);

            // A degenerate reversal with no turnout still needs the entire
            // consist on its final outbound component.
            var reversalTrack = route.pathTracks.Count == 0
                ? null : route.pathTracks[route.pathTracks.Count - 1];
            if (reversalTrack == null)
                return false;
            foreach (var track in occupied)
            {
                if (track != reversalTrack)
                    return false;
            }

            return true;
        }

        /// Hand the road over to the second leg once the train stands clear.
        private static void BeginReverseLeg(TrainRoute route)
        {
            // The outbound junctions are released outright: the second leg wants
            // at least one of them lying the other way, so holding them would
            // block the very move being set up.
            ReleaseAllocation(route);

            route.onReverseLeg = true;
            route.pathSteps = route.reverseSteps;
            route.pathTracks = route.reverseTracks;
            route.trackIds = route.reverseTrackIds;
            route.settings = SettingsForPath(route.reverseSteps);
            route.progressIndex = 0;
            route.frontIndex = 0;
            route.releasedUpTo = 0;
            route.allocatedUpTo = int.MaxValue;
            route.pending.Clear();
            route.wrongWaySince = 0f;
            route.offRouteSince = 0f;
            route.notice = "Reversing. ";
            CountDivergences(route.reverseSteps, route);
            TryActivate(route);
        }

        private static bool TryActivate(TrainRoute route)
        {
            var limit = ConflictLimit(route, out var heldBy);
            route.allocatedUpTo = limit;
            route.heldShortOf = heldBy;
            if (limit <= route.progressIndex)
            {
                // Not even the first junction ahead is free, so there is nothing
                // to give the train yet.
                route.status = RouteStatus.Pending;
                route.message = "Waiting to allocate route: held by " + heldBy + ".";
                route.allocationApplied = false;
                return false;
            }
            route.waitingForSignal = false;
            foreach (var setting in PendingSettings(route))
                reservations[setting.junction] = route.id;
            route.allocationApplied = true;
            Apply(route);
            if (route.pending.Count == 0 && !TryReserveNextSignal(route))
            {
                ReleaseAllocation(route);
                route.waitingForSignal = true;
                route.status = RouteStatus.Pending;
                route.message = "Waiting for the protecting signal route to clear.";
                return false;
            }
            return true;
        }

        private static bool TryReserveNextSignal(TrainRoute route)
        {
            if (!SignalManager.Running || route.pathSteps.Count == 0)
                return true;
            var trainset = FindTrainset(route.trainsetId);
            var fallback = trainset?.firstCar ?? trainset?.cars?.FirstOrDefault(car => car != null);
            if (fallback == null)
                return false;
            var index = Mathf.Clamp(route.progressIndex, 0, route.pathSteps.Count - 1);
            var step = route.pathSteps[index];
            var curve = step.track.curve;
            var heading = curve == null || curve.pointCount < 2
                ? fallback.transform.forward
                : curve[curve.pointCount - 1].position - curve[0].position;
            if (!step.enteredViaIn)
                heading = -heading;
            var lead = Signalling.LeadingCar(trainset, fallback, heading);
            var controller = Signalling.NextDvSignalController(
                step, lead.transform.position);
            var signal = controller?.GetControllerSignal();
            if (signal == null)
                return true;

            if (signal == route.reservedSignal)
            {
                // DV Signals or multiplayer can drop a reservation after it was
                // granted. Remembering the Signal object is not proof that its
                // block is still reserved; treating it as success leaves an
                // otherwise empty signal at red forever. Reacquire below.
                if (TrackReserver.HasReservation(signal))
                    return true;
                route.reservedSignal = null;
                Main.DebugLog(() => $"Route {route.id}: signal reservation was lost; reacquiring.");
            }

            if (route.reservedSignal != null && TrackReserver.HasReservation(route.reservedSignal))
                ReleaseSignal(route.reservedSignal);

            if (ReserveSignal(signal))
            {
                route.reservedSignal = signal;
                route.waitingForSignal = false;
                return true;
            }

            // Departures have priority over inbound trains. If the conflicting
            // DV Signals reservation belongs to one of our lower-priority
            // routes, release it and leave that inbound route pending for the
            // retry coroutine to allocate again.
            var block = signal.Block;
            if (block != null)
            {
                foreach (var track in block.AllTracks)
                {
                    if (!TrackReserver.IsTrackReserved(track, out var by))
                        continue;
                    var owner = routes.Values.FirstOrDefault(candidate =>
                        candidate != route && candidate.reservedSignal == by);
                    if (owner == null || route.priority <= owner.priority)
                        continue;
                    ReleaseAllocation(owner);
                    owner.waitingForSignal = true;
                    owner.status = RouteStatus.Pending;
                    owner.message = "Held for higher-priority departure " + route.id + ".";
                    if (ReserveSignal(signal))
                    {
                        route.reservedSignal = signal;
                        route.waitingForSignal = false;
                        return true;
                    }
                }
            }
            route.waitingForSignal = true;
            return false;
        }

        private static bool ReserveSignal(Signals.Game.Signal signal)
        {
            if (!TrackReserver.ReserveForSignal(signal))
                return false;
            if (MultiplayerIntegration.IsMpRunning && MultiplayerIntegration.IsHost)
                MultiplayerIntegration.SendReservationRequest(signal, -1f);
            return true;
        }

        private static void ReleaseSignal(Signals.Game.Signal signal)
        {
            TrackReserver.ClearFromSignal(signal);
            if (MultiplayerIntegration.IsMpRunning && MultiplayerIntegration.IsHost)
                MultiplayerIntegration.SendReservationCancelRequest(signal);
        }

        private static void ReleaseAllocation(TrainRoute route)
        {
            foreach (var junction in reservations.Where(pair => pair.Value == route.id)
                .Select(pair => pair.Key).ToList())
                reservations.Remove(junction);
            if (route.reservedSignal != null && TrackReserver.HasReservation(route.reservedSignal)
                && (!MultiplayerIntegration.IsMpRunning || MultiplayerIntegration.IsHost))
                ReleaseSignal(route.reservedSignal);
            route.reservedSignal = null;
            route.allocationApplied = false;
        }

        /// A modest distance-equivalent cost for a right-hand choice. Physical
        /// distance remains dominant, but near-equivalent roads consistently
        /// choose the left line even while a turnout is still diverging.
        private const float RightHandCostMeters = 25f;

        private static float LeftHandRunningPenalty(TrackGraph.Step from, TrackGraph.Step to) =>
            IsRightHandChoice(from, to) ? RightHandCostMeters : 0f;

        /// Reports whether a path takes the right-hand option relative to its
        /// actual direction of travel.
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

        private static int CountRightHandChoices(List<TrackGraph.Step> path)
        {
            var count = 0;
            for (var i = 0; i + 1 < path.Count; i++)
            {
                if (IsRightHandChoice(path[i], path[i + 1]))
                    count++;
            }
            return count;
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
        /// How far along the road this route may be allocated before it would
        /// take a junction another route is holding the other way.
        ///
        /// A crossing road is not a reason to refuse the whole route. The train
        /// is given everything up to the contested turnout and stands at the
        /// signal protecting it; when the route in front releases that junction,
        /// the next tick takes the rest and the signal clears. That is how two
        /// trains share a crossing, rather than the second waiting for the first
        /// to finish its entire journey.
        private static int ConflictLimit(TrainRoute route, out string heldBy)
        {
            heldBy = "";
            route.heldForApproach = false;
            // The whole road ahead, not merely as far as the allocation reaches.
            // A route already held short of a crossing has that crossing outside
            // its allocation, so looking only within it would find no conflict
            // and hand the route the very junction it is waiting for.
            foreach (var setting in FutureSettings(route).OrderBy(s => s.pathIndex))
            {
                var contested = false;
                if (reservations.TryGetValue(setting.junction, out var ownerId) && ownerId != route.id)
                {
                    var owner = GetRoute(ownerId);
                    var ownerSetting = owner == null ? null : FutureSettings(owner)
                        .FirstOrDefault(s => s.junction == setting.junction);
                    if (owner != null && ownerSetting != null && ownerSetting.branch != setting.branch)
                    {
                        contested = true;
                        if (!TryTakeJunction(route, setting, owner, ownerSetting))
                        {
                            heldBy = "route " + ownerId + " (train " + owner.trainsetId + ")";
                            route.approachHeld = null;
                            return setting.pathIndex;
                        }
                    }
                }

                if (!contested)
                    contested = OthersWant(route, setting);
                if (contested && !ClaimApproach(route, setting))
                {
                    heldBy = "an interlocked crossing";
                    route.heldForApproach = true;
                    return setting.pathIndex;
                }
            }
            route.approachHeld = null;
            return int.MaxValue;
        }

        /// Whether this route may take a contested junction from the route
        /// currently holding it.
        ///
        /// The nearer approach wins, by enough of a margin that a crossing does
        /// not change hands over a few metres. A holder whose train is already
        /// on approach keeps it whatever the distances say: a road in front of a
        /// train committed to it is not altered.
        private static bool TryTakeJunction(
            TrainRoute route, JunctionSetting setting, TrainRoute owner, JunctionSetting ownerSetting)
        {
            if (owner.approachLocked.ContainsKey(setting.junction)
                && !LockHasGoneStale(owner, ownerSetting))
                return false;

            var challengerDistance = DistanceToJunction(route, setting);
            var ownerDistance = DistanceToJunction(owner, ownerSetting);
            var challengerIsCloser = challengerDistance + JunctionPriorityHysteresisMeters
                < ownerDistance;
            var approachesAreTied = Mathf.Abs(challengerDistance - ownerDistance)
                <= JunctionPriorityHysteresisMeters;
            if (!challengerIsCloser && !(approachesAreTied && route.priority > owner.priority))
                return false;

            // Release the holder's entire allocation so its signal and junctions
            // agree, then let it compete again as the live approach distances
            // change.
            ReleaseAllocation(owner);
            owner.waitingForSignal = true;
            owner.status = RouteStatus.Pending;
            owner.message = "Held for nearer train on " + route.id + ".";
            return true;
        }

        /// Whether the train holding a locked crossing has stopped closing on it
        /// for long enough that it should give it up.
        ///
        /// Only ever asked when someone else wants the crossing, so a train
        /// standing at a signal on a railway nobody else is using is left alone.
        /// The lock is dropped here rather than by the holder, which by
        /// definition is not running its own arbitration while it sits still.
        private static bool LockHasGoneStale(TrainRoute owner, JunctionSetting ownerSetting)
        {
            if (!owner.approachLocked.TryGetValue(ownerSetting.junction, out var held))
                return false;
            var distance = DistanceToJunction(owner, ownerSetting);
            if (distance + JunctionPriorityHysteresisMeters < held.distance)
            {
                // Still closing on it, so it is still going somewhere.
                owner.approachLocked[ownerSetting.junction] =
                    new ApproachLock(distance, Time.time);
                return false;
            }
            if (Time.time - held.since < ApproachLockStaleSeconds)
                return false;
            owner.approachLocked.Remove(ownerSetting.junction);
            return true;
        }

        /// Whether any other live road wants this junction lying the other way,
        /// whoever holds it at the moment.
        ///
        /// A junction only two roads want set the same way is not contested:
        /// one position satisfies both, and holding it neutral would stop two
        /// trains that were never in each other's way.
        private static bool OthersWant(TrainRoute route, JunctionSetting setting)
        {
            foreach (var other in routes.Values)
            {
                if (other == route || other.status == RouteStatus.Failed
                    || other.status == RouteStatus.Cleared)
                    continue;
                foreach (var otherSetting in other.settings)
                {
                    if (otherSetting.junction != setting.junction
                        || otherSetting.pathIndex < other.progressIndex)
                        continue;
                    if (otherSetting.branch != setting.branch)
                        return true;
                }
            }
            return false;
        }

        /// Whether some other road wanting this crossing the other way stands
        /// meaningfully nearer to it than this one.
        private static bool AnotherIsNearer(
            TrainRoute route, JunctionSetting setting, float distance)
        {
            foreach (var other in routes.Values)
            {
                if (other == route || other.status == RouteStatus.Failed
                    || other.status == RouteStatus.Cleared)
                    continue;
                var otherSetting = FutureSettings(other)
                    .FirstOrDefault(s => s.junction == setting.junction);
                if (otherSetting == null || otherSetting.branch == setting.branch)
                    continue;
                if (DistanceToJunction(other, otherSetting) + JunctionPriorityHysteresisMeters
                    < distance)
                    return true;
            }
            return false;
        }

        /// Whether this route may set a contested crossing yet, locking it if so.
        ///
        /// The crossing stays as it lies while every claimant is still a long way
        /// off; the first train to come within the approach distance takes it and
        /// holds it until it has passed. A road that has stopped closing on a
        /// crossing it is first in line for takes it anyway after a while, since
        /// it is usually standing at the signal that protects the crossing and
        /// can get no nearer until the crossing is set.
        private static bool ClaimApproach(TrainRoute route, JunctionSetting setting)
        {
            if (route.approachLocked.ContainsKey(setting.junction))
                return true;

            var distance = DistanceToJunction(route, setting);
            if (distance > ApproachLockMeters)
            {
                if (route.approachHeld != setting.junction)
                {
                    route.approachHeld = setting.junction;
                    route.approachHeldSince = Time.time;
                    route.approachHeldDistance = distance;
                    return false;
                }
                if (distance + JunctionPriorityHysteresisMeters < route.approachHeldDistance)
                {
                    // Still closing, so there is no deadlock to break.
                    route.approachHeldDistance = distance;
                    route.approachHeldSince = Time.time;
                    return false;
                }
                if (Time.time - route.approachHeldSince < ApproachHoldTimeoutSeconds)
                    return false;
                // Breaking the deadlock is still the nearer train's right, or
                // the crossing would go to whichever road happened to be walked
                // first this tick.
                if (AnotherIsNearer(route, setting, distance))
                    return false;
            }

            route.approachLocked[setting.junction] = new ApproachLock(distance, Time.time);
            route.approachHeld = null;
            return true;
        }

        /// Approximate along-road distance from the leading occupied route
        /// component to a contested junction. The current component uses the
        /// nearest bogie's real position; complete components ahead use their
        /// rail length. This is stable, cheap at the one-second arbitration
        /// cadence, and substantially fairer than reservation creation order.
        private static float DistanceToJunction(TrainRoute route, JunctionSetting setting)
        {
            if (setting.pathIndex < route.progressIndex)
                return float.PositiveInfinity;

            var trainset = FindTrainset(route.trainsetId);
            var occupied = distanceScratch;
            Occupancy.TracksOccupiedBy(trainset, occupied);

            var frontIndex = Mathf.Clamp(route.progressIndex, 0, setting.pathIndex);
            for (var i = frontIndex; i <= setting.pathIndex && i < route.pathTracks.Count; i++)
            {
                if (occupied.Contains(route.pathTracks[i]))
                    frontIndex = i;
            }

            var distance = 0f;
            if (frontIndex < route.pathSteps.Count)
            {
                var exit = TrackGraph.EndPosition(route.pathSteps[frontIndex]);
                var nearest = float.PositiveInfinity;
                var cars = trainset?.cars;
                if (cars != null)
                {
                    foreach (var car in cars)
                    {
                        if (car == null || car.Bogies == null)
                            continue;
                        foreach (var bogie in car.Bogies)
                        {
                            if (bogie == null || bogie.track != route.pathTracks[frontIndex]
                                || bogie.traveller == null)
                                continue;
                            var p = bogie.traveller.worldPosition;
                            var position = new Vector3((float)p.x, (float)p.y, (float)p.z);
                            nearest = Mathf.Min(nearest, Vector3.Distance(position, exit));
                        }
                    }
                }
                distance = float.IsPositiveInfinity(nearest)
                    ? TrackGraph.TrackLength(route.pathTracks[frontIndex]) : nearest;
            }

            for (var i = frontIndex + 1; i <= setting.pathIndex && i < route.pathTracks.Count; i++)
                distance += TrackGraph.TrackLength(route.pathTracks[i]);
            return distance;
        }

        /// Take any junction that has become available since the road was laid,
        /// so a train held short of a crossing moves up as the one in front
        /// clears instead of waiting for a whole new plan.
        private static void ExtendAllocation(TrainRoute route)
        {
            var limit = ConflictLimit(route, out var heldBy);
            route.heldShortOf = heldBy;
            if (limit <= route.allocatedUpTo)
                return;

            route.allocatedUpTo = limit;
            foreach (var setting in PendingSettings(route))
                reservations[setting.junction] = route.id;
            Apply(route);
            TryReserveNextSignal(route);
            Sessions.AddTag("routes");
        }

        /// Set every junction that is currently clear; defer the rest.
        public static void Apply(TrainRoute route)
        {
            route.pending.Clear();
            foreach (var setting in PendingSettings(route))
            {
                if (setting.junction.selectedBranch == setting.branch)
                    continue;
                if (Occupancy.IsJunctionClear(setting.junction))
                    setting.junction.Switch(Junction.SwitchMode.NO_SOUND, setting.branch);
                else
                    route.pending.Add(setting.junction);
            }
            UpdateStatus(route);
        }

        private static void UpdateStatus(TrainRoute route)
        {
            // A road that failed keeps the reason it failed. Recomputing a
            // status from an empty path would report it as set instead.
            if (route.status == RouteStatus.Failed)
                return;

            // Anything already said about how the road was chosen is kept, since
            // this used to overwrite it and hide notices entirely.
            var notice = route.notice;

            if (route.hasReverseLeg && !route.onReverseLeg)
            {
                route.status = RouteStatus.AwaitingReversal;
                var past = string.IsNullOrEmpty(route.reversalSignalName)
                    ? "" : " past signal " + route.reversalSignalName;
                route.message = notice + "Draw forward" + past + " onto "
                    + route.reversalTrackId + " (about "
                    + Mathf.RoundToInt(route.runOutMeters) + " m) and stop clear of the"
                    + " junction, then reverse. The rest of the road is set once you"
                    + " are clear."
                    + (route.pending.Count > 0
                        ? " Waiting for " + route.pending.Count + " occupied junction(s)."
                        : "");
                return;
            }

            route.status = route.pending.Count > 0 || route.waitingForSignal
                ? RouteStatus.Pending : RouteStatus.Active;
            // Only what the driver has to act on. Which way the road diverges,
            // and that the train will propel, are on the route itself for a page
            // to show however it likes; repeating them in prose on every road
            // buried the messages that actually needed reading.
            var prefix = notice;
            if (route.allocatedUpTo != int.MaxValue && !string.IsNullOrEmpty(route.heldShortOf))
            {
                // Set as far as it can go. The train runs up to the signal
                // protecting the crossing and waits there rather than being
                // refused the whole road.
                route.status = RouteStatus.Pending;
                route.message = prefix + (route.heldForApproach
                    ? "Road set up to the crossing ahead. The crossing is interlocked and"
                        + " stays as it lies until a train is close enough to claim it."
                    : "Road set as far as the crossing, held short of " + route.heldShortOf
                        + ". It will be extended when that route clears.");
                return;
            }
            // Nothing to say when the road is simply set. The status already
            // reads Active and the card already names where the train is going,
            // so a line restating it on every road is noise that buries the
            // ones a driver has to act on. Notices - rerouted, which call this
            // is - still speak, and so does anything holding the road up.
            route.message = route.waitingForSignal
                ? prefix + "Waiting for the protecting signal route to clear."
                : route.pending.Count > 0
                ? prefix + "Waiting for " + route.pending.Count + " occupied junction(s) to clear."
                : notice;
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
            Occupancy.TracksOccupiedBy(route.trainsetId, occupied);
            if (occupied.Count == 0)
                return;

            // Both ends of the consist, over the whole path rather than from
            // the last known point: a train that backs up along its road should
            // light the track behind it again rather than leaving it cleared.
            // The rearmost is how far the road may be handed back; the frontmost
            // is where the train actually is on it, which is what the direction
            // check has to read.
            var rearmost = -1;
            var front = -1;
            for (var i = 0; i < route.pathTracks.Count; i++)
            {
                if (!occupied.Contains(route.pathTracks[i]))
                    continue;
                if (rearmost < 0)
                    rearmost = i;
                front = i;
            }
            if (rearmost < 0)
            {
                // Off its road. Give a shunt a moment to step back on before
                // recomputing, so a brief excursion does not trigger a reroute.
                if (route.offRouteSince <= 0f)
                    route.offRouteSince = Time.time;
                else if (Time.time - route.offRouteSince > OffRouteGraceSeconds)
                    Reroute(route, "the train left its road");
                return;
            }
            route.offRouteSince = 0f;

            var rearMoved = rearmost != route.progressIndex;
            var frontMoved = front != route.frontIndex;
            route.progressIndex = rearmost;
            route.frontIndex = front;

            // Ground covered gives the recovery budget back. The count is there
            // to stop a road being re-laid over and over from a spot it cannot
            // be run from; it was never meant to ration how many times a train
            // may be put back on its road over a whole journey. Never cleared,
            // it did exactly that: five recomputations from any mix of causes -
            // traffic diverted around, a shunt stepping off the road, a curve
            // misread - could fall hours apart and still retire the road for
            // good, blaming the train for leaving a road it was still on. Once
            // the back of the train has moved up onto the second component of
            // the road it was given, that road is working and the budget is
            // whole again.
            if (rearmost > 0)
                route.rerouteCount = 0;

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
                    route.approachLocked.Remove(setting.junction);
                }
                route.releasedUpTo = rearmost;
            }

            if (route.progressIndex >= route.pathTracks.Count - 1
                && HasReachedDestination(route))
            {
                if (AdvanceToNextStop(route))
                    return;
                route.message = "Arrived at "
                    + RouteDestination.Describe(route.destinationTrackId) + ".";
                ClearRoute(route.id);
                return;
            }
            // The rear controls reservations, but the front controls what is
            // visibly still ahead of the train. Publish either movement: on a
            // long consist the front can pass several route components while
            // the rear remains on the same one, which previously left already
            // travelled road painted until the rear finally crossed a switch.
            if (rearMoved || frontMoved)
                Sessions.AddTag("routes");
        }

        /// How near a junction a train has to come for a call there to count as
        /// made. Generous: a train this close is at the turnout for any purpose
        /// a dispatcher has in mind.
        public const float JunctionArrivalMeters = 50f;
        public const float RegionalStationArrivalMeters = 75f;

        /// Whether the train has actually got to the place the road was booked
        /// to.
        ///
        /// Being on a named track is being there - it is the destination. A
        /// junction is not: it is a point, and the road to one ends on the track
        /// that meets it, which may run for a mile before it gets there. Without
        /// this the train was told it had arrived the moment its last vehicle
        /// rolled onto that track, which on a long approach is nowhere near the
        /// turnout that was asked for.
        private static bool HasReachedDestination(TrainRoute route)
        {
            if (RouteDestination.IsRegional(route.destinationTrackId))
            {
                var stationId = route.destinationTrackId
                    .Substring(RouteDestination.RegionalPrefix.Length);
                if (!Stations.TryRegionalStation(stationId, out _, out var center))
                    return false;
                var regionalCars = FindTrainset(route.trainsetId)?.cars;
                if (regionalCars == null)
                    return false;
                foreach (var car in regionalCars)
                {
                    if (car == null)
                        continue;
                    var absolute = car.transform.position - WorldMover.currentMove;
                    var dx = absolute.x - center.x;
                    var dz = absolute.z - center.z;
                    if (dx * dx + dz * dz <= RegionalStationArrivalMeters
                        * RegionalStationArrivalMeters)
                        return true;
                }
                return false;
            }
            var junction = RouteDestination.FindJunction(route.destinationTrackId);
            if (junction == null)
                return true;
            var cars = FindTrainset(route.trainsetId)?.cars;
            if (cars == null)
                return true;

            // Any vehicle, not the leading one: a train that is propelling puts
            // its far end at the junction first, and either way the consist has
            // reached the turnout.
            var position = junction.position;
            foreach (var car in cars)
            {
                if (car == null || car.Bogies == null)
                    continue;
                foreach (var bogie in car.Bogies)
                {
                    if (bogie == null || bogie.traveller == null)
                        continue;
                    var p = bogie.traveller.worldPosition;
                    var at = new Vector3((float)p.x, (float)p.y, (float)p.z);
                    if (Vector3.Distance(at, position) <= JunctionArrivalMeters)
                        return true;
                }
            }
            return false;
        }

        /// Lay the next leg of a road that calls at more than one place.
        ///
        /// The same route entry is kept rather than a new one booked, so the
        /// train is not shown a fresh road at every call and the stops still to
        /// come are not lost. Returns false when this was the last stop, which
        /// is the caller's cue to finish the road.
        private static bool AdvanceToNextStop(TrainRoute route)
        {
            if (route.stopIndex + 1 >= route.stops.Count)
                return false;
            var trainset = FindTrainset(route.trainsetId);
            if (trainset == null)
                return false;

            var arrivedAt = RouteDestination.Describe(route.destinationTrackId);
            ReleaseAllocation(route);
            route.ResetLeg();
            route.stopIndex++;
            route.destinationTrackId = route.stops[route.stopIndex];

            var stopLabel = "Stop " + (route.stopIndex + 1) + " of " + route.stops.Count;
            if (!PlanLeg(route, trainset, allowReversal: true))
            {
                // Held in the table rather than dropped: the train has arrived
                // somewhere real, and the driver needs to see why the rest of
                // the itinerary cannot be run from there.
                route.message = "Arrived at " + arrivedAt + ". " + route.message;
                return true;
            }
            route.notice = stopLabel + ", after " + arrivedAt + ". ";
            TryActivate(route);
            Sessions.AddTag("routes");
            return true;
        }

        /// How long a train may be off its road before the route is recomputed,
        /// and how many times that may happen before giving up. The delay keeps
        /// a shunt that briefly steps off the path from triggering a reroute.
        public const float OffRouteGraceSeconds = 3f;
        public const int MaxReroutes = 5;

        /// Tracks carrying an ID, in either the canonical or the display form.
        /// A lookup, not a scan: see TrackCatalog.
        public static IEnumerable<RailTrack> FindTracks(string trackId) =>
            TrackCatalog.WithId(trackId);

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
                if (Occupancy.IsJunctionClear(setting.junction))
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

        /// Recompute a road for a train that has left the one it was given, or
        /// is running against it. The cause is what prompted this attempt, so a
        /// road that gives up can say what it gave up on rather than blaming the
        /// train for leaving a road it may never have left.
        private static void Reroute(TrainRoute route, string cause)
        {
            if (route.rerouteCount >= MaxReroutes)
            {
                route.status = RouteStatus.Failed;
                route.message = "Gave up after " + MaxReroutes + " attempts to re-lay this"
                    + " road without the train getting anywhere on it (last attempt: "
                    + cause + "). Set the road again from where the train stands.";
                return;
            }
            Main.DebugLog(() =>
                $"Route {route.id}: re-laying, attempt {route.rerouteCount + 1}"
                + $" of {MaxReroutes} ({cause}).");

            var trainset = FindTrainset(route.trainsetId);
            if (trainset == null || !RouteDestination.Exists(route.destinationTrackId))
            {
                ClearRoute(route.id);
                return;
            }

            // The stops still to come travel with the replacement, so a train
            // that wanders off its road part way through an itinerary is put
            // back on it rather than losing everything after the current leg.
            var stops = route.stops;
            var startStop = route.stopIndex;
            var attempts = route.rerouteCount + 1;
            ClearRoute(route.id);

            var replacement = SetRoute(trainset, stops, startStop: startStop);
            replacement.rerouteCount = attempts;
            if (replacement.status != RouteStatus.Failed)
                replacement.message = "Rerouted (" + attempts + "). " + replacement.message;
        }

        /// How far along the booked road to watch for newly loaded consists.
        /// Two kilometres gives multiplayer enough time to stream cars and lets
        /// a train receive a new road well before reaching the protecting signal.
        private const float ObstructionLookaheadMeters = 2000f;
        private const float ObstructionRetrySeconds = 10f;

        private static RailTrack? ObstructedTrackAhead(TrainRoute route)
        {
            var occupied = new HashSet<RailTrack>();
            var powered = new HashSet<RailTrack>();
            Occupancy.OccupiedTracksByOthers(route.trainsetId, occupied, powered);

            var distance = 0f;
            for (var i = Mathf.Max(0, route.progressIndex + 1);
                i < route.pathTracks.Count && distance <= ObstructionLookaheadMeters; i++)
            {
                var track = route.pathTracks[i];
                distance += TrackGraph.TrackLength(track);
                if (!occupied.Contains(track))
                    continue;

                // Loose cars on the selected destination are an intentional
                // coupling target. Any through-track occupancy, or a powered
                // train at the destination, is an obstruction. A junction stop
                // has no such target: its last track is a running line that the
                // train is only being brought up to.
                var isDestination = i == route.pathTracks.Count - 1
                    && !RouteDestination.IsJunction(route.destinationTrackId);
                if (isDestination && !powered.Contains(track))
                    continue;
                return track;
            }
            return null;
        }

        /// Replan around live occupancy. Returns true while the old route should
        /// stop processing this tick (either replaced or held at red).
        private static bool HandleLiveObstruction(TrainRoute route)
        {
            var obstruction = ObstructedTrackAhead(route);
            if (obstruction == null)
            {
                route.obstructedTrackId = "";
                route.nextObstructionRerouteTime = 0f;
                return false;
            }

            var obstructionId = DescribeTrack(obstruction);
            if (route.obstructedTrackId != obstructionId)
            {
                route.obstructedTrackId = obstructionId;
                route.nextObstructionRerouteTime = 0f;
            }

            // Drop the signal reservation immediately. Until a replacement is
            // proven clear, DV Signals must protect the train with a red aspect.
            ReleaseAllocation(route);
            route.waitingForSignal = true;
            route.status = RouteStatus.Pending;
            route.message = "Obstruction ahead on " + obstructionId
                + "; protecting signal held at red while looking for a clear route.";

            if (Time.time < route.nextObstructionRerouteTime)
                return true;
            route.nextObstructionRerouteTime = Time.time + ObstructionRetrySeconds;

            var trainset = FindTrainset(route.trainsetId);
            if (trainset == null || !RouteDestination.Exists(route.destinationTrackId))
                return true;

            // Remove the old route from conflict consideration while testing a
            // replacement. If no alternative exists it is restored below and
            // remains held at red rather than disappearing from the UI.
            routes.Remove(route.id);
            // A live diversion must remain a through movement. Running an
            // expensive reversal search every retry would hitch the host and
            // could instruct a moving train to change ends unexpectedly.
            var replacement = SetRoute(
                trainset, route.stops, allowReversal: false, startStop: route.stopIndex);
            if (replacement.status != RouteStatus.Failed)
            {
                // Deliberately not charged to rerouteCount. That budget is for
                // recovering a train that has come off its road; a diversion
                // round traffic is a road working as intended, and it already
                // has its own ten-second gate and its own restore-on-failure
                // above. Charging it there only leaked: a train held at a red
                // behind an obstruction spent the whole recovery budget standing
                // still, then died on the first real re-lay once it got moving.
                replacement.requestedBy = route.requestedBy;
                replacement.notice = "Rerouted around occupied " + obstructionId + ". ";
                UpdateStatus(replacement);
                Sessions.AddTag("routes");
                return true;
            }

            ReleaseAllocation(replacement);
            routes.Remove(replacement.id);
            routes[route.id] = route;
            Sessions.AddTag("routes");
            return true;
        }

        /// The settings still to be applied, one per junction.
        ///
        /// A road that passes through the same junction twice - which propelling
        /// and run-round moves do - carries two settings for it, usually wanting
        /// opposite branches. Asserting both every tick throws the switch back
        /// and forth continuously. Only the earliest requirement still ahead of
        /// the train is held; the later one takes over once the train has passed
        /// the first.
        private static IEnumerable<JunctionSetting> PendingSettings(TrainRoute route) =>
            SettingsAhead(route, route.allocatedUpTo);

        /// Everything the road still asks of the junctions ahead of the train,
        /// whether or not the allocation reaches that far. What arbitration
        /// looks at, since a route held short of a crossing has to keep seeing
        /// the crossing it is held short of.
        private static IEnumerable<JunctionSetting> FutureSettings(TrainRoute route) =>
            SettingsAhead(route, int.MaxValue);

        private static IEnumerable<JunctionSetting> SettingsAhead(TrainRoute route, int limit)
        {
            var chosen = new Dictionary<Junction, JunctionSetting>();
            foreach (var setting in route.settings)
            {
                if (setting.junction == null || setting.pathIndex < route.progressIndex
                    || setting.pathIndex >= limit)
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

            var trainset = FindTrainset(route.trainsetId);
            if (trainset == null)
                return;

            // The same measure the road was planned against, so the two cannot
            // disagree: the reverser where it is set, motion where it is not. A
            // check reading motion while the planner read the reverser is a
            // check that re-lays every road a train rolls back a yard on.
            var heading = IntendedHeading(trainset);
            if (heading.sqrMagnitude < 0.001f)
                return;   // standing with the reverser centred: nothing to check

            // Where the booked road goes from the leading end of the train, not
            // from the back of it. Progress is measured at the rearmost vehicle,
            // and on a long consist - or anywhere the line curves hard, which on
            // this map is most of it - the stretch of road under the back of the
            // train sits at any angle to the stretch under the front. Reading
            // the whole train's motion against the rear's stretch called an
            // ordinary run wrong-way and re-laid the road out from under it.
            if (route.pathSteps.Count < 2)
                return;
            var index = Mathf.Clamp(
                Mathf.Max(route.frontIndex, route.progressIndex),
                0, route.pathSteps.Count - 2);
            if (route.pathTracks[index] == null || route.pathSteps[index].track == null)
                return;

            // The chord of that component in the direction the road is travelled:
            // from where the train came onto it to where it leaves. A line
            // between two track centres was the wrong measure - on a curve it
            // can sit at any angle to the rails the train is actually on.
            var alongRoute = TrackGraph.EndPosition(route.pathSteps[index])
                - (index > 0
                    ? TrackGraph.EndPosition(route.pathSteps[index - 1])
                    : CentreOf(route.pathTracks[index]));
            alongRoute.y = 0;
            if (alongRoute.sqrMagnitude < 0.01f)
                return;

            // Clearly opposed, not merely off square. Square to the road is what
            // a train rounding a curve reads as, and a threshold of exactly
            // ninety degrees made that enough to re-lay it.
            if (Vector3.Dot(alongRoute.normalized, heading) > -OpposedDot)
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
            Reroute(route, "the train was travelling against its road");
        }

        /// How long a train must be heading against its road before it is
        /// re-laid, so a shunt or a rollback does not trigger one.
        public const float WrongWayGraceSeconds = 2f;

        /// How far past square a train has to be pointed before it counts as
        /// running against its road: about a hundred and five degrees. Anything
        /// tighter reads a curve as a reversal.
        public const float OpposedDot = 0.25f;

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
                // Only the host advances and reallocates the shared route table.
                if (!RouteNetwork.IsAuthority)
                    continue;
                foreach (var route in routes.Values.ToList())
                {
                    if (route.status == RouteStatus.Failed || route.status == RouteStatus.Cleared)
                        continue;
                    if (HandleLiveObstruction(route))
                        continue;
                    if (!route.allocationApplied)
                    {
                        TryActivate(route);
                        if (!route.allocationApplied)
                            continue;
                        Sessions.AddTag("routes");
                    }
                    // Refresh progress on every periodic pass, including the
                    // outbound half of a reversing move. Visual progress uses
                    // the leading end; safe release of switches continues to
                    // use the rearmost vehicle inside UpdateProgress.
                    UpdateProgress(route);
                    if (!routes.ContainsKey(route.id))
                        continue;   // arrived or rerouted during the update
                    // A train working towards a reversal is travelling the way
                    // it was told to; the direction check would read the coming
                    // change of ends as running the wrong way and re-lay the
                    // road out from under it.
                    if (route.hasReverseLeg && !route.onReverseLeg)
                    {
                        if (ConsistClearOfReversal(route))
                        {
                            BeginReverseLeg(route);
                            Sessions.AddTag("routes");
                            continue;
                        }
                        UpdateStatus(route);
                        continue;
                    }

                    VerifyDirection(route);
                    if (!routes.ContainsKey(route.id))
                        continue;   // re-laid during verification
                    // Held short of another train's road: take whatever it has
                    // released since the last tick.
                    if (route.allocationApplied && route.allocatedUpTo != int.MaxValue)
                        ExtendAllocation(route);
                    Revalidate(route);
                    if (route.pending.Count == 0)
                    {
                        if (!TryReserveNextSignal(route))
                        {
                            ReleaseAllocation(route);
                            route.waitingForSignal = true;
                            UpdateStatus(route);
                            Sessions.AddTag("routes");
                            continue;
                        }
                        UpdateStatus(route);
                        continue;
                    }
                    // What the road asks of each junction ahead - not the first
                    // setting in the list carrying that junction. A road that
                    // passes one twice, as a run-round or a shunt does, holds two
                    // settings for it wanting opposite branches, and the earlier
                    // one is behind the train by the time the later one matters.
                    // Taking the first threw the switch back under the train.
                    var wanted = new Dictionary<Junction, JunctionSetting>();
                    foreach (var setting in PendingSettings(route))
                        wanted[setting.junction] = setting;

                    var stillPending = new List<Junction>();
                    foreach (var junction in route.pending)
                    {
                        if (!wanted.TryGetValue(junction, out var setting))
                            continue;
                        if (Occupancy.IsJunctionClear(junction))
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

                PublishRoutes();
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
            new JProperty("stops", new JArray(route.stops)),
            new JProperty("stopIndex", route.stopIndex),
            new JProperty("status", route.status.ToString()),
            new JProperty("message", route.message),
            new JProperty("priority", route.priority),
            new JProperty("pendingJunctions", route.pending.Count),
            new JProperty("requiresReverse", route.requiresReverse),
            new JProperty("hasReverseLeg", route.hasReverseLeg),
            new JProperty("onReverseLeg", route.onReverseLeg),
            new JProperty("reversalTrack", route.reversalTrackId),
            new JProperty("reversalSignal", route.reversalSignalName),
            new JProperty("heldShortOf", route.heldShortOf),
            new JProperty("requestedBy", route.requestedBy),
            new JProperty("sequence", route.sequence),
            new JProperty("colorIndex", route.colorIndex),
            new JProperty("leftDivergences", route.leftDivergences),
            new JProperty("rightDivergences", route.rightDivergences),
            new JProperty("distanceMeters", System.Math.Round(route.distanceMeters, 1)),
            new JProperty("divergenceDetail", route.divergenceDetail),
            // Drawing follows the leading end so only road still ahead remains
            // highlighted. progressIndex deliberately remains the rear for
            // reservations and junction release safety.
            new JProperty("tracks", new JArray(route.trackIds.Skip(route.frontIndex))),
            new JProperty("passedTracks", route.progressIndex));

        public static string AllRoutesJson() =>
            AllRoutesToken().ToString(Newtonsoft.Json.Formatting.None);

        public static JArray AllRoutesToken()
        {
            if (!RouteNetwork.IsRemoteClient)
                return new JArray(routes.Values.Select(ToJson));
            try
            {
                return JArray.Parse(RouteNetwork.MirroredRoutesJson);
            }
            catch
            {
                return new JArray();
            }
        }

        public static void PublishRoutes()
        {
            if (!RouteNetwork.Present || !RouteNetwork.IsAuthority)
                return;
            var current = AllRoutesJson();
            if (current == lastPublishedJson)
                return;
            lastPublishedJson = current;
            RouteNetwork.BroadcastRoutes(current);
        }

        private static string lastPublishedJson = "";
    }
}
