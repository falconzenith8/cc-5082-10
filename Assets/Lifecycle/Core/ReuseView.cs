using System;
using System.Collections.Generic;
using System.Text;

namespace Lifecycle.Core
{
    /// <summary>
    /// One live lease as seen by the object-reuse monitor: the requested entity, the slot's
    /// generation and activation state, and whether its async preparation is still outstanding.
    /// </summary>
    public sealed class ReuseEntityView
    {
        public string EntityId;
        public string PoolId;
        public int LeaseGeneration;
        public int SlotGeneration;
        public string State;
        public bool ActivationComplete;
        public bool PreparationPending;
        public bool HasPendingWork;
        public long AgeTicks;
        public bool Anomaly;
        public string AnomalyReason;
    }

    /// <summary>
    /// Snapshot used by the demo HUD and the object-reuse CLI. It intentionally lists every live
    /// entity (not just counts) and singles out async-state anomalies, so reuse problems are
    /// visible next to the entities that caused them.
    /// </summary>
    public sealed class ReuseView
    {
        public string ScopeId;
        public bool InTransition;
        public long Tick;
        public int StaleCallbacks;
        public int LeasesHeld;
        public int PendingWorkItems;
        public int ActiveSubscriptions;
        public readonly List<ReuseEntityView> ActiveEntities = new List<ReuseEntityView>();
        public readonly List<string> AsyncAnomalies = new List<string>();

        public bool HasAnomalies => AsyncAnomalies.Count > 0;

        public string ToReport()
        {
            var sb = new StringBuilder();
            sb.Append("reuse scope=").Append(ScopeId ?? "-")
                .Append(" transition=").Append(InTransition)
                .Append(" tick=").Append(Tick)
                .Append(" leases=").Append(LeasesHeld)
                .Append(" subscriptions=").Append(ActiveSubscriptions)
                .Append(" pendingWork=").Append(PendingWorkItems)
                .Append(" stale=").Append(StaleCallbacks)
                .Append(" anomalies=").Append(AsyncAnomalies.Count);
            for (var i = 0; i < ActiveEntities.Count; i++)
            {
                var e = ActiveEntities[i];
                sb.Append('\n').Append("  active ").Append(e.EntityId)
                    .Append(" pool=").Append(e.PoolId)
                    .Append(" gen=").Append(e.LeaseGeneration);
                if (e.SlotGeneration != e.LeaseGeneration)
                {
                    sb.Append("!slotGen=").Append(e.SlotGeneration);
                }

                sb.Append(" state=").Append(e.State)
                    .Append(" async=").Append(e.ActivationComplete ? "complete" : "pending")
                    .Append(" age=").Append(e.AgeTicks);
                if (e.Anomaly)
                {
                    sb.Append(" !ASYNC:").Append(e.AnomalyReason);
                }
            }

            for (var i = 0; i < AsyncAnomalies.Count; i++)
            {
                sb.Append('\n').Append("  !ASYNC anomaly: ").Append(AsyncAnomalies[i]);
            }

            return sb.ToString();
        }
    }
}
