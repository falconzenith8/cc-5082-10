using System;
using System.Collections.Generic;
using Lifecycle.Core;
using Lifecycle.CoreTests.Faults;
using Lifecycle.CoreTests.Fixtures;
using Lifecycle.CoreTests.Scenarios;

namespace Lifecycle.CoreTests
{
    /// <summary>
    /// Object-reuse regression sample. Every case drives the real lifecycle manager through the
    /// schedulable async fixture and asserts on generations, subscriptions and visible entities,
    /// not merely on whether a task finished.
    /// </summary>
    internal static class ReuseTests
    {
        public static void Register()
        {
            RegisterUseCases();
            RegisterOrderReplay();
            RegisterFaultInjection();
            RegisterCleanup();
        }

        // ------------------------------------------------------------------ use cases

        private static void RegisterUseCases()
        {
            TestRunner.Test("borrow/return/re-activate keeps one instance with strictly increasing generations",
                TestCategory.Reuse, () =>
                {
                    var h = new ReuseHarness();
                    var first = h.Request("pickup", "p1");
                    h.Complete(oldest: true);
                    Assert.Equal(RequestStatus.Completed, first.Status, "first request completes");
                    var slot = (AsyncSlot)first.Lease.Slot;
                    Assert.Equal(1, first.Lease.Generation.Value, "first borrow is generation 1");

                    Assert.True(h.Manager.Return(first), "first return accepted");
                    Assert.Equal(ActivationState.Pooled, slot.State, "slot pooled after return");
                    Assert.Equal(0, slot.InputBindings, "return removed input bindings from the entity");
                    Assert.Equal(0, slot.RuleBindings, "return removed rule bindings from the entity");
                    Assert.Equal(0, h.Ledger.ActiveCountOf(SubscriptionKind.TemporaryResource),
                        "return disposed the temporary resource");

                    var second = h.Request("pickup", "p2");
                    h.Complete(oldest: true);
                    Assert.Same(slot, second.Lease.Slot, "same instance re-activated");
                    Assert.Equal(2, second.Lease.Generation.Value, "re-activation advanced the generation");
                    Assert.Equal(slot.Generation.Value, second.Lease.Generation.Value,
                        "lease generation matches the slot's generation");
                    Assert.Equal(1, h.Source.Created.Count, "no second instance was ever built");

                    Assert.True(h.Manager.Return(second), "second return accepted");
                    var third = h.Request("pickup", "p3");
                    h.Complete(oldest: true);
                    Assert.Same(slot, third.Lease.Slot, "the same instance is lent a third time");
                    Assert.Equal(3, third.Lease.Generation.Value, "generation keeps increasing across borrows");
                    Assert.True(h.Manager.Return(third), "return should be accepted");
                });

            TestRunner.Test("stale completion from an old generation cannot mutate a re-leased instance",
                TestCategory.Reuse, () =>
                {
                    var h = new ReuseHarness();
                    var first = h.Request("pickup", "p1");
                    var slot = (AsyncSlot)first.Lease.Slot;
                    var oldLease = first.Lease;

                    // Return while the preparation is still outstanding.
                    Assert.True(h.Manager.Return(first), "return while preparing");
                    var second = h.Request("pickup", "p2");
                    h.Complete(oldest: true);
                    Assert.Equal(RequestStatus.Completed, second.Status, "second borrow completed on its own load");
                    var activationsAfterReLease = slot.ActivateCount;

                    // Now the first borrow's load resolves late, against generation 2. Both the
                    // manager's generation fence and the entity's own gated apply must refuse it.
                    Assert.False(oldLease.IsCurrent, "the old lease is no longer current");
                    Assert.False(h.Manager.IsGenerationCurrent(oldLease),
                        "the manager's generation-first check refuses the old lease");
                    var applied = false;
                    slot.ApplyIfCurrent(oldLease, "late-load", () => { slot.ActivateCount = 999; applied = true; });
                    Assert.False(applied, "the gated apply never ran for the stale lease");
                    Assert.Contains(string.Join(",", slot.Trace), "rejected-stale:late-load",
                        "the rejection was recorded by the entity");
                    Assert.Equal(activationsAfterReLease, slot.ActivateCount, "the stale lease did not mutate the instance");
                    Assert.Equal(2, slot.BoundLease.Generation.Value, "the bound lease is the second generation");
                });

            TestRunner.Test("reused slot's visible entity stays one active object and its subscriptions are per-generation",
                TestCategory.Reuse, () =>
                {
                    var h = new ReuseHarness();
                    var first = h.Request("machine", "m1");
                    h.Complete(oldest: true);
                    Assert.Equal(RequestStatus.Completed, first.Status, "machine request completes");
                    var slot = (AsyncSlot)first.Lease.Slot;
                    Assert.Equal(1, slot.InputBindings, "one input binding for the live generation");
                    Assert.Equal(1, slot.RuleBindings, "one rule binding for the live generation");
                    Assert.Equal(1, h.Ledger.ActiveCountOf(SubscriptionKind.TemporaryResource),
                        "one temporary resource for the live generation");
                    Assert.Equal(3, h.Snapshot.ActiveSubscriptionsTotal, "three visible subscriptions");

                    h.Manager.Return(first);
                    Assert.Equal(0, h.Snapshot.ActiveSubscriptionsTotal, "return removed every visible subscription");
                    Assert.Equal(0, slot.ActiveHandles, "the entity dropped every handle");

                    var second = h.Request("machine", "m2");
                    h.Complete(oldest: true);
                    Assert.Equal(2, second.Lease.Generation.Value, "the second generation is distinct");
                    Assert.Equal(0, h.Reporter.CountOf(LifecycleIssueCode.DuplicateRelease),
                        "the second generation did not double-release the first generation's subscriptions");
                    Assert.Equal(3, h.Snapshot.ActiveSubscriptionsTotal,
                        "the second generation registered exactly its own set");
                    Assert.True(h.Manager.Return(second), "return should be accepted");
                });

            TestRunner.Test("completion order decides which of two requests completes first, not the generations",
                TestCategory.Reuse, () =>
                {
                    var h = new ReuseHarness();
                    var a = h.Request("pickup", "pa");
                    var b = h.Request("pickup", "pb");
                    var slotA = (AsyncSlot)a.Lease.Slot;
                    var slotB = (AsyncSlot)b.Lease.Slot;
                    Assert.NotSame(slotA, slotB, "two concurrent borrows get two distinct slots");
                    Assert.Equal(1, a.Lease.Generation.Value, "the first slot starts at generation 1");
                    Assert.Equal(1, b.Lease.Generation.Value, "the second slot also starts at generation 1");

                    // Resolve the newer borrow first.
                    h.Complete(ticket: b);
                    Assert.Equal(RequestStatus.Completed, b.Status, "newer borrow completed first");
                    Assert.Equal(RequestStatus.Granted, a.Status, "older borrow is still preparing");

                    h.Complete(ticket: a);
                    Assert.Equal(RequestStatus.Completed, a.Status, "older borrow completes on its own load");
                    Assert.Equal(1, a.Lease.Generation.Value, "a's generation never changed");
                    Assert.Equal(1, b.Lease.Generation.Value, "b's generation never changed");
                    Assert.True(h.Manager.Return(a), "return should be accepted");
                    Assert.True(h.Manager.Return(b), "return should be accepted");
                });

            TestRunner.Test("scope close with async work in flight clears references and settles every ticket",
                TestCategory.Reuse, () =>
                {
                    var h = new ReuseHarness();
                    var pending = h.Request("pickup", "p1");
                    var mixing = h.Manager.Request(new SpawnRequest(EntityKind.Machine, "m1", "reuse"));
                    h.Track(mixing);
                    Assert.Equal(RequestStatus.Granted, pending.Status, "first request still preparing");
                    Assert.Equal(RequestStatus.Granted, mixing.Status, "second request still preparing");

                    h.Manager.BeginTransition("Arena", ClosePolicy.CancelOnClose);
                    Assert.Null(h.Manager.CurrentScope, "current scope reference cleared at close");
                    Assert.Equal(0, h.Manager.Ledger.ActiveCount, "ledger empty after close");
                    Assert.Equal(0, h.Snapshot.LeasesHeld, "lease book empty after close");
                    Assert.Equal(0, h.Snapshot.PendingWorkItems, "no pending work survives the close");

                    // Resolve the old loads after the close; they must be ignored safely.
                    h.Complete(ticket: pending);
                    h.Complete(ticket: mixing);
                    Assert.Equal(RequestStatus.Canceled, pending.Status, "in-flight ticket stays canceled");
                    Assert.Equal(RequestStatus.Canceled, mixing.Status, "in-flight ticket stays canceled");

                    h.Manager.ActivateScope("Arena");
                    h.Manager.EndTransition();
                    Assert.Equal("Arena", h.Manager.CurrentScope.SceneName, "replacement scope active");
                    Assert.Equal(0, h.Snapshot.UnfinishedRequests, "nothing left unfinished after the close");
                    Assert.Equal(0, h.Snapshot.DeferredRequests, "no deferred work left over");
                    Assert.Equal(0, h.Manager.CreateReuseView().LeasesHeld, "reuse monitor reports no live leases");
                    Assert.False(h.Manager.CreateReuseView().HasAnomalies, "no async anomaly in the fresh scope");

                    var fresh = h.Request("pickup", "fresh");
                    h.Complete(oldest: true);
                    Assert.Equal(RequestStatus.Completed, fresh.Status, "fresh scope still works");
                    Assert.True(h.Manager.Return(fresh), "return should be accepted");
                });

            TestRunner.Test("returning while preparing settles the ticket instead of leaving it unfinished",
                TestCategory.Reuse, () =>
                {
                    var h = new ReuseHarness();
                    var ticket = h.Request("pickup", "p1");
                    Assert.Equal(RequestStatus.Granted, ticket.Status, "request is preparing");
                    Assert.Equal(1, h.Snapshot.UnfinishedRequests, "one unfinished request before the return");

                    Assert.True(h.Manager.Return(ticket), "return of a preparing ticket is accepted");
                    Assert.True(ticket.IsSettled, "returned ticket is settled, was " + ticket.Status);
                    Assert.Equal(RequestFailureKind.CanceledByCaller, ticket.Failure,
                        "the explicit return is reported as the terminal reason");
                    Assert.Equal(0, h.Snapshot.UnfinishedRequests, "no request is left unfinished");
                    Assert.Equal(0, h.Snapshot.PendingWorkItems, "the returned preparation is not still queued");

                    var next = h.Request("pickup", "p2");
                    h.Complete(oldest: true);
                    Assert.Equal(RequestStatus.Completed, next.Status, "the pool is still healthy after a mid-prepare return");
                    Assert.Equal(2, next.Lease.Generation.Value, "the next borrow advanced the generation");
                    Assert.True(h.Manager.Return(next), "return should be accepted");
                });

            TestRunner.Test("preparation failure retires the lease and leaves the instance reusable",
                TestCategory.Reuse, () =>
                {
                    var h = new ReuseHarness();
                    var failing = h.Request("pickup", "p1");
                    var slot = (AsyncSlot)failing.Lease.Slot;
                    h.Complete(ticket: failing, mode: CompletionMode.Fail, message: "asset load failed");

                    Assert.Equal(RequestStatus.Failed, failing.Status, "failed request is reported");
                    Assert.Contains(failing.FailureMessage ?? string.Empty, "asset load failed",
                        "failure message propagated");
                    Assert.Equal(0, h.Snapshot.LeasesHeld, "failed lease retired");
                    Assert.Equal(0, h.Snapshot.ActiveSubscriptionsTotal, "failed lease's subscriptions removed");
                    Assert.Equal(ActivationState.Pooled, slot.State, "failed slot returns to the pool");
                    Assert.Equal(0, slot.ActiveHandles, "no stale subscription handles remain on the entity");

                    var retry = h.Request("pickup", "p2");
                    h.Complete(oldest: true);
                    Assert.Same(slot, retry.Lease.Slot, "the same slot is retried");
                    Assert.Equal(2, retry.Lease.Generation.Value, "the retry gets a fresh generation");
                    Assert.Equal(RequestStatus.Completed, retry.Status, "the retry completes");
                    Assert.True(h.Manager.Return(retry), "return should be accepted");
                });

            TestRunner.Test("synchronous activation failure is contained and retires the lease",
                TestCategory.Reuse, () =>
                {
                    var h = new ReuseHarness();
                    h.Source.Plan("boom", new AsyncSlotSource.SlotConfig { ThrowOnActivate = true });
                    var ticket = h.Manager.Request(new SpawnRequest(EntityKind.Prop, "boom", "activate-throws"));

                    Assert.Equal(RequestStatus.Failed, ticket.Status, "activation throw fails the request");
                    Assert.Equal(RequestFailureKind.ProviderThrew, ticket.Failure, "reported as a provider throw");
                    Assert.Equal(0, h.Snapshot.LeasesHeld, "the throwing lease is retired");
                    Assert.Equal(0, h.Snapshot.PendingWorkItems, "no pending work was queued for the throw");
                });

            TestRunner.Test("external destruction is evicted, never handed to the next borrower",
                TestCategory.Reuse, () =>
                {
                    var h = new ReuseHarness();
                    var first = h.Request("pickup", "p1");
                    h.Complete(oldest: true);
                    var slot = (AsyncSlot)first.Lease.Slot;
                    slot.EngineAlive = false;
                    Assert.True(h.Manager.Return(first), "return of a destroyed object is handled");
                    Assert.Equal(ActivationState.Disposed, slot.State, "destroyed slot is evicted");
                    Assert.Equal(1, h.Source.DestroyedCount - h.BootDestroyed, "the dead slot was destroyed");

                    var next = h.Request("pickup", "p2");
                    h.Complete(oldest: true);
                    Assert.NotSame(slot, next.Lease.Slot, "destroyed slot is never reused");
                    Assert.Equal(1, next.Lease.Generation.Value, "the replacement instance starts at generation 1");
                    Assert.True(h.Manager.Return(next), "return should be accepted");
                });

            TestRunner.Test("duplicate return is reported and the pool stays healthy",
                TestCategory.Reuse, () =>
                {
                    var h = new ReuseHarness();
                    var ticket = h.Request("pickup", "p1");
                    h.Complete(oldest: true);
                    Assert.True(h.Manager.Return(ticket), "first return ok");
                    Assert.False(h.Manager.Return(ticket), "second return refused");
                    Assert.Equal(1, h.Reporter.CountOf(LifecycleIssueCode.DuplicateRelease), "duplicate return reported");

                    var next = h.Request("pickup", "p2");
                    h.Complete(oldest: true);
                    Assert.Equal(2, next.Lease.Generation.Value, "pool healthy after duplicate return");
                });

            TestRunner.Test("reuse monitor lists active entities and flags a forced async anomaly",
                TestCategory.Reuse, () =>
                {
                    var h = new ReuseHarness();
                    var ticket = h.Request("pickup", "p1");
                    var slot = (AsyncSlot)ticket.Lease.Slot;

                    // Force the async state to look complete while the slot is still Preparing:
                    // the monitor must call that out next to the entity.
                    slot.Complete(CompletionMode.Succeed);
                    var view = h.Manager.CreateReuseView();
                    Assert.Equal(1, view.ActiveEntities.Count, "the live entity is listed");
                    Assert.Contains(view.ToReport(), "active pickup", "the report names the active entity");
                    Assert.Contains(view.ToReport(), "gen=1", "the report shows the generation");
                    Assert.True(view.HasAnomalies, "the forced mismatch is flagged");
                    Assert.Contains(view.ToReport(), "Preparing", "the anomaly describes the async state");

                    h.Manager.Pump();
                    Assert.False(h.Manager.CreateReuseView().HasAnomalies, "pumping clears the anomaly");
                    Assert.True(h.Manager.Return(ticket), "return should be accepted");
                });
        }

