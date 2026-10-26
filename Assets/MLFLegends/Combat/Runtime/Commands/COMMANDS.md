# Combat command processor (Stage 01.3, Issue #4)

One queue, one processor, one application point for player and AI commands. This folder implements
rules 1–6 of `Core/CONTRACTS.md` ("Command contract") on the fixed tick of `Simulation/SIMULATION.md`.
It never writes position, facing, `InCombat`, the clock or any other combat state, and has no
movement, attack or AI logic.

## Pieces

| Type | Role | Writer |
|---|---|---|
| `CombatCommandQueue` | Singleton: `BattleStartTick`, `LastProcessedTick`, bounds, `ResetForBattle`. Its entity holds the three buffers below. | processor; `ResetForBattle` |
| `CombatCommandSubmission` (buffer) | Producer inbox. Producers only append. | producers (player input adapter, AI) |
| `PendingCombatCommand` (buffer, internal) | Valid future commands, submission order, ≤ `MaxPendingCommands`. | processor |
| `CombatCommandResult` (buffer) | Outcome of every command processed on `LastProcessedTick`, sorted by (`Combatant`, `Tick`, submission order). | processor |
| `AppliedCombatCommand` (per combatant) | The command each in-combat combatant executes on the current tick (accepted or implicit None). Input of the Stage 02+ state owners (`CombatFacingSystem`, `CombatMovementSystem`). | processor |
| `CombatCommandProcessorSystem` | The single consumer and applier. `OrderLast` in `CombatCommandCollectionSystemGroup`. | — |

