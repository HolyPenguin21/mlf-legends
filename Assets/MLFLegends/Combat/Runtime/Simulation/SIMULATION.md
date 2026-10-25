# Combat simulation tick (Stage 01.2, Issue #3)

One fixed simulation tick drives every combat system, for a duel and for 200 vs 200 alike. The tick
is independent of the render frame rate. This folder owns the tick counter and the phase order; it
reads and writes no Core component.

## Pieces

| Type | Role |
|---|---|
| `SimulationTiming` | The only place the rate is defined: `TickRateHz = 60` (calibration value), `TickDuration = 1/60 s`. |
| `SimulationClock` | Singleton `IComponentData`, one per combat `World`. `Tick` is a read-only `uint`. |
| `CombatClockSystem` | Creates/destroys the clock, sets the fixed-step timestep, the **only writer** of `Tick`. |
| `CombatCommandCollectionSystemGroup`, `CombatStateUpdateSystemGroup`, `CombatPublicationSystemGroup` | Empty ordering anchors for the phases of a tick. No logic. |

## Scheduling
Unity Entities' standard `FixedStepSimulationSystemGroup` (catch-up rate manager) decides how many
ticks run in a frame: one per elapsed `TickDuration` of world time, zero or several per frame. There
is no other time accumulator in combat code: no `MonoBehaviour.Update`/`FixedUpdate`, no
`Time.deltaTime` accumulation, no second counter. A faster render frame rate does not produce more
ticks. Catch-up after a long frame is capped by `World.MaximumDeltaTime` (Unity default).

`CombatClockSystem.OnCreate` sets `FixedStepSimulationSystemGroup.Timestep` from
`SimulationTiming.TickDuration`. No other code may change that timestep. Simulation code uses
`SimulationTiming.TickDuration` (or counts ticks) rather than frame delta time.

## Order inside one tick

```text
SimulationSystemGroup
└─ FixedStepSimulationSystemGroup                  (runs N times per frame, N = elapsed / TickDuration)
   ├─ BeginFixedStepSimulationEntityCommandBufferSystem   (Unity, OrderFirst)
   ├─ CombatClockSystem                                   (OrderFirst) Tick += 1
   ├─ CombatCommandCollectionSystemGroup                  producers in fixed order, then #4 processor (OrderLast)
   ├─ CombatStateUpdateSystemGroup                        single owners of combat state (Stage 02+)
   ├─ CombatPublicationSystemGroup                        read-only snapshots/events (later)
   └─ EndFixedStepSimulationEntityCommandBufferSystem     (Unity, OrderLast)
```

- The tick number of the step is available before commands for that step are collected.
- Future systems join a phase with `[UpdateInGroup(typeof(<phase group>))]`; they never update
  `SimulationClock` and never run combat logic outside these groups.
- Only owners from later issues fill the groups; no placeholder processors are added here.

## Tick semantics
- `uint`, `0` = `SimulationClock.NoTick` = "no tick yet"; never simulated, never valid for a command.
- The first simulated tick is `1`. Each fixed step increments exactly once, before every phase.
- Inside the fixed step `Tick` is the tick being simulated. Outside it, `Tick` is the last completed
  tick and the next one is `Tick + 1` (what a frame-rate producer targets).
- Upper bound: `uint.MaxValue` (~828 days at 60 Hz). The clock never wraps to `0` and never repeats a
  tick: at the bound it logs an error and stops advancing; the battle must end.

## Lifetime and worlds
- Clock lifetime = `World` lifetime. `CombatClockSystem` is the only creator and destroyer; nobody
  else creates `SimulationClock`. A second `CombatClockSystem` instance in the same world fails in
  `OnCreate` (`CreateSingleton` throws) and is not added; a stray `SimulationClock` created by other
  code makes the clock's `GetSingletonRW` throw instead of silently ticking two counters.
- No static state. Exiting Play Mode disposes the default world with its clock; entering Play Mode
  again (domain reload on or off) creates a new world whose clock starts at `0`. A battle restart that
  needs `Tick` back at `0` recreates the world (#5 owns battle bootstrap); there is no in-place reset
  and no second writer.
- Default runtime world: auto-created through the default `WorldSystemFilter` (game worlds only, not
  the Editor world). Tests build an isolated `World`, create `FixedStepSimulationSystemGroup` first and
  then `CombatClockSystem`, and call the group `Update()` directly; that world never touches the
  default world or its clock.
