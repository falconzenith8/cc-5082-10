using System;
using System.Collections.Generic;
using Lifecycle.Core;

namespace Lifecycle.Unity
{
    /// <summary>A temporary resource that runs a cleanup callback exactly once, when disposed.</summary>
    public sealed class TemporaryResourceHandle : TemporaryResource
    {
        private readonly Action _onDispose;
        private bool _disposed;

        public TemporaryResourceHandle(string key, Action onDispose)
        {
            Key = key;
            _onDispose = onDispose;
        }

        public string Key { get; }
        public override string ResourceKey => Key;
        public bool IsDisposed => _disposed;

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _onDispose?.Invoke();
        }
    }

    /// <summary>An input registration the game's input router owns; Unbind detaches for real.</summary>
    public sealed class InputBinding
    {
        private readonly string _action;
        private readonly Lease _lease;
        private readonly PoolSlot _owner;
        private readonly Action<Lease> _handler;
        private bool _active;

        internal InputBinding(string action, PoolSlot owner, Lease lease, Action<Lease> handler)
        {
            _action = action;
            _owner = owner;
            _lease = lease;
            _handler = handler;
            _active = true;
            InputRouter.Register(this);
        }

        public string Action => _action;
        public PoolSlot Owner => _owner;
        public Lease Lease => _lease;
        public bool IsActive => _active;

        internal void Press()
        {
            if (!_active) return;
            // The router checks currency before invoking, so stale input can never reach a player.
            if (!_lease.IsCurrent) return;
            _handler(_lease);
        }

        internal void Unbind()
        {
            if (!_active) return;
            _active = false;
            InputRouter.Unregister(this);
        }

        internal void ForceUnbind()
        {
            _active = false;
            InputRouter.Unregister(this);
        }
    }

    public readonly struct RuleEvent
    {
        public RuleEvent(string rule, int pressCount)
        {
            Rule = rule;
            PressCount = pressCount;
        }

        public string Rule { get; }
        public int PressCount { get; }
    }

    /// <summary>A rule listener; Unsubscribe detaches from the scene rule bus.</summary>
    public sealed class RuleBusSubscription
    {
        private readonly string _ruleName;
        private readonly PoolSlot _owner;
        private readonly Lease _lease;
        private readonly Action<RuleEvent, Lease> _handler;
        private bool _active;

        internal RuleBusSubscription(string ruleName, PoolSlot owner, Lease lease, Action<RuleEvent, Lease> handler)
        {
            _ruleName = ruleName;
            _owner = owner;
            _lease = lease;
            _handler = handler;
            _active = true;
            RuleBus.Register(this);
        }

        public string RuleName => _ruleName;
        public PoolSlot Owner => _owner;
        public Lease Lease => _lease;
        public bool IsActive => _active;

        internal void Fire(RuleEvent ruleEvent)
        {
            if (!_active) return;
            if (!_lease.IsCurrent) return;
            _handler(ruleEvent, _lease);
        }

        internal void Unsubscribe()
        {
            if (!_active) return;
            _active = false;
            RuleBus.Unregister(this);
        }

        internal void ForceUnsubscribe()
        {
            _active = false;
            RuleBus.Unregister(this);
        }
    }

    /// <summary>
    /// Scene input router. Bindings are keyed by action and only fire while their lease is current.
    /// The demo drives it from its own keys; tests can press actions directly.
    /// </summary>
    public static class InputRouter
    {
        /// <summary>Attaches the real binding; called only from <see cref="UnitySubscriptionSink"/>.</summary>
        internal static InputBinding Attach(PoolSlot owner, Lease lease, string action, Action<Lease> handler) =>
            new InputBinding(action, owner, lease, handler);

        internal static void Detach(PoolSlot owner, string action)
        {
            if (!Bindings.TryGetValue(action, out var list)) return;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(list[i].Owner, owner))
                {
                    list[i].ForceUnbind();
                }
            }
        }

        private static readonly Dictionary<string, List<InputBinding>> Bindings =
            new Dictionary<string, List<InputBinding>>(StringComparer.Ordinal);

        public static int TotalBindings
        {
            get
            {
                var n = 0;
                foreach (var list in Bindings.Values) n += list.Count;
                return n;
            }
        }

        public static int BindingsFor(string action) =>
            Bindings.TryGetValue(action, out var list) ? list.Count : 0;

        internal static void Register(InputBinding binding)
        {
            if (!Bindings.TryGetValue(binding.Action, out var list))
            {
                list = new List<InputBinding>();
                Bindings[binding.Action] = list;
            }

            list.Add(binding);
        }

        internal static void Unregister(InputBinding binding)
        {
            if (Bindings.TryGetValue(binding.Action, out var list))
            {
                list.Remove(binding);
                if (list.Count == 0) Bindings.Remove(binding.Action);
            }
        }

        public static void Press(string action)
        {
            if (!Bindings.TryGetValue(action, out var list)) return;
            for (var i = 0; i < list.Count; i++) list[i].Press();
        }

        public static int PressAny()
        {
            var pressed = 0;
            foreach (var action in new List<string>(Bindings.Keys))
            {
                if (Bindings[action].Count > 0)
                {
                    Press(action);
                    pressed++;
                }
            }

            return pressed;
        }

        public static void UnbindAllForLease(Lease lease)
        {
            foreach (var list in new List<List<InputBinding>>(Bindings.Values))
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].Lease.LeaseId == lease.LeaseId) list[i].Unbind();
                }
            }
        }

        internal static string GetAction(PropEntity.PropKind kind)
        {
            switch (kind)
            {
                case PropEntity.PropKind.Pickup: return "use.pickup";
                case PropEntity.PropKind.WeaponAttachment: return "use.attachment";
                default: return "use.machine";
            }
        }

        public static void ResetForTests() => Bindings.Clear();
    }

    /// <summary>Scene rule bus. Firing a rule invokes only listeners whose lease is current.</summary>
    public static class RuleBus
    {
        /// <summary>Attaches the real listener; called only from <see cref="UnitySubscriptionSink"/>.</summary>
        internal static RuleBusSubscription Attach(PoolSlot owner, Lease lease, string rule, Action<RuleEvent, Lease> handler) =>
            new RuleBusSubscription(rule, owner, lease, handler);

        internal static void Detach(PoolSlot owner, string rule)
        {
            if (!Listeners.TryGetValue(rule, out var list)) return;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(list[i].Owner, owner)) list[i].ForceUnsubscribe();
            }
        }

        private static readonly Dictionary<string, List<RuleBusSubscription>> Listeners =
            new Dictionary<string, List<RuleBusSubscription>>(StringComparer.Ordinal);

        public static int TotalListeners
        {
            get
            {
                var n = 0;
                foreach (var list in Listeners.Values) n += list.Count;
                return n;
            }
        }

        public static int ListenersFor(string rule) => Listeners.TryGetValue(rule, out var list) ? list.Count : 0;

        internal static void Register(RuleBusSubscription subscription)
        {
            if (!Listeners.TryGetValue(subscription.RuleName, out var list))
            {
                list = new List<RuleBusSubscription>();
                Listeners[subscription.RuleName] = list;
            }

            list.Add(subscription);
        }

        internal static void Unregister(RuleBusSubscription subscription)
        {
            if (Listeners.TryGetValue(subscription.RuleName, out var list))
            {
                list.Remove(subscription);
                if (list.Count == 0) Listeners.Remove(subscription.RuleName);
            }
        }

        public static void Fire(string rule, int pressCount)
        {
            if (!Listeners.TryGetValue(rule, out var list)) return;
            var ruleEvent = new RuleEvent(rule, pressCount);
            for (var i = 0; i < list.Count; i++) list[i].Fire(ruleEvent);
        }

        public static void FireAll(int pressCount)
        {
            foreach (var rule in new List<string>(Listeners.Keys)) Fire(rule, pressCount);
        }

        public static void ResetForTests() => Listeners.Clear();
    }
}
