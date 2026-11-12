using System;
using System.Collections.Generic;

namespace Lifecycle.Core
{
    public enum ActivationState
    {
        /// <summary>Cold instance, never borrowed or already reset; not usable.</summary>
        Inactive,
        /// <summary>Borrowed, activation running (possibly awaiting async preparation).</summary>
        Preparing,
        /// <summary>Activation complete and lease live; usable.</summary>
        Ready,
        /// <summary>Deactivation running; old-generation callbacks must not mutate it.</summary>
        Releasing,
        /// <summary>Clean and waiting for the next borrow.</summary>
        Pooled,
        /// <summary>Permanent teardown.</summary>
        Disposed
    }

    /// <summary>
    /// A reusable instance handed out by a <see cref="ObjectPool{T}"/>. The pool is the only
    /// writer of <see cref="Generation"/>; every other component observes it.
    /// </summary>
    public abstract class PoolSlot : IResetable
    {
        protected PoolSlot(string poolId, string entityId, PlacementStamp coldPlacement)
        {
            PoolId = poolId;
            EntityId = entityId;
            ColdPlacement = coldPlacement;
        }

        public string PoolId { get; }
        public string EntityId { get; }
        public int SlotIndex { get; internal set; }

        /// <summary>Generation of the current or most recent borrow. 0 means never borrowed.</summary>
        public Generation Generation { get; internal set; } = Generation.None;

        /// <summary>True once the underlying engine object is unusable (destroyed externally).</summary>
        public bool Broken { get; internal set; }

        public ActivationState State { get; internal set; } = ActivationState.Inactive;

        public Lease CurrentLease { get; internal set; }

        public PlacementStamp ColdPlacement { get; }

        /// <summary>-1 while preparing; completion tick once ready.</summary>
        public long PreparedAtTick { get; internal set; } = -1;

        public bool HasDirtyState { get; protected internal set; }

        /// <summary>Called once preparation finishes, before the ticket is reported complete.</summary>
        protected internal virtual void OnBecameReady() { }

        public virtual void ResetForPool()
        {
            HasDirtyState = false;
        }

        public override string ToString() =>
            EntityId + "#" + PoolId + "[" + State + ", " + Generation + "]";
    }

    public delegate PoolSlot SlotFactory(string poolId, string entityId, int slotIndex);

    /// <summary>Engine-facing operations the pool needs. Unity binds these to GameObject/Component.</summary>
    public interface ISlotSource
    {
        /// <summary>Create an inactive instance. May return null when the engine object was destroyed.</summary>
        PoolSlot Create(string poolId, string entityId, int slotIndex);

        /// <summary>Reactivate a pool-owned instance for a new generation.</summary>
        void Reattach(PoolSlot slot);

        /// <summary>Move the instance's engine object out of the active scene (deactivate/detach).</summary>
        void Detach(PoolSlot slot, ActivationState destination);

        /// <summary>Permanently destroy the engine object.</summary>
        void Destroy(PoolSlot slot);

        /// <summary>Capture placement before the entity leaves the pool.</summary>
        PlacementStamp CapturePlacement(PoolSlot slot);

        /// <summary>Write a placement back to the engine object.</summary>
        void ApplyPlacement(PoolSlot slot, PlacementStamp placement);

        bool IsAlive(PoolSlot slot);
    }

    /// <summary>Headless source for tests: plain objects, placements held in memory.</summary>
    public sealed class DelegateSlotSource : ISlotSource
    {
        private readonly SlotFactory _factory;
        private readonly Dictionary<PoolSlot, PlacementStamp> _placements = new Dictionary<PoolSlot, PlacementStamp>();

        public DelegateSlotSource(SlotFactory factory)
        {
            _factory = factory;
        }

        public PoolSlot Create(string poolId, string entityId, int slotIndex)
        {
            var slot = _factory(poolId, entityId, slotIndex);
            if (slot != null)
            {
                slot.SlotIndex = slotIndex;
                _placements[slot] = slot.ColdPlacement;
            }

            return slot;
        }

        public void Reattach(PoolSlot slot) { }
        public void Detach(PoolSlot slot, ActivationState destination) { }

        public void Destroy(PoolSlot slot)
        {
            _placements.Remove(slot);
            slot.Broken = true;
        }

        public PlacementStamp CapturePlacement(PoolSlot slot) =>
            _placements.TryGetValue(slot, out var placement) ? placement : PlacementStamp.None;

        public void ApplyPlacement(PoolSlot slot, PlacementStamp placement)
        {
            _placements[slot] = placement;
        }

        public bool IsAlive(PoolSlot slot) => !slot.Broken && slot.State != ActivationState.Disposed;
    }

    /// <summary>
    /// Generation pool for one entity kind inside one scene scope.
    ///
    /// Guarantees:
    /// - Every <see cref="Rent"/> advances the slot generation; generations are never reused.
    /// - The lease is fully written before user code (Activate) runs, so a callback triggered
    ///   synchronously by Activate already sees the final generation.
    /// - Return is recorded before Deactivate runs, so a stale callback firing during teardown
    ///   fails the currency check instead of mutating the next lease.
    /// - A slot whose reset leaves dirty state, or whose engine object died, is evicted rather
    ///   than returned; that is reported, never masked.
    /// </summary>
    public sealed class ObjectPool<T> where T : PoolSlot
    {
        private readonly List<T> _free = new List<T>();
        private readonly HashSet<T> _leased = new HashSet<T>();
        private readonly List<T> _all = new List<T>();
        private readonly ISlotSource _source;
        private readonly ILifecycleReporter _reporter;
        private readonly int _maxSlots;