        // ------------------------------------------------------------------ order replay

        private static void RegisterOrderReplay()
        {
            TestRunner.Test("same request set in different completion orders conserves resources and allows only named variation",
                TestCategory.Reuse, () =>
                {
                    foreach (var document in ScenarioCatalog.All)
                    {
                        var orders = OrderReplay.Run(document);
                        Assert.Equal(2, orders.Count, "two completion orders were replayed");
                        var handles = new List<string>();
                        foreach (var operation in document.Operations)
                        {
                            if (operation.Kind == "request") handles.Add(operation.Handle);
                        }

                        foreach (var order in orders)
                        {
                            foreach (var violation in ScenarioInvariants.Check(order.Observation, handles))
                            {
                                throw new AssertionException("[" + document.Name + "/" + order.Name +
                                                             "] invariant failed -> " + violation +
                                                             "\n" + document.ToScript());
                            }
                        }

                        foreach (var violation in ScenarioInvariants.CheckAcrossOrders(
                                     orders[0].Observation, orders[1].Observation, handles, handles))
                        {
                            throw new AssertionException("[" + document.Name +
                                                         "] cross-order invariant failed -> " + violation +
                                                         "\n" + document.ToScript());
                        }
                    }
                });

            TestRunner.Test("two-borrow completion order changes outcomes but never aliases a generation",
                TestCategory.Reuse, () =>
                {
                    foreach (var order in OrderReplay.Run(ScenarioCatalog.TwoRequestsSameEntity()))
                    {
                        Assert.Equal(ScenarioOutcome.Completed, order.Observation.Outcomes["a"],
                            "request a completed in order " + order.Name);
                        Assert.Equal(ScenarioOutcome.Completed, order.Observation.Outcomes["b"],
                            "request b completed in order " + order.Name);
                        Assert.Equal(2, order.Observation.LeasesHeld,
                            "both leases stay live until the scenario ends (order " + order.Name + ")");
                        Assert.Equal(6, order.Observation.ActiveSubscriptions,
                            "each live lease owns its own input, rule, and resource subscriptions (" +
                            order.Name + ")");
                    }
                });

            TestRunner.Test("allowed variation and required invariants are documented, not implied",
                TestCategory.Reuse, () =>
                {
                    Assert.True(ScenarioInvariants.AllowedToVary.Count >= 4, "the varying results are enumerated");
                    Assert.True(ScenarioInvariants.MustHold.Count >= 8, "the conservation rules are enumerated");
                    var vary = string.Join("\n", ScenarioInvariants.AllowedToVary);
                    var hold = string.Join("\n", ScenarioInvariants.MustHold);
                    Assert.Contains(vary, "terminal outcome", "outcome variation is explicitly allowed");
                    Assert.Contains(hold, "subscriptions", "subscription conservation is required");
                    Assert.Contains(hold, "generation", "generation safety is required");
                });
        }

