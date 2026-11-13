# Object reuse: graphical sample and test harness

The object-reuse regression sample has a graphical entry point in the Unity editor and a
secondary command-line companion. Use the graphical entry point to reproduce the regression by
hand; use the CLI and the batch tests in CI.

## Graphical entry point (preferred)

**Unity menu: `Lifecycle > Open Object Reuse Sample`**

Defined in `Assets/Lifecycle/Demo/Editor/DemoSceneSetup.cs`. It opens the demo scene, enters Play
mode, and switches on the reuse panel. The panel is also part of the regular demo scene: press
`O` to toggle it, or use `Lifecycle > Open Demo Scene`.

In the scene:

- `1` borrow a prop, `2` a character attachment, `3` a machine.
- The reuse panel (`Borrow prop`, `Borrow machine`, `Return all`) lets you generate recycled
  objects by hand and return them while their asynchronous preparation may still be pending.
- `T`, `X`, `V` switch scenes with `QueueUntilNextScope`, `CancelOnClose`, and
  `RejectWhenClosed` respectively; the panel keeps running across the transition so active
  entities and async state can be checked after the switch.
- The panel renders `LifecycleManager.CreateReuseView()`: every active entity with its lease and
  slot generation, state, and activation state, plus a list of async anomalies. An entity whose
  slot generation has moved past its lease (`!slotGen=`) or that is flagged as an async anomaly
  is marked inline in red with `!ASYNC` and its reason, so the problem is visible next to the
  object that caused it. The full report string (`ReuseView.ToReport()`) also includes each
  entity's age in ticks.
- The HUD adds a one-line summary: `object reuse: active=… asyncAnomalies=… pendingWork=…`.

The PlayMode test `ObjectReusePanelBorrowsReturnsAndSwitchesSceneVisibly` exercises the same
panel path in the editor.

## CLI (secondary)

The CLI only assists object-reuse work. It replays scenarios, prints the invariant report, runs
the reuse test subset, and demonstrates the controlled source mutation. Run it from the repository
root:

```sh
dotnet run --project Tools/CoreTests/CoreTests.csproj -- run object-reuse list
dotnet run --project Tools/CoreTests/CoreTests.csproj -- run object-reuse script
dotnet run --project Tools/CoreTests/CoreTests.csproj -- run object-reuse replay
dotnet run --project Tools/CoreTests/CoreTests.csproj -- run object-reuse report --out=report.txt
dotnet run --project Tools/CoreTests/CoreTests.csproj -- run object-reuse tests
dotnet run --project Tools/CoreTests/CoreTests.csproj -- run object-reuse mutate
```

Exit codes: `0` success, `1` a fault or invariant violation, `2` usage error.

## Batch runs

`Tools/CoreTests/CoreTests.csproj` compiles only `Assets/Lifecycle/Core` (engine-free) and the
headless tests. It needs no Unity editor, no loaded scene, and no state from a previous editor
test session, so it starts from a clean checkout:

```sh
dotnet run --project Tools/CoreTests/CoreTests.csproj                 # all tests
dotnet run --project Tools/CoreTests/CoreTests.csproj -- --category=reuse
```

## What the tests control and assert

- **Completion order.** `AsyncScheduler` and the `AsyncSlotSource` manifest decide when each
  asynchronous load resolves (oldest, newest, or a chosen handle), including failure and
  null-result completion.
- **Races.** Scenarios cover borrow, return, re-activation, and scope close, replayed in arrival
  order and reverse-completion order. `ScenarioInvariants` names the outcomes that may legally
  vary (for example, a terminal outcome when a completion races a return) and the conservation
  relations that must always hold (leases, subscriptions, pending work, generations).
- **Meaningful state.** Assertions use lease/pool generations, per-generation subscription
  counts, visible active entities from `ReuseView`, and pending-work snapshots rather than task
  completion alone.
- **Fault injection.** `GenerationFenceProbe` removes the generation fence at runtime and checks
  that the stale completion is applied to the re-leased slot, producing an anomaly; with the
  fence restored the stale completion is rejected. `SourceMutationDemo` removes one line of
  `ObjectPool.Rent` in a temporary copy, recompiles, and requires the removed build to fail while
  the untouched build passes.
- **Cleanup.** Tests assert that dispose leaves no leases, pending work, or static registrations
  behind; a failure report names the shortest operation sequence that reproduced it.
