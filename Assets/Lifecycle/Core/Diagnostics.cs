using System;
using System.Collections.Generic;

namespace Lifecycle.Core
{
    /// <summary>
    /// Lifecycle anomalies. Reporting them keeps leaks and unfinished requests visible; it is
    /// deliberately separate from the control-flow result so no issue is silently swallowed.
    /// </summary>
    public enum LifecycleIssueCode
    {
        /// <summary>An old-generation callback tried to mutate an instance that is leased again.</summary>
        StaleCallback = 1,
        /// <summary>Release/expire was attempted twice on the same lease.</summary>
        DuplicateRelease = 2,
        /// <summary>A scope closed while it still had leases, subscriptions, or temporary resources.</summary>
        ScopeLeakedState = 3,
        /// <summary>An async completion arrived after its scope closed; the slot was retired safely.</summary>
        CompletionAfterScopeClose = 4,
        /// <summary>A provider produced an instance for a request that had already been canceled/expired.</summary>
        LateProviderResult = 5,
        /// <summary>A pooled instance was returned with device/environment state that was not reset.</summary>
        UnresetState = 6,
        /// <summary>Generation would alias an older one; the slot is evicted instead.</summary>
        GenerationOverflow = 7,
        /// <summary>A request was rejected because its scope was closing under a strict policy.</summary>
        RequestRejected = 8,
        /// <summary>Moving queued requests to the next scope overflowed the queue and was rejected.</summary>
        RequestQueueOverflow = 9
    }

    public sealed class LifecycleIssue
    {
        public LifecycleIssue(LifecycleIssueCode code, string scopeId, string entityId, Generation generation, string message)
        {
            Code = code;
            ScopeId = scopeId;
            EntityId = entityId;
            Generation = generation;
            Message = message;
        }

        public LifecycleIssueCode Code { get; }
        public string ScopeId { get; }
        public string EntityId { get; }
        public Generation Generation { get; }
        public string Message { get; }

        public override string ToString() =>
            "[" + Code + "] scope=" + (ScopeId ?? "-") + " entity=" + (EntityId ?? "-") + " gen=" + Generation + " :: " + Message;
    }

    public interface ILifecycleReporter
    {
        void Report(LifecycleIssue issue);
    }

    /// <summary>Keeps issues in memory; used by tests and the demo HUD.</summary>
    public sealed class RecordingLifecycleReporter : ILifecycleReporter
    {
        private readonly List<LifecycleIssue> _issues = new List<LifecycleIssue>();

        public IReadOnlyList<LifecycleIssue> Issues => _issues;
        public int Count => _issues.Count;

        public void Report(LifecycleIssue issue) => _issues.Add(issue);

        public int CountOf(LifecycleIssueCode code)
        {
            var n = 0;
            for (var i = 0; i < _issues.Count; i++)
            {
                if (_issues[i].Code == code) n++;
            }

            return n;
        }

        public LifecycleIssue LastOf(LifecycleIssueCode code)
        {
            for (var i = _issues.Count - 1; i >= 0; i--)
            {
                if (_issues[i].Code == code) return _issues[i];
            }

            return null;
        }

        public void Clear() => _issues.Clear();
    }

    /// <summary>Reporter for builds where a stale callback is a hard bug (CI, assert-enabled servers).</summary>
    public sealed class ThrowingLifecycleReporter : ILifecycleReporter
    {
        private readonly bool _onlyFatal;

        public ThrowingLifecycleReporter(bool onlyFatal = false)
        {
            _onlyFatal = onlyFatal;
        }

        public void Report(LifecycleIssue issue)
        {
            if (_onlyFatal && issue.Code != LifecycleIssueCode.StaleCallback &&
                issue.Code != LifecycleIssueCode.ScopeLeakedState)
            {
                return;
            }

            throw new InvalidOperationException(issue.ToString());
        }
    }
}
