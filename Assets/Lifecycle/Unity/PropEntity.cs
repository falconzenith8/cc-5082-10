using System.Collections;
using Lifecycle.Core;
using UnityEngine;

namespace Lifecycle.Unity
{
    /// <summary>
    /// A real interactive prop. It owns the Unity side effects a pooled object actually has:
    /// an input-action binding, a rule-event listener on the scene rule bus, and a temporary
    /// material instance. Activation waits a few frames (simulating resource loading) before
    /// reporting completion; failure is surfaced through <see cref="PreparationFailure"/> so the
    /// manager fails the request instead of letting a broken prop become usable.
    /// </summary>
    public sealed class PropEntity : MonoBehaviour
    {
        public enum PropKind
        {
            Pickup,
            WeaponAttachment,
            Machine
        }

        [Header("Identity")]
        public PropKind Kind = PropKind.Pickup;
        public string EntityLabel = "prop";

        [Header("Visuals")]
        public Renderer Body;
        public Color BaseColor = Color.white;

        [Header("Pulse")]
        public float PulseAmplitude = 0.25f;
        public float PulseSpeed = 2f;

        [Header("Preparation")]
        public int PreparationFrames = 2;

        public Transform DefaultParent;
        public Transform PoolParent;

        private static bool _failNextActivation;
        private static string _failureInjectionMessage = "injected asset load failure";

        private PropEntitySlot _slot;
        private Lease _lease;
        private int _readyFrame;
        private string _failure;
        private bool _activated;
        private Material _materialInstance;
        private TemporaryResourceHandle _glowResource;
        private SubscriptionHandle _inputSubscription;
        private SubscriptionHandle _ruleSubscription;
        private bool _hasInputSubscription;
        private bool _hasRuleSubscription;
        private Color _baseColorBeforeLease;
        private int _activationCount;

        public int ActivationCount => _activationCount;
        public bool IsActivated => _activated;
        public string ActiveRule => _hasRuleSubscription ? RuleForKind(Kind) : null;
        public bool HasInputBinding => _hasInputSubscription && _inputSubscription.IsValid;
        public bool HasTemporaryResource => _glowResource != null && !_glowResource.IsDisposed;

        /// <summary>Test/demo hook: make the next activation fail its async preparation.</summary>
        public static void FailNextActivation(string message = null)
        {
            _failNextActivation = true;
            if (!string.IsNullOrEmpty(message)) _failureInjectionMessage = message;
        }

        public static void ClearFailureInjection() => _failNextActivation = false;

        public void BindSlot(PropEntitySlot slot) => _slot = slot;

        private void Awake()
        {
            if (Body != null && Application.isPlaying) BaseColor = Body.material.color;
        }

        #region IPooledEntity

        public void Activate(Lease lease, PropEntitySlot slot)
        {
            if (!IsSlotCurrent(slot, lease)) return;

            _slot = slot;
            _lease = lease;
            _activated = true;
            _activationCount++;
            _readyFrame = Time.frameCount + Mathf.Max(1, PreparationFrames);
            _failure = null;

            if (_failNextActivation)
            {
                _failNextActivation = false;
                _failure = _failureInjectionMessage;
            }

            _baseColorBeforeLease = BaseColor;
            _materialInstance = Body != null ? Body.material : null;
            if (_materialInstance != null) _materialInstance.color = BaseColor;

            if (Kind == PropKind.Machine && Body != null)
            {
                _glowResource = new TemporaryResourceHandle("machine-glow:" + slot.EntityId, RestoreBaseTint);
                lease.Subscribe(SubscriptionKind.TemporaryResource, _glowResource.Key, _glowResource);
            }

            // Register through the lease only. The ledger owns the attachment and drives the sink,
            // which performs the real router/bus registration. That keeps the engine bindings and
            // the reported subscription counts in lockstep.
            var inputPayload = new InputSubscriptionPayload(slot, lease, InputRouter.GetAction(Kind), OnInputPressed);
            _inputSubscription = lease.Subscribe(SubscriptionKind.Input, "input:" + slot.EntityId, inputPayload);
            _hasInputSubscription = _inputSubscription.IsValid;

            var rulePayload = new RuleSubscriptionPayload(slot, lease, RuleForKind(Kind), OnRuleEvent);
            _ruleSubscription = lease.Subscribe(SubscriptionKind.RuleEvent, "rule:" + slot.EntityId, rulePayload);
            _hasRuleSubscription = _ruleSubscription.IsValid;
        }

