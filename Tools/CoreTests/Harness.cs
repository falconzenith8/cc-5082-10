using System;
using Lifecycle.Core;

namespace Lifecycle.CoreTests
{
    /// <summary>Shared wiring: one clock, recording reporter, and manager with prop + attachment kinds.</summary>
    internal sealed class Harness
    {
        public readonly Clock Clock = new Clock();
        public readonly RecordingLifecycleReporter Reporter = new RecordingLifecycleReporter();
        public readonly FakeSlotSource Props = new FakeSlotSource();
        public readonly FakeSlotSource Attachments = new FakeSlotSource();
        public readonly LifecycleManager Manager;
        public readonly SubscriptionRecorder Sink = new SubscriptionRecorder();

        public readonly ClosePolicy Policy;

        public Harness(ClosePolicy policy = ClosePolicy.QueueUntilNextScope)
        {
            Policy = policy;
            Manager = new LifecycleManager(Clock, Reporter, Sink);
            Manager.RegisterEntity(EntityKind.Prop, Props);
            Manager.RegisterEntity(EntityKind.CharacterAttachment, Attachments);
            Manager.BeginTransition("Boot", policy);
            Manager.ActivateScope("Boot");
            Manager.EndTransition();
        }

        public void Transition(string scene, ClosePolicy? policy = null)
        {
            Manager.BeginTransition(scene, policy ?? Policy);
            Manager.ActivateScope(scene);
            Manager.EndTransition();
        }

        public SpawnTicket RequestProp(string id) => Manager.Request(SpawnRequest.Prop(id, "test"));

        public SpawnTicket RequestReadyProp(string id)
        {
            var ticket = RequestProp(id);
            CompletePreparation(ticket);
            return ticket;
        }

        /// <summary>Simulates the async resource finishing on the next frame, then pumps.</summary>
        public void CompletePreparation(SpawnTicket ticket)
        {
            if (!ticket.Lease.IsAssigned) return;
            var gadget = (FakeGadget)ticket.Lease.Slot;
            gadget.AsyncPending = false;
            Manager.Pump();
            Clock.Advance();
        }

        public void PumpUntilSettled(SpawnTicket ticket, int maxPumps = 8)
        {
            for (var i = 0; i < maxPumps && !ticket.IsSettled; i++)
            {
                Manager.Pump();
                Clock.Advance();
            }
        }

        public int Subscriptions => Manager.Ledger.ActiveCount;

        public int InputSubs => Manager.Ledger.ActiveCountOf(SubscriptionKind.Input);

        public int RuleSubs => Manager.Ledger.ActiveCountOf(SubscriptionKind.RuleEvent);

        public int Resources => Manager.Ledger.ActiveCountOf(SubscriptionKind.TemporaryResource);

        public int Stale => Reporter.CountOf(LifecycleIssueCode.StaleCallback);
    }

    internal sealed class SubscriptionRecorder : ISubscriptionSink
    {
        public readonly System.Collections.Generic.List<string> Attached = new System.Collections.Generic.List<string>();
        public readonly System.Collections.Generic.List<string> Detached = new System.Collections.Generic.List<string>();

        public void OnSubscribed(SubscriptionInfo info) => Attached.Add(info.Kind + ":" + info.Key);

        public void OnUnsubscribed(SubscriptionInfo info) => Detached.Add(info.Kind + ":" + info.Key);

        public int Net => Attached.Count - Detached.Count;
    }
}
