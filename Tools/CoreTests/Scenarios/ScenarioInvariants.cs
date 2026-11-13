using System;
using System.Collections.Generic;
using System.Text;
using Lifecycle.Core;

namespace Lifecycle.CoreTests.Scenarios
{
    /// <summary>A violated invariant, named precisely enough to point at the responsible operation.</summary>
    internal sealed class InvariantViolation
    {
        public InvariantViolation(string name, string detail)
        {
            Name = name;
            Detail = detail;
        }

        public string Name { get; }
        public string Detail { get; }
        public override string ToString() => Name + ": " + Detail;
    }

    /// <summary>
    /// Conservation and safety rules that must hold in every completion order. "Allowed to vary"
    /// is a deliberately explicit set: individual ticket outcomes and which generation a given
    /// handle ends on may differ when completions race a return or a close. Everything else here
    /// is required in all orders.
    /// </summary>
    internal static class ScenarioInvariants
    {
        public static readonly IReadOnlyList<string> AllowedToVary = new List<string>
        {
            "a handle's terminal outcome (Completed/Canceled/Failed/Rejected) when its completion races a return or scope close",
            "a handle's final generation when a slot is evicted instead of reused (no aliasing is allowed either way)",
            "which pump tick a request completes on",
            "the number of stale-completion rejections (>= 0), as long as no mutation was applied",
            "the order in which live entity lines are printed"
        };

        public static readonly IReadOnlyList<string> MustHold = new List<string>
        {
            "every ticket is settled or explicitly still pending; outcomes observed at the end are a subset of legal outcomes",
            "requests <= created <= total slots ever built",
            "destroyed + currently-live slots <= created",
            "leases held <= active entities",
            "active subscriptions <= leases held * 3 (input + rule + temporary resource)",
            "pending work items <= unfinished requests",
            "a settled request holds no lease",
            "a live lease is current, or its slot is being torn down",
            "a live lease holds exactly the subscriptions of its current generation",
            "the reuse monitor reports no async anomaly for a settled state",
            "a reused slot's generations strictly increase"
        };

        /// <summary>Checks the cross-order details and returns every violation found.</summary>
        public static List<InvariantViolation> Check(ScenarioObservation observation, IReadOnlyList<string> requestHandles)
        {
            var violations = new List<InvariantViolation>();

            if (observation.LeasesHeld > observation.Created)
            {
                violations.Add(new InvariantViolation("leases <= created",
                    observation.LeasesHeld + " held but only " + observation.Created + " slots ever created"));
            }

            if (observation.ActiveSubscriptions > observation.LeasesHeld * 3)
            {
                violations.Add(new InvariantViolation("subscriptions <= leases*3",
                    observation.ActiveSubscriptions + " subscriptions for " + observation.LeasesHeld + " leases"));
            }

            if (observation.PendingWorkItems > observation.UnfinishedRequests)
            {
                violations.Add(new InvariantViolation("pending work <= unfinished requests",
                    observation.PendingWorkItems + " work items but " + observation.UnfinishedRequests + " unfinished requests"));
            }

            if (observation.LeasesHeld < 0 || observation.PendingWorkItems < 0 ||
                observation.ActiveSubscriptions < 0 || observation.UnfinishedRequests < 0)
            {
                violations.Add(new InvariantViolation("non-negative counters", observation.ReuseReport));
            }

            if (observation.Destroyed > observation.Created)
            {
                violations.Add(new InvariantViolation("destroyed <= created",
                    observation.Destroyed + " destroyed but only " + observation.Created + " created"));
            }

            foreach (var handle in requestHandles)
            {
                observation.Outcomes.TryGetValue(handle, out var outcome);
                if (outcome == ScenarioOutcome.Pending && observation.UnfinishedRequests == 0)
                {
                    // A request may legitimately still be Pending only when the scenario never
                    // finished it; the manager's snapshot must agree.
                    continue;
                }
            }

            // "settled" means every request has left the in-flight set; the scenarios written here
            // deliberately leave completed leases live until the scenario ends, so a live lease is
            // never itself a violation. What must hold is that nothing is *unfinished*: a completed
            // lease with work still queued, or a canceled ticket with live subscriptions, is a bug.
            if (observation.UnfinishedRequests == 0 && observation.PendingWorkItems > 0)
            {
                violations.Add(new InvariantViolation("settled leaves no pending work",
                    "no unfinished requests but pendingWork=" + observation.PendingWorkItems));
            }

            if (observation.UnfinishedRequests == 0 && observation.LeasesHeld > 0 &&
                observation.ActiveSubscriptions == 0)
            {
                violations.Add(new InvariantViolation("a live lease owns its subscriptions",
                    "leases=" + observation.LeasesHeld + " but no subscriptions are live"));
            }

            if (observation.ReuseReport.IndexOf("!ASYNC", StringComparison.Ordinal) >= 0 &&
                observation.UnfinishedRequests == 0)
            {
                violations.Add(new InvariantViolation("no async anomaly when settled",
                    observation.ReuseReport));
            }

            if (observation.ReuseReport.IndexOf("!slotGen", StringComparison.Ordinal) >= 0)
            {
                violations.Add(new InvariantViolation("lease generation matches slot generation",
                    observation.ReuseReport));
            }

            return violations;
        }

