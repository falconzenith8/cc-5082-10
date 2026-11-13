using System;
using System.Collections.Generic;

namespace Lifecycle.Core
{
    /// <summary>
    /// Central authority for scene entity lifetimes.
    ///
    /// Responsibilities:
    /// - Scope transitions (<see cref="BeginTransition"/>/<see cref="ActivateScope"/>/<see cref="EndTransition"/>)
    ///   with an explicit <see cref="ClosePolicy"/> for requests issued while a scene is closing.
    /// - Generation leases: each borrow advances the slot generation; the manager is the only
    ///   place that decides whether a lease is still current, so old callbacks cannot touch a
    ///   slot that has been lent to another player.
    /// - Request tracking: every request is a <see cref="SpawnTicket"/> until Completed/Failed/
    ///   Canceled/Rejected. Late provider results are retired and reported, not swallowed.
    /// - Per-frame pump: applies completions for live generations, marks failed preparations,
    ///   fails leaked requests, and dispatches late ticks.
    /// </summary>
    public sealed class LifecycleManager : ILeaseBinder
    {
        private readonly Clock _clock;
        private readonly ILifecycleReporter _reporter;
        private readonly ISubscriptionSink _sink;
        private readonly SubscriptionLedger _ledger;
        private readonly Dictionary<string, EntityRegistration> _registrations =
            new Dictionary<string, EntityRegistration>(StringComparer.Ordinal);
        private readonly Dictionary<long, LeaseRecord> _leaseBook = new Dictionary<long, LeaseRecord>();
        private readonly List<SpawnTicket> _tickets = new List<SpawnTicket>();
        private readonly List<PendingWork> _pendingWork = new List<PendingWork>();
        private readonly List<SpawnTicket> _deferred = new List<SpawnTicket>();
        private readonly List<ILifecycleObserver> _observers = new List<ILifecycleObserver>();
        private SceneScope _current;
        private long _nextRequestId = 1;
        private long _nextLeaseId = 1;
        private int _lineage;
        private bool _inTransition;
        private bool _inPump;
        private bool _disposed;
        private int _leaksAtClose;
        private int _staleCallbacks;

        public LifecycleManager(Clock clock, ILifecycleReporter reporter = null, ISubscriptionSink sink = null)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _reporter = reporter;
            _sink = sink;
            _ledger = new SubscriptionLedger(sink, reporter);
        }

        public Clock Clock => _clock;
        public SubscriptionLedger Ledger => _ledger;
        public SceneScope CurrentScope => _current;
        public bool InTransition => _inTransition;
        public int TotalTicketsTracked => _tickets.Count;

        #region Entity registration

