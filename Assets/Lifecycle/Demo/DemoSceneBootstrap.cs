using System;
using System.Collections.Generic;
using Lifecycle.Core;
using Lifecycle.Unity;
using UnityEngine;

namespace Lifecycle.Demo
{
    /// <summary>
    /// The runnable demonstration. Builds its own scene (ground, camera, light, pool roots,
    /// pedestal, HUD) at runtime so no hand-authored scene asset is required, and exercises the
    /// whole lifecycle: spawn, prepare, use, return, load failure, duplicate release, and scene
    /// transitions that keep accepting requests under a chosen policy.
    ///
    /// Keys:
    ///   1/2/3  request pickup / attachment / machine
    ///   U      press the input action on every live prop (real input bindings fire)
    ///   R      fire a rule event on the scene rule bus
    ///   G      make the next spawn fail its async preparation
    ///   C      return all live props
    ///   T      transition (QueueUntilNextScope)
    ///   X      transition (CancelOnClose)
    ///   V      transition (RejectWhenClosed)
    ///   H      toggle HUD
    /// </summary>
    public sealed class DemoSceneBootstrap : MonoBehaviour
    {
        private LifecycleManager _manager;
        private RecordingLifecycleReporter _reporter;
        private DemoPresenter _presenter;
        private readonly List<SpawnTicket> _tickets = new List<SpawnTicket>();
        private readonly List<string> _log = new List<string>();
        private int _spawnSequence;
        private int _sceneSequence;
        private Transform _sceneRoot;
        private Transform _poolRoot;
        private Transform _characterSocket;
        private GameObject _character;
        private GameObject _hud;
        private bool _showHud = true;
        private int _rulePressCount;
        private string _currentSceneName = "Courtyard";
        private string _lastSpawnLabel = "-";
        private float _lastSpawnTime = -1f;

        public LifecycleManager Manager => _manager;
        public RecordingLifecycleReporter Reporter => _reporter;
        public DemoPresenter Presenter => _presenter;
        public IReadOnlyList<string> LogLines => _log;
        public string CurrentSceneName => _currentSceneName;

        private void Awake()
        {
            BuildWorld();
        }

        private void Start()
        {
            BuildPrototypes();
            _reporter = new RecordingLifecycleReporter();
            _presenter = new DemoPresenter();
            _manager = new LifecycleManager(new Clock(), _reporter, UnitySubscriptionSink.Instance);
            _manager.RegisterObserver(_presenter);
            _manager.RegisterEntity(EntityKind.Prop,
                new GameObjectSlotSource(_poolRoot, _pickupPrototype.gameObject, "prop"), 24);
            _manager.RegisterEntity(EntityKind.CharacterAttachment,
                new GameObjectSlotSource(_poolRoot, _attachmentPrototype.gameObject, "attach"), 16);
            _manager.RegisterEntity(EntityKind.Machine,
                new GameObjectSlotSource(_poolRoot, _machinePrototype.gameObject, "machine"), 8);

            _manager.BeginTransition("Courtyard");
            _manager.ActivateScope("Courtyard");
            _manager.EndTransition();

            BuildHud();
            Log("ready: 1 pickup / 2 attachment / 3 machine; U input; R rules; G fail next; C return; T/X/V transition");
        }

        private void Update()
        {
            if (_manager == null) return;

            _manager.Pump();
            HandleInput();
            SyncCharacterAttachment();
            UpdateHud();
        }

