using System;
using System.Collections.Generic;
using Lifecycle.Core;

namespace Lifecycle.CoreTests.Fixtures
{
    /// <summary>How a scheduled load behaves when it finally resolves.</summary>
    internal enum CompletionMode
    {
        Succeed,
        Fail,
        ReturnNull
    }

    /// <summary>A scheduled, but not yet resolved, async preparation.</summary>
    internal sealed class PendingLoad
    {
        public PendingLoad(string key, AsyncSlot slot, Lease lease, int generation, CompletionMode mode, string message)
        {
            Key = key;
            Slot = slot;
            Lease = lease;
            Generation = generation;
            Mode = mode;
            Message = message;
        }

        public string Key { get; }
        public AsyncSlot Slot { get; }
        public Lease Lease { get; }
        public int Generation { get; }
        public CompletionMode Mode { get; }
        public string Message { get; }
        public bool Resolved { get; internal set; }

        public override string ToString() => Key + "@GEN[" + Generation + "]";
    }

    /// <summary>
    /// A pooled entity whose activation never completes until a test resolves its scheduled load.
    /// Mutual exclusion is proven the same way the real prop does it: the entity keeps the exact
    /// lease it was activated with, and only the authoritative binder may apply a completion.
    /// That keeps a late completion from a previous borrow from mutating a re-leased instance.
    /// </summary>
    internal sealed class AsyncSlot : PoolSlot, IPooledEntity, IPooledRule, ILateTickable
    {
        public AsyncSlot(string poolId, string entityId, PlacementStamp coldPlacement)
            : base(poolId, entityId, coldPlacement)
        {
        }

        public readonly List<string> Trace = new List<string>();

        // ---- request-supplied configuration, consumed by Activate ----
        public bool AutoComplete;
        public CompletionMode Mode = CompletionMode.Succeed;
        public string FailureMessage;
        public bool ThrowOnActivate;
        public bool ForgetReset;

        // ---- observed effects a real entity would have ----
        public Lease BoundLease;
        public bool ActivationComplete;
        public string PreparationError;
        public int ActivateCount;
        public int DeactivateCount;
        public int AttachCount;
        public int DetachCount;
        public int InputBindings;
        public int RuleBindings;
        public int ResourceAcquired;
        public int ResourceDisposed;
        public int LateTicks;
        public bool Dirty;
        public bool EngineAlive = true;
        public PendingLoad Pending;
        private readonly List<SubscriptionHandle> _handles = new List<SubscriptionHandle>();

        public bool HasInput => InputBindings > 0;
        public bool HasRule => RuleBindings > 0;
        public int ActiveHandles => _handles.Count;

        public override string ToString() =>
            EntityId + "#" + PoolId + "[" + State + ", " + Generation + "]";

        public void Activate(Lease lease)
        {
            BoundLease = lease;
            ActivateCount++;
            Trace.Add("activate:" + lease.Generation.Value);
            if (ThrowOnActivate)
            {
                throw new InvalidOperationException("activate exploded for " + EntityId);
            }

            ActivationComplete = false;
            PreparationError = null;
            Dirty = true;

            // Prove a stale lease is refused at the boundary: the engine bindings only exist if
            // the manager still considers this exact lease current.
            var input = lease.Subscribe(SubscriptionKind.Input, "input:" + EntityId);
            if (input.IsValid)
            {
                InputBindings++;
                _handles.Add(input);
            }

            var rule = lease.Subscribe(SubscriptionKind.RuleEvent, "rule:" + EntityId);
            if (rule.IsValid)
            {
                RuleBindings++;
                _handles.Add(rule);
            }

            var resource = new FakeResource(EntityId + "/tmp-" + lease.Generation.Value);
            var resourceHandle = lease.Subscribe(SubscriptionKind.TemporaryResource, "res:" + EntityId, resource);
            if (resourceHandle.IsValid)
            {
                ResourceAcquired++;
                _handles.Add(resourceHandle);
            }

            if (AutoComplete)
            {
                Complete(Mode, FailureMessage);
            }
        }

