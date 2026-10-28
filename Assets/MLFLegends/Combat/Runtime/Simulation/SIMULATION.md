# Stage 03 final polish — movement and landing (2026-10-28)

See [configuration and owner handoff](../../../../../Docs/STAGE03_FINAL_POLISH.md).
CombatMovementTuning now supplies 50%/25%/25% planar speed for Preparation/Active/Recovery, selected from the running
strike's authoritative frozen timing and applied once by CombatMovementSystem. Vertical physics is unchanged.
The existing CombatStrikeSchedule also carries the configured jump landing duration. The sole movement owner resolves
physical contact first, copies LandingRecoveryTicks into CombatMotion, clears the run streak and blocks controlled
movement/jump for that window. Action and facing owners read the same IsLandingRecovery(tick); Attack/Kick presses are
dropped, not buffered, during recovery. Input polling/command collection continue. Clock pause freezes the window;
spawn/restart/despawn use the existing motion lifecycle. No additional ECS systems, singleton or position writer.
These rules replace old Preparation-only movement and visual-only landing below. Stage 03 awaits owner confirmation.

# Combat simulation tick (Stage 01.2, Issue #3)

One fixed simulation tick drives every combat system, for a duel and for 200 vs 200 alike. The tick
is independent of the render frame rate. Clock and phase groups own tick scheduling and do not
write Core state; in this folder Stage 02.2 CombatFacingSystem owns facing, Stage 02.1
CombatMovementSystem owns position/motion integration, and Stage 05.1 CombatActionSystem / CombatHitSystem own strike
state and HP/hit ledgers.

## Pieces

