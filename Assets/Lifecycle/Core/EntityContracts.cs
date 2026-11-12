using System;

namespace Lifecycle.Core
{
    /// <summary>Engine-neutral transform snapshot captured when an instance leaves the pool.</summary>
    public readonly struct PlacementStamp : IEquatable<PlacementStamp>
    {
        public static readonly PlacementStamp None = new PlacementStamp(0f, 0f, 0f, 0f, 0f, 0f, 0f, 1f);

        public PlacementStamp(float px, float py, float pz, float rx, float ry, float rz, float rw, float scale)
        {
            Px = px; Py = py; Pz = pz;
            Rx = rx; Ry = ry; Rz = rz; Rw = rw;
            Scale = scale;
        }

        public float Px { get; }
        public float Py { get; }
        public float Pz { get; }
        public float Rx { get; }
        public float Ry { get; }
        public float Rz { get; }
        public float Rw { get; }
        public float Scale { get; }

        public bool Equals(PlacementStamp other) =>
            Px == other.Px && Py == other.Py && Pz == other.Pz &&
            Rx == other.Rx && Ry == other.Ry && Rz == other.Rz && Rw == other.Rw && Scale == other.Scale;

        public override bool Equals(object obj) => obj is PlacementStamp other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var h = Px.GetHashCode();
                h = (h * 397) ^ Py.GetHashCode();
                h = (h * 397) ^ Pz.GetHashCode();
                h = (h * 397) ^ Rx.GetHashCode();
                h = (h * 397) ^ Ry.GetHashCode();
                h = (h * 397) ^ Rz.GetHashCode();
                h = (h * 397) ^ Rw.GetHashCode();
                h = (h * 397) ^ Scale.GetHashCode();
                return h;
            }
        }

        public override string ToString() =>
            "pos(" + Px + "," + Py + "," + Pz + ") rot(" + Rx + "," + Ry + "," + Rz + "," + Rw + ") scale=" + Scale;
    }

    /// <summary>
    /// Context handed to entity callbacks. Carries the generation and the binder for the exact
    /// lease; copying the context does not grant new rights. Nothing here can mutate the lease
    /// unless the binder still reports it current.
    /// </summary>
    public readonly struct Lease
    {
        internal Lease(PoolSlot slot, string scopeId, string entityId, string poolId, long leaseId,
            Generation generation, long leasedAtTick, int lineage, ILeaseBinder binder)
        {
            Slot = slot;
            ScopeId = scopeId;
            EntityId = entityId;
            PoolId = poolId;
            LeaseId = leaseId;
            Generation = generation;
            LeasedAtTick = leasedAtTick;
            Lineage = lineage;
            Binder = binder;
        }

        public PoolSlot Slot { get; }
        public string ScopeId { get; }
        public string EntityId { get; }
        public string PoolId { get; }
        public long LeaseId { get; }
        public Generation Generation { get; }
        public long LeasedAtTick { get; }
        /// <summary>Global, monotonically increasing borrow ordinal; orders requests and borrows.</summary>
        public int Lineage { get; }
        internal ILeaseBinder Binder { get; }

        public bool IsAssigned => Slot != null;

        /// <summary>True while this exact lease is still the live lease of the slot in the current scope.</summary>
        public bool IsCurrent => Binder != null && Binder.IsCurrent(this);

        /// <summary>Registers engine state owned by this lease. Invalid once the lease is stale.</summary>
        public SubscriptionHandle Subscribe(SubscriptionKind kind, string key, object payload = null) =>
            Binder != null ? Binder.Subscribe(this, kind, key, payload) : SubscriptionHandle.None;

        /// <summary>Removes one reference-counted registration owned by this lease.</summary>
        public bool Unsubscribe(SubscriptionHandle handle) =>
            Binder != null && Binder.Unsubscribe(this, handle);

        public override string ToString() =>
            IsAssigned
                ? "Lease(" + EntityId + "#" + PoolId + ", " + Generation + ", leaseId=" + LeaseId +
                  ", lineage=" + Lineage + ", scope=" + ScopeId + ")"
                : "Lease(unassigned)";
    }

    /// <summary>Bridge from lease copies to the manager that owns the authoritative lease book.</summary>
    public interface ILeaseBinder
    {
        bool IsCurrent(Lease lease);
        SubscriptionHandle Subscribe(Lease lease, SubscriptionKind kind, string key, object payload);
        bool Unsubscribe(Lease lease, SubscriptionHandle handle);
    }

    /// <summary>Engine-facing behaviour of a pooled interactive entity.</summary>
    public interface IPooledEntity
    {
        /// <summary>Begin per-lease work. Async preparation may continue after this returns.</summary>
        void Activate(Lease lease);

        /// <summary>False while a deferred load/task is outstanding.</summary>
        bool IsActivationComplete { get; }

        /// <summary>Non-null when preparation failed; consumed by the manager to fail the request.</summary>
        string PreparationFailure { get; }

        /// <summary>Stop work and reverse activation. Must be safe to call once per lease.</summary>
        void Deactivate(Lease lease);
    }

    /// <summary>A reusable rule hook (event listener / game rule) attached to a lease.</summary>
    public interface IPooledRule
    {
        void Attach(Lease lease);
        void Detach(Lease lease);
    }

    /// <summary>
    /// State that must not survive between borrows (device flags, timers, tint, attachments).
    /// The lifecycle calls ResetForPool on return and refuses to pool instances that stay dirty.
    /// </summary>
    public interface IResetable
    {
        bool HasDirtyState { get; }
        void ResetForPool();
    }

    /// <summary>Optional late-tick hook; only live, Ready leases receive it.</summary>
    public interface ILateTickable
    {
        void LateTick(Lease lease);
    }
}
