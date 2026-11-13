using System;
using System.Collections.Generic;
using System.Text;
using Lifecycle.Core;
using Lifecycle.CoreTests.Fixtures;

namespace Lifecycle.CoreTests.Scenarios
{
    internal enum ScenarioOutcome
    {
        Pending,
        Completed,
        Canceled,
        Failed,
        Rejected
    }

    /// <summary>
    /// One accepted operation in a scenario. <see cref="Handle"/> is a unique name per spawned
    /// request; <see cref="EntityId"/> is the pooled entity identity, so two handles can share an
    /// entity to express reuse. <see cref="Index"/> is filled in by the recorder as the scenario
    /// executes, giving every failure a position in the shortest failing sequence.
    /// </summary>
    internal sealed class ScenarioOperation
    {
        public ScenarioOperation(string kind, string handle, string entityId, EntityKind entityKind,
            CompletionMode mode, string message, ClosePolicy? policy)
        {
            Kind = kind;
            Handle = handle;
            EntityId = entityId;
            EntityKind = entityKind;
            Mode = mode;
            Message = message;
            Policy = policy;
        }

        public string Kind { get; }
        public string Handle { get; }
        public string EntityId { get; }
        public EntityKind EntityKind { get; }
        public CompletionMode Mode { get; }
        public string Message { get; }
        public ClosePolicy? Policy { get; }
        public int Index { get; internal set; } = -1;

