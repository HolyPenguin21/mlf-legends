# Battle bootstrap and lifecycle (Stage 01.4, Issue #5)

One owner creates and destroys combatants, allocates `CombatantId` and records when a battle started.
A battle restart reuses the same `World`: the World, `SimulationClock`, the command queue and the fixed
groups are never recreated or reset (see `Docs/DECISIONS.md`, "Battle restart and time policy"). This
folder has no movement, attack, input or AI logic.

## Pieces

| Type | Role | Writer |
|---|---|---|
| `BattleLifecycleSystem` | The single creator/destroyer of combatant entities and the only caller of `CombatCommandQueue.ResetForBattle`. Applies `BattleRequest`s. | — |
| `BattleSession` | Singleton: `State`, `BattleStartTick`, `BattleNumber`, `HumanCombatant`, `AiCombatant`, `LastOutcome`. Its entity holds the request buffer and the allocator. | lifecycle system |
| `BattleRequest` (buffer) | `Start`, `Restart`, `End`, `Despawn(id)`. Appended by anyone on the main thread (`BattleRequest.Submit`). | requesters |
| `CombatantIdAllocator` (internal) | Monotonic, non-reusing for the whole World lifetime; never wraps to `0`. | lifecycle system |
| `BattleSetup` / `CombatantSetup` | Team, position and facing of the human and the AI combatant. Values, not state. | — |
| `CombatSmokeBootstrap` | Optional `MonoBehaviour` for a scene: `Restart` on enable, `End` on disable/destroy. No combat state. | — |

`BattleLifecycleSystem.OnCreate` creates the session entity (a second instance throws, so a world never has
two owners) and `OnDestroy` removes it together with any remaining combatant. Exiting Play Mode disposes
the default World with everything in it; entering it again starts with IDs from 1 and battle number 0.

## Where it runs
`SimulationSystemGroup`, `OrderFirst`, after `BeginSimulationEntityCommandBufferSystem` and **before**
`FixedStepSimulationSystemGroup`, once per frame. That is a boundary between fixed steps: `SimulationClock.Tick`
is the last completed tick, the command processor has already handled it, and the next step is `Tick + 1`.
Requests are therefore never applied in the state-update or publication phase of a tick. A request made from
a `MonoBehaviour`, a test or another system during a frame waits for this point (at most one frame).

## Combatant created by `Start`

| Component | Value |
|---|---|
| `CombatantId` | Fresh, from the World allocator (human = first, AI = first + 1). |
| `CombatTeam` | `Setup.Human.Team` / `Setup.Ai.Team` (must differ). |
| `CombatPosition` | `Setup.*.Position` (finite). |
| `CombatFacing` | `Setup.*.Facing`, `Left` or `Right` (0 rejected). |
| `InCombat` | Enabled. |

Human and AI combatants have exactly the same archetype. Who is human is only `BattleSession.HumanCombatant`;
`CombatCommand.Source` stays diagnostics. `AppliedCombatCommand` is added by the command processor on the
first tick. Entities are named `Combatant <id>` for the Entities Hierarchy window.

## Requests
Applied in append order; every outcome is stored in `BattleSession.LastOutcome`. A rejection has no side
effect (no destroy, no queue reset, no ID consumed).

| Request | Idle | Active |
|---|---|---|
| `Start(setup)` | starts | `AlreadyActive` |
| `Restart(setup)` | starts | ends the battle and starts a new one in the same step |
| `End` | `NotActive` | destroys all combatants, resets the queue, `Idle` |
| `Despawn(id)` | `NotActive` | destroys that combatant (`UnknownCombatant` if none); the other is untouched |

`Start`/`Restart` are rejected with `InvalidSetup` (equal teams, facing not ±1, non-finite position),
`ClockStopped` (the clock reached `SimulationClock.LastTick`: this World cannot host a new battle, restart the
real World) or `IdsExhausted` (fewer than 2 IDs left; the allocator never wraps), checked before anything changes.

## Start / restart sequence (one step, between fixed ticks)
1. Destroy every entity with `CombatantId` (also defeated, i.e. `InCombat` disabled). Nothing else is destroyed.
2. `CombatCommandQueue.ResetForBattle(EntityManager, SimulationClock.Tick)`: drops submissions, pending and
   future commands, results and `AppliedCombatCommand` of the previous battle.
3. Spawn the two combatants with new IDs.
4. `BattleSession`: `Active`, `BattleStartTick = SimulationClock.Tick`, `BattleNumber + 1`.

`End` does steps 1–2 and sets `Idle`. The next fixed step is absolute tick `BattleStartTick + 1` = relative
tick 1 (`BattleSession.RelativeTick(tick) = tick - BattleStartTick`); commands for `Tick <= BattleStartTick`
are rejected by the processor as `BeforeBattleStart`. `BattleStartTick` is a recorded value, not a clock.
Because IDs are never reused, a late command for a previous battle's combatant is `UnknownCombatant`.
No `Entity` handle or ID→Entity cache is kept between updates.

## Usage
```csharp
BattleRequest.Submit(world.EntityManager, BattleRequest.Start(BattleSetup.Default));
// ... next frame, before the first fixed step of the battle:
var session = query.GetSingleton<BattleSession>(); // HumanCombatant, AiCombatant, BattleStartTick
```
In a scene: add `CombatSmokeBootstrap` to one GameObject (`Assets/Scenes/CombatSmoke.unity`). Use one per
scene; disabling it ends the battle of the World.

## Not implemented here
Input and AI producers, movement, attacks, collisions, rendering. A spawn of many combatants for the
benchmark (#7) is to extend `BattleSetup`/`Start` here, reusing `Spawn` and the same allocator, not a copy.
