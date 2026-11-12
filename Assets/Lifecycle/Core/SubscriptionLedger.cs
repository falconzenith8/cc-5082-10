using System;
using System.Collections.Generic;

namespace Lifecycle.Core
{
    public enum SubscriptionKind
    {
        Input,
        RuleEvent,
        TemporaryResource,
        AsyncTask,
        Custom
    }

    /// <summary>An engine resource owned by a lease; released exactly once when that lease ends.</summary>
    public abstract class TemporaryResource
    {
        public abstract string ResourceKey { get; }
        public abstract void Dispose();
    }

    public readonly struct SubscriptionInfo
    {
        internal SubscriptionInfo(SubscriptionKind kind, string key, object payload)
        {
            Kind = kind;
            Key = key;
            Payload = payload;
        }

        public SubscriptionKind Kind { get; }
        public string Key { get; }
        public object Payload { get; }
        public TemporaryResource Resource => Payload as TemporaryResource;
    }

    /// <summary>
    /// Engine attach/detach boundary. Unity implementations bind input actions, rule listeners,
    /// and texture handles here so tests can assert that real outside-world state is removed.
    /// </summary>
    public interface ISubscriptionSink
    {
        void OnSubscribed(SubscriptionInfo info);
        void OnUnsubscribed(SubscriptionInfo info);
    }

    public readonly struct SubscriptionHandle
    {
        internal SubscriptionHandle(long registrationId)
        {
            RegistrationId = registrationId;
        }

        public static SubscriptionHandle None => new SubscriptionHandle(0);
        public long RegistrationId { get; }
        public bool IsValid => RegistrationId > 0;
        public override string ToString() => IsValid ? "Sub[" + RegistrationId + "]" : "Sub[none]";
    }

    /// <summary>
    /// Tracks per-lease input bindings, rule events, and temporary resources with reference
    /// counts, so double-subscribe/double-release cannot drop a live binding or leak one.
    /// The ledger is closed when the manager shuts down; late registrations are reported.
    /// </summary>
    public sealed class SubscriptionLedger
    {
        private sealed class Registration
        {
            public long Id;
            public string ScopeId;
            public long LeaseId;
            public SubscriptionKind Kind;
            public string Key;
            public object Payload;
            public int RefCount;
            public bool Removed;
        }

        private readonly Dictionary<long, Registration> _byId = new Dictionary<long, Registration>();
        private readonly Dictionary<string, Registration> _byComposite = new Dictionary<string, Registration>();
        private readonly Dictionary<long, List<Registration>> _byLease = new Dictionary<long, List<Registration>>();
        private readonly Dictionary<SubscriptionKind, HashSet<Registration>> _byKind =
            new Dictionary<SubscriptionKind, HashSet<Registration>>();
        private readonly ISubscriptionSink _sink;
        private readonly ILifecycleReporter _reporter;
        private long _nextId = 1;
        private bool _closed;

        public SubscriptionLedger(ISubscriptionSink sink, ILifecycleReporter reporter)
        {
            _sink = sink;
            _reporter = reporter;
        }

        public bool IsClosed => _closed;
        public int ActiveCount => _byId.Count;

        public int ActiveCountOf(SubscriptionKind kind)
        {
            var set = _byKind.TryGetValue(kind, out var found) ? found : null;
            return set == null ? 0 : set.Count;
        }

        public int ActiveCountForScope(string scopeId)
        {
            var n = 0;
            foreach (var registration in _byId.Values)
            {
                if (registration.ScopeId == scopeId) n++;
            }

            return n;
        }

        public IReadOnlyList<SubscriptionInfo> ActiveForLease(long leaseId)
        {
            var result = new List<SubscriptionInfo>();
            var list = _byLease.TryGetValue(leaseId, out var found) ? found : null;
            if (list == null) return result;
            for (var i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (!r.Removed) result.Add(new SubscriptionInfo(r.Kind, r.Key, r.Payload));
            }

            return result;
        }

        public SubscriptionHandle Subscribe(string scopeId, long leaseId, SubscriptionKind kind, string key,
            object payload = null)
        {
            if (_closed)
            {
                _reporter?.Report(new LifecycleIssue(LifecycleIssueCode.StaleCallback, scopeId, key, Generation.None,
                    "Subscription ledger closed; registration of " + kind + " '" + key + "' refused."));
                return SubscriptionHandle.None;
            }

            if (string.IsNullOrEmpty(scopeId) || leaseId <= 0)
            {
                throw new ArgumentException("Subscriptions require a live scope and lease identity.");
            }

            var composite = Composite(scopeId, leaseId, kind, key);
            var existing = _byComposite.TryGetValue(composite, out var found) ? found : null;
            if (existing != null)
            {
                existing.RefCount++;
                return new SubscriptionHandle(existing.Id);
            }