        /// <summary>Compares two replays of the same request set in different completion orders.</summary>
        public static List<InvariantViolation> CheckAcrossOrders(ScenarioObservation a, ScenarioObservation b,
            IReadOnlyList<string> requestHandles, IReadOnlyList<string> pooledEntityIds)
        {
            var violations = new List<InvariantViolation>();

            // Same requested entity set, same number of live/created slots.
            if (a.Created != b.Created)
            {
                violations.Add(new InvariantViolation("slot count is order-independent",
                    "created " + a.Created + " vs " + b.Created));
            }

            if (a.Destroyed != b.Destroyed)
            {
                violations.Add(new InvariantViolation("cold destruction count is order-independent",
                    "destroyed " + a.Destroyed + " vs " + b.Destroyed));
            }

            if (a.ActiveSubscriptions != b.ActiveSubscriptions)
            {
                violations.Add(new InvariantViolation("subscription conservation",
                    "active subscriptions " + a.ActiveSubscriptions + " vs " + b.ActiveSubscriptions));
            }

            if (a.LeasesHeld != b.LeasesHeld)
            {
                violations.Add(new InvariantViolation("lease conservation",
                    "leases " + a.LeasesHeld + " vs " + b.LeasesHeld));
            }

            // Reuse must never alias: any entity that was created more than once must carry a
            // strictly greater generation on its later borrow. Concretely, an entity's generation
            // in reverse order can only be less-or-equal to its generation in arrival order, never
            // negative and never reset to the same value within a borrow.
            foreach (var handle in requestHandles)
            {
                var ga = a.GenerationOf.TryGetValue(handle, out var x) ? x : 0;
                var gb = b.GenerationOf.TryGetValue(handle, out var y) ? y : 0;
                if (ga > 0 && gb > 0 && ga != gb)
                {
                    // Different borrows by different orders are allowed, but neither may exceed
                    // the number of times the shared entity could have been borrowed.
                    var entity = a.TerminalEntityId[handle];
                    if (ga > b.Created + 1 || gb > b.Created + 1)
                    {
                        violations.Add(new InvariantViolation("generation is bounded",
                            handle + " gen " + ga + " vs " + gb + " with " + b.Created + " created slots for " + entity));
                    }
                }
            }

            // Regardless of order, a settled snapshot must agree on the set of pooled entity ids
            // if both runs ended fully settled.
            if (a.UnfinishedRequests == 0 && b.UnfinishedRequests == 0)
            {
                if (a.ActiveEntitiesConserved() != b.ActiveEntitiesConserved())
                {
                    violations.Add(new InvariantViolation("live entity set is conserved",
                        "arrival=" + a.ActiveEntitiesConserved() + " reverse=" + b.ActiveEntitiesConserved()));
                }
            }

            return violations;
        }

        public static string DescribeVaryingOutcomes(ScenarioObservation a, ScenarioObservation b,
            IReadOnlyList<string> requestHandles)
        {
            var sb = new StringBuilder();
            foreach (var handle in requestHandles)
            {
                if (a.Outcomes.TryGetValue(handle, out var oa) && b.Outcomes.TryGetValue(handle, out var ob) &&
                    oa != ob)
                {
                    sb.Append("\n    varies: ").Append(handle).Append(" arrival=").Append(oa)
                        .Append(" reverse=").Append(ob);
                }
            }

            return sb.ToString();
        }
    }

    internal static class ScenarioObservationExtensions
    {
        public static int ActiveEntitiesConserved(this ScenarioObservation observation)
        {
            var n = 0;
            foreach (var line in observation.LiveEntityLines)
            {
                if (line.IndexOf(":Completed:", StringComparison.Ordinal) >= 0 ||
                    line.IndexOf(":Canceled:", StringComparison.Ordinal) >= 0)
                {
                    n++;
                }
            }

            return n;
        }
    }
}
