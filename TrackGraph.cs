using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    /// A* pathfinding over the RailTrack graph.
    ///
    /// A junction is a turnout: arriving along the stem (inBranch) you may leave
    /// by any outBranch; arriving along an outBranch you may only reach the stem.
    /// Traversal therefore has to carry direction, since which junction you leave
    /// by depends on which end you entered.
    public static class TrackGraph
    {
        /// A track together with the end it was entered from. Entering via the
        /// "in" end means leaving via the "out" end, and vice versa.
        public readonly struct Step : IEquatable<Step>
        {
            public readonly RailTrack track;
            public readonly bool enteredViaIn;

            public Step(RailTrack track, bool enteredViaIn)
            {
                this.track = track;
                this.enteredViaIn = enteredViaIn;
            }

            public Junction? ExitJunction => enteredViaIn ? track.outJunction : track.inJunction;


            public bool Equals(Step other) => track == other.track && enteredViaIn == other.enteredViaIn;
            public override bool Equals(object obj) => obj is Step step && Equals(step);
            public override int GetHashCode() =>
                (track == null ? 0 : track.GetInstanceID()) * 2 + (enteredViaIn ? 1 : 0);
        }

        public static float TrackLength(RailTrack track)
        {
            var curve = track == null ? null : track.curve;
            return curve == null ? 1f : Mathf.Max(1f, curve.length);
        }

        private static Vector3 EndPosition(Step step)
        {
            var junction = step.ExitJunction;
            if (junction != null)
                return junction.position;
            var curve = step.track.curve;
            if (curve == null || curve.pointCount == 0)
                return step.track.transform.position;
            return step.enteredViaIn ? curve.Last().position : curve[0].position;
        }

        /// Successors of a step: what can be reached off the far end of this
        /// track.
        ///
        /// Delegates to the game's own GetAllOutBranches/GetAllInBranches, which
        /// resolve a junction fan when there is a junction and fall back to the
        /// directly connected neighbour when there is not. Doing this by hand
        /// means restating the convention that a track presents itself as
        /// first:true at its in end and first:false at its out end, and getting
        /// that backwards silently yields no successors at all.
        public static IEnumerable<Step> Successors(Step step)
        {
            if (step.track == null)
                yield break;

            // Entering via the in end means leaving by the out end.
            var connected = step.enteredViaIn ? step.track.outIsConnected : step.track.inIsConnected;
            if (!connected)
                yield break;

            var branches = step.enteredViaIn
                ? step.track.GetAllOutBranches()
                : step.track.GetAllInBranches();
            if (branches == null)
                yield break;

            foreach (var candidate in branches)
            {
                if (candidate == null || candidate.track == null || candidate.track == step.track)
                    continue;
                // A branch's `first` says which end of that track meets this
                // point, which is the end we enter it by.
                yield return new Step(candidate.track, candidate.first);
            }
        }

        /// `extraCost` adds a penalty to a transition, used to express left-hand
        /// running. It is a cost rather than a hard rule, so an equal-length left
        /// road always wins while a much shorter right-hand route stays reachable.
        public static List<Step>? FindPath(
            Step start,
            HashSet<RailTrack> goals,
            Func<Step, Step, float>? extraCost = null,
            Func<Step, bool>? isBlocked = null,
            int maxExpansions = 200000)
        {
            if (goals.Contains(start.track))
                return new List<Step> { start };

            var goalPositions = goals
                .Where(track => track != null && track.curve != null && track.curve.pointCount > 0)
                .Select(track => track.curve[0].position)
                .ToList();

            float Heuristic(Step step)
            {
                if (goalPositions.Count == 0)
                    return 0f;
                var position = EndPosition(step);
                var best = float.MaxValue;
                foreach (var goal in goalPositions)
                    best = Mathf.Min(best, Vector3.Distance(position, goal));
                return best;
            }

            var cameFrom = new Dictionary<Step, Step>();
            var costSoFar = new Dictionary<Step, float> { [start] = 0f };
            var open = new SortedSet<(float priority, int tie, Step step)>(
                Comparer<(float priority, int tie, Step step)>.Create((a, b) =>
                    a.priority != b.priority ? a.priority.CompareTo(b.priority) : a.tie.CompareTo(b.tie)));
            var tieBreaker = 0;
            open.Add((Heuristic(start), tieBreaker++, start));

            var expansions = 0;
            while (open.Count > 0 && expansions++ < maxExpansions)
            {
                var current = open.Min;
                open.Remove(current);
                var step = current.step;

                if (goals.Contains(step.track))
                    return Reconstruct(cameFrom, start, step);

                foreach (var next in Successors(step))
                {
                    if (isBlocked != null && isBlocked(next))
                        continue;
                    var transitionCost = TrackLength(next.track)
                        + (extraCost == null ? 0f : extraCost(step, next));
                    var newCost = costSoFar[step] + transitionCost;
                    if (costSoFar.TryGetValue(next, out var existing) && newCost >= existing)
                        continue;
                    costSoFar[next] = newCost;
                    cameFrom[next] = step;
                    open.Add((newCost + Heuristic(next), tieBreaker++, next));
                }
            }
            return null;
        }

        /// How many distinct track/direction states are reachable from a step.
        /// A tiny number means graph traversal is broken rather than the
        /// destination being genuinely unreachable, which is the useful thing to
        /// know when a route fails.
        public static int CountReachable(Step start, int limit = 20000)
        {
            var seen = new HashSet<Step> { start };
            var queue = new Queue<Step>();
            queue.Enqueue(start);
            while (queue.Count > 0 && seen.Count < limit)
            {
                foreach (var next in Successors(queue.Dequeue()))
                {
                    if (seen.Add(next))
                        queue.Enqueue(next);
                }
            }
            return seen.Count;
        }

        private static List<Step> Reconstruct(Dictionary<Step, Step> cameFrom, Step start, Step goal)
        {
            var path = new List<Step> { goal };
            var current = goal;
            while (!current.Equals(start) && cameFrom.TryGetValue(current, out var previous))
            {
                current = previous;
                path.Add(current);
            }
            path.Reverse();
            return path;
        }
    }
}
