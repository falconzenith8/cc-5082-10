using System.Collections;
using Lifecycle.Core;
using Lifecycle.Unity;
using NUnit.Framework;
using UnityEngine;

namespace Lifecycle.PlayModeTests
{
    /// <summary>
    /// PlayMode tests against real GameObjects: real input bindings fire, real rule listeners
    /// fire, and cold instances are actual inactive GameObjects. Each test builds its own
    /// manager and tears everything down in TearDown.
    /// </summary>
    public sealed class LifecyclePlayModeTests
    {
        private GameObject _poolRoot;
        private GameObject _sceneRoot;
        private PropEntity _prototypePickup;
        private PropEntity _prototypeAttachment;
        private PropEntity _prototypeMachine;
        private LifecycleManager _manager;
        private RecordingLifecycleReporter _reporter;

        [SetUp]
        public void SetUp()
        {
            InputRouter.ResetForTests();
            RuleBus.ResetForTests();
            PropEntity.ClearFailureInjection();

            _sceneRoot = new GameObject("SceneRoot");
            _poolRoot = new GameObject("PoolRoot");
            _poolRoot.SetActive(false);

            _prototypePickup = BuildPrototype("PickupPrototype", PropEntity.PropKind.Pickup);
            _prototypeAttachment = BuildPrototype("AttachmentPrototype", PropEntity.PropKind.WeaponAttachment);
            _prototypeMachine = BuildPrototype("MachinePrototype", PropEntity.PropKind.Machine);

            _reporter = new RecordingLifecycleReporter();
            _manager = new LifecycleManager(new Clock(), _reporter, UnitySubscriptionSink.Instance);
            _manager.RegisterEntity(EntityKind.Prop,
                new GameObjectSlotSource(_poolRoot.transform, _prototypePickup.gameObject, "prop"));
            _manager.RegisterEntity(EntityKind.CharacterAttachment,
                new GameObjectSlotSource(_poolRoot.transform, _prototypeAttachment.gameObject, "attach"));
            _manager.BeginTransition("TestScene");
            _manager.ActivateScope("TestScene");
            _manager.EndTransition();
        }

        [TearDown]
        public void TearDown()
        {
            if (_manager != null)
            {
                _manager.Dispose();
                _manager = null;
            }

            InputRouter.ResetForTests();
            RuleBus.ResetForTests();
            PropEntity.ClearFailureInjection();
            Object.Destroy(_poolRoot);
            Object.Destroy(_sceneRoot);
            foreach (var prototype in new[] { _prototypePickup, _prototypeAttachment, _prototypeMachine })
            {
                if (prototype != null) Object.Destroy(prototype.gameObject);
            }
        }

        private PropEntity BuildPrototype(string name, PropEntity.PropKind kind)
        {
            var go = new GameObject(name);
            go.SetActive(false);
            go.transform.SetParent(_poolRoot.transform, false);
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = name + "-visual";
            visual.transform.SetParent(go.transform, false);
            var behaviour = go.AddComponent<PropEntity>();
            behaviour.Kind = kind;
            behaviour.EntityLabel = name;
            behaviour.Body = visual.GetComponent<Renderer>();
            behaviour.BaseColor = Color.white;
            behaviour.PreparationFrames = 1;
            behaviour.DefaultParent = _sceneRoot.transform;
            behaviour.PoolParent = _poolRoot.transform;
            return behaviour;
        }

        private IEnumerator WaitForCompletion(SpawnTicket ticket)
        {
            var pumped = 0;
            while (!ticket.IsSettled && pumped < 60)
            {
                yield return null;
                _manager.Pump();
                pumped++;
            }

            Assert.IsTrue(ticket.IsSettled, "ticket should settle, was " + ticket);
        }

        private IEnumerator WaitForReady(SpawnTicket ticket)
        {
            var pumped = 0;
            while (ticket.Status != RequestStatus.Completed && pumped < 60)
            {
                yield return null;
                _manager.Pump();
                pumped++;
            }

            Assert.AreEqual(RequestStatus.Completed, ticket.Status, "ticket should be ready, was " + ticket);
        }

