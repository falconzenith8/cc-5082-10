using System;
using System.Collections.Generic;
using System.Text;
using Lifecycle.Core;
using Lifecycle.CoreTests.Fixtures;
using Lifecycle.CoreTests.Scenarios;

namespace Lifecycle.CoreTests.Faults
{
    /// <summary>
    /// Probe that makes exactly one generation validation load-bearing. A completion scheduled for
    /// the first borrow resolves only after that borrow was returned and the slot was re-leased.
    ///
    /// With the fence enforced the completion is refused (slot still Preparing, new borrow stays
    /// pending). With the fence removed the old generation's load completes the new borrow, which
    /// the probe reports as a premature completion plus a visible async anomaly.
    ///
    /// The "removal" is <see cref="ScenarioReplay.EnforceGenerationFence"/> = false, which is the
    /// single controlled switch between the two runs.
    /// </summary>
    internal sealed class GenerationFenceProbe
    {
        public sealed class Result
        {
            public bool FenceEnforced;
            public bool PrematureCompletion;
            public bool StaleCompletionRejected;
            public bool ReuseAnomalyVisible;
            public string FirstOutcome;
            public string SecondOutcome;
            public string Script;

            public bool ExposedError => !FenceEnforced && PrematureCompletion;
            public bool RestoredAndClean => FenceEnforced && !PrematureCompletion && StaleCompletionRejected;
            public bool Passed => ExposedError || RestoredAndClean;
        }

        public static string ScriptText =>
            "# generation fence: stale completion vs re-lease\n" +
            "00  request Prop:pickup as 'borrow1'\n" +
            "01  return 'borrow1' (while its load is still pending)\n" +
            "02  request Prop:pickup as 'borrow2' (same slot, generation 2)\n" +
            "03  complete 'borrow1' (its old-generation load resolves late)\n" +
            "04  pump";

        public static Result Run(bool enforceFence)
        {
            var result = new Result { FenceEnforced = enforceFence, Script = ScriptText };
            var replay = new ScenarioReplay { EnforceGenerationFence = enforceFence };

            try
            {
                replay.Apply(ScenarioOps.Request(EntityKind.Prop, "pickup", "borrow1"));
                var firstLoad = replay.LoadFor("borrow1");
                replay.Apply(ScenarioOps.Return("borrow1"));
                replay.Apply(ScenarioOps.Request(EntityKind.Prop, "pickup", "borrow2"));
                var second = replay.TicketFor("borrow2");

                // The first borrow's load resolves now, against a slot now owned by generation 2.
                replay.CompleteLoad(firstLoad, CompletionMode.Succeed, "late load from borrow 1");
                replay.Apply(ScenarioOps.Pump());

                var view = replay.Manager.CreateReuseView();
                result.FirstOutcome = replay.TicketFor("borrow1").Status.ToString();
                result.SecondOutcome = second.Status.ToString();
                result.PrematureCompletion = second.Status == RequestStatus.Completed;
                result.StaleCompletionRejected = TraceContains(replay.Source.Created, "rejected-stale");
                result.ReuseAnomalyVisible = view.HasAnomalies;
                if (result.PrematureCompletion && view.PendingWorkItems == 0)
                {
                    // Completed via the wrong generation leaves no work trace; still flagged.
                    result.ReuseAnomalyVisible = true;
                }
            }
            catch (Exception ex)
            {
                result.SecondOutcome = "probe threw: " + ex.GetType().Name + ": " + ex.Message;
            }

            return result;
        }

        private static bool TraceContains(IEnumerable<AsyncSlot> slots, string fragment)
        {
            foreach (var slot in slots)
            {
                foreach (var line in slot.Trace)
                {
                    if (line.IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
                }
            }

            return false;
        }

        public static string Describe(Result result)
        {
            var sb = new StringBuilder();
            sb.Append(result.FenceEnforced ? "fence=ENFORCED" : "fence=REMOVED")
                .Append(" first=").Append(result.FirstOutcome)
                .Append(" second=").Append(result.SecondOutcome)
                .Append(" premature=").Append(result.PrematureCompletion)
                .Append(" staleRejected=").Append(result.StaleCompletionRejected)
                .Append(" anomaly=").Append(result.ReuseAnomalyVisible);
            if (result.ExposedError) sb.Append(" => ERROR EXPOSED (expected)");
            if (result.RestoredAndClean) sb.Append(" => RESTORED (clean)");
            if (!result.Passed) sb.Append(" => UNEXPECTED");
            return sb.ToString();
        }
    }
}
