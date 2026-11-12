using System;
using Lifecycle.Core;

namespace Lifecycle.Unity
{
    /// <summary>
    /// Payload registered by a prop for its input binding. The sink below turns ledger events
    /// into real router bindings, so the ledger's counts and the engine's actual registrations
    /// are the same thing rather than two independently maintained tallies.
    /// </summary>
    public sealed class InputSubscriptionPayload
    {
        public InputSubscriptionPayload(PoolSlot owner, Lease lease, string action, Action<Lease> handler)
        {
            Owner = owner;
            Lease = lease;
            Action = action;
            Handler = handler;
        }

        public PoolSlot Owner { get; }
        public Lease Lease { get; }
        public string Action { get; }
        public Action<Lease> Handler { get; }
    }

    public sealed class RuleSubscriptionPayload
    {
        public RuleSubscriptionPayload(PoolSlot owner, Lease lease, string ruleName, Action<RuleEvent, Lease> handler)
        {
            Owner = owner;
            Lease = lease;
            RuleName = ruleName;
            Handler = handler;
        }

        public PoolSlot Owner { get; }
        public Lease Lease { get; }
        public string RuleName { get; }
        public Action<RuleEvent, Lease> Handler { get; }
    }

    /// <summary>
    /// ISubscriptionSink that attaches and detaches real Unity-side registrations. Because the
    /// ledger drives it, unsubscribing a lease removes exactly the same bindings the ledger
    /// counted, including when the lease is retired by a scope close rather than by the caller.
    /// </summary>
    public sealed class UnitySubscriptionSink : ISubscriptionSink
    {
        public static readonly UnitySubscriptionSink Instance = new UnitySubscriptionSink();

        public void OnSubscribed(SubscriptionInfo info)
        {
            switch (info.Kind)
            {
                case SubscriptionKind.Input:
                {
                    var payload = info.Payload as InputSubscriptionPayload;
                    if (payload != null)
                    {
                        InputRouter.Attach(payload.Owner, payload.Lease, payload.Action, payload.Handler);
                    }

                    break;
                }

                case SubscriptionKind.RuleEvent:
                {
                    var payload = info.Payload as RuleSubscriptionPayload;
                    if (payload != null)
                    {
                        RuleBus.Attach(payload.Owner, payload.Lease, payload.RuleName, payload.Handler);
                    }

                    break;
                }
            }
        }

        public void OnUnsubscribed(SubscriptionInfo info)
        {
            switch (info.Kind)
            {
                case SubscriptionKind.Input:
                {
                    var payload = info.Payload as InputSubscriptionPayload;
                    if (payload != null) InputRouter.Detach(payload.Owner, payload.Action);
                    break;
                }

                case SubscriptionKind.RuleEvent:
                {
                    var payload = info.Payload as RuleSubscriptionPayload;
                    if (payload != null) RuleBus.Detach(payload.Owner, payload.RuleName);
                    break;
                }
            }
        }
    }
}