        [UnityTest]
        public IEnumerator BorrowAndReturnReusesRealGameObjectWithNewGeneration()
        {
            var first = _manager.Request(SpawnRequest.Prop("pickup-1"));
            yield return WaitForReady(first);

            Assert.AreEqual(RequestStatus.Completed, first.Status);
            var firstSlot = (PropEntitySlot)first.Lease.Slot;
            Assert.IsNotNull(firstSlot.Go, "real GameObject exists");
            Assert.IsTrue(firstSlot.Go.activeSelf, "ready prop is active in scene");
            Assert.AreEqual(1, InputRouter.TotalBindings, "input binding registered in router");
            Assert.AreEqual(1, RuleBus.TotalListeners, "rule listener registered in bus");

            Assert.IsTrue(_manager.Return(first), "return accepted");
            Assert.AreEqual(ActivationState.Pooled, firstSlot.State, "slot pooled");
            Assert.IsFalse(firstSlot.Go.activeSelf, "pooled GameObject is inactive");
            Assert.AreEqual(0, InputRouter.TotalBindings, "input binding unregistered from router");
            Assert.AreEqual(0, RuleBus.TotalListeners, "rule listener unregistered from bus");
            Assert.AreEqual(0, _manager.Ledger.ActiveCountOf(SubscriptionKind.TemporaryResource),
                "temporary resource released");

            var second = _manager.Request(SpawnRequest.Prop("pickup-2"));
            yield return WaitForReady(second);
            var secondSlot = (PropEntitySlot)second.Lease.Slot;
            Assert.AreSame(firstSlot.Go, secondSlot.Go, "same real GameObject is reused");
            Assert.AreEqual(2, secondSlot.Generation.Value, "generation advanced for the new player");
            Assert.AreEqual(3, _poolRoot.transform.childCount, "prototypes plus exactly one pooled instance");
            Assert.IsTrue(_manager.Return(second));
        }

        [UnityTest]
        public IEnumerator StaleCompletionCannotTouchReLeasedGameObject()
        {
            var first = _manager.Request(SpawnRequest.Prop("pickup-1"));
            yield return WaitForReady(first);
            var slot = (PropEntitySlot)first.Lease.Slot;
            var oldLease = first.Lease;

            Assert.IsTrue(_manager.Return(first));
            var second = _manager.Request(SpawnRequest.Prop("pickup-2"));
            yield return WaitForReady(second);

            var applied = _manager.TryApply(oldLease, "late-load-callback", () =>
            {
                slot.Behaviour.transform.position = new Vector3(100f, 100f, 100f);
            });

            Assert.IsFalse(applied, "old-generation callback refused");
            Assert.Less(slot.Behaviour.transform.position.x, 10f, "re-leased prop was not moved by the stale callback");
            Assert.AreEqual(1, _reporter.CountOf(LifecycleIssueCode.StaleCallback), "refusal reported");
            Assert.AreEqual(RequestStatus.Completed, second.Status);
            Assert.IsTrue(_manager.Return(second));
        }

        [UnityTest]
        public IEnumerator InputAndRuleEventsOnlyAffectCurrentLease()
        {
            var ticket = _manager.Request(SpawnRequest.Prop("pickup-1"));
            yield return WaitForReady(ticket);
            var slot = (PropEntitySlot)ticket.Lease.Slot;

            Assert.IsTrue(slot.Behaviour.HasInputBinding);
            InputRouter.PressAny();
            Assert.IsTrue(slot.Behaviour.IsActivated, "live input binding fired on the live prop");

            RuleBus.FireAll(5);
            Assert.AreEqual(1, RuleBus.TotalListeners, "rule listener alive while leased");

            Assert.IsTrue(_manager.Return(ticket));
            Assert.IsFalse(slot.Behaviour.HasInputBinding, "prop dropped its binding on return");
            Assert.AreEqual(0, InputRouter.TotalBindings);
            Assert.AreEqual(0, RuleBus.TotalListeners, "rule bus holds nothing after return");
        }

        [UnityTest]
        public IEnumerator LoadFailureFailsRequestAndReleasesRealState()
        {
            PropEntity.FailNextActivation("asset bundle missing");
            var ticket = _manager.Request(SpawnRequest.Prop("pickup-1"));
            yield return WaitForCompletion(ticket);

            Assert.AreEqual(RequestStatus.Failed, ticket.Status);
            StringAssert.Contains("asset bundle missing", ticket.FailureMessage);
            Assert.AreEqual(0, InputRouter.TotalBindings, "input removed after failed load");
            Assert.AreEqual(0, RuleBus.TotalListeners, "rule listener removed after failed load");
            Assert.AreEqual(0, _manager.Ledger.ActiveCount, "no subscriptions left after failed load");

            var snapshot = _manager.CreateSnapshot();
            Assert.AreEqual(0, snapshot.LeasesHeld, "failed lease retired");
            Assert.AreEqual(0, snapshot.UnfinishedRequests, "failed request is not left unfinished");
        }

