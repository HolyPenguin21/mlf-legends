# Battle bootstrap and lifecycle (Stage 01.4, Issue #5; tick-safe requests and atomic reset, Issue #14; rosters up to 200 vs 200, Issue #7)

One owner creates and destroys combatants, allocates `CombatantId` and records when a battle started.
A battle restart reuses the same `World`: the World, `SimulationClock`, the command queue and the fixed
groups are never recreated or reset (see `Docs/DECISIONS.md`, "Battle restart and time policy"). This
folder has no movement, attack, input or AI logic.

## Pieces

| Type | Role | Writer |
|---|---|---|
| `BattleLifecycleSystem` | The single creator/destroyer of combatant entities and the only caller of `CombatCommandQueue.ResetForBattle`. Applies `BattleRequest`s at the start of every fixed step. | — |
| `BattleSession` | Singleton: `State`, `BattleStartTick`, `BattleNumber`, `HumanCombatant`, `AiCombatant`, `Owner`, `LastOutcome`. Its entity holds the request buffer and the allocator. | lifecycle system |
| `BattleRequest` (buffer) | `Start`, `Restart`, `End`, `Despawn(id)`, optional `Owner` token. Appended by anyone on the main thread (`BattleRequest.Submit`). | requesters |
| `CombatantIdAllocator` (internal) | Monotonic, non-reusing for the whole World lifetime; never wraps to `0`. | lifecycle system |
| `BattleSetup` / `CombatantSetup` | Team, position and facing of the human and the AI combatant, and how many allies each has (`HumanAllies`, `AiAllies`, default 0 = 1v1). Values, not state. | — |
| `CombatSmokeBootstrap` | Optional `MonoBehaviour` for a scene: owned `Restart` on enable, owned `End` on disable/destroy. No combat state. | — |

`BattleLifecycleSystem.OnCreate` creates the session entity (a second instance throws, so a world never has
two owners) and `OnDestroy` removes it together with any remaining combatant. Exiting Play Mode disposes
the default World with everything in it; entering it again starts with IDs from 1 and battle number 0.

## Where it runs
`FixedStepSimulationSystemGroup`, `OrderFirst`, after `BeginFixedStepSimulationEntityCommandBufferSystem` and
**before** `CombatClockSystem`, i.e. at the start of **every** fixed step, catch-up steps included. That is a
boundary between fixed steps: the clock has not advanced yet, so `SimulationClock.Tick` is the last completed
tick, the command processor has already handled it, and the step that follows in the same update is `Tick + 1`.
Requests are therefore never applied in the command-collection, state-update or publication phase of a tick.
Still one owner, one clock and the standard fixed-step group: only the position of the lifecycle system changed.

```text
frame N    Update: UI / MonoBehaviour submits Start ..................................... queued
frame N+1  step 1: Lifecycle (applies Start, BattleStartTick = T) -> Clock T+1 (relative 1) -> Collection -> State -> Publication
           step 2: Lifecycle (nothing)                             -> Clock T+2 (relative 2) -> Collection -> State (submits End) -> Publication
           step 3: Lifecycle (applies End, queue boundary = T+2)   -> Clock T+3 -> phases see no combatant of the ended battle
```

| Request made | Applied |
|---|---|
| between frames (UI, `MonoBehaviour`, test) | at the start of the next fixed step, before its tick |
| inside tick T, any phase (collection, state update, publication) | tick T completes; applied at the start of tick T + 1, also when T + 1 is a catch-up step of the same frame |

