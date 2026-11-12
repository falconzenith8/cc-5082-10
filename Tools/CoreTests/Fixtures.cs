using System;
using System.Collections.Generic;
using Lifecycle.Core;

namespace Lifecycle.CoreTests
{
    /// <summary>Engine object stand-in that records what a real prop would actually do.</summary>
    internal sealed class FakeGadget : PoolSlot, IPooledEntity, IPooledRule, ILateTickable
    {
        public FakeGadget(string poolId, string entityId, PlacementStamp coldPlacement)
            : base(poolId, entityId, coldPlacement)
        {
        }

        public readonly List<string> Trace = new List<string>();

        public bool AsyncPending;
        public bool FailPreparation;
        public bool ThrowOnActivate;
        public bool ForgetReset;
        public bool EngineAlive = true;
        public bool LateTickIncrements;
        public int LateTicks;
        public int ActivateCount;
        public int DeactivateCount;
        public int AttachCount;
        public int DetachCount;
        public bool InputActive;
        public bool RuleActive;
        public TemporaryResource Resource;
        public int ResourceAcquired;
        public int ResourceDisposed;
        public Lease BoundLease;
        public string PreparationError;
        private bool _failureReported;

        public override string ToString() => EntityId + "#" + PoolId + "[" + State + ", " + Generation + "]";

        // ---- IPooledEntity ----
        public void Activate(Lease lease)
        {
            BoundLease = lease;
            ActivateCount++;
            Trace.Add("activate:" + lease.Generation.Value);
            if (ThrowOnActivate)
            {
                throw new InvalidOperationException("activate exploded for " + EntityId);
            }

            HasDirtyState = true;
            InputActive = true;
            RuleActive = true;
            Resource = new FakeResource(EntityId + "/tmp-" + lease.Generation.Value);
            ResourceAcquired++;
            lease.Subscribe(SubscriptionKind.Input, "input:" + EntityId);
            lease.Subscribe(SubscriptionKind.RuleEvent, "rule:" + EntityId);
            lease.Subscribe(SubscriptionKind.TemporaryResource, "res:" + EntityId, Resource);
            AsyncPending = true;
        }

        public bool IsActivationComplete => !AsyncPending && !FailPreparation;

        public string PreparationFailure
        {
            get
            {
                if (FailPreparation) return PreparationError ?? ("load failed for " + EntityId);
                return null;
            }
        }

        /// <summary>Consume a configured failure the first time the pump observes it.</summary>
        public void ConsumeFailure()
        {
            _failureReported = true;
        }

        public void Deactivate(Lease lease)
        {
            DeactivateCount++;
            Trace.Add("deactivate:" + lease.Generation.Value);
            InputActive = false;
            RuleActive = false;
            // TemporaryResource disposal is the ledger's job (it owns the payload); the entity
            // only drops its reference so a stale field can't be reused.
            if (Resource != null)
            {
                ResourceDisposed++;
                Resource = null;
            }
        }

        // ---- IPooledRule ----
        public void Attach(Lease lease)
        {
            AttachCount++;
            LateTickIncrements = true;
        }

        public void Detach(Lease lease)
        {
            DetachCount++;
            LateTickIncrements = false;
        }

        // ---- IResetable ----
        public override void ResetForPool()
        {
            Trace.Add("reset");
            FailPreparation = false;
            PreparationError = null;
            if (!ForgetReset)
            {
                HasDirtyState = false;
                InputActive = false;
                RuleActive = false;
            }
        }

        public void LateTick(Lease lease)
        {
            if (LateTickIncrements) LateTicks++;
        }
    }

    internal sealed class FakeResource : TemporaryResource
    {
        private bool _disposed;

        public FakeResource(string key)
        {
            Key = key;
        }

        public string Key { get; }
        public override string ResourceKey => Key;
        public bool Disposed => _disposed;

        public override void Dispose()
        {
            if (_disposed) throw new InvalidOperationException("double dispose of " + Key);
            _disposed = true;
        }
    }

    /// <summary>In-memory placement store, standing in for Unity transforms.</summary>
    internal sealed class FakeSlotSource : ISlotSource
    {
        public readonly Dictionary<PoolSlot, PlacementStamp> Placements = new Dictionary<PoolSlot, PlacementStamp>();
        public readonly List<FakeGadget> Created = new List<FakeGadget>();
        public int CreatedNullFor = -1;
        public int DestroyedCount;
        public bool DestroyExternalObjects;

        public PoolSlot Create(string poolId, string entityId, int slotIndex)
        {
            if (slotIndex == CreatedNullFor) return null;
            var slot = new FakeGadget(poolId, entityId, new PlacementStamp(slotIndex, 0f, 0f, 0f, 0f, 0f, 1f, 1f));
            Placements[slot] = slot.ColdPlacement;
            Created.Add(slot);
            return slot;
        }

        public void Reattach(PoolSlot slot)
        {
            var gadget = (FakeGadget)slot;
            gadget.EngineAlive = true;
        }

        public void Detach(PoolSlot slot, ActivationState destination)
        {
        }

        public void Destroy(PoolSlot slot)
        {
            Placements.Remove(slot);
            ((FakeGadget)slot).EngineAlive = false;
            DestroyedCount++;
        }

        public PlacementStamp CapturePlacement(PoolSlot slot) =>
            Placements.TryGetValue(slot, out var placement) ? placement : PlacementStamp.None;

        public void ApplyPlacement(PoolSlot slot, PlacementStamp placement) => Placements[slot] = placement;

        public bool IsAlive(PoolSlot slot)
        {
            var gadget = (FakeGadget)slot;
            return gadget.EngineAlive && slot.State != ActivationState.Disposed;
        }
    }
}