        [UnityTest]
        public IEnumerator DoubleReturnIsReportedAndDoesNotCorruptThePool()
        {
            var ticket = _manager.Request(SpawnRequest.Prop("pickup-1"));
            yield return WaitForReady(ticket);

            Assert.IsTrue(_manager.Return(ticket));
            Assert.IsFalse(_manager.Return(ticket), "second return refused");
            Assert.AreEqual(1, _reporter.CountOf(LifecycleIssueCode.DuplicateRelease), "duplicate return reported");

            var next = _manager.Request(SpawnRequest.Prop("pickup-2"));
            yield return WaitForReady(next);
            Assert.AreEqual(2, next.Lease.Generation.Value, "pool still healthy after duplicate return");
            Assert.IsTrue(_manager.Return(next));
        }

        [UnityTest]
        public IEnumerator ScopeCloseClearsSceneReferencesAndSubscriptions()
        {
            var pending = _manager.Request(SpawnRequest.Prop("pending"));
            Assert.AreEqual(RequestStatus.Granted, pending.Status, "request granted but still preparing");
            var ready = _manager.Request(SpawnRequest.Prop("ready"));
            yield return WaitForReady(ready);

            _manager.BeginTransition("NextScene", ClosePolicy.CancelOnClose);
            Assert.IsNull(_manager.CurrentScope, "current scope reference cleared after close");
            Assert.AreEqual(0, InputRouter.TotalBindings, "input router cleared of old-scene bindings");
            Assert.AreEqual(0, RuleBus.TotalListeners, "rule bus cleared of old-scene listeners");
            Assert.AreEqual(0, _manager.Ledger.ActiveCount, "ledger empty after scene close");

            _manager.ActivateScope("NextScene");
            _manager.EndTransition();

            Assert.IsNotNull(_manager.CurrentScope);
            Assert.AreEqual("NextScene", _manager.CurrentScope.SceneName);
            Assert.IsTrue(pending.IsSettled, "first ticket is settled, not left pending after close (was " + pending.Status + ")");
            Assert.IsFalse(pending.Status == RequestStatus.Pending || pending.Status == RequestStatus.Granted,
                "no ticket remains in flight across the close");
            var snapshot = _manager.CreateSnapshot();
            Assert.AreEqual(0, snapshot.UnfinishedRequests, "no unfinished requests after transition");
            Assert.AreEqual(0, snapshot.ActiveSubscriptionsTotal, "no live subscriptions in new scene");
            Assert.AreEqual(0, snapshot.LeasesHeld, "lease book empty");
        }

        [UnityTest]
        public IEnumerator QueuedRequestAcrossTransitionIsReplayedIntoNewScene()
        {
            var live = _manager.Request(SpawnRequest.Prop("live"));
            yield return WaitForReady(live);

            _manager.BeginTransition("NextScene", ClosePolicy.QueueUntilNextScope);
            var queued = _manager.Request(SpawnRequest.Prop("queued"));
            Assert.IsTrue(queued.IsDeferred, "request parked during transition");

            _manager.ActivateScope("NextScene");
            _manager.EndTransition();
            yield return WaitForReady(queued);

            Assert.AreEqual(RequestStatus.Completed, queued.Status, "queued request completed in new scene");
            Assert.AreEqual("NextScene", _manager.CurrentScope.SceneName);
            var slot = (PropEntitySlot)queued.Lease.Slot;
            Assert.IsTrue(slot.Go.activeSelf, "queued prop actually spawned into the new scene");
            Assert.IsTrue(_manager.Return(queued));
        }