`CombatCommandProcessorSystem` creates the queue entity in `OnCreate` (a second instance throws, so a
world never has two processors) and destroys it in `OnDestroy`. It adds `AppliedCombatCommand` to every
entity with `CombatantId` + `InCombat` that lacks it, so the spawner (#5) needs no command-specific setup.

## Producing commands
```csharp
SystemAPI.GetSingletonBuffer<CombatCommandSubmission>().Add(new CombatCommandSubmission(command));
```
- Inside `CombatCommandCollectionSystemGroup` (before the processor): target the current
  `SimulationClock.Tick`. The order of producer systems in that group is the submission order, so
  producers must fix it explicitly with `[UpdateBefore]`/`[UpdateAfter]`; the relative order of
  unconstrained systems is not a contract.
- Outside the fixed step (frame-rate input): target `Tick + 1` (or up to `MaxLeadTicks` ahead). These
  submissions precede every in-step producer of the next tick. On frames that run no tick the producer
  must not resubmit the same key: the first submission wins and later ones are `Duplicate`/`Conflict`.
- Never write `AppliedCombatCommand` or any combat component. No `UnityEngine.Input` in the processor.

## Policy per tick T (same rules for every `Source`)
Validation, in this order, at submission processing:

| Case | Outcome |
|---|---|
| `Tick == 0` | `InvalidTick` |
| `Combatant` = `CombatantId.Invalid` | `InvalidCombatant` |
| `MoveX`/`MoveZ` ∉ −1..1, undefined `Held`/`Pressed` bits, `Source` ∉ {Player, AI} | `InvalidValue` |
| `Tick <= BattleStartTick` (previous battle / already started tick) | `BeforeBattleStart` |
| `Tick < T` | `Stale` |
| `Tick > T + MaxLeadTicks` (32) | `TooFarAhead` |
| `T < Tick <= T + 32`, combatant not in combat now | `UnknownCombatant` |
| `T < Tick <= T + 32`, key already pending | `Duplicate` / `Conflict` against the pending (first) command |
| `T < Tick <= T + 32`, otherwise | `Deferred`, kept pending; `QueueFull` if the pending store is full |
| `Tick == T` | resolved below |

Resolution of the commands for T (pending ones first, then this tick's submissions, i.e. submission order):

| Case | Outcome |
|---|---|
| No entity with that `CombatantId` and enabled `InCombat` (never spawned, defeated, despawned) | `UnknownCombatant` (all commands of that key) |
| First command for (`Combatant`, T) | `Accepted` |
| Later command, equal to the accepted one | `Duplicate` |
| Later command that differs (values or `Source`, e.g. player vs AI) | `Conflict`; never merged, never overwrites |
| Pending command whose tick passed without processing (processor did not run) | `Stale` |
| In-combat combatant without an accepted command | no result entry; `AppliedCombatCommand` = None for T, `WasSubmitted == false` |

A submitted None command is a normal `Accepted` command (`WasSubmitted == true`).

## Order and cost
- Commands for T are sorted by (`CombatantId`, submission order); results by (`CombatantId`, `Tick`,
  submission order). Nothing depends on chunk or `EntityQuery` order. The only order-sensitive input is
  the submission order itself (rule 3: first wins).
- Per tick: one pass over pending + submissions, one sort of the processed commands, one pass over
  in-combat combatants with an O(1) hash lookup. O(C log C + N); no combatant × command scan.
- Bounds: pending holds at most one command per (in-combat combatant, future tick), i.e. ≤ N × 32, and is
  capped at `MaxPendingCommands` = 400 × 32 = 12 800 × 16 B ≈ 200 KB, so `QueueFull` only happens with
  more than 400 combatants. Garbage IDs and repeated keys never occupy it. Submissions and results
  hold at most one tick of commands and are cleared every tick. `AppliedCombatCommand` is 16 B per
  combatant. No Entity handles are kept between ticks.
- Native lifetime: per-tick scratch (in-combat IDs, hash set/maps, sort lists) is `Allocator.Temp` and
  disposed at the end of `OnUpdate`. Main-thread Temp is only reclaimed by Unity at the end of the
  frame and the processor runs once per fixed tick (catch-up is capped by `World.MaximumDeltaTime`,
  1/3 s by default: 19 ticks per frame at 60 Hz after float rounding), so scratch is sized by the
  commands of the tick, not by the pending store; the only pending-sized scratch is the key map of
  kept commands (≤ 12 800 keys, ≈ 0.4 MB at the cap, ≈ 0.5 MB of scratch per tick in total). `ResetForBattle` disposes the `EntityQuery`s it builds (Persistent until World disposal
  otherwise) and its Temp array on every path.

## Battle cleanup (contract for #5)
`CombatCommandQueue.ResetForBattle(EntityManager, uint battleStartTick)` — call when a battle ends and
when the next one begins in the same World (the World and `SimulationClock` are not recreated):
- clears submissions, pending commands and results, resets `AppliedCombatCommand` on all entities;
- stores `battleStartTick`: any later command with `Tick <= battleStartTick` is `BeforeBattleStart`,
  so the first accepted tick is `battleStartTick + 1` and relative battle tick = `Tick - BattleStartTick`;
- throws if `battleStartTick` is before the current `SimulationClock.Tick` or before the previous
  boundary, before changing anything; never touches the clock, combatants or other systems.

`CombatCommandQueue.CanResetForBattle(EntityManager, uint battleStartTick)` answers whether that reset would
succeed now, with the same single set of checks and no side effect (Burst-compatible). The lifecycle owner calls
it before destroying the previous battle's combatants, so a reset that would throw rejects the whole request
instead of failing after the destroy (#14). Rules are not repeated outside this queue.

Call it on the main thread at a tick boundary: between fixed steps, e.g. at the start of a step before
`CombatClockSystem` as the lifecycle owner does (where `Tick` is the last completed tick, the usual `BattleStartTick`) or in the command-collection phase before the processor, and after
the previous battle's combatants were despawned or disabled (same step), so a late command cannot find
them in combat. It is Burst-compatible (callable from a Burst `ISystem`). #5 owns the
battle lifecycle and its `BattleStartTick`; it passes that value here. A late producer targeting an old
combatant after the reset is rejected as `UnknownCombatant` because IDs are never reused in a World.

## Not implemented here
Derived edges (rule 7, e.g. "fresh direction press" for dash) are not produced yet: no consumer exists
before Stage 02 and the dash rule is not specified. When needed they are added to this processor
(computed from the previous `AppliedCombatCommand` before it is overwritten), not to a new system.
Action legality (stamina, locks) belongs to later rule owners.