        public ObjectPool(string poolId, ISlotSource source, ILifecycleReporter reporter, int maxSlots = 128)
        {
            PoolId = poolId;
            _source = source;
            _reporter = reporter;
            _maxSlots = maxSlots;
        }

        public string PoolId { get; }
        public int FreeCount => _free.Count;
        public int LeasedCount => _leased.Count;
        public int TotalCount => _all.Count;
        public IReadOnlyList<T> AllSlots => _all;

        internal T Rent(string scopeId, string entityId, long tick, int lineage)
        {
            var slot = TakeFree();
            if (slot == null)
            {
                if (_all.Count >= _maxSlots) throw new PoolCapacityException(PoolId, _maxSlots);
                slot = (T)_source.Create(PoolId, entityId, _all.Count);
                if (slot == null) return null;
                slot.SlotIndex = _all.Count;
                _all.Add(slot);
            }

            if (!_source.IsAlive(slot) || slot.Broken)
            {
                Evict(slot);
                return Rent(scopeId, entityId, tick, lineage);
            }

            slot.Generation = slot.Generation.Next();
            slot.CurrentLease = default;
            slot.PreparedAtTick = -1;
            slot.State = ActivationState.Preparing;
            slot.HasDirtyState = true;
            _leased.Add(slot);
            _source.Reattach(slot);
            return slot;
        }

        /// <summary>Bind the lease after the manager has assigned its lease id and binder.</summary>
        internal void BindLease(T slot, Lease lease)
        {
            slot.CurrentLease = lease;
        }

        internal void MarkReady(T slot)
        {
            if (slot.State != ActivationState.Preparing)
            {
                _reporter?.Report(new LifecycleIssue(LifecycleIssueCode.StaleCallback, slot.CurrentLease.ScopeId,
                    slot.EntityId, slot.Generation,
                    "Readiness for " + slot.Generation + " arrived while slot was " + slot.State + "; ignored."));
                return;
            }

            slot.State = ActivationState.Ready;
            slot.PreparedAtTick = slot.CurrentLease.LeasedAtTick;
            slot.OnBecameReady();
        }

        /// <summary>
        /// Returns the lease to the pool. Returns false and reports when the lease is stale,
        /// was already returned, belongs to another pool, or the instance could not be reset.
        /// </summary>
        internal bool Return(T slot, Lease lease, LeaseReturnReason reason)
        {
            if (!_leased.Contains(slot) || !slot.CurrentLease.IsAssigned ||
                slot.CurrentLease.LeaseId != lease.LeaseId ||
                slot.CurrentLease.Generation != lease.Generation ||
                slot.Generation != lease.Generation)
            {
                _reporter?.Report(new LifecycleIssue(LifecycleIssueCode.DuplicateRelease, lease.ScopeId, lease.EntityId,
                    lease.Generation,
                    "Return rejected for " + lease + " (slot is " + slot.State + ", current lease " +
                    (slot.CurrentLease.IsAssigned ? slot.CurrentLease.ToString() : "none") + ", reason=" + reason + ")."));
                return false;
            }

            if (slot.PoolId != PoolId)
            {
                _reporter?.Report(new LifecycleIssue(LifecycleIssueCode.StaleCallback, lease.ScopeId, lease.EntityId,
                    lease.Generation, "Return routed to the wrong pool: " + PoolId + " vs " + slot.PoolId + "."));
                return false;
            }

            _leased.Remove(slot);
            slot.State = ActivationState.Releasing;
            return true;
        }

        /// <summary>
        /// Final half of return: engine teardown ordering. Deactivation callbacks run while the
        /// slot generation is still the old one but the lease is no longer live, so any callback
        /// that consults the manager is rejected. Then reset is verified.
        /// </summary>
        internal void FinishReturn(T slot, Lease lease, bool engineObjectAlive)
        {
            if (!engineObjectAlive || slot.Broken)
            {
                Evict(slot);
                return;
            }

            slot.ResetForPool();
            if (slot.HasDirtyState)
            {
                _reporter?.Report(new LifecycleIssue(LifecycleIssueCode.UnresetState, lease.ScopeId, lease.EntityId,
                    lease.Generation, "ResetForPool left dirty state on " + slot.EntityId + "; evicted instead of pooled."));
                Evict(slot);
                return;
            }

            slot.CurrentLease = default;
            slot.State = ActivationState.Pooled;
            _free.Add(slot);
        }

        internal void Evict(T slot)
        {
            if (slot == null) return;
            _free.Remove(slot);
            _leased.Remove(slot);
            _all.Remove(slot);
            try
            {
                _source.Destroy(slot);
            }
            catch (Exception ex)
            {
                _reporter?.Report(new LifecycleIssue(LifecycleIssueCode.ScopeLeakedState, null, slot.EntityId,
                    slot.Generation, "Slot destruction threw: " + ex.Message));
            }

            slot.Broken = true;
            slot.State = ActivationState.Disposed;
        }

        internal void DisposeAll()
        {
            for (var i = _all.Count - 1; i >= 0; i--) Evict(_all[i]);
            _free.Clear();
            _leased.Clear();
            _all.Clear();
        }

        private T TakeFree()
        {
            var index = _free.Count - 1;
            if (index < 0) return null;
            var slot = _free[index];
            _free.RemoveAt(index);
            return slot;
        }

        public override string ToString() =>
            "Pool(" + PoolId + ", free=" + FreeCount + ", leased=" + LeasedCount + ", total=" + TotalCount + ")";
    }

    public enum LeaseReturnReason
    {
        Normal,
        LoadFailed,
        ScopeClosing,
        Canceled,
        EntityDestroyed
    }

    public sealed class PoolCapacityException : InvalidOperationException
    {
        public PoolCapacityException(string poolId, int max)
            : base("Pool '" + poolId + "' reached its cap of " + max + " slots.") { }
    }
}
