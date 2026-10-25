# Combat simulation tick (Stage 01.2, Issue #3)

One fixed simulation tick drives every combat system, for a duel and for 200 vs 200 alike. The tick
is independent of the render frame rate. This folder owns the tick counter and the phase order; it
reads and writes no Core component.

## Pieces

| Type | Role |
|---|---|
| `SimulationTiming` | The only place the rate is defined: `TickRateHz = 60` (calibration value), `TickDuration = 1/60 s`. |
| `SimulationClock` | Singleton `IComponentData`, one per combat `World`. `Tick` is a read-only `uint`. |
| `SimulationClockRunning` | Tag on the clock entity while it can advance. Required by the clock and by every phase group. |
| `CombatClockSystem` | Creates/destroys the clock, sets the fixed-step timestep, the **only writer** of `Tick` and of the tag. |
| `CombatCommandCollectionSystemGroup`, `CombatStateUpdateSystemGroup`, `CombatPublicationSystemGroup` | Empty ordering anchors for the phases of a tick (base `CombatTickPhaseSystemGroup`). No logic. |

## Scheduling
Unity Entities' standard `FixedStepSimulationSystemGroup` (catch-up rate manager) decides how many
ticks run in a frame: one per elapsed `TickDuration` of world time, zero or several per frame. There
is no other time accumulator in combat code: no `MonoBehaviour.Update`/`FixedUpdate`, no
`Time.deltaTime` accumulation, no second counter. A faster render frame rate does not produce more
ticks.

World time comes from Entities' `UpdateWorldTimeSystem`: `UnityEngine.Time.deltaTime` clamped to
`World.MaximumDeltaTime` (default 1/3 s, ~20 ticks). Consequences:
- `Time.timeScale` scales the combat tick rate (`0` pauses combat); it is the only intended way to
  pause or slow the battle globally.
- A frame longer than `MaximumDeltaTime` loses the excess: the battle falls behind wall time instead
  of spiralling into ever more catch-up ticks.
- `ProjectSettings/TimeManager` *Fixed Timestep* (`0.02`, `MonoBehaviour.FixedUpdate`/PhysX) is
  unrelated to the combat tick and must not be used for combat.

`CombatClockSystem.OnCreate` sets `FixedStepSimulationSystemGroup.Timestep` from
`SimulationTiming.TickDuration`. No other code may change that timestep. Simulation code uses
`SimulationTiming.TickDuration` (or counts ticks) rather than frame delta time.

## Order inside one tick

```text
SimulationSystemGroup
└─ FixedStepSimulationSystemGroup                  (runs N times per frame, N = elapsed / TickDuration)
   ├─ BeginFixedStepSimulationEntityCommandBufferSystem   (Unity, OrderFirst)
   ├─ BattleLifecycleSystem                               (#5/#14/#19) battle requests; Tick = last completed tick
   ├─ CombatClockSystem                                   (after Lifecycle) Tick += 1
   ├─ CombatCommandCollectionSystemGroup                  producers in fixed order, then #4 processor (OrderLast)
   ├─ CombatStateUpdateSystemGroup                        single owners of combat state (Stage 02+)
   ├─ CombatPublicationSystemGroup                        read-only snapshots/events (later)
   └─ EndFixedStepSimulationEntityCommandBufferSystem     (Unity, OrderLast)
```

- The tick number of the step is available before commands for that step are collected.
- The battle lifecycle owner runs before the clock, i.e. at the boundary between two steps, also between the
  catch-up steps of one frame. It reads the clock and never writes it (see `Bootstrap/BOOTSTRAP.md`); it is not
  a combat phase. The same lifecycle handle also belongs to Initialization (`OrderFirst`), so End and
  Restart apply on paused frames with no fixed step. Initialization does not update the clock, processor
  or phases. Fixed lifecycle and clock use the normal ordering bucket after BeginFixedStep; Collection
  explicitly follows Clock. See Bootstrap's ECB lifetime restriction: actor ECBs must play back at the
  end of their producing fixed tick, not be retained until the next BeginFixedStep.
- Future systems join a phase with `[UpdateInGroup(typeof(<phase group>))]`; they never update
  `SimulationClock` and never run combat logic outside these groups.
- Only owners from later issues fill the groups; no placeholder processors are added here.
- Phase groups require `SimulationClockRunning`: they never run in a world without a clock (e.g. an
  isolated test world that has no `CombatClockSystem`) or after the clock stopped.

## Tick semantics
- `uint`, `0` = `SimulationClock.NoTick` = "no tick yet"; never simulated, never valid for a command.
- The first simulated tick is `1`. Each fixed step increments exactly once, before every phase.
- From `CombatClockSystem` on (every combat phase) `Tick` is the tick being simulated. Outside the fixed
  step, and inside it before the clock (`BeginFixedStepSimulationEntityCommandBufferSystem`,
  `BattleLifecycleSystem`), `Tick` is the last completed tick and the next one is `Tick + 1` (what a
  frame-rate producer targets).
- Upper bound: `uint.MaxValue` (~828 days at 60 Hz). The clock never wraps to `0` and never repeats a
  tick: on the step after `LastTick` it logs an error and removes `SimulationClockRunning`, which stops
  the clock and every phase in that same step. The battle must end.
- The tick is **world-scoped, not battle-scoped**: it counts fixed steps since the world was created,
  including time before any combatant exists. Loading a Unity scene does not reset it (the default
  world outlives scenes). Bootstrap (#5) records `BattleStartTick = SimulationClock.Tick` at a
  fixed-tick-safe boundary; first battle action is on absolute tick `BattleStartTick + 1`.
  Replay tests (#6) use `relativeTick = absoluteTick - BattleStartTick`. This is not a second
  accumulator or clock; nothing resets or rewrites the World clock.

## Lifetime and worlds
- Clock lifetime = `World` lifetime. `CombatClockSystem` is the only creator and destroyer; nobody
  else creates `SimulationClock`. A second `CombatClockSystem` instance in the same world fails in
  `OnCreate` (`CreateSingleton` throws) and is not added; a stray `SimulationClock` created by other
  code makes the clock's `GetSingletonRW` throw instead of silently ticking two counters.
- The clock entity is not a combat entity: battle cleanup (#5) destroys combatants only, never the
  `SimulationClock` entity.
- No static state. Exiting Play Mode disposes the default world with its clock; entering Play Mode
  again (domain reload on or off) creates a new world whose clock starts at `0`. A battle restart that
  begins a new battle **in the same default World**, without resetting the World clock. Bootstrap
  (#5) clears only prior battle-owned entities and state, invokes the canonical command-queue cleanup
  (#4), preserves monotonic non-reused Combatant IDs within the World, captures a new battle start
  tick and spawns fresh fighters. Real World disposal is separate (Play Mode/app shutdown).
  There is no in-place clock reset and no second writer.
- Default runtime world: auto-created through the default `WorldSystemFilter` (game worlds only, not
  the Editor world). Tests build an isolated `World`, create `FixedStepSimulationSystemGroup` first and
  then `CombatClockSystem`, and call the group `Update()` directly; that world never touches the
  default world or its clock.