        // ------------------------------------------------------------------ fault injection

        private static void RegisterFaultInjection()
        {
            TestRunner.Test("controlled removal of the generation fence exposes an error; restoring it verifies the fix",
                TestCategory.Reuse, () =>
                {
                    var removed = GenerationFenceProbe.Run(enforceFence: false);
                    Assert.True(removed.PrematureCompletion,
                        "with the generation fence removed, the stale completion must prematurely complete the new borrow (" +
                        GenerationFenceProbe.Describe(removed) + ")");
                    Assert.True(removed.ReuseAnomalyVisible,
                        "the removed fence must be visible as an async anomaly (" +
                        GenerationFenceProbe.Describe(removed) + ")");

                    var restored = GenerationFenceProbe.Run(enforceFence: true);
                    Assert.False(restored.PrematureCompletion,
                        "with the fence restored the new borrow stays pending (" +
                        GenerationFenceProbe.Describe(restored) + ")");
                    Assert.True(restored.StaleCompletionRejected,
                        "the old completion is routed to the stale path (" +
                        GenerationFenceProbe.Describe(restored) + ")");
                    Assert.True(restored.RestoredAndClean,
                        "restored implementation is clean (" + GenerationFenceProbe.Describe(restored) + ")");
                });

            TestRunner.Test("fault probe failure report points at the shortest operation sequence",
                TestCategory.Reuse, () =>
                {
                    var result = GenerationFenceProbe.Run(enforceFence: false);
                    Assert.Contains(result.Script, "00  request", "the report starts at the first operation");
                    Assert.Contains(result.Script, "03  complete", "the stale completion is the decisive step");
                    Assert.True(result.Script.Split('\n').Length <= 6,
                        "the failing sequence is short and ordered, was:\n" + result.Script);
                });

            TestRunner.Test("removing one generation validation from a copy of the source must fail the reuse suite",
                TestCategory.Reuse, () =>
                {
                    if (!SourceMutationDemo.CanRun(out var reason))
                    {
                        Assert.True(false, "the environment cannot run the source-mutation demo: " + reason);
                        return;
                    }

                    var outcome = SourceMutationDemo.Run();
                    Assert.True(outcome.ExposedError,
                        "the build with one generation validation removed must fail (" +
                        SourceMutationDemo.Describe(outcome) + ")");
                    Assert.True(outcome.RestoredAndClean,
                        "the untouched build must still pass (" + SourceMutationDemo.Describe(outcome) + ")");
                });
        }

