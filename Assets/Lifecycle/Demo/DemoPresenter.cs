using System;
using System.Collections.Generic;
using Lifecycle.Core;
using UnityEngine;

namespace Lifecycle.Demo
{
    /// <summary>
    /// Bridges core lifecycle events to the Unity demo: logs each scope transition and request
    /// outcome, and keeps a rolling issue feed for the on-screen HUD. It never mutates lifecycle
    /// state; it only observes.
    /// </summary>
    public sealed class DemoPresenter : ILifecycleObserver
    {
        private readonly List<string> _log = new List<string>();
        private readonly int _maxLines;
        private int _granted;
        private int _completed;
        private int _failed;

        public DemoPresenter(int maxLines = 22)
        {
            _maxLines = maxLines;
        }

        public IReadOnlyList<string> Log => _log;
        public int GrantedCount => _granted;
        public int CompletedCount => _completed;
        public int FailedCount => _failed;
        public string LastFailure { get; private set; }

        public void OnScopeOpened(SceneScope scope) => Append("scope opened: " + scope.SceneName + " #" + scope.ScopeId);

        public void OnScopeClosing(SceneScope scope) => Append("scope closing: " + scope.SceneName + " (leases=" + scope.ActiveLeaseCount + ")");

        public void OnScopeClosed(string scopeId, string sceneName) => Append("scope closed: " + sceneName + " #" + scopeId);

        public void OnTransitionStarted(string nextSceneName, ClosePolicy policy) =>
            Append("transition -> " + nextSceneName + " policy=" + policy);

        public void OnTransitionEnded(string nextSceneName) => Append("transition ended: " + nextSceneName);

        public void OnRequestGranted(SpawnTicket ticket, Lease lease)
        {
            _granted++;
            Append("granted #" + ticket.RequestId + " " + ticket.Request + " -> " + lease);
        }

        public void OnRequestCompleted(SpawnTicket ticket)
        {
            _completed++;
            Append("completed #" + ticket.RequestId + " " + ticket.Request + " in " + (ticket.CompletedAtTick - ticket.IssuedAtTick) + " ticks");
        }

        public void OnRequestFailed(SpawnTicket ticket)
        {
            _failed++;
            LastFailure = ticket.ToString();
            Append("settled #" + ticket.RequestId + " " + ticket.Status + ": " + ticket.Failure + " - " + ticket.FailureMessage);
        }

        public void OnIssue(LifecycleIssue issue) => Append("issue " + issue.Code + ": " + issue.Message);

        private void Append(string line)
        {
            _log.Add(line);
            if (_log.Count > _maxLines) _log.RemoveAt(0);
        }
    }
}
