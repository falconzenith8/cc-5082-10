using System.Collections.Generic;
using Lifecycle.Core;
using UnityEngine;

namespace Lifecycle.Demo
{
    /// <summary>
    /// Operator surface for the object-reuse sample. It borrows and returns through the
    /// <see cref="LifecycleManager"/> and renders the manager's <see cref="ReuseView"/>, so the
    /// actors visible on screen and the async state of each live lease are the same data the
    /// tests assert on. Async anomalies are called out inline next to the entity that caused them.
    /// </summary>
    public sealed class ObjectReusePanel : MonoBehaviour
    {
        private LifecycleManager _manager;
        private bool _visible = true;
        private GUIStyle _style;
        private GUIStyle _header;
        private GUIStyle _anomalyStyle;
        private readonly List<SpawnTicket> _tickets = new List<SpawnTicket>();
        private int _nextOrder;
        private string _lastAction = "-";

        public void Refresh(LifecycleManager manager, bool visible)
        {
            _manager = manager;
            _visible = visible;
        }

        /// <summary>Total manual borrows issued from this panel.</summary>
        public int BorrowCount { get; private set; }

        /// <summary>Number of live entities currently visible in the reuse view.</summary>
        public int ActiveEntityCount { get; private set; }

        /// <summary>Number of async anomalies currently reported.</summary>
        public int AsyncAnomalyCount { get; private set; }

        public void BorrowProp()
        {
            Borrow(EntityKind.Prop, "reuse-prop");
        }

        public void BorrowMachine()
        {
            Borrow(EntityKind.Machine, "reuse-machine");
        }

        private void Borrow(EntityKind kind, string label)
        {
            if (_manager == null) return;
            var id = label + "-" + (++_nextOrder);
            var ticket = _manager.Borrow(kind, id, "manual reuse");
            _tickets.Add(ticket);
            BorrowCount++;
            _lastAction = "borrowed " + id + " -> " + ticket.Status;
        }

        public void ReturnAll()
        {
            if (_manager == null) return;
            for (var i = _tickets.Count - 1; i >= 0; i--)
            {
                var ticket = _tickets[i];
                if (!ticket.IsSettled || ticket.Status == RequestStatus.Completed || ticket.Status == RequestStatus.Granted)
                {
                    _manager.Return(ticket);
                }
            }

            _lastAction = "returned all outstanding";
        }

        private void Update()
        {
            if (_manager == null || !_visible) return;
            var view = _manager.CreateReuseView();
            ActiveEntityCount = view.ActiveEntities.Count;
            AsyncAnomalyCount = view.AsyncAnomalies.Count;
        }

        private void OnGUI()
        {
            if (_manager == null || !_visible) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 11, richText = true };
                _style.normal.textColor = Color.white;
                _header = new GUIStyle(_style) { fontStyle = FontStyle.Bold, fontSize = 12 };
                _anomalyStyle = new GUIStyle(_style);
                _anomalyStyle.normal.textColor = new Color(1f, 0.45f, 0.45f);
            }

            var rect = new Rect(10f, 350f, 560f, 360f);
            GUI.Box(rect, GUIContent.none);
            GUILayout.BeginArea(new Rect(rect.x + 10f, rect.y + 8f, rect.width - 20f, rect.height - 16f));

            var view = _manager.CreateReuseView();
            GUILayout.Label("Object Reuse (manual borrow / return)", _header);
            GUILayout.Label("scope=" + (view.ScopeId ?? "-") + "  leases=" + view.LeasesHeld +
                            "  subs=" + view.ActiveSubscriptions + "  pendingWork=" + view.PendingWorkItems +
                            "  stale=" + view.StaleCallbacks, _style);
            GUILayout.Label("key O toggles this panel; last action: " + _lastAction, _style);
            GUILayout.Space(3f);
            GUILayout.Label("Active entities (" + view.ActiveEntities.Count + "):", _style);
            for (var i = 0; i < view.ActiveEntities.Count; i++)
            {
                var entity = view.ActiveEntities[i];
                var line = "  " + entity.EntityId + "  " + entity.PoolId +
                           "  gen=" + entity.LeaseGeneration + (entity.SlotGeneration != entity.LeaseGeneration
                               ? "!slotGen=" + entity.SlotGeneration
                               : string.Empty) +
                           "  state=" + entity.State +
                           "  async=" + (entity.ActivationComplete ? "complete" : "pending");
                GUILayout.Label(line, entity.Anomaly ? _anomalyStyle : _style);
                if (entity.Anomaly)
                {
                    GUILayout.Label("     !ASYNC " + entity.AnomalyReason, _anomalyStyle);
                }
            }

            if (view.AsyncAnomalies.Count > 0)
            {
                GUILayout.Space(3f);
                GUILayout.Label("Async anomalies (" + view.AsyncAnomalies.Count + "):", _anomalyStyle);
                for (var i = 0; i < view.AsyncAnomalies.Count; i++)
                {
                    GUILayout.Label("  " + view.AsyncAnomalies[i], _anomalyStyle);
                }
            }
            else
            {
                GUILayout.Label("  (none)", _style);
            }

            GUILayout.Space(4f);
            if (GUILayout.Button("Borrow prop")) BorrowProp();
            if (GUILayout.Button("Borrow machine")) BorrowMachine();
            if (GUILayout.Button("Return all")) ReturnAll();
            GUILayout.EndArea();
        }

        /// <summary>Test hook: number of tickets the panel believes it owns.</summary>
        public int TrackedTickets => _tickets.Count;
    }
}
