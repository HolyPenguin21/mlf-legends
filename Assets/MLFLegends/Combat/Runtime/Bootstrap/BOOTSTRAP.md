# Battle bootstrap and lifecycle (Stage 01.4, Issue #5; tick-safe requests and atomic reset, Issue #14; rosters up to 200 vs 200, Issue #7)

One owner creates and destroys combatants, allocates `CombatantId` and records when a battle started.
A battle restart reuses the same `World`: the World, `SimulationClock`, the command queue and the fixed
groups are never recreated or reset (see `Docs/DECISIONS.md`, "Battle restart and time policy"). This
folder has no movement, attack, input or AI logic.

## Pieces

| Type | Role | Writer |
|---|---|---|
| `BattleLifecycleSystem` | The single creator/destroyer of combatant entities and the only caller of `CombatCommandQueue.ResetForBattle`. Applies `BattleRequest`s in Initialization and before every fixed tick, including catch-up. | — |
| `BattleSession` | Singleton: `State`, `BattleStartTick`, `BattleNumber`, `HumanCombatant`, `AiCombatant`, `Owner`, `LastOutcome`. Its entity holds the request buffer and the allocator. | lifecycle system |
| `BattleRequest` (buffer) | `Start`, `Restart`, `End`, `Despawn(id)`, optional `Owner` token. Appended by anyone on the main thread (`BattleRequest.Submit`). | requesters |
| `CombatantIdAllocator` (internal) | Monotonic, non-reusing for the whole World lifetime; never wraps to `0`. | lifecycle system |
| `BattleSetup` / `CombatantSetup` | Team, position and facing of the human and the AI combatant, and how many allies each has (`HumanAllies`, `AiAllies`, default 0 = 1v1). Values, not state. | — |
| `CombatSmokeBootstrap` | Optional `MonoBehaviour` for a scene: owned `Restart` on enable, owned `End` on disable/destroy. No combat state. | — |

`BattleLifecycleSystem.OnCreate` creates the session entity (a second instance throws, so a world never has
two owners) and `OnDestroy` removes it together with any remaining combatant. Exiting Play Mode disposes
the default World with everything in it; entering it again starts with IDs from 1 and battle number 0.