        private void HandleInput()
        {
            if (Input.GetKeyDown(KeyCode.Alpha1)) SpawnPickup();
            if (Input.GetKeyDown(KeyCode.Alpha2)) SpawnAttachment();
            if (Input.GetKeyDown(KeyCode.Alpha3)) SpawnMachine();
            if (Input.GetKeyDown(KeyCode.U)) InputRouter.PressAny();
            if (Input.GetKeyDown(KeyCode.R)) RuleBus.FireAll(++_rulePressCount);
            if (Input.GetKeyDown(KeyCode.G))
            {
                PropEntity.FailNextActivation();
                Log("next spawn will fail its async load");
            }

            if (Input.GetKeyDown(KeyCode.C)) ReturnAllLive();
            if (Input.GetKeyDown(KeyCode.T)) Transition("Atrium", ClosePolicy.QueueUntilNextScope);
            if (Input.GetKeyDown(KeyCode.X)) Transition("Rooftop", ClosePolicy.CancelOnClose);
            if (Input.GetKeyDown(KeyCode.V)) Transition("Cellar", ClosePolicy.RejectWhenClosed);
            if (Input.GetKeyDown(KeyCode.H)) _showHud = !_showHud;
        }

        public void SpawnPickup() => Spawn(EntityKind.Prop, "pickup");

        public void SpawnAttachment() => Spawn(EntityKind.CharacterAttachment, "attachment");

        public void SpawnMachine() => Spawn(EntityKind.Machine, "machine");

        public SpawnTicket Spawn(EntityKind kind, string label)
        {
            if (_manager == null) return null;
            var entityId = label + "-" + (++_spawnSequence);
            SpawnRequest request;
            switch (kind)
            {
                case EntityKind.CharacterAttachment:
                    request = SpawnRequest.Attachment(entityId);
                    break;
                case EntityKind.Machine:
                    request = SpawnRequest.Machine(entityId);
                    break;
                default:
                    request = SpawnRequest.Prop(entityId);
                    break;
            }

            var ticket = _manager.Request(request);
            _tickets.Add(ticket);
            _lastSpawnLabel = entityId;
            _lastSpawnTime = Time.time;
            return ticket;
        }

        private void ReturnAllLive()
        {
            for (var i = _tickets.Count - 1; i >= 0; i--)
            {
                var ticket = _tickets[i];
                if (ticket.Status == RequestStatus.Completed || ticket.Status == RequestStatus.Granted)
                {
                    if (!_manager.Return(ticket))
                    {
                        Log("return refused for #" + ticket.RequestId + " (see issue feed)");
                    }
                }
            }

            Log("returned all live props");
        }

        private void Transition(string sceneName, ClosePolicy policy)
        {
            _sceneSequence++;
            _currentSceneName = sceneName;
            Log("--- transition -> " + sceneName + " policy=" + policy + " ---");
            _manager.BeginTransition(sceneName, policy);

            // Demonstrating the policy on purpose: a request issued mid-transition.
            var during = Spawn(EntityKind.Prop, "during-" + sceneName);
            if (during != null)
            {
                Log("requested mid-transition: status=" + during.Status +
                    " deferred=" + during.IsDeferred + " failure=" + during.Failure);
            }

            // The old scope destroyed its cold instances when it closed; rebuild the surface.
            BuildGround();
            _manager.ActivateScope(sceneName);
            _manager.EndTransition();
            _rulePressCount = 0;
            Log("now playing: " + sceneName);
        }

        private void SyncCharacterAttachment()
        {
            if (_manager == null) return;
            foreach (var ticket in _tickets)
            {
                if (ticket.Status != RequestStatus.Completed) continue;
                var slot = ticket.Lease.Slot as PropEntitySlot;
                if (slot == null || !slot.IsAlive) continue;
                if (slot.Behaviour.Kind != PropEntity.PropKind.WeaponAttachment) continue;
                if (!ticket.Lease.IsCurrent) continue;
                if (_characterSocket == null) return;
                if (slot.Go.transform.parent != _characterSocket)
                {
                    slot.Go.transform.SetParent(_characterSocket, true);
                    slot.Go.transform.localPosition = Vector3.zero;
                    slot.Go.transform.localRotation = Quaternion.identity;
                }
            }
        }

        private PropEntity _pickupPrototype;
        private PropEntity _attachmentPrototype;
        private PropEntity _machinePrototype;