        public void Complete(CompletionMode mode, string message = null)
        {
            if (mode == CompletionMode.Fail)
            {
                PreparationError = message ?? ("load failed for " + EntityId);
                ActivationComplete = false;
                return;
            }

            PreparationError = null;
            ActivationComplete = true;
        }

        /// <summary>
        /// A stale completion reached the instance. The lease it was scheduled for is checked
        /// explicitly, so an old borrow's callback can never be mistaken for the current one.
        /// </summary>
        public void ApplyIfCurrent(Lease lease, string what, Action action)
        {
            if (lease.IsAssigned && lease.IsCurrent)
            {
                Trace.Add("applied:" + what);
                action();
                return;
            }

            Trace.Add("rejected-stale:" + what);
        }

        public bool IsActivationComplete => ActivationComplete;

        public string PreparationFailure => PreparationError;

        public void Deactivate(Lease lease)
        {
            DeactivateCount++;
            Trace.Add("deactivate:" + lease.Generation.Value);
            // The entity drops its engine handles; the ledger disposes the resource exactly once.
            InputBindings = 0;
            RuleBindings = 0;
            _handles.Clear();
            ActivationComplete = false;
            Pending = null;
        }

        public void Attach(Lease lease)
        {
            AttachCount++;
        }

        public void Detach(Lease lease)
        {
            DetachCount++;
        }

        public void LateTick(Lease lease) => LateTicks++;

        public override void ResetForPool()
        {
            Trace.Add("reset@" + Generation.Value);
            Pending = null;
            PreparationError = null;
            if (!ForgetReset)
            {
                Dirty = false;
                HasDirtyState = false;
                InputBindings = 0;
                RuleBindings = 0;
                _handles.Clear();
            }
        }
    }

    /// <summary>Async entity source; consumes the next per-entity manifest configuration on Create.</summary>
    internal sealed class AsyncSlotSource : ISlotSource
    {
        private readonly Dictionary<string, Queue<SlotConfig>> _manifest =
            new Dictionary<string, Queue<SlotConfig>>(StringComparer.Ordinal);
        private readonly Dictionary<PoolSlot, AsyncSlot> _slots = new Dictionary<PoolSlot, AsyncSlot>();

        public readonly List<AsyncSlot> Created = new List<AsyncSlot>();
        public int DestroyedCount;
        public bool ReturnNullForNext;
        public bool ThrowOnNextCreate;

        public sealed class SlotConfig
        {
            public bool AutoComplete;
            public CompletionMode Mode;
            public string FailureMessage;
            public bool ThrowOnActivate;
            public bool ForgetReset;
        }

        public void Plan(string entityId, params SlotConfig[] configs)
        {
            var queue = new Queue<SlotConfig>();
            foreach (var config in configs) queue.Enqueue(config);
            _manifest[entityId] = queue;
        }

        public PoolSlot Create(string poolId, string entityId, int slotIndex)
        {
            if (ThrowOnNextCreate)
            {
                ThrowOnNextCreate = false;
                throw new InvalidOperationException("slot factory exploded for " + entityId);
            }

            if (ReturnNullForNext)
            {
                ReturnNullForNext = false;
                return null;
            }

            var slot = new AsyncSlot(poolId, entityId,
                new PlacementStamp(slotIndex, 0f, 0f, 0f, 0f, 0f, 1f, 1f));
            slot.SlotIndex = slotIndex;
            if (_manifest.TryGetValue(entityId, out var queue) && queue.Count > 0)
            {
                var config = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
                slot.AutoComplete = config.AutoComplete;
                slot.Mode = config.Mode;
                slot.FailureMessage = config.FailureMessage;
                slot.ThrowOnActivate = config.ThrowOnActivate;
                slot.ForgetReset = config.ForgetReset;
            }

            _slots[slot] = slot;
            Created.Add(slot);
            return slot;
        }

        public void Reattach(PoolSlot slot)
        {
            ((AsyncSlot)slot).EngineAlive = true;
        }

        public void Detach(PoolSlot slot, ActivationState destination)
        {
        }

        public void Destroy(PoolSlot slot)
        {
            if (_slots.TryGetValue(slot, out var async))
            {
                async.EngineAlive = false;
            }

            DestroyedCount++;
        }