## Where it runs (#19, #35)
The **same `BattleLifecycleSystem` instance** runs at two boundaries. It has exactly one
`[UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]` (normal ordering bucket): Entities 6.5 does not
allow a second `UpdateInGroup` attribute (CS0579, the #19 version did not compile). In
`InitializationSystemGroup` (`OrderFirst`) the thin `BattleLifecycleInitializationProxy` calls `Update()` on
that existing handle (`WorldUnmanaged.GetExistingUnmanagedSystem` + `SystemHandle.Update`). The proxy has no
logic, state or Burst path; it requires a `BattleSession` and skips a missing/destroyed lifecycle, and the
lifecycle's own `RequireForUpdate` (clock, queue) still decides whether it runs. There is one session, one
request buffer, one allocator and one `OnUpdate`/`Apply` path, not two lifecycle owners. Default world
auto-creates both; `ReplayWorld.RuntimeSystems` (also the benchmark) lists both; manual test worlds add
the proxy to Initialization and the lifecycle to FixedStep. The benchmark applies requests by updating the
lifecycle handle directly, outside measured regions.

- Initialization is a main-thread boundary outside combat phases. It runs on rendered frames even with
  `Time.timeScale = 0`, without calling the fixed-step group, clock, command processor or combat phases.
- Inside each fixed step the order remains **BeginFixedStep ECB → Lifecycle → Clock → Collection →
  State → Publication → EndFixedStep ECB**. Lifecycle and clock use the normal bucket, after the
  `OrderFirst` BeginFixedStep ECB. Fixed-only Clock declares `UpdateAfter(BattleLifecycleSystem)`;
  Collection explicitly follows Clock. Clock needs no explicit dependency on BeginFixedStep: its
  `OrderFirst` bucket already precedes Lifecycle and Clock. Lifecycle declares no UpdateBefore/After
  constraint, since Clock exists only in FixedStep and must not be an ordering target in Initialization.
- Both boundaries read `SimulationClock.Tick` as the last completed tick. Each call copies and clears
  the request buffer before structural changes. The next call sees an empty buffer, so no request or
  queue reset is repeated merely because the system is reached from two groups.

| Request made | Applied |
|---|---|
| UI / MonoBehaviour / scene OnDisable | next Initialization or fixed boundary, whichever runs first |
| inside tick T, any combat phase | after T completes, before T + 1; also between catch-up ticks in the same frame |
| on pause with no further fixed ticks | next frame's Initialization; no simulated tick is manufactured |

Start and Restart also apply while paused: they replace the roster with fresh IDs, clear old commands,
and record the unchanged tick as `BattleStartTick`. Relative time remains 0 until the first real fixed
step after resume, which is relative tick 1. End removes even defeated combatants, clears command stores
through the existing `ResetForBattle`, and makes the session Idle. Nothing is replayed on resume.

The guarantee is **next safe boundary**, not synchronous removal in `Submit`/`OnDisable`: after a scene
unloads, its owned End is consumed by the next Initialization even if the game stays paused. If the World
is disposed before then (application/Play Mode exit), World teardown removes its entities instead.
Async scene ownership is unchanged: Restart(B), End(A) leaves B alive, whether processed together or on
separate paused frames. Enable then disable before any tick gives Restart(A), End(A), leaving no actors.
The rate manager's mandatory first World step at t = 0 is unchanged; pause tests first consume it.

**Entity command buffers / jobs.** `EntityManager` structural operations synchronize relevant ECS jobs.
No runtime combat system currently records ECB operations. Future combatant structural operations must use
`EndFixedStepSimulationEntityCommandBufferSystem` and play back in the producing tick. Do not defer actor
handles to BeginFixedStep of the next tick, Initialization, EndSimulation or a later frame: paused-frame
lifecycle cleanup can destroy those actors before playback. Request buffers store IDs/values, never Entity
handles. This tightens the old permission to use next-tick BeginFixedStep ECBs; there are no existing
producers to migrate. Do not add another spawner/destructor or flush arbitrary ECBs from scene callbacks.

## Combatant created by `Start`

| Component | Value |
|---|---|
| `CombatantId` | Fresh, from the World allocator (human = first, AI = first + 1, then the allies; see Rosters). |
| `CombatTeam` | `Setup.Human.Team` / `Setup.Ai.Team` (must differ). |
| `CombatPosition` | `Setup.*.Position` (finite). |
| `CombatFacing` | `Setup.*.Facing`, `Left` or `Right` (0 rejected). |
| `InCombat` | Enabled. |

Human and AI combatants have exactly the same archetype. Who is human is only `BattleSession.HumanCombatant`;
`CombatCommand.Source` stays diagnostics. `AppliedCombatCommand` is added by the command processor on the
first tick. The human and AI leaders have constant diagnostic names **`Combatant Human`** and
**`Combatant AI`**; allies are unnamed. `CombatantId` is the sole authoritative identity and remains
monotonic across battles. Names never include IDs, team values or counters: this bootstrap contributes at
most two unique actor names to Entities' process-wide name store, regardless of roster size, Restart count
or new Worlds without Domain Reload. Roles here describe the two session leaders, not all AI-controlled allies.
The regression inspects every actor over 45 alternating 1v1/200v200 sessions with Start/End and Restart;
an ID-derived name fails after the second battle, without thousands of cycles or filling the global store.

## Rosters (Issue #7)
`BattleSetup.HumanAllies` / `AiAllies` add ordinary combatants to the human's and the AI's team; both 0 (the
default, `BattleSetup.Default`, `CombatSmokeBootstrap`) is the 1v1 duel, unchanged. Allies are spawned by the same
`Start`, through the same private `Spawn`, with the same archetype; there is no other factory.

| Rule | Value |
|---|---|
| Team size | leader + allies, at most `BattleSetup.MaxCombatantsPerTeam` = 200 per team (`HumanAllies`, `AiAllies` ≤ 199), so ≤ 400 combatants. More is `InvalidSetup`, checked before any side effect. The bound matches the command queue's `MaxPendingCommands` = 400 × `MaxLeadTicks`. |
| IDs | one `TryReserve(Setup.CombatantCount)` per start: human = `first`, AI = `first + 1`, human allies `first + 2 …`, then AI allies, consecutive. Too few IDs left → `IdsExhausted`, nothing consumed. |
| Team, facing | the leader's. |
| Position | ally `k` (0-based) of a leader at `P` facing `f` (±1): `P + (-f · (1 + k / 10), 0, k % 10 - 4.5) × AllySpacing` (1 world unit): rows of 10 along Z behind the leader. Deterministic; Stage 02.1 adds movement but no collisions or AI, so actor overlap is still unresolved. |
| Session | `HumanCombatant`/`AiCombatant` are the two leaders; allies are only entities. A restart destroys every combatant and reserves fresh IDs for the new roster, of any size. |

Example: `HumanAllies = AiAllies = 199` is 200 vs 200 with IDs `first … first + 399`.

## Requests
Applied in append order; every outcome is stored in `BattleSession.LastOutcome`. A rejection has no side
effect (no destroy, no queue reset, no ID consumed) and does not stop the following requests of the batch.

| Request | Idle | Active |
|---|---|---|
| `Start(setup)` | starts | `AlreadyActive` |
| `Restart(setup)` | starts | ends the battle and starts a new one in the same step |
| `End(owner)` | `NotActive` | destroys all combatants, resets the queue, `Idle`; `NotOwner` if `owner != 0` did not start this battle |
| `Despawn(id)` | `NotActive` | destroys that combatant (`UnknownCombatant` if none); the other is untouched |

**Owner.** `Start`/`Restart` store the request's `Owner` in `BattleSession.Owner` (0 = unowned); a `Restart`
takes a running battle over. An `End` with a non-zero owner only ends that owner's battle, an `End` with 0 ends
any. This keeps requesters that come and go independent: on an async scene switch Unity enables the next
scene's bootstrap (its `Restart`) before it disables the previous one (its late `End`), which is then
`NotOwner` instead of stopping the new battle.

`Start`/`Restart` are rejected with `InvalidSetup` (equal teams, facing not ±1, non-finite position),
`ClockStopped` (the clock reached `SimulationClock.LastTick`: this World cannot host a new battle, restart the
real World), `CommandQueueRejected` (below) or `IdsExhausted` (fewer IDs left than `Setup.CombatantCount`; the
allocator never wraps),
checked in this order before anything changes.

**Atomic reset.** `Start`, `Restart` and `End` destroy combatants and then reset the command queue. Before any
destroy they ask the queue's own validator, `CombatCommandQueue.CanResetForBattle(EntityManager, Tick)`: the same
checks `ResetForBattle` throws on, kept only in the queue. If it is false (e.g. a foreign caller of
`ResetForBattle` moved the boundary past the current tick) the request is `CommandQueueRejected`: combatants, IDs,
owner, `BattleStartTick` and the queue stay as they were, and the next requests of the batch are still applied.
Nothing in the lifecycle path throws mid-batch, so a batch never loses its remaining requests or the
session/allocator write-back. Such a boundary clears itself once the clock reaches it.

## Start / restart sequence (either safe boundary, before the next tick advances)
0. Check: setup, state, running clock, `CombatCommandQueue.CanResetForBattle(Tick)`, ID reservation.
1. Destroy every entity with `CombatantId` (also defeated, i.e. `InCombat` disabled). Nothing else is destroyed.
2. `CombatCommandQueue.ResetForBattle(EntityManager, SimulationClock.Tick)`: drops submissions, pending and
   future commands, results and `AppliedCombatCommand` of the previous battle.
3. Spawn the human, the AI and their allies with new IDs (`Setup.CombatantCount`, reserved in step 0). The same private Spawn initialises `CombatMotion` with spawn Y support, zero vertical velocity and grounded = true (#31); no second spawner. Motion is destroyed with its entity on End/Restart/Despawn.
4. `BattleSession`: `Active`, `BattleStartTick = SimulationClock.Tick`, `BattleNumber + 1`.

`End` does steps 0 (owner, queue check), 1–2 and sets `Idle`. The next real fixed step advances to absolute tick `BattleStartTick + 1` = relative tick 1 (`BattleSession.RelativeTick(tick) = tick - BattleStartTick`); commands for `Tick <= BattleStartTick`
are rejected by the processor as `BeforeBattleStart`. `BattleStartTick` is a recorded value, not a clock.
Because IDs are never reused, a late command for a previous battle's combatant is `UnknownCombatant`.
No `Entity` handle or ID→Entity cache is kept between updates.

## Usage
```csharp
BattleRequest.Submit(world.EntityManager, BattleRequest.Start(BattleSetup.Default));
// ... after the next fixed step (it already simulated relative tick 1):
var session = query.GetSingleton<BattleSession>(); // HumanCombatant, AiCombatant, BattleStartTick
```
In a scene: add `CombatSmokeBootstrap` to one GameObject (`Assets/Scenes/CombatSmoke.unity`). Disabling it
ends the battle only if that bootstrap started it (the latest enabled bootstrap owns the battle).
`CombatSmoke.unity` also holds *Player Input* (`PlayerInputAdapter`, #22) and its existing *Main Camera* with
`CombatCameraController` (#22) and `CombatSpriteRenderer` (#38, technical atlas). They only read
`BattleSession`/combatants; none sends requests, so the bootstrap's ownership rules are unchanged (see
`Input/INPUT.md`, `Presentation/CAMERA.md`, `Presentation/Sprites/SPRITES.md`).

A request made inside a fixed step (e.g. a later defeat owner ending the battle on tick T) is applied before
tick T + 1, also when T + 1 is a catch-up step of the same render frame: no later tick runs combat logic of the
ended battle.

`BattleRequest.Submit` and `CombatSmokeBootstrap` dispose the `EntityQuery` they build (a query created from an
`EntityManager` is Persistent until the World is disposed), on the error path too.

## Manual check: Enter/Exit Play Mode with Domain Reload disabled
Not automated (no Editor automation for this). Record the result in `Docs/QA.md`; `NOT RUN` until performed.
1. *Project Settings > Editor > Enter Play Mode Settings*: *Do not reload Domain or Scene* (also try *Reload
   Scene only*). Open `Assets/Scenes/CombatSmoke.unity` and *Window > Entities > Hierarchy*.
2. Enter Play Mode: exactly two entities `Combatant Human` and `Combatant AI` (IDs 1 and 2); the `BattleSession` entity is `Active`,
   `BattleNumber` 1, `Owner` not 0; no Console errors.
3. Exit Play Mode: no errors and no "leak"/"not disposed" warnings in the Console.
4. Repeat Enter/Exit 3 times: each entry again shows `Combatant Human`/`Combatant AI` (IDs 1/2), `BattleNumber` 1, a clock
   starting near tick 0 (a new World each time; the static owner counter of `CombatSmokeBootstrap` keeps
   growing between sessions, which is harmless).
5. In Play Mode, disable the bootstrap GameObject: the combatants disappear at the next safe boundary, including while paused. Enable it again:
   `Combatant Human` and `Combatant AI`, with fresh IDs 3 and 4, `BattleNumber` 2.
6. In Play Mode set `Time.timeScale = 0` from the debugger/test harness after at least one fixed tick.
   Disable/enable the bootstrap and unload/reload CombatSmoke additively. Verify End removes all actors on
   the next rendered frame, Restart changes IDs without advancing Tick, and a late old-owner End does not
   stop the replacement scene's battle. Restore `timeScale = 1`: no old command executes; the new battle's
   first real simulated tick is relative tick 1. Check Console for errors/leaks.
7. Restore the Enter Play Mode setting and timeScale.

## Not implemented here
AI producers, movement integration, attacks, collisions, rendering (player input lives in Input/ #22, the camera in
Presentation/ #22, movement and facing in Simulation/CombatMovementSystem #31 and CombatFacingSystem #33; Spawn only sets
the initial facing). Large rosters (#7) are the `Start` above with
allies, reusing `Spawn` and the same allocator; the benchmark in `Assets/Tests/EditMode/Performance/` uses only that.
