using System.Collections.Generic;
using Lifecycle.Core;
using UnityEngine;

namespace Lifecycle.Demo
{
    /// <summary>
    /// IMGUI HUD: live scope, request, lease, and subscription counters plus the event feed.
    /// Counters read the manager's snapshot, never maintain their own bookkeeping.
    /// </summary>
    public sealed class DemoHud : MonoBehaviour
    {
        private DemoSceneBootstrap _bootstrap;
        private GUIStyle _style;
        private GUIStyle _headerStyle;
        private GUIStyle _reuseAnomalyStyle;
        private readonly List<string> _rendered = new List<string>();

        public void Bind(DemoSceneBootstrap bootstrap) => _bootstrap = bootstrap;

        public void Refresh(LifecycleManager manager, DemoPresenter presenter, RecordingLifecycleReporter reporter,
            string sceneName, bool visible, IReadOnlyList<string> demoLog)
        {
            _visible = visible;
            if (manager == null) return;

            var snapshot = manager.CreateSnapshot();
            _snapshot = snapshot;
            _manager = manager;
            _sceneName = sceneName;
            _issues = reporter != null ? reporter.Count : 0;
            _presenter = presenter;
            _demoLog = demoLog;
        }

        private LifecycleSnapshot _snapshot;
        private LifecycleManager _manager;
        private string _sceneName = "-";
        private int _issues;
        private DemoPresenter _presenter;
        private bool _visible = true;
        private IReadOnlyList<string> _demoLog;

        private void OnGUI()
        {
            if (!_visible || _snapshot == null) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true };
                _style.normal.textColor = Color.white;
                _headerStyle = new GUIStyle(_style) { fontStyle = FontStyle.Bold, fontSize = 13 };
                _reuseAnomalyStyle = new GUIStyle(_style);
                _reuseAnomalyStyle.normal.textColor = new Color(1f, 0.45f, 0.45f);
            }

            var width = 430f;
            var height = 330f;
            var rect = new Rect(10f, 10f, width, height);
            GUI.Box(rect, GUIContent.none);

            GUILayout.BeginArea(new Rect(rect.x + 10f, rect.y + 8f, rect.width - 20f, rect.height - 16f));
            GUILayout.Label("Lifecycle Demo - scene: " + _sceneName, _headerStyle);
            GUILayout.Label("scope=" + (_snapshot.CurrentScopeId ?? "-") +
                            "  activeScopes=" + _snapshot.ActiveScopes +
                            "  leases=" + _snapshot.LeasesHeld, _style);
            GUILayout.Label("requests: pending=" + _snapshot.PendingRequests +
                            " preparing=" + _snapshot.GrantedAwaitingReady +
                            " completed=" + _snapshot.CompletedRequests +
                            " failed=" + _snapshot.FailedRequests +
                            " canceled=" + _snapshot.CanceledRequests, _style);
            GUILayout.Label("deferred (queued across transition)=" + _snapshot.DeferredRequests +
                            "  unfinished=" + _snapshot.UnfinishedRequests, _style);
            GUILayout.Label("subscriptions: input=" + _snapshot.ActiveInputSubscriptions +
                            " rules=" + _snapshot.ActiveRuleSubscriptions +
                            " tempResources=" + _snapshot.ActiveTemporaryResources +
                            " total=" + _snapshot.ActiveSubscriptionsTotal, _style);
            GUILayout.Label("staleCallbacks=" + _snapshot.StaleCallbacks +
                            " leaksAtClose=" + _snapshot.LeaksReportedAtClose +
                            " issues=" + _issues, _style);
            GUILayout.Label("engine bindings: inputRouter=" + Lifecycle.Unity.InputRouter.TotalBindings +
                            " ruleBus=" + Lifecycle.Unity.RuleBus.TotalListeners, _style);
            var reuse = _manager.CreateReuseView();
            var reuseLine = "object reuse: active=" + reuse.ActiveEntities.Count +
                            " asyncAnomalies=" + reuse.AsyncAnomalies.Count +
                            " pendingWork=" + reuse.PendingWorkItems;
            GUILayout.Label(reuseLine, reuse.HasAnomalies ? _reuseAnomalyStyle : _style);
            GUILayout.Space(4f);
            GUILayout.Label("last events:", _headerStyle);
            for (var i = _demoLog.Count - 1; i >= 0 && i >= _demoLog.Count - 8; i--)
            {
                GUILayout.Label(_demoLog[i], _style);
            }

            GUILayout.Space(4f);
            GUILayout.Label("1 pickup  2 attachment  3 machine  U input  R rule  G fail next  C return  T/X/V transition  H hud",
                _style);
            GUILayout.EndArea();
        }
    }
}