            var registration = new Registration
            {
                Id = _nextId++,
                ScopeId = scopeId,
                LeaseId = leaseId,
                Kind = kind,
                Key = key,
                Payload = payload,
                RefCount = 1
            };

            _byId.Add(registration.Id, registration);
            _byComposite.Add(composite, registration);
            AddOwned(registration);
            GetKindSet(kind).Add(registration);
            _sink?.OnSubscribed(new SubscriptionInfo(kind, key, payload));
            return new SubscriptionHandle(registration.Id);
        }

        public bool Unsubscribe(SubscriptionHandle handle)
        {
            if (!handle.IsValid) return false;
            var registration = _byId.TryGetValue(handle.RegistrationId, out var found) ? found : null;
            if (registration == null)
            {
                _reporter?.Report(new LifecycleIssue(LifecycleIssueCode.DuplicateRelease, null, null, Generation.None,
                    "Unsubscribe of an already-removed subscription " + handle + "."));
                return false;
            }

            registration.RefCount--;
            if (registration.RefCount > 0) return true;
            Remove(registration);
            return true;
        }

        /// <summary>Removes all registrations of a lease regardless of ref counts (owner is gone).</summary>
        public int DisposeAllForLease(long leaseId)
        {
            var list = _byLease.TryGetValue(leaseId, out var found) ? found : null;
            if (list == null) return 0;
            var removed = 0;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var r = list[i];
                if (!r.Removed)
                {
                    Remove(r);
                    removed++;
                }
            }

            return removed;
        }

        /// <summary>
        /// Closes a scope's registrations. Anything still live is a leak: each is reported and
        /// then torn down so the engine cannot keep firing into a dead scene.
        /// </summary>
        public int CloseScope(string scopeId)
        {
            var leaked = 0;
            foreach (var registration in new List<Registration>(_byId.Values))
            {
                if (registration.ScopeId != scopeId) continue;
                leaked++;
                _reporter?.Report(new LifecycleIssue(LifecycleIssueCode.ScopeLeakedState, scopeId, registration.Key,
                    Generation.None, "Leaked " + registration.Kind + " subscription '" + registration.Key +
                    "' (lease " + registration.LeaseId + ") at scope close."));
                Remove(registration);
            }

            return leaked;
        }

        public void Close()
        {
            foreach (var registration in new List<Registration>(_byId.Values)) Remove(registration);
            _closed = true;
        }

        private void Remove(Registration registration)
        {
            if (registration.Removed) return;
            registration.Removed = true;
            _byId.Remove(registration.Id);
            _byComposite.Remove(Composite(registration.ScopeId, registration.LeaseId, registration.Kind, registration.Key));
            var owned = _byLease.TryGetValue(registration.LeaseId, out var list) ? list : null;
            if (owned != null)
            {
                owned.Remove(registration);
                if (owned.Count == 0) _byLease.Remove(registration.LeaseId);
            }

            var kindSet = GetKindSet(registration.Kind);
            kindSet.Remove(registration);
            if (kindSet.Count == 0) _byKind.Remove(registration.Kind);
            _sink?.OnUnsubscribed(new SubscriptionInfo(registration.Kind, registration.Key, registration.Payload));

            // The ledger owns temporary resources registered through a lease: exactly one disposal
            // per resource, even when the lease was retired by scope close rather than by the caller.
            var resource = registration.Payload as TemporaryResource;
            if (resource != null)
            {
                try
                {
                    resource.Dispose();
                }
                catch (Exception ex)
                {
                    _reporter?.Report(new LifecycleIssue(LifecycleIssueCode.ScopeLeakedState,
                        registration.ScopeId, registration.Key, Generation.None,
                        "Temporary resource disposal threw: " + ex.Message));
                }
            }
        }

        private void AddOwned(Registration registration)
        {
            var list = _byLease.TryGetValue(registration.LeaseId, out var found) ? found : null;
            if (list == null)
            {
                list = new List<Registration>();
                _byLease.Add(registration.LeaseId, list);
            }

            list.Add(registration);
        }

        private HashSet<Registration> GetKindSet(SubscriptionKind kind)
        {
            var set = _byKind.TryGetValue(kind, out var found) ? found : null;
            if (set == null)
            {
                set = new HashSet<Registration>();
                _byKind.Add(kind, set);
            }

            return set;
        }

        private static string Composite(string scopeId, long leaseId, SubscriptionKind kind, string key) =>
            scopeId + "\u001f" + leaseId + "\u001f" + (int)kind + "\u001f" + key;

        public override string ToString() =>
            "Ledger(active=" + ActiveCount + ", input=" + ActiveCountOf(SubscriptionKind.Input) +
            ", rules=" + ActiveCountOf(SubscriptionKind.RuleEvent) +
            ", resources=" + ActiveCountOf(SubscriptionKind.TemporaryResource) +
            ", async=" + ActiveCountOf(SubscriptionKind.AsyncTask) + ")";
    }
}