| Type | Role |
|---|---|
| `SimulationTiming` | The only place the rate is defined: `TickRateHz = 60` (calibration value), `TickDuration = 1/60 s`. |
| `SimulationClock` | Singleton `IComponentData`, one per combat `World`. `Tick` is a read-only `uint`. |
| `SimulationClockRunning` | Tag on the clock entity while it can advance. Required by the clock and by every phase group. |
| `CombatClockSystem` | Creates/destroys the clock, sets the fixed-step timestep, the **only writer** of `Tick` and of the tag. |
| `CombatCommandCollectionSystemGroup`, `CombatStateUpdateSystemGroup`, `CombatPublicationSystemGroup` | Ordering anchors for the phases of a tick (base `CombatTickPhaseSystemGroup`). No logic. |

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
   ├─ CombatCommandCollectionSystemGroup                  PlayerInputCommandSystem (#22, default world), then #4 processor (OrderLast)
   ├─ CombatStateUpdateSystemGroup                        CombatActionSystem (#48) → CombatFacingSystem (#33) → CombatMovementSystem (#31) → CombatHitSystem (#48)
   ├─ CombatPublicationSystemGroup                        read-only snapshots/events (later)
   └─ EndFixedStepSimulationEntityCommandBufferSystem     (Unity, OrderLast)
```

- The tick number of the step is available before commands for that step are collected.
- The battle lifecycle owner runs before the clock, i.e. at the boundary between two steps, also between the
  catch-up steps of one frame. It reads the clock and never writes it (see `Bootstrap/BOOTSTRAP.md`); it is not
  a combat phase. In Initialization (`OrderFirst`), `BattleLifecycleInitializationProxy` updates that same
  lifecycle handle (#35; a second `UpdateInGroup` does not compile), so End and Restart apply on paused frames
  with no fixed step. Initialization does not update the clock, processor
  or phases. Fixed lifecycle and clock use the normal ordering bucket after the `OrderFirst`
  BeginFixedStep ECB. Fixed-only Clock declares `UpdateAfter(BattleLifecycleSystem)`; Collection explicitly
  follows Clock. Lifecycle has no cross-group UpdateBefore/After constraint, and Clock has no redundant
  UpdateAfter targeting the OrderFirst ECB. See Bootstrap's ECB lifetime restriction: actor ECBs must play back at the
  end of their producing fixed tick, not be retained until the next BeginFixedStep.
- Future systems join a phase with `[UpdateInGroup(typeof(<phase group>))]`; they never update
  `SimulationClock` and never run combat logic outside these groups.
- Concrete owners fill the groups: player producer and command processor in Collection, facing and movement in
  State; no placeholder processors.
- Around the fixed step (#22, verified in the real PlayerLoop): Input System events are processed in
  `PreUpdate` (default dynamic update mode) before `SimulationSystemGroup` (end of `Update`), and the camera
  updates in `LateUpdate` after it. Neither is a clock: input only feeds the Collection producer, the camera only
  reads. See `Input/INPUT.md` and `Presentation/CAMERA.md`.
- Phase groups require `SimulationClockRunning`: they never run in a world without a clock (e.g. an
  isolated test world that has no `CombatClockSystem`) or after the clock stopped.

## Tick semantics
- `uint`, `0` = `SimulationClock.NoTick` = "no tick yet"; never simulated, never valid for a command.
- The first simulated tick is `1`. Each fixed step increments exactly once, before every phase.
- From `CombatClockSystem` on (every combat phase) `Tick` is the tick being simulated. Outside the fixed
  step, and inside it before the clock (`BeginFixedStepSimulationEntityCommandBufferSystem`,
  `BattleLifecycleSystem`), `Tick` is the last completed tick and the next one is `Tick + 1` (what a
  frame-rate producer would target; the player producer of #22 runs in Collection and targets `Tick`).
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


## Stage 02.1 movement (#31)

`CombatMovementSystem` is the sole runtime integration writer of `CombatPosition` and `CombatMotion`.
It belongs only to `CombatStateUpdateSystemGroup`. The Collection group (processor last) completes first;
Publication reads the resulting positions and kinetics afterwards. No cross-group system-level sort target
is needed. The same auto-created system is listed in `ReplayWorld.RuntimeSystems` and thus also runs in
`CombatBenchmarkWorld`. It requires `SimulationClockRunning`, as does its parent phase; clock exhaustion
stops integration. No second tick counter or elapsed-time accumulator is introduced.

Each enabled `InCombat` with position, motion and applied command is integrated once per fixed step with
`SimulationTiming.TickDuration`. X/Z = validated axes times speed times dt, with a fixed `1/sqrt(2)` scale
when both axes are nonzero. No inertia: implicit or explicit None stops X/Z. Air control uses the same
speed. `Pressed & Jump` applies the impulse only when grounded; Held alone does nothing, including after
landing. Airborne presses are discarded, not buffered. Attack/Guard are not executed by movement, facing is untouched (Stage 05.1: a running strike makes movement ignore Move/Jump, see below).

Vertical integration is semi-implicit Euler: impulse if eligible, subtract gravity * dt from velocity,
then add velocity * dt to Y, then clamp landing and zero velocity. None still integrates gravity.
`CombatMotion.GroundHeight` is initial spawn Y (any legal finite positive/negative Y), **temporary per-actor
flat support**, not terrain physics. Spawn initialises velocity = 0 and grounded = true for leaders and
allies alike. End/Despawn/Restart destroys motion with its entity; no cache or separate cleanup system.
Initialization still processes paused requests without integrating or touching the clock. Actor ECBs
remain restricted to playback at EndFixedStep of their producing tick, before possible paused cleanup.

Central **provisional** tunables in `CombatMovementTuning` (not final balance):

| Value | Initial tuning | Units |
|---|---|---|
| MoveSpeed | 4 | world units / simulated second |
| JumpImpulse | 8 | world units / simulated second upward |
| Gravity | 20 | world units / simulated second squared downward |

One synchronous Burst pass, O(N), cached source-generated query, no managed/native scratch allocations,
structural changes or ID/entity lookup in movement. Actual timings and Burst compilation are NOT RUN.
Strict float mode avoids fast-math reassociation; replay assertions target the same Unity/Burst/platform,
**not a promise of bit-identical floating-point across CPUs or platforms**. Stage 04 must replace the
spawn-height support and add collision corrections inside this owner, not write a second position.

## Stage 02.2 facing (#33)

`CombatFacingSystem` is the sole runtime writer of `CombatFacing` after `BattleLifecycleSystem.Spawn` set it
from `BattleSetup`. It belongs only to `CombatStateUpdateSystemGroup` with `[UpdateBefore(CombatMovementSystem)]`,
so movement and every later State owner (attacks, guard) see the facing of the same tick. Like movement, it needs
no cross-group sort target: Collection, with the processor last, has already completed. It is listed in
`ReplayWorld.RuntimeSystems` next to movement, so default world, replay and benchmark run the same pair.

Each enabled `InCombat` reads only its published `AppliedCombatCommand`: `MoveX > 0` faces `Right`, `MoveX < 0`
faces `Left`, `MoveX == 0` keeps the previous facing (idle, Z-only, jump in place, explicit or implicit None).
Diagonals turn by the sign of X; airborne actors turn as well. The turn is instant, on the same fixed tick, with
the same rule for player and AI. Rejected, stale, duplicate, conflicting, unknown and not-yet-due commands never
reach `AppliedCombatCommand`, so they cannot turn anybody. A disabled `InCombat` keeps its facing.

Historical #33 text: there were no turn locks; Stage 05.1 adds the strike lock below (Guard still does not lock). A future attack/guard stage that
needs a turn restriction must define it explicitly and apply it in this owner, not add a second facing writer.
There is no "face the enemy" rule. End/Despawn/Restart destroy facing with its entity; a restarted battle spawns
fresh facing from its own setup. One synchronous Burst pass, O(N), no allocations or structural changes;
requires `SimulationClockRunning` like its phase. Burst compilation and Unity runtime are tracked in #30/#17.

## Stage 05.1 strikes and hits (PR #48)

State now holds four owners, in this order (attributes, checked by replay/default-world/benchmark tests):

```text
CombatStateUpdateSystemGroup
├─ CombatActionSystem    [UpdateBefore(Facing)]   sole writer of CombatAttackState (+ clears the hit ledger on a strike start)
├─ CombatFacingSystem    [UpdateBefore(Movement)] facing; locked during a strike except on its first tick
├─ CombatMovementSystem                           position/motion; Move/Jump ignored during a strike; records motion facts
└─ CombatHitSystem       [UpdateAfter(Movement)]  sole writer of CombatHealth and the hit ledger (CombatHitRecord)
```

- `CombatActionSystem` reads only `AppliedCombatCommand` + `CombatMotion.Grounded` (state at the start of the tick):
  series start/chain/end rules in `COMBAT_SPEC.md` ("Implemented minimal strike set"); timing/reach/damage in
  `CombatAttackTuning` (single authoritative table; presentation reads the same table). One Burst pass, no allocations.
- `CombatHitSystem` runs after movement, so boxes use this tick's final positions. Attackers in their Active phase are
  sorted by `CombatantId`, targets too; every enemy inside the box and not in the strike's ledger loses Damage HP (clamped
  at 0) once. Temp scratch is released in the same tick; brute force O(active attackers × combatants) until Stage 04's
  spatial index. No events/log are kept, so nothing is pending between ticks: a despawned target is simply absent.
- `CombatMotion` gains movement facts written only by the movement owner: `LandTick`, `LastMoveTick`,
  `MoveStreakStartTick`/`MoveStreakDistance` (stops of ≤ `MoveStreakGraceTicks` = 6 continue a streak, landing restarts it).
  They describe what actually moved (after strike locks), never the raw command, so Stage 04 collisions stay compatible.
- Facing and movement read `CombatAttackState` through a read-only lookup; entities without it (isolated unit-test
  actors) are never locked, so earlier writer-separation tests keep their meaning.
- Lifecycle: the spawn archetype carries `CombatAttackState` (free), `CombatHealth` (100/100) and an empty
  `CombatHitRecord` buffer; End/Restart/Despawn destroy them with the entity. Pause runs no tick, so phases freeze.

## Stage 03 tuning: strike windows from the animation phases

`CombatStrikeSchedule` (singleton, created and destroyed by `CombatActionSystem`) holds one `CombatStrikeTiming` per strike:
`ActiveStart`, `RecoveryStart`, `End` and `ChainPoint` in ticks since the strike's first tick. It starts as `Default`
(the `CombatAttackTuning` windows: what tests, benchmarks and worlds without a publisher use). A scene replaces it with
`CombatStrikeSchedule.Publish(EntityManager, table)` (main thread, outside a fixed step; validated, `Revision + 1`):
`CombatSmokeBootstrap` derives it from the strike clips' Preparation/Active/Recovery frames, duration and curve.

- `CombatActionSystem` copies `schedule.Get(kind)` into `CombatAttackState.Timing` on every strike start (series start or
  follow-up). Buffering from `ActiveStart`, chaining at `ChainPoint`, ending at `End` read only that copy. `CombatHitSystem`
  resolves hits on ticks where `Timing.PhaseAt(tick − StartTick) == Active`; the per-strike ledger still allows one hit per
  target. `CombatMovementSystem` applies planar Move normally (diagonal scaling, same position owner) on **Preparation (Windup)
  ticks**, ignores Move in Active/Recovery and Jump during the whole strike; moving never cancels the strike. Facing is unchanged
  (turn only on a strike's first tick).
- A published table therefore changes **new** strikes only; nothing is recomputed per fighter or per tick (one singleton read
  per tick, a struct copy per strike start). Quantization (seconds → ticks, `CombatStrikeTiming.TryFromSeconds`): Active =
  ticks overlapping the Active time, so it always has ≥ 1 tick; Preparation/Recovery may have 0 ticks when shorter than one
  tick. `ChainPoint = min(RecoveryStart + ChainDelay, End)`.
- The table is World state outside the command stream: a replay must publish the same table before its first strike
  (the replay tests do). Pause freezes strikes with the clock; restart/despawn destroy the attack state with the fighter.
