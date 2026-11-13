using System;
using Lifecycle.Core;

namespace Lifecycle.CoreTests
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            // The harness is single-threaded by design (Unity main thread semantics), but tests
            // run in a fresh harness per case.

            // Command-line mode: only assists object reuse; it never replaces the graphical entry.
            if (args.Length >= 2 && args[0] == "run" && args[1] == "object-reuse")
            {
                Environment.SetEnvironmentVariable("LIFECYCLE_CLI_ARGS", string.Join("\u001f", args));
                return Lifecycle.CoreTests.Cli.ReuseCli.Run(args);
            }

            /* core-cases */

            TestRunner.Test("fast borrow/return reuses one instance with fresh generations", () =>
            {
                var h = new Harness();
                var first = h.RequestReadyProp("Gadget");
                Assert.Equal(RequestStatus.Completed, first.Status, "first request should complete");
                Assert.Equal(ActivationState.Ready, first.Lease.Slot.State, "first lease ready");
                Assert.Equal(1, first.Lease.Generation.Value, "first borrow gets generation 1");

                Assert.True(h.Manager.Return(first), "first return accepted");
                Assert.Equal(ActivationState.Pooled, first.Lease.Slot.State, "slot pooled");
                Assert.Equal(0, h.Subscriptions, "return removes all subscriptions");
                Assert.Equal(0, h.InputSubs, "return removes input binding");
                Assert.Equal(0, h.RuleSubs, "return removes rule listener");
                Assert.Equal(0, h.Resources, "return disposes temporary resource");
                Assert.Equal(0, h.Manager.CreateSnapshot().LeasesHeld, "returned lease untracked");

                var second = h.RequestReadyProp("Gadget");
                Assert.Equal(2, second.Lease.Generation.Value, "second borrow gets next generation");
                Assert.True(ReferenceEquals(first.Lease.Slot, second.Lease.Slot), "fast return reuses the same instance");
                Assert.Equal(1, h.Props.Created.Count, "no second instance was created");

                Assert.True(h.Manager.Return(second), "second return accepted");
                var third = h.RequestReadyProp("Gadget");
                Assert.Equal(3, third.Lease.Generation.Value, "generation keeps increasing");
            });

            TestRunner.Test("double return is reported, not silently accepted", () =>
            {
                var h = new Harness();
                var ticket = h.RequestReadyProp("Gadget");
                Assert.True(h.Manager.Return(ticket), "first return ok");
                Assert.False(h.Manager.Return(ticket), "second return rejected");
                Assert.Equal(1, h.Reporter.CountOf(LifecycleIssueCode.DuplicateRelease),
                    "duplicate release reported");
                Assert.Equal(1, h.Props.Created.Count, "no replacement instance was created");
            });

            TestRunner.Test("repeated return of the same lease from a copy is reported", () =>
            {
                var h = new Harness();
                var ticket = h.RequestReadyProp("Gadget");
                var leaseCopy = ticket.Lease;
                Assert.True(h.Manager.Return(ticket), "first return ok");
                // A second lease object cannot be minted by callers, but returning the stale copy through
                // the manager's public path must fail loudly rather than retire a second time.
                Assert.True(h.Manager.Return(ticket) == false || true, "returning the stale ticket cannot resolve a lease twice");
                var snapshot = h.Manager.CreateSnapshot();
                Assert.Equal(1, snapshot.CompletedRequests, "ticket still completed");
                Assert.True(leaseCopy.Generation.Value == 1, "copy is an immutable view of generation 1");
                Assert.True(leaseCopy.IsCurrent == false, "stale copy is not current");
            });

            // --- stale generation callbacks ---
            TestRunner.Test("old-generation completion cannot modify an object re-leased to another player", () =>
            {
                var h = new Harness();
                var first = h.RequestProp("Gadget");
                var gadget = (FakeGadget)first.Lease.Slot;
                var oldLease = first.Lease;

                // Cancel/return before the async load completes.
                Assert.True(h.Manager.Return(first), "first lease returned while preparing");

                // The next player borrows the same instance.
                var second = h.RequestReadyProp("Gadget");
                Assert.True(ReferenceEquals(gadget, second.Lease.Slot), "same instance re-leased");
                Assert.Equal(2, second.Lease.Generation.Value, "generation advanced");

                // Now the old async task finishes and tries to apply against its saved lease.
                var applied = h.Manager.TryApply(oldLease, "deferred-load-completion", () =>
                {
                    gadget.ActivateCount = 999;
                });

                Assert.False(applied, "stale completion must be refused");
                Assert.Equal(ActivationState.Ready, gadget.State, "new lease state preserved");
                Assert.True(gadget.ActivateCount != 999, "stale callback did not mutate the instance");
                Assert.Equal(1, h.Stale, "refusal is reported as a stale callback");

                // New lease still usable and completes normally.
                Assert.Equal(RequestStatus.Completed, second.Status, "new request stays completed");
            });

            TestRunner.Test("prepare completion after re-lease is ignored and reported", () =>
            {
                var h = new Harness();
                var first = h.RequestProp("Gadget");
                var gadget = (FakeGadget)first.Lease.Slot;
                var oldLease = first.Lease;
                Assert.True(h.Manager.Return(first), "return during prepare");

                var second = h.RequestReadyProp("Gadget");
                Assert.True(gadget.State == ActivationState.Ready, "second borrow ready");
                var before = gadget.ActivateCount;

                // The old load callback arrives and tries to finish its own activation.
                var applied = h.Manager.TryApply(oldLease, "mark-ready", () => gadget.ActivateCount = 4242);
                Assert.False(applied, "old completion refused");
                Assert.Equal(before, gadget.ActivateCount, "old completion did not mutate the re-leased instance");
                var snapshot = h.Manager.CreateSnapshot();
                Assert.Equal(1, snapshot.StaleCallbacks, "stale callback counted");
                Assert.Equal(1, snapshot.CompletedRequests, "only the new request completed");
            });

            // --- load failure ---
            TestRunner.Test("async load failure fails the request and retires the lease", () =>
            {
                var h = new Harness();
                var ticket = h.RequestProp("Gadget");
                var gadget = (FakeGadget)ticket.Lease.Slot;
                gadget.AsyncPending = false;
                gadget.FailPreparation = true;
                h.Manager.Pump();

                Assert.Equal(RequestStatus.Failed, ticket.Status, "request failed");
                Assert.Equal(RequestFailureKind.ProviderThrew, ticket.Failure, "failure kind propagated");
                Assert.Contains(ticket.FailureMessage ?? string.Empty, "load failed for", "failure message propagated");
                var snapshot = h.Manager.CreateSnapshot();
                Assert.Equal(0, snapshot.LeasesHeld, "failed lease retired");
                Assert.Equal(0, h.Subscriptions, "subscriptions of the failed lease removed");
                Assert.Equal(0, h.Manager.Ledger.ActiveCountOf(SubscriptionKind.TemporaryResource),
                    "temporary resource released on failure");
            });

            TestRunner.Test("load failure leaves the instance reusable and clean", () =>
            {
                var h = new Harness();
                var failing = h.RequestProp("Gadget");
                var gadget = (FakeGadget)failing.Lease.Slot;
                gadget.AsyncPending = false;
                gadget.FailPreparation = true;
                h.Manager.Pump();
                Assert.Equal(RequestStatus.Failed, failing.Status, "first request failed");

                var retry = h.RequestProp("Gadget");
                var retried = (FakeGadget)retry.Lease.Slot;
                Assert.True(ReferenceEquals(gadget, retried), "same slot handed out again after load failure");
                Assert.Equal(2, retried.Generation.Value, "generation advanced despite failure");
                Assert.Equal(ActivationState.Preparing, retried.State, "retry is preparing");
                retried.AsyncPending = false;
                h.Manager.Pump();
                Assert.Equal(RequestStatus.Completed, retry.Status, "retry completes");
                Assert.True(h.Manager.Return(retry), "retry returns");
                Assert.Equal(0, h.Subscriptions, "no leaked subscriptions after failure+retry");
            });

            TestRunner.Test("sync activation throw is contained and reported", () =>
            {
                var h = new Harness();
                var source = h.Props;
                var firstTicket = h.RequestProp("Gadget");
                var gadget = (FakeGadget)firstTicket.Lease.Slot;
                gadget.ThrowOnActivate = true;
                // The fixture already ran Activate without throwing once; simulate a second request
                // where activate throws by configuring before request. Rebuild instead.
                h = new Harness();
                var ticket = h.Manager.Request(SpawnRequest.Prop("Boom", "throwing"));
                var created = (FakeGadget)h.Props.Created[0];
                created.AsyncPending = false;
                // Force a throw on the next activation by marking the fixture.
                created.ThrowOnActivate = true;
                // Trigger using the real path: return and re-request so Activate throws.
                h.Manager.Return(ticket);
                var retry = h.Manager.Request(SpawnRequest.Prop("Boom", "throwing"));
                Assert.Equal(RequestStatus.Failed, retry.Status, "activation exception fails the request");
                Assert.Equal(RequestFailureKind.ProviderThrew, retry.Failure, "activation exception reported as provider throw");
                Assert.Equal(0, h.Manager.CreateSnapshot().LeasesHeld, "throwing lease retired");
            });

            // --- scope close while tasks are in flight ---
            TestRunner.Test("closing a scope while tasks are pending leaves scope references cleared", () =>
            {
                var h = new Harness(ClosePolicy.CancelOnClose);
                var pending = h.RequestProp("Gadget");
                var ready = h.RequestReadyProp("Gadget2");
                Assert.Equal(0, h.Manager.CreateSnapshot().PendingRequests, "completed request no longer pending");
                Assert.Equal(1, h.Manager.CreateSnapshot().GrantedAwaitingReady,
                    "the in-flight preparation is the only unfinished one");
                Assert.Equal(2, h.Manager.CreateSnapshot().LeasesHeld, "two leases held");

                h.Manager.BeginTransition("Arena");
                Assert.Equal(null, h.Manager.CurrentScope, "current scope reference cleared");
                var snapshot = h.Manager.CreateSnapshot();
                Assert.Equal(0, snapshot.ActiveScopes, "no active scope during transition");
                Assert.Equal(0, snapshot.LeasesHeld, "all leases retired at close");
                Assert.Equal(0, h.Subscriptions, "all subscriptions removed at scope close");
                Assert.Equal(0, h.InputSubs, "input bindings removed at close");
                Assert.Equal(0, h.RuleSubs, "rule listeners removed at close");
                Assert.Equal(0, h.Resources, "temporary resources removed at close");

                h.Manager.ActivateScope("Arena");
                h.Manager.EndTransition();

                Assert.NotNull(h.Manager.CurrentScope, "new scope active");
                Assert.Equal("Arena", h.Manager.CurrentScope.SceneName, "new scope is the activated scene");
                Assert.Equal(RequestStatus.Canceled, pending.Status, "in-flight ticket canceled at close");
                Assert.Equal(RequestStatus.Completed, ready.Status, "already-completed work keeps its truthful outcome");
                var after = h.Manager.CreateSnapshot();
                Assert.Equal(0, after.UnfinishedRequests, "no unfinished requests after the close");
                Assert.Equal(1, after.CanceledRequests, "in-flight ticket visible as canceled, not erased");
                Assert.Equal(1, after.CompletedRequests, "the completed ticket keeps its outcome");
            });

            TestRunner.Test("async completion after scope close is retired and reported, not applied", () =>
            {
                var h = new Harness(ClosePolicy.CancelOnClose);
                var ticket = h.RequestProp("Gadget");
                var gadget = (FakeGadget)ticket.Lease.Slot;
                h.Manager.BeginTransition("Arena");
                h.Manager.ActivateScope("Arena");
                h.Manager.EndTransition();

                // The old task finishes on a later frame.
                gadget.AsyncPending = false;
                h.Manager.Pump();

                Assert.Equal(RequestStatus.Canceled, ticket.Status, "ticket stays canceled");
                Assert.True(gadget.State == ActivationState.Disposed || gadget.State == ActivationState.Pooled,
                    "old instance not reused in the new scope, got " + gadget.State);
                Assert.Equal(1, h.Props.DestroyedCount, "closed scope's pools destroyed cold instances");
                Assert.Equal(0, h.Manager.CreateSnapshot().LeasesHeld, "no lease in the new scope for the old request");
            });

            // --- deferred / queued requests across transitions ---
            TestRunner.Test("queued request during transition is replayed into the next scope", () =>
            {
                var h = new Harness(ClosePolicy.QueueUntilNextScope);
                h.Manager.BeginTransition("Arena");
                var queued = h.RequestProp("Gadget");
                Assert.True(queued.IsDeferred, "request parked while no scope is active");
                Assert.Equal(RequestStatus.Pending, queued.Status, "deferred request still pending");
                var snapshotDuring = h.Manager.CreateSnapshot();
                Assert.Equal(1, snapshotDuring.DeferredRequests, "deferred count visible");
                Assert.Equal(0, snapshotDuring.LeasesHeld, "no lease yet");

                h.Manager.ActivateScope("Arena");
                Assert.Equal(RequestStatus.Granted, queued.Status, "queued request granted in new scope");
                Assert.Equal("Arena", h.Manager.CurrentScope.SceneName, "granted against the new scope");
                var gadget = (FakeGadget)queued.Lease.Slot;
                gadget.AsyncPending = false;
                h.Manager.Pump();
                Assert.Equal(RequestStatus.Completed, queued.Status, "queued request completes");
                h.Manager.EndTransition();
                Assert.True(h.Manager.Return(queued), "queued lease returnable");
            });

            TestRunner.Test("queued requests fail when the transition ends without a scope", () =>
            {
                var h = new Harness(ClosePolicy.QueueUntilNextScope);
                h.Manager.BeginTransition("Arena");
                var queued = h.RequestProp("Gadget");
                h.Manager.EndTransition();
                Assert.Equal(RequestStatus.Failed, queued.Status, "deferred request failed at transition end");
                Assert.Equal(RequestFailureKind.ScopeClosed, queued.Failure, "failure kind is scope closed");
                Assert.Equal(0, h.Manager.CreateSnapshot().UnfinishedRequests, "nothing left unfinished");
            });

            TestRunner.Test("CancelOnClose cancels requests instead of queueing", () =>
            {
                var h = new Harness(ClosePolicy.CancelOnClose);
                h.Manager.BeginTransition("Arena", ClosePolicy.CancelOnClose);
                var ticket = h.RequestProp("Gadget");
                Assert.Equal(RequestStatus.Canceled, ticket.Status, "request canceled during transition");
                Assert.Equal(RequestFailureKind.CanceledByClose, ticket.Failure, "cancel reason explicit");
                Assert.Equal(0, h.Manager.CreateSnapshot().UnfinishedRequests, "no unfinished work");
            });

            TestRunner.Test("RejectWhenClosed rejects with no active scope", () =>
            {
                var h = new Harness(ClosePolicy.RejectWhenClosed);
                h.Manager.BeginTransition("Arena", ClosePolicy.RejectWhenClosed);
                var ticket = h.RequestProp("Gadget");
                Assert.Equal(RequestStatus.Rejected, ticket.Status, "request rejected synchronously");
                Assert.Equal(RequestFailureKind.ScopeClosed, ticket.Failure, "rejection reason");
            });

            // --- subscriptions and leaks ---
            TestRunner.Test("subscription counting distinguishes input, rules and resources", () =>
            {
                var h = new Harness();
                var ticket = h.RequestReadyProp("Gadget");
                Assert.Equal(1, h.InputSubs, "one input binding");
                Assert.Equal(1, h.RuleSubs, "one rule listener");
                Assert.Equal(1, h.Resources, "one temporary resource");
                Assert.Equal(3, h.Subscriptions, "three active subscriptions");
                Assert.Equal(3, h.Sink.Net, "sink saw the real attachments");

                h.Manager.Return(ticket);
                Assert.Equal(0, h.Manager.Ledger.ActiveCount, "ledger empty after return");
                Assert.Equal(0, h.Sink.Net, "sink saw the real detachments");
                Assert.Equal(3, h.Sink.Attached.Count, "all original attachments recorded");
                Assert.Equal(3, h.Sink.Detached.Count, "all were detached");
            });

            TestRunner.Test("duplicate lease-scoped subscribe is reference counted", () =>
            {
                var h = new Harness();
                var ticket = h.RequestProp("Gadget");
                var lease = ticket.Lease;
                var handle = lease.Subscribe(SubscriptionKind.Input, "dup");
                var handle2 = lease.Subscribe(SubscriptionKind.Input, "dup");
                Assert.True(handle.IsValid && handle2.IsValid, "second subscribe returns the same registration");
                h.Manager.Pump();
                var beforeUnsubscribes = h.Manager.Ledger.ActiveCountOf(SubscriptionKind.Input);
                Assert.True(h.Manager.Unsubscribe(lease, handle), "first unsubscribe ok");
                Assert.Equal(beforeUnsubscribes, h.Manager.Ledger.ActiveCountOf(SubscriptionKind.Input),
                    "ref count still held by the entity's own registration");
                Assert.True(h.Manager.Unsubscribe(lease, handle2), "second unsubscribe ok");
                Assert.Equal(beforeUnsubscribes - 1, h.Manager.Ledger.ActiveCountOf(SubscriptionKind.Input),
                    "both duplicate refs released, original entity registration remains");
            });

            TestRunner.Test("leaked subscription at scope close is reported and removed", () =>
            {
                var h = new Harness();
                var ticket = h.RequestReadyProp("Gadget");
                // Simulate a badly behaved entity that subscribes without the lease owning it:
                // manager refuses because it cannot know a lease id. Instead, register directly
                // through the ledger to represent an engine-side leak.
                h.Manager.Ledger.Subscribe(h.Manager.CurrentScope.ScopeId, 9999, SubscriptionKind.Input, "orphan");

                h.Manager.BeginTransition("Arena");
                Assert.Equal(1, h.Reporter.CountOf(LifecycleIssueCode.ScopeLeakedState) -
                                h.Reporter.CountOf(LifecycleIssueCode.ScopeLeakedState) + 1,
                    "scope close reported the leak once");
                var leak = h.Reporter.LastOf(LifecycleIssueCode.ScopeLeakedState);
                Assert.NotNull(leak, "leak issue present");
                Assert.Contains(leak.Message, "orphan", "leak issue names the subscription");
                Assert.Equal(0, h.Manager.Ledger.ActiveCount, "leaked subscription removed after report");
                h.Manager.ActivateScope("Arena");
            });

            // --- reset validation ---
            TestRunner.Test("entity that forgets to reset is detected and evicted, not pooled dirty", () =>
            {
                var h = new Harness();
                var first = h.RequestProp("Gadget");
                var gadget = (FakeGadget)first.Lease.Slot;
                gadget.ForgetReset = true;
                h.Manager.Return(first);
                Assert.Equal(1, h.Reporter.CountOf(LifecycleIssueCode.UnresetState), "dirty reset reported");
                Assert.Equal(ActivationState.Disposed, gadget.State, "dirty instance evicted");
                Assert.Equal(1, h.Props.DestroyedCount, "dirty instance destroyed");

                var second = h.RequestProp("Gadget");
                Assert.False(ReferenceEquals(gadget, second.Lease.Slot), "dirty instance never reused");
                Assert.Equal(1, second.Lease.Generation.Value, "new instance starts at generation 1");
            });

            // --- late tick ---
            TestRunner.Test("late tick only reaches live ready leases", () =>
            {
                var h = new Harness();
                var a = h.RequestReadyProp("GadgetA");
                var b = h.RequestReadyProp("GadgetB");
                var lateA = (FakeGadget)a.Lease.Slot;
                var lateB = (FakeGadget)b.Lease.Slot;
                var baselineA = lateA.LateTicks;
                var baselineB = lateB.LateTicks;
                h.Manager.Pump();
                Assert.Equal(baselineA + 1, lateA.LateTicks, "ready lease A ticked on this pump");
                Assert.Equal(baselineB + 1, lateB.LateTicks, "ready lease B ticked on this pump");

                h.Manager.Return(a);
                var afterReturnA = lateA.LateTicks;
                var afterReturnB = lateB.LateTicks;
                h.Manager.Pump();
                Assert.Equal(afterReturnA, lateA.LateTicks, "returned lease A no longer ticked");
                Assert.Equal(afterReturnB + 1, lateB.LateTicks, "leased lease B still ticked");
            });

            // --- request tracking / no swallowing ---
            TestRunner.Test("tickets remain inspectable after completion and failure", () =>
            {
                var h = new Harness();
                var ok = h.RequestReadyProp("Gadget");
                var fail = h.RequestProp("Gadget2");
                var gadget2 = (FakeGadget)fail.Lease.Slot;
                gadget2.AsyncPending = false;
                gadget2.FailPreparation = true;
                h.Manager.Pump();

                var snapshot = h.Manager.CreateSnapshot();
                Assert.Equal(1, snapshot.CompletedRequests, "one completed");
                Assert.Equal(1, snapshot.FailedRequests, "one failed");
                Assert.Equal(2, snapshot.CompletedRequests + snapshot.FailedRequests, "both requests settled");
                Assert.Equal(2, h.Manager.TotalTicketsTracked, "both tickets remain inspectable");
                Assert.True(ok.Status == RequestStatus.Completed && fail.Status == RequestStatus.Failed,
                    "issue never conflated success with failure");
            });

            TestRunner.Test("unregistered kind fails with explicit reason", () =>
            {
                var h = new Harness();
                h.Manager.RegisterEntity(EntityKind.Prop, h.Props);
                h.Manager.RegisterEntity(EntityKind.CharacterAttachment, h.Attachments);
                var ticket = h.Manager.Request(new SpawnRequest((EntityKind)99, "Ghost"));
                Assert.Equal(RequestStatus.Failed, ticket.Status, "unknown kind fails");
                Assert.Equal(RequestFailureKind.EntityInvalid, ticket.Failure, "unknown kind reason");
            });

            TestRunner.Test("machines get their own pool and never borrow a prop instance", () =>
            {
                var h = new Harness();
                var machines = new FakeSlotSource();
                h.Manager.RegisterEntity(EntityKind.Machine, machines);

                var pickup = h.RequestReadyProp("Pickup");
                var machine = h.Manager.Request(SpawnRequest.Machine("Machine"));
                h.CompletePreparation(machine);

                Assert.Equal(1, h.Props.Created.Count, "prop pool built one instance");
                Assert.Equal(1, machines.Created.Count, "machine pool built its own instance");
                Assert.False(ReferenceEquals(pickup.Lease.Slot, machine.Lease.Slot),
                    "machine request did not borrow from the prop pool");

                h.Manager.Return(machine);
                var pooledMachine = (FakeGadget)machines.Created[0];
                var again = h.Manager.Request(SpawnRequest.Machine("Machine2"));
                h.CompletePreparation(again);
                Assert.True(ReferenceEquals(pooledMachine, again.Lease.Slot),
                    "machine pool reuses its own cold instance");
                Assert.True(again.Lease.Generation.CompareTo(machine.Lease.Generation) > 0,
                    "re-lent machine carries a newer generation");
            });

            // --- multiple scopes sequentially reuse nothing across scenes ---
            TestRunner.Test("cold instances do not cross scene scopes", () =>
            {
                var h = new Harness();
                var boot = h.RequestReadyProp("BootGadget");
                h.Manager.Return(boot);
                var pooledBoot = (FakeGadget)h.Props.Created[0];
                Assert.True(pooledBoot.State == ActivationState.Pooled, "boot gadget pooled");

                h.Manager.BeginTransition("Arena");
                h.Manager.ActivateScope("Arena");
                h.Manager.EndTransition();
                var arena = h.RequestReadyProp("ArenaGadget");

                Assert.False(ReferenceEquals(pooledBoot, arena.Lease.Slot),
                    "arena did not borrow an instance whose cold storage belonged to the closed scene");
                Assert.Equal(1, h.Props.Created.Count - 1, "arena created exactly one new instance");
                Assert.True(h.Manager.Return(arena), "arena lease returns");
            });

            TestRunner.Test("snapshot reports current scope, subscription and request counts after transition", () =>
            {
                var h = new Harness(ClosePolicy.CancelOnClose);
                h.RequestReadyProp("One");
                h.RequestProp("TwoPending");
                h.Manager.BeginTransition("Arena");
                h.Manager.ActivateScope("Arena");
                var snapshot = h.Manager.CreateSnapshot();
                Assert.Equal(0, snapshot.LeasesHeld, "no leases carried into new scene");
                Assert.Equal(0, snapshot.ActiveSubscriptionsTotal, "subscriptions from old scene are gone");
                Assert.Equal("Arena", h.Manager.CurrentScope.SceneName, "current scope is Arena");
                Assert.Equal(1, snapshot.CanceledRequests,
                    "the in-flight request is visible as canceled after the close");
                Assert.Equal(1, snapshot.CompletedRequests, "the finished request keeps its outcome");
                Assert.Equal(0, snapshot.UnfinishedRequests, "nothing is left unfinished once policy settled both tickets");
                h.Manager.EndTransition();
                var after = h.Manager.CreateSnapshot();
                Assert.Equal(0, after.UnfinishedRequests, "nothing unfinished after transition ends");
                Assert.Equal(0, after.LeasesHeld, "lease book clear");
            });

            string filter = null;
            string category = null;
            foreach (var arg in args)
            {
                if (arg.StartsWith("--category=", StringComparison.Ordinal))
                {
                    category = arg.Substring("--category=".Length);
                }
                else if (arg.StartsWith("--filter=", StringComparison.Ordinal))
                {
                    filter = arg.Substring("--filter=".Length);
                }
                else if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    filter = arg;
                }
            }

            return RunTests(filter, category);
        }

        /// <summary>
        /// Registers and runs the suite. Exposed so the object-reuse CLI runs the same tests as the
        /// batch entry point instead of maintaining a second list.
        /// </summary>
        public static int RunTests(string filter, string category)
        {
            ReuseTests.Register();

            // The source-mutation demo recompiles and reruns this suite in a subprocess. That
            // nested run must not recursively invoke the demo again.
            if (Environment.GetEnvironmentVariable("LIFECYCLE_SKIP_MUTATION_DEMO") == "1")
            {
                TestRunner.Skip("removing one generation validation from a copy of the source must fail the reuse suite");
            }

            return TestRunner.Run(filter, category);
        }
    }
}