        public PlacementStamp CapturePlacement(PoolSlot slot) => slot.ColdPlacement;

        public void ApplyPlacement(PoolSlot slot, PlacementStamp placement)
        {
        }

        public bool IsAlive(PoolSlot slot) =>
            ((AsyncSlot)slot).EngineAlive && slot.State != ActivationState.Disposed;

        public AsyncSlot SlotFor(SpawnTicket ticket) => ticket.Lease.IsAssigned ? (AsyncSlot)ticket.Lease.Slot : null;
    }

    /// <summary>
    /// Drives asynchronous preparations in a chosen order. Each completion resolves one scheduled
    /// load and carries the generation it was scheduled for, so the harness can apply completions
    /// out of order and prove the generation fence, not just the arrival order.
    /// </summary>
    internal sealed class AsyncScheduler
    {
        private readonly Clock _clock;
        private readonly AsyncSlotSource _source;
        private readonly LifecycleManager _manager;
        private readonly List<PendingLoad> _pending = new List<PendingLoad>();

        public AsyncScheduler(Clock clock, AsyncSlotSource source, LifecycleManager manager)
        {
            _clock = clock;
            _source = source;
            _manager = manager;
        }

        public int PendingCount => _pending.Count;
        public IReadOnlyList<PendingLoad> Pending => _pending;

        /// <summary>Schedules the load for a just-issued request without completing it.</summary>
        public PendingLoad Schedule(SpawnTicket ticket, CompletionMode mode = CompletionMode.Succeed,
            string message = null)
        {
            var slot = _source.SlotFor(ticket);
            if (slot == null) throw new InvalidOperationException("ticket has no slot: " + ticket);
            slot.ActivationComplete = false;
            slot.PreparationError = null;
            slot.Mode = mode;
            slot.FailureMessage = message;
            var load = new PendingLoad(ticket.Request.EntityId + "#" + ticket.RequestId + "#g" +
                                       ticket.Lease.Generation.Value, slot, ticket.Lease,
                ticket.Lease.Generation.Value, mode, message);
            slot.Pending = load;
            _pending.Add(load);
            return load;
        }

        /// <summary>Resolves one scheduled load by position in the schedule and pumps once.</summary>
        public PendingLoad Complete(int index)
        {
            if (index < 0 || index >= _pending.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "no pending load at index " + index);
            }

            var load = _pending[index];
            _pending.RemoveAt(index);
            load.Resolved = true;
            Apply(load);
            _manager.Pump();
            _clock.Advance();
            return load;
        }

        /// <summary>Resolves the oldest pending load (the usual "in order" replay).</summary>
        public PendingLoad CompleteOldest() => Complete(0);

        /// <summary>Resolves the newest pending load (the "reverse order" replay).</summary>
        public PendingLoad CompleteNewest() => Complete(_pending.Count - 1);

        /// <summary>Resolves all pending loads in arrival order, pumping after each.</summary>
        public void CompleteAllInArrivalOrder()
        {
            while (_pending.Count > 0) CompleteOldest();
        }

        /// <summary>Resolves all pending loads in reverse arrival order, pumping after each.</summary>
        public void CompleteAllInReverseOrder()
        {
            while (_pending.Count > 0) CompleteNewest();
        }

        /// <summary>
        /// Applies a completion through the generation fence. A load whose slot has since been
        /// re-leased is rejected and must not mutate the instance.
        /// </summary>
        private void Apply(PendingLoad load)
        {
            var generationCurrent = _manager.IsGenerationCurrent(load.Lease);
            if (!generationCurrent)
            {
                load.Slot.ApplyIfCurrent(load.Lease, "stale-load", () => { });
                return;
            }

            if (load.Mode == CompletionMode.Fail)
            {
                load.Slot.Complete(CompletionMode.Fail, load.Message);
                return;
            }

            if (load.Mode == CompletionMode.ReturnNull)
            {
                load.Slot.Complete(CompletionMode.Fail, "provider returned null for " + load.Slot.EntityId);
                return;
            }

            load.Slot.Complete(CompletionMode.Succeed);
        }
    }
}
