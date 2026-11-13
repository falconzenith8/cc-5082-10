using System;
using System.Collections.Generic;

namespace Lifecycle.Core
{
    /// <summary>
    /// What to do with spawn requests issued while the issuer's scene is closing or already closed.
    /// The policy is explicit and observable; nothing is silently dropped.
    /// </summary>
    public enum ClosePolicy
    {
        /// <summary>A closing/closed scope rejects requests synchronously; active scopes answer normally.</summary>
        RejectWhenClosed,
        /// <summary>
        /// Requests arriving during a transition are parked and replayed, in order, into the next
        /// scope; if no next scope arrives, they fail with ScopeClosed at transition end.
        /// </summary>
        QueueUntilNextScope,
        /// <summary>Requests from a closing scope are canceled, each with a terminal canceled result.</summary>
        CancelOnClose
    }

    public enum EntityKind
    {
        /// <summary>Interactive prop spawned into the current scene's pool.</summary>
        Prop,
        /// <summary>Character attachment (weapon, gadget, aura) spawned into the current scene's pool.</summary>
        CharacterAttachment,
        /// <summary>Interactive device with its own prototype and pool, distinct from a plain prop.</summary>
        Machine
    }

    /// <summary>Description of a spawn request. Callers never see the pooled instance directly.</summary>
    public sealed class SpawnRequest
    {
        public SpawnRequest(EntityKind kind, string entityId, string purpose = null)
        {
            Kind = kind;
            EntityId = entityId;
            Purpose = purpose;
        }

        public EntityKind Kind { get; }
        public string EntityId { get; }
        public string Purpose { get; }

        public static SpawnRequest Prop(string entityId, string purpose = null) =>
            new SpawnRequest(EntityKind.Prop, entityId, purpose);

        public static SpawnRequest Attachment(string entityId, string purpose = null) =>
            new SpawnRequest(EntityKind.CharacterAttachment, entityId, purpose);

        public static SpawnRequest Machine(string entityId, string purpose = null) =>
            new SpawnRequest(EntityKind.Machine, entityId, purpose);

        public override string ToString() => Kind + ":" + EntityId + (Purpose != null ? " (" + Purpose + ")" : "");
    }

    public enum RequestStatus
    {
        /// <summary>Accepted; waiting for a lease or for preparation to finish.</summary>
        Pending,
        Granted,
        /// <summary>Ready and usable inside the requesting scope.</summary>
        Completed,
        Failed,
        Canceled,
        /// <summary>Refused synchronously by policy; no entity was touched.</summary>
        Rejected
    }

    public enum RequestFailureKind
    {
        None,
        ScopeClosed,
        ProviderThrew,
        ProviderReturnedNull,
        CanceledByCaller,
        CanceledByClose,
        EntityInvalid,
        QueueOverflow
    }

    /// <summary>
    /// One tracked spawn request. Exposes its status so callers, tests, and the HUD can see
    /// unfinished or canceled work instead of relying on a pooled-object counter.
    /// </summary>
    public sealed class SpawnTicket
    {
        internal SpawnTicket(long id, string scopeId, SpawnRequest request, long issuedAtTick)
        {
            RequestId = id;
            ScopeId = scopeId;
            Request = request;
            IssuedAtTick = issuedAtTick;
            Status = RequestStatus.Pending;
        }

        public long RequestId { get; }
        public string ScopeId { get; private set; }
        public SpawnRequest Request { get; }
        public long IssuedAtTick { get; private set; }
        public long CompletedAtTick { get; private set; } = -1;
        public RequestStatus Status { get; private set; }
        public Lease Lease { get; private set; }
        public RequestFailureKind Failure { get; private set; }
        public string FailureMessage { get; private set; }

        /// <summary>Parked during a transition and not yet replayed into a new scope.</summary>
        public bool IsDeferred { get; internal set; }

        /// <summary>
        /// Called when a deferred ticket is replayed into a replacement scope. Keeps the same
        /// ticket identity visible to the caller so a queued request can be followed across a scene change.
        /// </summary>
        internal void RetargetScope(string scopeId, long issuedAtTick)
        {
            ScopeId = scopeId;
            IssuedAtTick = issuedAtTick;
            Status = RequestStatus.Pending;
            Failure = RequestFailureKind.None;
            FailureMessage = null;
            CompletedAtTick = -1;
            Lease = default;
        }