        // ------------------------------------------------------------------ cleanup

        private static void RegisterCleanup()
        {
            TestRunner.Test("after dispose there are no residual leases, pending work or static registrations",
                TestCategory.Reuse, () =>
                {
                    var h = new ReuseHarness();
                    var awaiting = h.Request("pickup", "p1");
                    var live = h.Request("machine", "m1");
                    h.Complete(ticket: live);
                    Assert.Equal(1, h.Snapshot.PendingWorkItems, "one async item is genuinely outstanding");
                    Assert.Equal(6, h.Sink.Net, "two live leases own three engine subscriptions each");

                    h.Manager.Dispose();
                    Assert.Equal(0, h.Snapshot.LeasesHeld, "dispose retired every lease");
                    Assert.Equal(0, h.Snapshot.PendingWorkItems, "dispose dropped every pending item");
                    Assert.Equal(RequestStatus.Canceled, awaiting.Status, "the in-flight request settled as canceled");
                    Assert.Equal(RequestStatus.Completed, live.Status, "a completed ticket keeps its truthful outcome");
                    Assert.Equal(0, h.Ledger.ActiveCount, "the ledger is empty after dispose");
                    Assert.Equal(0, h.Ledger.ActiveCountForScope("Boot"), "no boot-scope registrations survived");
                    Assert.Equal(0, h.Sink.Net, "the engine boundary saw balanced attach/detach calls");
                    Assert.False(h.Manager.CreateReuseView().HasAnomalies, "the disposed manager reports no anomaly");
                });

            TestRunner.Test("a fresh run starts from a clean state without an editor cache",
                TestCategory.Reuse, () =>
                {
                    // Every harness constructs its own manager, reporter, clock, and slot source;
                    // no static counters or warm assets are reused. Two runs observe the same clean
                    // baseline, so batch runs are independent of what the editor tested last.
                    var first = new ReuseHarness();
                    var second = new ReuseHarness();
                    Assert.Equal(0, first.Source.Created.Count, "a fresh harness starts with an empty pool");
                    Assert.Equal(0, second.Source.Created.Count, "the second run starts just as empty");
                    Assert.Equal(0, first.Snapshot.LeasesHeld, "no lease leaks from the first run");
                    Assert.Equal(0, second.Snapshot.LeasesHeld, "no lease leaks from the second run");
                    Assert.Equal(0, first.Reporter.Count, "the first run starts with no recorded issues");
                    Assert.Equal(0, second.Reporter.Count, "the second run starts with no recorded issues");
                    var firstTicket = first.Request("pickup", "p1");
                    var secondTicket = second.Request("pickup", "p1");
                    Assert.Equal(1, firstTicket.Lease.Generation.Value, "the first run's pool starts at generation 1");
                    Assert.Equal(1, secondTicket.Lease.Generation.Value, "the second run's pool starts at generation 1");
                });
        }