        /// <summary>
        /// Registers how to build and destroy one entity kind. The same registration is shared by
        /// every scene scope; each scope gets its own pools so cold instances never cross scenes.
        /// </summary>
        public void RegisterEntity(EntityKind kind, ISlotSource source, int maxSlotsPerPool = 64)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var keys = PoolKeysFor(kind);
            var registration = new EntityRegistration(kind, source, maxSlotsPerPool, keys);
            foreach (var key in keys)
            {
                _registrations[key] = registration;
            }
        }

        public void RegisterObserver(ILifecycleObserver observer)
        {
            if (observer != null) _observers.Add(observer);
        }

        public void RemoveObserver(ILifecycleObserver observer) => _observers.Remove(observer);

        private static string[] PoolKeysFor(EntityKind kind)
        {
            switch (kind)
            {
                case EntityKind.Prop:
                    return new[] { "prop" };
                case EntityKind.CharacterAttachment:
                    return new[] { "attachment" };
                case EntityKind.Machine:
                    return new[] { "machine" };
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        #endregion

        #region Scope transitions

        /// <summary>
        /// Starts a transition: the current scope (if any) is closed under its policy and the
        /// manager records that a replacement scene is being loaded. Spawn requests may still
        /// arrive during the transition; they are handled by <see cref="ClosePolicy"/>.
        /// </summary>
        public void BeginTransition(string nextSceneName, ClosePolicy? nextPolicy = null)
        {
            ThrowIfDisposed();
            if (_inTransition)
            {
                throw new InvalidOperationException("A scene transition is already in progress.");
            }

            var policy = nextPolicy ?? _current?.Policy ?? ClosePolicy.RejectWhenClosed;
            _inTransition = true;
            _nextSceneName = nextSceneName;
            _nextPolicy = policy;
            CloseCurrentScope("transition to " + nextSceneName, policy);
            Notify(o => o.OnTransitionStarted(nextSceneName, policy));
        }

        /// <summary>Creates the new scope once the replacement scene is ready and replays deferred work.</summary>
        public SceneScope ActivateScope(string sceneName)
        {
            ThrowIfDisposed();
            if (_current != null && _current.State == SceneScopeState.Active)
            {
                throw new InvalidOperationException("A scope is already active; begin a transition first.");
            }

            _current = new SceneScope(NextScopeId(), sceneName, _nextPolicy ?? ClosePolicy.RejectWhenClosed);
            _inTransition = false;
            Notify(o => o.OnScopeOpened(_current));
            ReplayDeferred(_current);
            return _current;
        }

        public void EndTransition()
        {
            if (!_inTransition) return;
            _inTransition = false;
            FailDeferred(RequestFailureKind.ScopeClosed, "Transition ended without activating a replacement scope.");
            Notify(o => o.OnTransitionEnded(_nextSceneName));
            _nextSceneName = null;
            _nextPolicy = null;
        }

        private string _nextSceneName;
        private ClosePolicy? _nextPolicy;
        private long _nextScopeId = 1;

        private string NextScopeId() => (++_nextScopeId).ToString();

        private void CloseCurrentScope(string why, ClosePolicy? policyOverride = null)
        {
            if (_current == null) return;
            var scope = _current;
            scope.MarkClosing();
            Notify(o => o.OnScopeClosing(scope));

            // Every outstanding ticket belonging to this scope is settled per policy.
            for (var i = _tickets.Count - 1; i >= 0; i--)
            {
                var ticket = _tickets[i];
                if (ticket.ScopeId != scope.ScopeId || ticket.IsSettled) continue;
                switch (policyOverride ?? scope.Policy)
                {
                    case ClosePolicy.QueueUntilNextScope:
                        ticket.IsDeferred = true;
                        _deferred.Add(ticket);
                        break;
                    case ClosePolicy.CancelOnClose:
                        ticket.Cancel(RequestFailureKind.CanceledByClose, "Scope closed (" + why + ").", _clock.Now);
                        Notify(o => o.OnRequestFailed(ticket));
                        break;
                    default:
                        ticket.Reject(RequestFailureKind.ScopeClosed, "Scope closed (" + why + ").", _clock.Now);
                        Notify(o => o.OnRequestFailed(ticket));
                        break;
                }
            }

            // Retire leases. Any entity that still holds engine state is handled by its
            // Deactivate path; then the scope's pools are closed so cold instances cannot be
            // reused by a different scene.
            foreach (var record in new List<LeaseRecord>(_leaseBook.Values))
            {
                if (record.ScopeId != scope.ScopeId) continue;
                ReturnLease(record, LeaseReturnReason.ScopeClosing, closeCold: true);
            }

            var leaked = _ledger.CloseScope(scope.ScopeId);
            _leaksAtClose += leaked;
            if (leaked > 0)
            {
                // CloseScope already reported each registration.
            }

            foreach (var pool in scope.Pools)
            {
                pool.Value.DisposeAll();
            }

            scope.MarkClosed();
            Notify(o => o.OnScopeClosed(scope.ScopeId, scope.SceneName));
            _current = null;
        }

        private void ReplayDeferred(SceneScope scope)
        {
            if (_deferred.Count == 0) return;
            var replay = new List<SpawnTicket>(_deferred);
            _deferred.Clear();
            foreach (var ticket in replay)
            {
                if (ticket.IsSettled) continue;
                ticket.IsDeferred = false;
                ticket.RetargetScope(scope.ScopeId, _clock.Now);
                IssueRequest(ticket, scope);
            }
        }

        #endregion

        #region Requests

        /// <summary>
        /// Requests an entity. Never returns a raw instance. The returned ticket is the single
        /// source of truth for the request's outcome.
        /// </summary>
        public SpawnTicket Request(SpawnRequest request)
        {
            ThrowIfDisposed();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (_inPump)
            {
                throw new InvalidOperationException(
                    "Requests are refused from inside lifecycle callbacks; schedule them for the next frame.");
            }

            var ticket = new SpawnTicket(_nextRequestId++, _current?.ScopeId, request, _clock.Now);

            if (_current == null || !_current.IsActive)
            {
                var policy = _inTransition ? _nextPolicy ?? ClosePolicy.RejectWhenClosed : ClosePolicy.RejectWhenClosed;
                if (policy == ClosePolicy.QueueUntilNextScope)
                {
                    ticket.IsDeferred = true;
                    _deferred.Add(ticket);
                    _tickets.Add(ticket);
                    return ticket;
                }

                if (policy == ClosePolicy.CancelOnClose)
                {
                    ticket.Cancel(RequestFailureKind.CanceledByClose, "Request issued while no scene scope is active.",
                        _clock.Now);
                }
                else
                {
                    ticket.Reject(RequestFailureKind.ScopeClosed, "No active scene scope accepts requests.",
                        _clock.Now);
                }

                _tickets.Add(ticket);
                Notify(o => o.OnRequestFailed(ticket));
                return ticket;
            }

            _tickets.Add(ticket);
            IssueRequest(ticket, _current);
            return ticket;
        }

        /// <summary>Releases a successful ticket's lease early. Stale/repeat releases are reported.</summary>
        public bool Return(SpawnTicket ticket)
        {
            if (ticket == null) return false;
            if (!ticket.Lease.IsAssigned)
            {
                _reporter?.Report(new LifecycleIssue(LifecycleIssueCode.DuplicateRelease, ticket.ScopeId, ticket.Request.EntityId,
                    Generation.None, "Return of ticket " + ticket.RequestId + " with no live lease (" + ticket.Status + ")."));
                return false;
            }

            if (!_leaseBook.TryGetValue(ticket.Lease.LeaseId, out var record))
            {
                _reporter?.Report(new LifecycleIssue(LifecycleIssueCode.DuplicateRelease, ticket.ScopeId,
                    ticket.Request.EntityId, ticket.Lease.Generation,
                    "Return of ticket " + ticket.RequestId + " after its lease was already retired."));
                return false;
            }

            return ReturnLease(record, LeaseReturnReason.Normal, closeCold: false);
        }

        private void IssueRequest(SpawnTicket ticket, SceneScope scope)
        {
            var key = KeyFor(ticket.Request.Kind);
            var registration = key != null && _registrations.TryGetValue(key, out var found) ? found : null;
            if (registration == null)
            {
                ticket.Fail(RequestFailureKind.EntityInvalid, "No entity registered for " + ticket.Request.Kind,
                    _clock.Now);
                Notify(o => o.OnRequestFailed(ticket));
                return;
            }

            var pool = scope.GetPool(registration.PoolKey);
            if (pool == null)
            {
                pool = new ObjectPool<PoolSlot>(registration.PoolKey, registration.Source, _reporter,
                    registration.MaxSlots);
                scope.AddPool(registration.PoolKey, pool);
            }

            PoolSlot slot;
            Lease lease;
            LeaseRecord record;
            try
            {
                slot = pool.Rent(scope.ScopeId, ticket.Request.EntityId, _clock.Now, NextLineage());
                if (slot == null)
                {
                    ticket.Fail(RequestFailureKind.ProviderReturnedNull,
                        "Slot factory returned null for " + ticket.Request, _clock.Now);
                    Notify(o => o.OnRequestFailed(ticket));
                    return;
                }

                record = new LeaseRecord(++_nextLeaseId, scope.ScopeId, slot, ticket, registration);
                lease = CreateLease(record, slot);
                pool.BindLease(slot, lease);
                record.Lease = lease;
                _leaseBook.Add(record.LeaseId, record);
                scope.LeaseIds.Add(record.LeaseId);
            }
            catch (Exception ex)
            {
                ticket.Fail(RequestFailureKind.ProviderThrew, "Entity creation threw: " + ex.Message, _clock.Now);
                Notify(o => o.OnRequestFailed(ticket));
                return;
            }

            ticket.Grant(lease);
            Notify(o => o.OnRequestGranted(ticket, lease));

            try
            {
                var entity = slot as IPooledEntity;
                if (entity != null) entity.Activate(lease);
                var rule = slot as IPooledRule;
                rule?.Attach(lease);
            }
            catch (Exception ex)
            {
                FailRequestWithLease(ticket, RequestFailureKind.ProviderThrew,
                    "Activate threw: " + ex.Message);
                return;
            }

            _pendingWork.Add(new PendingWork(record));
        }

        private void FailRequestWithLease(SpawnTicket ticket, RequestFailureKind kind, string message)
        {
            var lease = ticket.Lease;
            ticket.Fail(kind, message, _clock.Now);
            Notify(o => o.OnRequestFailed(ticket));
            if (lease.IsAssigned && _leaseBook.TryGetValue(lease.LeaseId, out var record))
            {
                ReturnLease(record, LeaseReturnReason.LoadFailed, closeCold: false);
            }
        }

        private Lease CreateLease(LeaseRecord record, PoolSlot slot)
        {
            return new Lease(slot, record.ScopeId, record.EntityId, record.PoolKey, record.LeaseId,
                slot.Generation, _clock.Now, _lineage, this);
        }

        private int NextLineage() => (++_lineage);

        private static string KeyFor(EntityKind kind)
        {
            switch (kind)
            {
                case EntityKind.Prop: return "prop";
                case EntityKind.CharacterAttachment: return "attachment";
                case EntityKind.Machine: return "machine";
                default: return null;
            }
        }

        #endregion

        #region Lease authority

        public bool IsCurrent(Lease lease)
        {
            if (!lease.IsAssigned || lease.Binder != this) return false;
            if (!_leaseBook.TryGetValue(lease.LeaseId, out var record)) return false;
            var slot = record.Slot;
            return record.Returned == false &&
                   record.ScopeId == lease.ScopeId &&
                   slot.Generation == lease.Generation &&
                   slot.CurrentLease.LeaseId == lease.LeaseId;
        }

        public SubscriptionHandle Subscribe(Lease lease, SubscriptionKind kind, string key, object payload)
        {
            if (!IsCurrent(lease))
            {
                var issue = new LifecycleIssue(LifecycleIssueCode.StaleCallback, lease.ScopeId, lease.EntityId,
                    lease.Generation, "Subscribe(" + kind + ", '" + key + "') refused: lease is not current " +
                    "(slot now " + (lease.Slot != null ? lease.Slot.Generation.ToString() : "missing") + ").");
                _staleCallbacks++;
                _reporter?.Report(issue);
                Notify(o => o.OnIssue(issue));
                return SubscriptionHandle.None;
            }

            return _ledger.Subscribe(lease.ScopeId, lease.LeaseId, kind, key, payload);
        }

        public bool Unsubscribe(Lease lease, SubscriptionHandle handle)
        {
            if (lease.Binder != this || !handle.IsValid) return false;
            return _ledger.Unsubscribe(handle);
        }

        /// <summary>
        /// Generation-first validity check for a lease: true only while the slot still carries
        /// exactly the generation this lease was issued for. Unlike <see cref="IsCurrent"/>, it
        /// does not rely on lease-id identity (which already separates generations), so it is the
        /// direct proof that a callback cannot act on a slot lent to a newer borrow. The fault-
        /// injection scenario uses it to demonstrate that this single comparison is load-bearing.
        /// </summary>
        public bool IsGenerationCurrent(Lease lease)
        {
            if (!lease.IsAssigned || lease.Binder != this || !lease.Generation.IsValid) return false;
            if (!_leaseBook.TryGetValue(lease.LeaseId, out var record)) return false;
            var slot = record.Slot;
            return record.Returned == false &&
                   record.ScopeId == lease.ScopeId &&
                   slot.Generation == lease.Generation;
        }

        /// <summary>
        /// Guard for callbacks scheduled by a lease: returns true only if the generation is still
        /// current. False results are reported as stale callbacks so they stay visible.
        /// </summary>
        /// <summary>
        /// Test/diagnostic hook: reports a non-normal return for a still-live ticket. The demo
        /// uses it to prove an externally destroyed object cannot be handed to the next player.
        /// </summary>
        public bool ReturnWithReason(SpawnTicket ticket, LeaseReturnReason reason)
        {
            if (ticket == null || !ticket.Lease.IsAssigned) return false;
            if (!_leaseBook.TryGetValue(ticket.Lease.LeaseId, out var record)) return false;
            return ReturnLease(record, reason, closeCold: false);
        }

        public bool TryApply(Lease lease, string operation, Action action)
        {
            if (!IsCurrent(lease))
            {
                var issue = new LifecycleIssue(LifecycleIssueCode.StaleCallback, lease.ScopeId, lease.EntityId,
                    lease.Generation, "Callback '" + operation + "' for " + lease.Generation +
                    " rejected; slot is now " + (lease.Slot != null ? lease.Slot.Generation.ToString() : "missing") +
                    " in scope " + (lease.ScopeId ?? "-") + ".");
                _staleCallbacks++;
                _reporter?.Report(issue);
                Notify(o => o.OnIssue(issue));
                return false;
            }

            action();
            return true;
        }

        private bool ReturnLease(LeaseRecord record, LeaseReturnReason reason, bool closeCold)
        {
            if (record.Returned) return false;
            record.Returned = true;
            _leaseBook.Remove(record.LeaseId);
            var scope = _current;
            if (scope != null) scope.LeaseIds.Remove(record.LeaseId);

            var pool = FindPool(record);
            var slot = record.Slot;

            if (pool != null)
            {
                pool.Return(slot, record.Lease, reason);
                try
                {
                    var entity = slot as IPooledEntity;
                    if (entity != null) entity.Deactivate(record.Lease);
                    var rule = slot as IPooledRule;
                    rule?.Detach(record.Lease);
                }
                catch (Exception ex)
                {
                    _reporter?.Report(new LifecycleIssue(LifecycleIssueCode.UnresetState, record.ScopeId,
                        record.EntityId, record.Generation, "Deactivate threw: " + ex.Message));
                }

                _ledger.DisposeAllForLease(record.LeaseId);
                RemovePendingWork(record.LeaseId);
                pool.FinishReturn(slot, record.Lease, record.Source.IsAlive(slot));
            }

            if (reason != LeaseReturnReason.Normal)
            {
                // Non-normal returns settle the ticket if it was still Granted. A deferred ticket
                // outlives its lease on purpose: it will be retried in the replacement scope.
                if (record.Ticket != null && record.Ticket.Status == RequestStatus.Granted &&
                    !record.Ticket.IsDeferred)
                {
                    if (reason == LeaseReturnReason.ScopeClosing || reason == LeaseReturnReason.Canceled)
                    {
                        record.Ticket.Cancel(RequestFailureKind.CanceledByClose,
                            "Lease retired because the scope closed (" + reason + ").", _clock.Now);
                    }
                    else
                    {
                        record.Ticket.Fail(RequestFailureKind.EntityInvalid,
                            "Lease retired (" + reason + ").", _clock.Now);
                    }

                    Notify(o => o.OnRequestFailed(record.Ticket));
                }
            }
            else if (record.Ticket != null && record.Ticket.Status == RequestStatus.Granted)
            {
                // A normal return owns its lease, so it owns the preparation too: dropping the
                // pending work without settling the ticket would leave it "GrantedAwaitingReady"
                // forever. The explicit return is the terminal canceled outcome for that request.
                RemovePendingWork(record.LeaseId);
                record.Ticket.Cancel(RequestFailureKind.CanceledByCaller,
                    "Returned by the caller before preparation finished.", _clock.Now);
                Notify(o => o.OnRequestFailed(record.Ticket));
            }

            return true;
        }

        private ObjectPool<PoolSlot> FindPool(LeaseRecord record)
        {
            var scope = _current;
            if (scope == null || scope.ScopeId != record.ScopeId) return null;
            return scope.GetPool(record.PoolKey);
        }

        private void RemovePendingWork(long leaseId)
        {
            for (var i = _pendingWork.Count - 1; i >= 0; i--)
            {
                if (_pendingWork[i].Record.LeaseId == leaseId) _pendingWork.RemoveAt(i);
            }
        }

        #endregion

        #region Pump

        /// <summary>
        /// Called once per frame. Applies completions and failures for live generations, fails
        /// preparations whose request was canceled, and runs late ticks. Never touches a slot
        /// whose lease was retired in the meantime.
        /// </summary>
        public void Pump()
        {
            ThrowIfDisposed();
            if (_inPump) throw new InvalidOperationException("Pump is not re-entrant.");
            _inPump = true;
            try
            {
                for (var i = _pendingWork.Count - 1; i >= 0; i--)
                {
                    var work = _pendingWork[i];
                    var record = work.Record;
                    if (!_leaseBook.TryGetValue(record.LeaseId, out var live) || live != record)
                    {
                        _pendingWork.RemoveAt(i);
                        continue;
                    }

                    var slot = record.Slot;
                    var entity = slot as IPooledEntity;
                    if (entity == null)
                    {
                        _pendingWork.RemoveAt(i);
                        continue;
                    }

                    if (entity.PreparationFailure != null)
                    {
                        _pendingWork.RemoveAt(i);
                        var ticket = record.Ticket;
                        FailRequestWithLease(ticket, RequestFailureKind.ProviderThrew,
                            entity.PreparationFailure);
                        continue;
                    }

                    if (!entity.IsActivationComplete)
                    {
                        continue;
                    }

                    if (!IsCurrent(record.Lease))
                    {
                        // The generation advanced while the preparation was outstanding: the
                        // completion belongs to nobody. Report it; do not apply it.
                        _pendingWork.RemoveAt(i);
                        ReportStaleCompletion(record);
                        continue;
                    }

                    _pendingWork.RemoveAt(i);
                    var pool = FindPool(record);
                    pool?.MarkReady(slot);
                    record.Ticket?.Complete(_clock.Now);
                    Notify(o => o.OnRequestCompleted(record.Ticket));
                }

                var lateTicked = 0;
                foreach (var record in _leaseBook.Values)
                {
                    if (record.Slot.State != ActivationState.Ready) continue;
                    if (record.Slot is ILateTickable late && IsCurrent(record.Lease))
                    {
                        late.LateTick(record.Lease);
                        lateTicked++;
                    }
                }

                _ = lateTicked;
            }
            finally
            {
                _inPump = false;
            }
        }

        private void ReportStaleCompletion(LeaseRecord record)
        {
            var slot = record.Slot;
            var issue = new LifecycleIssue(LifecycleIssueCode.StaleCallback, record.ScopeId, record.EntityId,
                record.Generation,
                "Preparation completed for " + record.Generation + " but the slot is now " + slot.State +
                " with " + slot.Generation + "; completion was not applied.");
            _staleCallbacks++;
            _reporter?.Report(issue);
            Notify(o => o.OnIssue(issue));
        }

        #endregion

        #region Deferred queue

        private void FailDeferred(RequestFailureKind kind, string message)
        {
            if (_deferred.Count == 0) return;
            var deferred = new List<SpawnTicket>(_deferred);
            _deferred.Clear();
            foreach (var ticket in deferred)
            {
                if (ticket.IsSettled) continue;
                ticket.IsDeferred = false;
                ticket.Fail(kind, message, _clock.Now);
                Notify(o => o.OnRequestFailed(ticket));
            }
        }

        public IReadOnlyList<SpawnTicket> DeferredTickets => _deferred;

        /// <summary>
        /// Read-only monitor view: every live entity with its generation and async state, plus a
        /// focused anomaly list. Reuse problems surface next to the entity, not as bare counters.
        /// </summary>
        public ReuseView CreateReuseView()
        {
            var view = new ReuseView
            {
                ScopeId = _current?.ScopeId,
                InTransition = _inTransition,
                Tick = _clock.Now,
                StaleCallbacks = _staleCallbacks,
                LeasesHeld = _leaseBook.Count,
                PendingWorkItems = _pendingWork.Count,
                ActiveSubscriptions = _ledger.ActiveCount
            };

            foreach (var record in _leaseBook.Values)
            {
                var slot = record.Slot;
                var entity = slot as IPooledEntity;
                var entityView = new ReuseEntityView
                {
                    EntityId = record.EntityId,
                    PoolId = record.PoolKey,
                    LeaseGeneration = record.Lease.IsAssigned ? record.Lease.Generation.Value : 0,
                    SlotGeneration = slot.Generation.Value,
                    State = slot.State.ToString(),
                    ActivationComplete = entity == null || entity.IsActivationComplete,
                    PreparationPending = entity != null && !entity.IsActivationComplete,
                    HasPendingWork = HasPendingWorkFor(record.LeaseId),
                    AgeTicks = _clock.Now - (record.Lease.IsAssigned ? record.Lease.LeasedAtTick : _clock.Now)
                };

                if (entityView.LeaseGeneration != entityView.SlotGeneration)
                {
                    entityView.Anomaly = true;
                    entityView.AnomalyReason = "lease gen " + entityView.LeaseGeneration +
                                               " but slot is " + entityView.SlotGeneration;
                }
                else
                {
                    var leaseIsCurrent = record.Lease.IsAssigned && IsCurrent(record.Lease);
                    if (slot.State == ActivationState.Ready && entityView.PreparationPending)
                    {
                        entityView.Anomaly = true;
                        entityView.AnomalyReason = "Ready but async preparation still pending";
                    }
                    else if (slot.State == ActivationState.Preparing && entityView.ActivationComplete)
                    {
                        entityView.Anomaly = true;
                        entityView.AnomalyReason = "async complete but slot still Preparing (pump has not applied it)";
                    }
                    else if (!leaseIsCurrent && !entityView.HasPendingWork)
                    {
                        entityView.Anomaly = true;
                        entityView.AnomalyReason = "tracked lease is not live and has no pending work";
                    }
                }

                view.ActiveEntities.Add(entityView);
                if (entityView.Anomaly)
                {
                    view.AsyncAnomalies.Add(entityView.EntityId + " (" + entityView.State + "): " +
                                            entityView.AnomalyReason);
                }
            }

            foreach (var work in _pendingWork)
            {
                var record = work.Record;
                var slot = record.Slot;
                var entity = slot as IPooledEntity;
                if (!_leaseBook.TryGetValue(record.LeaseId, out var live) || live != record)
                {
                    view.AsyncAnomalies.Add(record.EntityId + " (" + slot.State + "): pending work for an untracked lease");
                }
                else if (!IsCurrent(record.Lease))
                {
                    view.AsyncAnomalies.Add(record.EntityId + " (" + slot.State + "): pending work for a non-current lease");
                }
                else if (entity == null)
                {
                    view.AsyncAnomalies.Add(record.EntityId + " (" + slot.State + "): pending work on a non-async slot");
                }
            }

            return view;
        }

        private bool HasPendingWorkFor(long leaseId)
        {
            for (var i = 0; i < _pendingWork.Count; i++)
            {
                if (_pendingWork[i].Record.LeaseId == leaseId) return true;
            }

            return false;
        }

        /// <summary>Finds a ticket by request id for observers that only kept the id (CLI, HUD).</summary>
        public bool TryGetTicket(long requestId, out SpawnTicket ticket)
        {
            for (var i = 0; i < _tickets.Count; i++)
            {
                if (_tickets[i].RequestId == requestId)
                {
                    ticket = _tickets[i];
                    return true;
                }
            }

            ticket = null;
            return false;
        }

        /// <summary>Issues a manual borrow from an operator surface. Returns null when refused.</summary>
        public SpawnTicket Borrow(EntityKind kind, string entityId, string purpose = null)
        {
            ThrowIfDisposed();
            return Request(new SpawnRequest(kind, entityId, purpose));
        }

        /// <summary>Returns a live ticket by id. Reports duplicates/stale releases like <see cref="Return"/>.</summary>
        public bool ReturnById(long requestId)
        {
            return TryGetTicket(requestId, out var ticket) && Return(ticket);
        }

        #endregion

        #region Snapshots and teardown

        public LifecycleSnapshot CreateSnapshot()
        {
            var snapshot = new LifecycleSnapshot
            {
                ActiveScopes = _current != null && _current.IsActive ? 1 : 0,
                CurrentScopeId = _current?.ScopeId,
                ActiveInputSubscriptions = _ledger.ActiveCountOf(SubscriptionKind.Input),
                ActiveRuleSubscriptions = _ledger.ActiveCountOf(SubscriptionKind.RuleEvent),
                ActiveTemporaryResources = _ledger.ActiveCountOf(SubscriptionKind.TemporaryResource),
                ActiveAsyncTasks = _ledger.ActiveCountOf(SubscriptionKind.AsyncTask),
                LeasesHeld = _leaseBook.Count,
                PendingWorkItems = _pendingWork.Count,
                StaleCallbacks = _staleCallbacks,
                LeaksReportedAtClose = _leaksAtClose,
                DeferredRequests = _deferred.Count
            };

            foreach (var ticket in _tickets)
            {
                switch (ticket.Status)
                {
                    case RequestStatus.Pending:
                        snapshot.PendingRequests++;
                        break;
                    case RequestStatus.Granted:
                        snapshot.GrantedAwaitingReady++;
                        break;
                    case RequestStatus.Completed:
                        snapshot.CompletedRequests++;
                        break;
                    case RequestStatus.Failed:
                        snapshot.FailedRequests++;
                        break;
                    case RequestStatus.Canceled:
                        snapshot.CanceledRequests++;
                        break;
                }
            }

            return snapshot;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CloseCurrentScope("manager dispose", ClosePolicy.CancelOnClose);
            FailDeferred(RequestFailureKind.ScopeClosed, "Manager disposed.");
            _pendingWork.Clear();
            _ledger.Close();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LifecycleManager));
        }

        private void Notify(Action<ILifecycleObserver> action)
        {
            for (var i = 0; i < _observers.Count; i++)
            {
                try
                {
                    action(_observers[i]);
                }
                catch (Exception ex)
                {
                    _reporter?.Report(new LifecycleIssue(LifecycleIssueCode.ScopeLeakedState, null, null,
                        Generation.None, "Observer threw: " + ex.Message));
                }
            }
        }

        #endregion
    }

    internal sealed class EntityRegistration
    {
        public EntityRegistration(EntityKind kind, ISlotSource source, int maxSlots, string[] keys)
        {
            Kind = kind;
            Source = source;
            MaxSlots = maxSlots;
            PoolKey = keys[0];
        }

        public EntityKind Kind { get; }
        public ISlotSource Source { get; }
        public int MaxSlots { get; }
        public string PoolKey { get; }
    }

    internal sealed class LeaseRecord
    {
        public LeaseRecord(long leaseId, string scopeId, PoolSlot slot, SpawnTicket ticket,
            EntityRegistration registration)
        {
            LeaseId = leaseId;
            ScopeId = scopeId;
            Slot = slot;
            Ticket = ticket;
            Registration = registration;
            EntityId = slot.EntityId;
            PoolKey = registration.PoolKey;
            Generation = slot.Generation;
            Source = registration.Source;
        }

        public long LeaseId { get; }
        public string ScopeId { get; }
        public PoolSlot Slot { get; }
        public SpawnTicket Ticket { get; }
        public EntityRegistration Registration { get; }
        public string EntityId { get; }
        public string PoolKey { get; }
        public Generation Generation { get; }
        public ISlotSource Source { get; }
        public bool Returned { get; set; }
        public Lease Lease { get; set; }
    }

    internal sealed class PendingWork
    {
        public PendingWork(LeaseRecord record)
        {
            Record = record;
        }

        public LeaseRecord Record { get; }
    }
}