        public bool IsSettled => Status == RequestStatus.Completed || Status == RequestStatus.Failed ||
                                 Status == RequestStatus.Canceled || Status == RequestStatus.Rejected;

        internal void Grant(Lease lease)
        {
            Status = RequestStatus.Granted;
            Lease = lease;
        }

        internal void Complete(long tick)
        {
            if (Status == RequestStatus.Granted) Status = RequestStatus.Completed;
            CompletedAtTick = tick;
        }

        internal void Fail(RequestFailureKind kind, string message, long tick)
        {
            Status = RequestStatus.Failed;
            Failure = kind;
            FailureMessage = message;
            CompletedAtTick = tick;
            Lease = default;
        }

        internal void Cancel(RequestFailureKind kind, string message, long tick)
        {
            if (Status == RequestStatus.Completed) return;
            Status = RequestStatus.Canceled;
            Failure = kind;
            FailureMessage = message;
            CompletedAtTick = tick;
            Lease = default;
        }

        internal void Reject(RequestFailureKind kind, string message, long tick)
        {
            Status = RequestStatus.Rejected;
            Failure = kind;
            FailureMessage = message;
            CompletedAtTick = tick;
            Lease = default;
        }

        public override string ToString() =>
            "Ticket[" + RequestId + "](" + Request + ", " + Status + ", scope=" + ScopeId +
            ", issued=" + IssuedAtTick + (CompletedAtTick >= 0 ? ", closed=" + CompletedAtTick : "") +
            (Failure != RequestFailureKind.None ? ", " + Failure + ": " + FailureMessage : "") +
            (IsDeferred ? ", deferred" : "") + ")";
    }

    /// <summary>Observers see lifecycle transitions without polling; used by the demo presenter.</summary>
    public interface ILifecycleObserver
    {
        void OnScopeOpened(SceneScope scope);
        void OnScopeClosing(SceneScope scope);
        void OnScopeClosed(string scopeId, string sceneName);
        void OnTransitionStarted(string nextSceneName, ClosePolicy policy);
        void OnTransitionEnded(string nextSceneName);
        void OnRequestGranted(SpawnTicket ticket, Lease lease);
        void OnRequestCompleted(SpawnTicket ticket);
        void OnRequestFailed(SpawnTicket ticket);
        void OnIssue(LifecycleIssue issue);
    }

    public sealed class LifecycleSnapshot
    {
        public int ActiveScopes;
        public string CurrentScopeId;
        public int PendingRequests;
        public int GrantedAwaitingReady;
        public int CompletedRequests;
        public int FailedRequests;
        public int CanceledRequests;
        public int DeferredRequests;
        public int ActiveInputSubscriptions;
        public int ActiveRuleSubscriptions;
        public int ActiveTemporaryResources;
        public int ActiveAsyncTasks;
        public int LeasesHeld;
        public int PendingWorkItems;
        public int StaleCallbacks;
        public int LeaksReportedAtClose;

        public int ActiveSubscriptionsTotal =>
            ActiveInputSubscriptions + ActiveRuleSubscriptions + ActiveTemporaryResources + ActiveAsyncTasks;

        public int UnfinishedRequests => PendingRequests + GrantedAwaitingReady + DeferredRequests;

        public override string ToString() =>
            "scopes=" + ActiveScopes + " current=" + (CurrentScopeId ?? "-") + " pending=" + PendingRequests +
            " granted=" + GrantedAwaitingReady + " completed=" + CompletedRequests +
            " failed=" + FailedRequests + " canceled=" + CanceledRequests + " deferred=" + DeferredRequests +
            " subs=" + ActiveSubscriptionsTotal + "(input=" + ActiveInputSubscriptions +
            ", rules=" + ActiveRuleSubscriptions + ", res=" + ActiveTemporaryResources +
            ", async=" + ActiveAsyncTasks + ") leases=" + LeasesHeld + " pendingWork=" + PendingWorkItems +
            " stale=" + StaleCallbacks + " leaksAtClose=" + LeaksReportedAtClose;
    }
}
