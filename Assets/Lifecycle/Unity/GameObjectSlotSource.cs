using System.Collections.Generic;
using Lifecycle.Core;
using UnityEngine;

namespace Lifecycle.Unity
{
    /// <summary>
    /// Creates and destroys real GameObjects for a pool. Activation is split so the visual object
    /// stays inactive while the detached preparation copy loads its assets; the lifecycle flips
    /// active only when the pool reports the generation Ready.
    /// </summary>
    public sealed class GameObjectSlotSource : ISlotSource
    {
        private readonly Transform _poolRoot;
        private readonly GameObject _prototype;
        private readonly string _entityName;

        public GameObjectSlotSource(Transform poolRoot, GameObject prototype, string entityName)
        {
            _poolRoot = poolRoot;
            _prototype = prototype;
            _entityName = entityName;
        }

        public PoolSlot Create(string poolId, string entityId, int slotIndex)
        {
            var go = Object.Instantiate(_prototype, _poolRoot);
            go.name = _entityName + "#" + slotIndex;
            go.SetActive(false);
            var behaviour = go.GetComponent<PropEntity>();
            if (behaviour == null)
            {
                Object.Destroy(go);
                return null;
            }

            var placement = TransformCapture.Capture(go.transform);
            var slot = new PropEntitySlot(poolId, entityId, go, behaviour, placement);
            behaviour.BindSlot(slot);
            return slot;
        }

        public void Reattach(PoolSlot slot)
        {
            var unitySlot = (PropEntitySlot)slot;
            unitySlot.RestoreForLease();
        }

        public void Detach(PoolSlot slot, ActivationState destination)
        {
            var unitySlot = (PropEntitySlot)slot;
            unitySlot.EnterColdState(destination);
        }

        public void Destroy(PoolSlot slot)
        {
            var unitySlot = (PropEntitySlot)slot;
            if (unitySlot.Go != null) Object.Destroy(unitySlot.Go);
        }

        public PlacementStamp CapturePlacement(PoolSlot slot)
        {
            var unitySlot = (PropEntitySlot)slot;
            return unitySlot.Go != null ? TransformCapture.Capture(unitySlot.Go.transform) : PlacementStamp.None;
        }

        public void ApplyPlacement(PoolSlot slot, PlacementStamp placement)
        {
            var unitySlot = (PropEntitySlot)slot;
            if (unitySlot.Go != null) TransformCapture.Apply(unitySlot.Go.transform, placement);
        }

        public bool IsAlive(PoolSlot slot) => ((PropEntitySlot)slot).IsAlive;
    }

    /// <summary>
    /// GameObject wrapper that keeps the Unity-specific lifetime separate from the core slot
    /// contract. A destroyed GameObject can never be re-leased: IsAlive goes false and the pool
    /// evicts the slot instead of handing out a dead reference.
    /// </summary>
    public sealed class PropEntitySlot : PoolSlot, IPooledEntity, IPooledRule, ILateTickable
    {
        public PropEntitySlot(string poolId, string entityId, GameObject go,
            PropEntity behaviour, PlacementStamp coldPlacement)
            : base(poolId, entityId, coldPlacement)
        {
            Go = go;
            Behaviour = behaviour;
        }

        public GameObject Go { get; private set; }
        public PropEntity Behaviour { get; }
        public bool IsAlive => Go != null && !Broken && State != ActivationState.Disposed;

        // IPooledEntity / IPooledRule / ILateTickable delegate to the MonoBehaviour; the slot is
        // the lifetime token, the behaviour is the thing that touches the scene.
        public void Activate(Lease lease) => Behaviour.Activate(lease, this);
        public bool IsActivationComplete => Behaviour.IsActivationComplete;
        public string PreparationFailure => Behaviour.PreparationFailure;
        public void Deactivate(Lease lease) => Behaviour.Deactivate(lease, this);
        public void Attach(Lease lease) => Behaviour.AttachRule(lease, this);
        public void Detach(Lease lease) => Behaviour.DetachRule(lease, this);
        public void LateTick(Lease lease) => Behaviour.LateTick(lease, this);

        internal void RestoreForLease()
        {
            if (Go == null) return;
            Go.transform.SetParent(Behaviour.DefaultParent, true);
            TransformCapture.Apply(Go.transform, ColdPlacement);
            // Active so the MonoBehaviour's Update can drive preparation; the visual is still
            // configured by the entity during Activate, and readiness gates actual gameplay.
            Go.SetActive(true);
        }

        internal void EnterColdState(ActivationState destination)
        {
            if (Go == null) return;
            Go.SetActive(false);
            TransformCapture.Apply(Go.transform, ColdPlacement);
            // Pool root is inactive; a cold object must not render or accept input.
            Go.transform.SetParent(Behaviour.PoolParent, true);
        }

        protected override void OnBecameReady()
        {
            if (Go != null) Go.SetActive(true);
        }

        public override void ResetForPool()
        {
            base.ResetForPool();
            if (Go != null) Go.SetActive(false);
            TransformCapture.Apply(Go.transform, ColdPlacement);
        }
    }

    public static class TransformCapture
    {
        public static PlacementStamp Capture(Transform t) =>
            new PlacementStamp(t.localPosition.x, t.localPosition.y, t.localPosition.z,
                t.localRotation.x, t.localRotation.y, t.localRotation.z, t.localRotation.w,
                t.localScale.x);

        public static void Apply(Transform t, PlacementStamp p)
        {
            t.localPosition = new Vector3(p.Px, p.Py, p.Pz);
            t.localRotation = new Quaternion(p.Rx, p.Ry, p.Rz, p.Rw);
            t.localScale = new Vector3(p.Scale, p.Scale, p.Scale);
        }
    }
}