        private void BuildWorld()
        {
            if (Camera.main == null)
            {
                var cameraObject = new GameObject("LifecycleDemoCamera", typeof(Camera));
                var camera = cameraObject.GetComponent<Camera>();
                camera.transform.position = new Vector3(0f, 1.4f, -4.6f);
                camera.transform.rotation = Quaternion.Euler(14f, 0f, 0f);
                cameraObject.tag = "MainCamera";
            }

            var lightObject = new GameObject("LifecycleDemoLight", typeof(Light));
            lightObject.GetComponent<Light>().type = LightType.Directional;
            lightObject.transform.rotation = Quaternion.Euler(48f, -28f, 0f);

            _sceneRoot = new GameObject("DemoSceneRoot").transform;
            _poolRoot = new GameObject("DemoPoolRoot (inactive)").transform;
            _poolRoot.gameObject.SetActive(false);

            BuildGround();
            BuildCharacter();
        }

        private void BuildGround()
        {
            var existing = GameObject.Find("DemoGround");
            if (existing != null) UnityEngine.Object.Destroy(existing);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "DemoGround";
            ground.transform.SetParent(_sceneRoot, false);
            ground.transform.localScale = new Vector3(1.4f, 1f, 1.4f);
        }

        private void BuildCharacter()
        {
            _character = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            _character.name = "DemoCharacter";
            _character.transform.SetParent(_sceneRoot, false);
            _character.transform.position = new Vector3(-1.6f, 0.9f, 0.6f);
            var socket = new GameObject("AttachmentSocket");
            socket.transform.SetParent(_character.transform, false);
            socket.transform.localPosition = new Vector3(0.55f, 0.1f, 0f);
            _characterSocket = socket.transform;
        }

        private void BuildPrototypes()
        {
            _pickupPrototype = BuildPrototype("PickupPrototype", PropEntity.PropKind.Pickup, Color.cyan,
                PrimitiveType.Sphere, new Vector3(0.55f, 0.55f, 0.55f));
            _attachmentPrototype = BuildPrototype("AttachmentPrototype", PropEntity.PropKind.WeaponAttachment,
                Color.yellow, PrimitiveType.Cube, new Vector3(0.22f, 0.22f, 0.9f));
            _machinePrototype = BuildPrototype("MachinePrototype", PropEntity.PropKind.Machine, Color.magenta,
                PrimitiveType.Cylinder, new Vector3(0.7f, 0.5f, 0.7f));
        }

        private PropEntity BuildPrototype(string name, PropEntity.PropKind kind, Color color,
            PrimitiveType shape, Vector3 scale)
        {
            var go = new GameObject(name);
            go.SetActive(false);
            go.transform.SetParent(_poolRoot, false);
            var visual = GameObject.CreatePrimitive(shape);
            visual.transform.SetParent(go.transform, false);
            visual.transform.localScale = scale;
            UnityEngine.Object.Destroy(visual.GetComponent<Collider>());

            var behaviour = go.AddComponent<PropEntity>();
            behaviour.Kind = kind;
            behaviour.EntityLabel = name;
            behaviour.Body = visual.GetComponent<Renderer>();
            behaviour.Body.material.color = color;
            behaviour.BaseColor = color;
            behaviour.PreparationFrames = 2;
            behaviour.DefaultParent = _sceneRoot;
            behaviour.PoolParent = _poolRoot;
            return behaviour;
        }

        #region HUD

        private void BuildHud()
        {
            _hud = new GameObject("DemoHud");
            _hud.AddComponent<DemoHud>().Bind(this);
        }

        private void UpdateHud()
        {
            if (_hud == null) return;
            var hud = _hud.GetComponent<DemoHud>();
            if (hud != null) hud.Refresh(_manager, _presenter, _reporter, _currentSceneName, _showHud, _log);
        }

        public void Log(string line)
        {
            _log.Add(line);
            if (_log.Count > 24) _log.RemoveAt(0);
            Debug.Log("[LifecycleDemo] " + line);
        }

        private void OnDestroy()
        {
            if (_manager != null) _manager.Dispose();
            InputRouter.ResetForTests();
            RuleBus.ResetForTests();
        }

        #endregion
    }
}