        // ------------------------------------------------------------------ replay helper

        /// <summary>
        /// Drives one manager through the schedulable fixture. Requests are tracked by ticket so a
        /// completion can be resolved in any order; the boot borrow is performed in the constructor
        /// so every case starts from a deterministic, warm-free baseline.
        /// </summary>
        private sealed class ReuseHarness
        {
            private readonly Dictionary<long, PendingLoad> _loads = new Dictionary<long, PendingLoad>();

            public readonly Clock Clock = new Clock();
            public readonly RecordingLifecycleReporter Reporter = new RecordingLifecycleReporter();
            public readonly SubscriptionRecorder Sink = new SubscriptionRecorder();
            public readonly AsyncSlotSource Source = new AsyncSlotSource();
            public readonly LifecycleManager Manager;

            public ReuseHarness()
            {
                Manager = new LifecycleManager(Clock, Reporter, Sink);
                foreach (var kind in new[] { EntityKind.Prop, EntityKind.CharacterAttachment, EntityKind.Machine })
                {
                    Manager.RegisterEntity(kind, kind == EntityKind.Prop ? Source : new AsyncSlotSource());
                }

                // No warm-up borrow: every case starts with an empty pool so generations and
                // "created" counts are deterministic from the first request onwards.
                Manager.BeginTransition("Boot", ClosePolicy.QueueUntilNextScope);
                Manager.ActivateScope("Boot");
                Manager.EndTransition();
                BootDestroyed = Source.DestroyedCount;
            }