A frame that runs no fixed step (high frame rate, `Time.timeScale = 0`) applies nothing and simulates nothing:
requests wait for the next step, so the tick timeline is the same as if they had been applied at once. Readers
of `BattleSession` (UI, tests) see the change after the next tick, not after the next frame; while paused, a
requested end keeps the combatants until the simulation resumes. The very first fixed step of a World always
runs (Entities' catch-up manager steps at t = 0), so a request made before it is applied immediately.

**Entity command buffers.** Combatants can now be destroyed between two ticks of the same frame. A system inside
the fixed step that records commands for combatants must use the fixed-step buffers
(`BeginFixedStepSimulationEntityCommandBufferSystem`, played back before this system, or
`EndFixedStepSimulationEntityCommandBufferSystem`, played back at the end of the same tick), never
`EndSimulationEntityCommandBufferSystem` or later: those play back after further ticks may have destroyed the
target. No system uses an ECB yet.

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
first tick. The human and the AI leader are named `Combatant <id>` for the Entities Hierarchy window; allies are
not named: Entities keeps entity names in a process-wide store of 16 384 unique strings that is only emptied by a domain
reload, and IDs are never reused, so a name per ally would fill it after ~40 restarts of a 200 vs 200 battle (one Editor
error, then no entity gets a name). Two names per battle still accumulate: ~8 000 battles per Editor domain.

## Rosters (Issue #7)
`BattleSetup.HumanAllies` / `AiAllies` add ordinary combatants to the human's and the AI's team; both 0 (the
default, `BattleSetup.Default`, `CombatSmokeBootstrap`) is the 1v1 duel, unchanged. Allies are spawned by the same
`Start`, through the same private `Spawn`, with the same archetype; there is no other factory.

| Rule | Value |
|---|---|
| Team size | leader + allies, at most `BattleSetup.MaxCombatantsPerTeam` = 200 per team (`HumanAllies`, `AiAllies` ≤ 199), so ≤ 400 combatants. More is `InvalidSetup`, checked before any side effect. The bound matches the command queue's `MaxPendingCommands` = 400 × `MaxLeadTicks`. |
| IDs | one `TryReserve(Setup.CombatantCount)` per start: human = `first`, AI = `first + 1`, human allies `first + 2 …`, then AI allies, consecutive. Too few IDs left → `IdsExhausted`, nothing consumed. |
| Team, facing | the leader's. |
| Position | ally `k` (0-based) of a leader at `P` facing `f` (±1): `P + (-f · (1 + k / 10), 0, k % 10 - 4.5) × AllySpacing` (1 world unit): rows of 10 along Z behind the leader. Deterministic; there is no movement, collision or AI yet, so overlap with the other team is irrelevant in Stage 01. |
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

## Start / restart sequence (start of a fixed step, before the clock advances)
0. Check: setup, state, running clock, `CombatCommandQueue.CanResetForBattle(Tick)`, ID reservation.
1. Destroy every entity with `CombatantId` (also defeated, i.e. `InCombat` disabled). Nothing else is destroyed.
2. `CombatCommandQueue.ResetForBattle(EntityManager, SimulationClock.Tick)`: drops submissions, pending and
   future commands, results and `AppliedCombatCommand` of the previous battle.
3. Spawn the human, the AI and their allies with new IDs (`Setup.CombatantCount`, reserved in step 0).
4. `BattleSession`: `Active`, `BattleStartTick = SimulationClock.Tick`, `BattleNumber + 1`.

`End` does steps 0 (owner, queue check), 1–2 and sets `Idle`. After a start the clock advances in the same
fixed step to absolute tick `BattleStartTick + 1` = relative tick 1 (`BattleSession.RelativeTick(tick) = tick - BattleStartTick`); commands for `Tick <= BattleStartTick`
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

A request made inside a fixed step (e.g. a later defeat owner ending the battle on tick T) is applied before
tick T + 1, also when T + 1 is a catch-up step of the same render frame: no later tick runs combat logic of the
ended battle.

`BattleRequest.Submit` and `CombatSmokeBootstrap` dispose the `EntityQuery` they build (a query created from an
`EntityManager` is Persistent until the World is disposed), on the error path too.

## Manual check: Enter/Exit Play Mode with Domain Reload disabled
Not automated (no Editor automation for this). Record the result in `Docs/QA.md`; `NOT RUN` until performed.
1. *Project Settings > Editor > Enter Play Mode Settings*: *Do not reload Domain or Scene* (also try *Reload
   Scene only*). Open `Assets/Scenes/CombatSmoke.unity` and *Window > Entities > Hierarchy*.
2. Enter Play Mode: exactly two entities `Combatant 1` and `Combatant 2`; the `BattleSession` entity is `Active`,
   `BattleNumber` 1, `Owner` not 0; no Console errors.
3. Exit Play Mode: no errors and no "leak"/"not disposed" warnings in the Console.
4. Repeat Enter/Exit 3 times: each entry again shows `Combatant 1`/`Combatant 2`, `BattleNumber` 1, a clock
   starting near tick 0 (a new World each time; the static owner counter of `CombatSmokeBootstrap` keeps
   growing between sessions, which is harmless).
5. In Play Mode, disable the bootstrap GameObject: the combatants disappear after the next tick. Enable it again:
   `Combatant 3` and `Combatant 4`, `BattleNumber` 2.
6. Restore the Enter Play Mode setting.

## Not implemented here
Input and AI producers, movement, attacks, collisions, rendering. Large rosters (#7) are the `Start` above with
allies, reusing `Spawn` and the same allocator; the benchmark in `Assets/Tests/EditMode/Performance/` uses only that.