        public bool IsActivationComplete => _failure == null && Time.frameCount >= _readyFrame;

        public string PreparationFailure => _failure;

        public void Deactivate(Lease lease, PropEntitySlot slot)
        {
            if (!IsLeaseOfSlot(slot, lease)) return;

            _activated = false;

            // The ledger retires these subscriptions for this lease during scope close/return;
            // detaching here would double-remove, so only drop local references. Explicit
            // unsubscribes still route through the lease for API completeness.
            _hasInputSubscription = false;
            _hasRuleSubscription = false;
            _inputSubscription = SubscriptionHandle.None;
            _ruleSubscription = SubscriptionHandle.None;

            // The ledger owns the temporary resource and disposes it; the entity drops the reference.
            _glowResource = null;

            if (_materialInstance != null && Body != null)
            {
                Body.material.color = _baseColorBeforeLease;
                _materialInstance = null;
            }

            var collider = GetComponent<Collider>();
            if (collider != null) collider.enabled = false;
            transform.localScale = Vector3.one;
        }

        #endregion

        #region IPooledRule

        public void AttachRule(Lease lease, PropEntitySlot slot)
        {
            // Rule registration happens in Activate through the same lease binder, so a stale
            // lease can never register into the live rule bus. Nothing further is needed.
        }

        public void DetachRule(Lease lease, PropEntitySlot slot)
        {
            // Symmetric: Deactivate removes the subscription through the ledger.
        }

        #endregion

        #region Late tick

        public void LateTick(Lease lease, PropEntitySlot slot)
        {
            if (!_activated || !IsSlotCurrent(slot, lease)) return;
            if (Kind != PropKind.Machine || Body == null) return;

            var glow = 0.5f + 0.5f * Mathf.Sin(Time.timeSinceLevelLoad * PulseSpeed);
            if (Body.material != null)
            {
                Body.material.color = Color.Lerp(BaseColor, BaseColor * 0.2f + Color.white * 0.1f,
                    glow * PulseAmplitude);
            }
        }

        #endregion

        internal void OnInputPressed(Lease lease)
        {
            if (!IsSlotCurrent(_slot, lease) || !_activated) return;
            StartCoroutine(Squash());
        }

        internal void OnRuleEvent(RuleEvent ruleEvent, Lease lease)
        {
            if (!IsSlotCurrent(_slot, lease) || !_activated) return;
            if (ruleEvent.PressCount % 5 != 0) return;
            StartCoroutine(Tint(0.35f));
        }

        private IEnumerator Squash()
        {
            transform.localScale = Vector3.one * 1.15f;
            yield return new WaitForSeconds(0.15f);
            if (_activated) transform.localScale = Vector3.one;
        }

        private IEnumerator Tint(float seconds)
        {
            if (Body == null) yield break;
            Body.material.color = Color.white;
            yield return new WaitForSeconds(seconds);
            if (_activated) Body.material.color = BaseColor;
        }

        private void RestoreBaseTint()
        {
            if (Body != null) Body.material.color = _baseColorBeforeLease;
        }

        private static bool IsSlotCurrent(PropEntitySlot slot, Lease lease)
        {
            return slot != null && slot.Generation == lease.Generation && lease.IsCurrent;
        }

        private static bool IsLeaseOfSlot(PropEntitySlot slot, Lease lease)
        {
            return slot != null && slot.CurrentLease.LeaseId == lease.LeaseId &&
                   slot.CurrentLease.Generation == lease.Generation;
        }

        private static string RuleForKind(PropKind kind)
        {
            switch (kind)
            {
                case PropKind.Pickup: return "rule.pickup";
                case PropKind.WeaponAttachment: return "rule.attachment";
                default: return "rule.machine";
            }
        }
    }
}