            public int BootDestroyed { get; }
            public int StaleRejections { get; private set; }
            public LifecycleSnapshot Snapshot => Manager.CreateSnapshot();
            public SubscriptionLedger Ledger => Manager.Ledger;

            public SpawnTicket Request(string entityId, string label)
            {
                return Request(EntityKind.Prop, entityId);
            }

            public SpawnTicket Request(EntityKind kind, string entityId)
            {
                var ticket = Manager.Request(new SpawnRequest(kind, entityId, "reuse"));
                Track(ticket);
                return ticket;
            }

            public void Track(SpawnTicket ticket)
            {
                if (ticket != null && ticket.Lease.IsAssigned && ticket.Lease.Slot is AsyncSlot slot)
                {
                    slot.ActivationComplete = false;
                    slot.PreparationError = null;
                    _loads[ticket.RequestId] = new PendingLoad("ticket#" + ticket.RequestId, slot, ticket.Lease,
                        ticket.Lease.Generation.Value, CompletionMode.Succeed, null);
                }
            }

            public void Complete(bool oldest = false, SpawnTicket ticket = null,
                CompletionMode mode = CompletionMode.Succeed, string message = null)
            {
                PendingLoad load = null;
                long loadKey = -1;
                if (ticket != null)
                {
                    if (_loads.TryGetValue(ticket.RequestId, out var found))
                    {
                        load = found;
                        loadKey = ticket.RequestId;
                    }
                }
                else if (oldest)
                {
                    var best = long.MaxValue;
                    foreach (var pair in _loads)
                    {
                        if (pair.Key < best && Manager.IsGenerationCurrent(pair.Value.Lease))
                        {
                            best = pair.Key;
                            load = pair.Value;
                            loadKey = pair.Key;
                        }
                    }
                }

                if (load == null) return;
                _loads.Remove(loadKey);
                if (!Manager.IsGenerationCurrent(load.Lease))
                {
                    load.Slot.ApplyIfCurrent(load.Lease, "stale", () => { });
                    StaleRejections++;
                    return;
                }

                load.Slot.Complete(mode, message);
                Manager.Pump();
                Clock.Advance();
            }
        }
    }
}