        public ScenarioOperation At(int index)
        {
            Index = index;
            return this;
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case "request":
                    return "request " + EntityKind + ":" + EntityId + " as '" + Handle + "'";
                case "complete":
                    return "complete '" + Handle + "' (" + Mode + ")" +
                           (Message != null ? " \"" + Message + "\"" : string.Empty);
                case "return":
                    return "return '" + Handle + "'";
                case "transition":
                    return "begin transition " + EntityId + " (" + Policy + ")";
                case "activate":
                    return "activate " + EntityId;
                case "end-transition":
                    return "end transition";
                case "pump":
                    return "pump";
                default:
                    return Kind + " '" + Handle + "'";
            }
        }
    }

    /// <summary>A named, replayable operation sequence. Renders one step per line for reports.</summary>
    internal sealed class ScenarioDocument
    {
        public ScenarioDocument(string name, params ScenarioOperation[] operations)
        {
            Name = name;
            Operations = operations;
            for (var i = 0; i < operations.Length; i++) operations[i].At(i);
        }

        public string Name { get; }
        public IReadOnlyList<ScenarioOperation> Operations { get; }

        public string ToScript()
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(Name).Append(" (").Append(Operations.Count).Append(" operations)");
            for (var i = 0; i < Operations.Count; i++)
            {
                sb.Append('\n').Append(i.ToString("D2")).Append("  ").Append(Operations[i]);
            }

            return sb.ToString();
        }
    }

    /// <summary>Declarative helpers to build scenario documents without touching a manager.</summary>
    internal static class ScenarioOps
    {
        public static ScenarioOperation Request(EntityKind kind, string entityId, string handle = null) =>
            new ScenarioOperation("request", handle ?? entityId, entityId, kind, CompletionMode.Succeed, null, null);

        public static ScenarioOperation Complete(string handle, CompletionMode mode = CompletionMode.Succeed,
            string message = null) =>
            new ScenarioOperation("complete", handle, null, EntityKind.Prop, mode, message, null);

        public static ScenarioOperation Return(string handle) =>
            new ScenarioOperation("return", handle, null, EntityKind.Prop, CompletionMode.Succeed, null, null);

        public static ScenarioOperation Transition(string scene, ClosePolicy policy) =>
            new ScenarioOperation("transition", null, scene, EntityKind.Prop, CompletionMode.Succeed, null, policy);

        public static ScenarioOperation Activate(string scene) =>
            new ScenarioOperation("activate", null, scene, EntityKind.Prop, CompletionMode.Succeed, null, null);

        public static ScenarioOperation EndTransition() =>
            new ScenarioOperation("end-transition", null, null, EntityKind.Prop, CompletionMode.Succeed, null, null);

        public static ScenarioOperation Pump() =>
            new ScenarioOperation("pump", null, null, EntityKind.Prop, CompletionMode.Succeed, null, null);
    }

    /// <summary>Snapshot the generator compares between replays.</summary>
    internal sealed class ScenarioObservation
    {
        public readonly Dictionary<string, ScenarioOutcome> Outcomes =
            new Dictionary<string, ScenarioOutcome>(StringComparer.Ordinal);
        public readonly Dictionary<string, int> GenerationOf =
            new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> TerminalEntityId =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public bool TransitionOpen;
        public string ScopeId;
        public int LeasesHeld;
        public int PendingWorkItems;
        public int ActiveSubscriptions;
        public int ActiveInputSubscriptions;
        public int ActiveRuleSubscriptions;
        public int ActiveResourceSubscriptions;
        public int StaleCallbacks;
        public int DeferredRequests;
        public int UnfinishedRequests;
        public int Created;
        public int Destroyed;
        public readonly List<string> LiveEntityLines = new List<string>();
        public string ReuseReport = string.Empty;

        public int SettledCount
        {
            get
            {
                var n = 0;
                foreach (var outcome in Outcomes.Values)
                {
                    if (outcome != ScenarioOutcome.Pending) n++;
                }

                return n;
            }
        }
    }

    /// <summary>
    /// Replays a scenario document against a fresh manager using only public APIs. Kept in its own
    /// type so the document model stays serializer-friendly and the fault-injection runner and the
    /// CLI can replay the very same operation sequence.
    /// </summary>
    internal sealed class ScenarioReplay
    {
        private readonly Dictionary<string, SpawnTicket> _tickets =
            new Dictionary<string, SpawnTicket>(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingLoad> _loads =
            new Dictionary<string, PendingLoad>(StringComparer.Ordinal);
        private readonly List<string> _handleOrder = new List<string>();

        /// <summary>
        /// The one switch the fault-injection runner flips. With the fence enforced (the shipped
        /// behaviour) a completion for an old generation is refused; with it removed the same
        /// script applies the completion to the re-leased instance and must expose an error.
        /// </summary>
        public bool EnforceGenerationFence = true;

        public readonly Clock Clock = new Clock();
        public readonly RecordingLifecycleReporter Reporter = new RecordingLifecycleReporter();
        public readonly AsyncSlotSource Source = new AsyncSlotSource();
        private readonly Dictionary<EntityKind, AsyncSlotSource> _sources =
            new Dictionary<EntityKind, AsyncSlotSource>();
        public readonly LifecycleManager Manager;

        public ScenarioReplay(ClosePolicy bootPolicy = ClosePolicy.QueueUntilNextScope)
        {
            Manager = new LifecycleManager(Clock, Reporter);
            foreach (var kind in new[] { EntityKind.Prop, EntityKind.CharacterAttachment, EntityKind.Machine })
            {
                var source = kind == EntityKind.Prop ? Source : new AsyncSlotSource();
                _sources[kind] = source;
                Manager.RegisterEntity(kind, source);
            }

            Manager.BeginTransition("Boot", bootPolicy);
            Manager.ActivateScope("Boot");
            Manager.EndTransition();
        }

        private AsyncSlotSource SourceFor(EntityKind kind) =>
            _sources.TryGetValue(kind, out var source) ? source : Source;

        public void Apply(ScenarioOperation op)
        {
            switch (op.Kind)
            {
                case "request":
                {
                    var ticket = Manager.Request(new SpawnRequest(op.EntityKind, op.EntityId, "scenario"));
                    _tickets[op.Handle] = ticket;
                    _handleOrder.Add(op.Handle);
                    if (ticket.Lease.IsAssigned)
                    {
                        var slot = (AsyncSlot)ticket.Lease.Slot;
                        slot.ActivationComplete = false;
                        slot.PreparationError = null;
                        _loads[op.Handle] = new PendingLoad(op.Handle, slot, ticket.Lease,
                            ticket.Lease.Generation.Value, op.Mode, op.Message);
                    }

                    break;
                }

                case "complete":
                {
                    if (!_loads.TryGetValue(op.Handle, out var load))
                    {
                        throw new InvalidOperationException("no scheduled load for '" + op.Handle + "'");
                    }

                    CompleteLoad(load, op.Mode, op.Message);
                    break;
                }

                case "return":
                {
                    if (!_tickets.TryGetValue(op.Handle, out var ticket))
                    {
                        throw new InvalidOperationException("unknown handle '" + op.Handle + "'");
                    }

                    Manager.Return(ticket);
                    break;
                }

                case "transition":
                    Manager.BeginTransition(op.EntityId, op.Policy ?? ClosePolicy.RejectWhenClosed);
                    break;
                case "activate":
                    Manager.ActivateScope(op.EntityId);
                    break;
                case "end-transition":
                    Manager.EndTransition();
                    break;
                case "pump":
                    Manager.Pump();
                    Clock.Advance();
                    break;
                default:
                    throw new InvalidOperationException("unsupported operation " + op.Kind);
            }
        }

        /// <summary>
        /// Resolves one scheduled load through the manager's own currency check (the generation
        /// fence), then pumps. With <see cref="EnforceGenerationFence"/> false the load is applied
        /// unconditionally, which is what a build missing the validation would do.
        /// </summary>
        public void ApplyCompletion(PendingLoad load, CompletionMode mode, string message)
        {
            if (EnforceGenerationFence && !Manager.IsGenerationCurrent(load.Lease))
            {
                load.Slot.ApplyIfCurrent(load.Lease, "stale-load", () => { });
                return;
            }

            load.Slot.Complete(mode, message);
            Manager.Pump();
            Clock.Advance();
        }

        /// <summary>Resolves a specific scheduled load (used by the probe to fire the old borrow's load).</summary>
        public void CompleteLoad(PendingLoad load, CompletionMode mode, string message)
        {
            if (load.Resolved) throw new InvalidOperationException("load already resolved: " + load.Key);
            load.Resolved = true;
            ApplyCompletion(load, mode, message);
        }

        /// <summary>The load scheduled by the request with this handle, resolved or not.</summary>
        public PendingLoad LoadFor(string handle) =>
            _loads.TryGetValue(handle, out var load) ? load : null;

        public ScenarioObservation Observe()
        {
            var snapshot = Manager.CreateSnapshot();
            var reuse = Manager.CreateReuseView();
            var created = 0;
            var destroyed = 0;
            foreach (var source in _sources.Values)
            {
                created += source.Created.Count;
                destroyed += source.DestroyedCount;
            }

            var observation = new ScenarioObservation
            {
                TransitionOpen = Manager.InTransition,
                ScopeId = snapshot.CurrentScopeId,
                LeasesHeld = snapshot.LeasesHeld,
                PendingWorkItems = snapshot.PendingWorkItems,
                ActiveSubscriptions = snapshot.ActiveSubscriptionsTotal,
                ActiveInputSubscriptions = snapshot.ActiveInputSubscriptions,
                ActiveRuleSubscriptions = snapshot.ActiveRuleSubscriptions,
                ActiveResourceSubscriptions = snapshot.ActiveTemporaryResources,
                StaleCallbacks = snapshot.StaleCallbacks,
                DeferredRequests = snapshot.DeferredRequests,
                UnfinishedRequests = snapshot.UnfinishedRequests,
                Created = created,
                Destroyed = destroyed,
                ReuseReport = reuse.ToReport()
            };

            foreach (var handle in _handleOrder)
            {
                var ticket = _tickets[handle];
                observation.Outcomes[handle] = ToOutcome(ticket.Status);
                observation.TerminalEntityId[handle] = ticket.Request.EntityId;
                observation.GenerationOf[handle] = ticket.Lease.IsAssigned ? ticket.Lease.Generation.Value : 0;
                observation.LiveEntityLines.Add(handle + ":" + ticket.Status + ":g" +
                                                observation.GenerationOf[handle]);
            }

            observation.LiveEntityLines.Sort(StringComparer.Ordinal);
            return observation;
        }

        public SpawnTicket TicketFor(string handle) => _tickets[handle];

        private static ScenarioOutcome ToOutcome(RequestStatus status)
        {
            switch (status)
            {
                case RequestStatus.Completed: return ScenarioOutcome.Completed;
                case RequestStatus.Canceled: return ScenarioOutcome.Canceled;
                case RequestStatus.Failed: return ScenarioOutcome.Failed;
                case RequestStatus.Rejected: return ScenarioOutcome.Rejected;
                default: return ScenarioOutcome.Pending;
            }
        }
    }

    /// <summary>
    /// Replays the same requests without touching completion operations in the document, but
    /// reorders *only* the completions. Non-completion operations keep their positions, so a
    /// return that sat between two completions still sits between them, but which entity finishes
    /// first can flip. Outcomes are allowed to differ; the invariants must not.
    /// </summary>
    internal static class OrderReplay
    {
        public sealed class OrderResult
        {
            public string Name;
            public ScenarioObservation Observation;
            public string FailureStep;
        }

        public static List<OrderResult> Run(ScenarioDocument document)
        {
            return new List<OrderResult>
            {
                Execute(document, "arrival-order", reverseCompletions: false),
                Execute(document, "reverse-completions", reverseCompletions: true)
            };
        }

        private static OrderResult Execute(ScenarioDocument document, string name, bool reverseCompletions)
        {
            var replay = new ScenarioReplay();
            var completions = new List<ScenarioOperation>();
            foreach (var op in document.Operations)
            {
                if (op.Kind == "complete") completions.Add(op);
            }

            if (reverseCompletions) completions.Reverse();

            var completionCursor = 0;
            var result = new OrderResult { Name = name };
            foreach (var op in document.Operations)
            {
                if (op.Kind == "complete")
                {
                    var completion = completions[completionCursor++];
                    try
                    {
                        replay.Apply(completion);
                    }
                    catch (Exception ex)
                    {
                        result.FailureStep = "operation " + completion.Index + " (" + completion + "): " + ex.Message;
                        result.Observation = replay.Observe();
                        return result;
                    }

                    continue;
                }

                try
                {
                    replay.Apply(op);
                }
                catch (Exception ex)
                {
                    result.FailureStep = "operation " + op.Index + " (" + op + "): " + ex.Message;
                    result.Observation = replay.Observe();
                    return result;
                }
            }

            result.Observation = replay.Observe();
            return result;
        }
    }
}