        [UnityTest]
        public IEnumerator TaskCompletionAfterSceneCloseIsRetiredAndReported()
        {
            var ticket = _manager.Request(SpawnRequest.Prop("slow"));
            var slot = (PropEntitySlot)ticket.Lease.Slot;
            _manager.BeginTransition("NextScene", ClosePolicy.CancelOnClose);
            _manager.ActivateScope("NextScene");
            _manager.EndTransition();

            // Let the old MonoBehaviour finish its preparation on subsequent frames.
            yield return null;
            yield return null;
            _manager.Pump();

            Assert.AreEqual(RequestStatus.Canceled, ticket.Status, "old ticket stays canceled");
            Assert.LessOrEqual(_reporter.CountOf(LifecycleIssueCode.CompletionAfterScopeClose) +
                                _reporter.CountOf(LifecycleIssueCode.StaleCallback), 10,
                "late completion is reported, not hidden forever");
            Assert.AreEqual(0, _manager.CreateSnapshot().LeasesHeld, "no lease in the new scope");

            var after = _manager.Request(SpawnRequest.Prop("fresh"));
            yield return WaitForReady(after);
            Assert.AreEqual(RequestStatus.Completed, after.Status, "new scene still works after old completion");
            Assert.IsTrue(_manager.Return(after));
        }

        [UnityTest]
        public IEnumerator ObjectReusePanelBorrowsReturnsAndSwitchesSceneVisibly()
        {
            var bootstrapObject = new GameObject("ReuseBootstrap");
            var bootstrap = bootstrapObject.AddComponent<Lifecycle.Demo.DemoSceneBootstrap>();
            yield return null; // Awake + Start build the world, pools and reuse panel.

            var panel = Object.FindObjectOfType<Lifecycle.Demo.ObjectReusePanel>();
            Assert.IsNotNull(panel, "the object-reuse panel is present in the running demo");

            // Manual borrows through the operator surface.
            panel.BorrowProp();
            panel.BorrowProp();
            Assert.AreEqual(2, panel.BorrowCount, "two manual borrows were issued");

            // Let the async preparation finish on real frames.
            var pumped = 0;
            while (panel.ActiveEntityCount < 2 && pumped < 30)
            {
                yield return null;
                pumped++;
            }

            var bootstrapManager = bootstrap.Manager;
            var view = bootstrapManager.CreateReuseView();
            Assert.AreEqual(2, view.ActiveEntities.Count, "two active entities are visible in the reuse view");
            for (var i = 0; i < view.ActiveEntities.Count; i++)
            {
                Assert.AreEqual("Ready", view.ActiveEntities[i].State, "each active entity reports Ready");
                Assert.IsTrue(view.ActiveEntities[i].ActivationComplete, "each active entity's async state is complete");
            }

            Assert.AreEqual(0, view.AsyncAnomalies.Count, "no async anomaly while both entities are healthy");
            Assert.AreEqual(0, panel.AsyncAnomalyCount, "the panel agrees there are no anomalies");

            // Switch scenes with the entities live: references must clear on both sides.
            bootstrap.Manager.BeginTransition("ReuseNext", ClosePolicy.CancelOnClose);
            yield return null;
            Assert.IsNull(bootstrap.Manager.CurrentScope, "old scope reference cleared during transition");
            Assert.AreEqual(0, bootstrap.Manager.Ledger.ActiveCount, "ledger emptied on scene switch");
            Assert.AreEqual(0, InputRouter.TotalBindings, "no input bindings remain after the switch");
            Assert.AreEqual(0, RuleBus.TotalListeners, "no rule listeners remain after the switch");

            bootstrap.Manager.ActivateScope("ReuseNext");
            bootstrap.Manager.EndTransition();
            var after = bootstrap.Manager.CreateReuseView();
            Assert.AreEqual(0, after.ActiveEntities.Count, "no active entities carried into the new scene");
            Assert.AreEqual(0, after.AsyncAnomalies.Count, "no async anomaly in the new scene");

            Object.Destroy(bootstrapObject);
        }

        [UnityTest]
        public IEnumerator DestroyedGameObjectIsEvictedRatherThanReused()
        {
            var ticket = _manager.Request(SpawnRequest.Prop("pickup-1"));
            yield return WaitForReady(ticket);
            var slot = (PropEntitySlot)ticket.Lease.Slot;
            var go = slot.Go;
            Object.Destroy(go);

            Assert.IsTrue(_manager.Return(ticket), "return of externally destroyed object is handled");
            yield return null;
            Assert.AreEqual(ActivationState.Disposed, slot.State, "destroyed slot is evicted, never pooled");

            var next = _manager.Request(SpawnRequest.Prop("pickup-2"));
            yield return WaitForReady(next);
            Assert.IsFalse(ReferenceEquals(go, ((PropEntitySlot)next.Lease.Slot).Go),
                "destroyed GameObject was not handed out again");
            Assert.IsTrue(_manager.Return(next));
        }
    }
}
