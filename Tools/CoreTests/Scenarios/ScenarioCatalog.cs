using System.Collections.Generic;
using Lifecycle.Core;
using Lifecycle.CoreTests.Fixtures;
using Lifecycle.CoreTests.Scenarios;

namespace Lifecycle.CoreTests.Scenarios
{
    /// <summary>
    /// Scenarios written as replayable documents. Each one models a real race the fixture must be
    /// able to schedule, not just a happy path.
    /// </summary>
    internal static class ScenarioCatalog
    {
        /// <summary>A completion that lands before the borrower returns; both settle normally.</summary>
        public static ScenarioDocument CompleteThenReturn() =>
            new ScenarioDocument("complete, then return",
                ScenarioOps.Request(EntityKind.Prop, "pickup", "a"),
                ScenarioOps.Complete("a"),
                ScenarioOps.Return("a"));

        /// <summary>Return while the preparation is still outstanding; the request must settle.</summary>
        public static ScenarioDocument ReturnWhilePreparing() =>
            new ScenarioDocument("return while preparing",
                ScenarioOps.Request(EntityKind.Machine, "machine", "m"),
                ScenarioOps.Return("m"));

        /// <summary>Stale completion after re-lease; the fence must refuse it.</summary>
        public static ScenarioDocument ReuseUnderStaleCompletion() =>
            new ScenarioDocument("reuse under stale completion",
                ScenarioOps.Request(EntityKind.Prop, "pickup", "first"),
                ScenarioOps.Return("first"),
                ScenarioOps.Request(EntityKind.Prop, "pickup", "second"),
                ScenarioOps.Complete("first", message: "late load from the first borrower"),
                ScenarioOps.Complete("second"));

        /// <summary>Preparation fails after the borrower returned; no lease may survive.</summary>
        public static ScenarioDocument FailureAfterReturn() =>
            new ScenarioDocument("failure after return",
                ScenarioOps.Request(EntityKind.Prop, "pickup", "a"),
                ScenarioOps.Return("a"),
                ScenarioOps.Complete("a", CompletionMode.Fail, "asset bundle missing"));

        /// <summary>A scope closes while two loads are in flight under QueueUntilNextScope.</summary>
        public static ScenarioDocument CloseWithWorkInFlight() =>
            new ScenarioDocument("close with work in flight",
                ScenarioOps.Request(EntityKind.Prop, "p1", "p1"),
                ScenarioOps.Request(EntityKind.Prop, "p2", "p2"),
                ScenarioOps.Transition("Arena", ClosePolicy.QueueUntilNextScope),
                ScenarioOps.Activate("Arena"),
                ScenarioOps.EndTransition(),
                ScenarioOps.Complete("p1"),
                ScenarioOps.Complete("p2"));

        /// <summary>Two requests for the same entity; completion order changes which slot lands which.</summary>
        public static ScenarioDocument TwoRequestsSameEntity() =>
            new ScenarioDocument("two requests, completion order varies",
                ScenarioOps.Request(EntityKind.Prop, "pickup", "a"),
                ScenarioOps.Request(EntityKind.Prop, "pickup", "b"),
                ScenarioOps.Complete("b"),
                ScenarioOps.Complete("a"));

        /// <summary>Interleave return and completion; order decides whether the lease or the ticket settles first.</summary>
        public static ScenarioDocument ReturnAndCompletionRace() =>
            new ScenarioDocument("return/completion race",
                ScenarioOps.Request(EntityKind.Prop, "pickup", "a"),
                ScenarioOps.Return("a"),
                ScenarioOps.Complete("a"));

        public static IReadOnlyList<ScenarioDocument> All => new[]
        {
            CompleteThenReturn(),
            ReturnWhilePreparing(),
            ReuseUnderStaleCompletion(),
            FailureAfterReturn(),
            CloseWithWorkInFlight(),
            TwoRequestsSameEntity(),
            ReturnAndCompletionRace()
        };
    }
}
