## Current #52 intention contract

CommandButtons uses ushort, preserving Attack=1, Jump=2, Guard=4, Kick=8. Exact PunchLeft/PunchRight/KickLeft/KickRight/
KickSpin = 16/32/64/128/256. All fields (Held/Pressed/Buffered, pending commands and replay) retain the complete flags;
undefined bits >=512 are rejected. Guard has no device binding; it remains reserved numerically for compatibility.
Player and AI use exactly the same intents. Priority is hand L/R, foot L/R/spin, general hand, general foot. The Action
owner resolves it; an exact request in the existing buffer stays exact until executed. Free grounded Jump wins.
[Current input layout](../../../../../Docs/STAGE03_CONTROLS.md); no additional simulation owner or command stream.

# Combat Core contracts (Stage 01.1, Issue #2)

Data-only contracts shared by every combat system. This folder contains no systems and no
behaviour. All combat simulation code lives in the single assembly `MLFLegends.Combat.Runtime`
(`Assets/MLFLegends/Combat/Runtime/`); future features add folders (`Simulation/`, `Commands/`,
`Bootstrap/`, …) to this assembly instead of creating new runtime asmdefs.

## Type conventions
- Per-combatant state with exactly one value per entity: `IComponentData` (unmanaged, blittable,
  no `bool` fields, no managed references, Burst-compatible).
- Variable-length per-entity data or per-tick queues (future hit ledgers, contact lists, the command
  queue of #4): `IBufferElementData` or Native containers owned by one system, with an explicit
  capacity. Core defines none of them.
- Values exchanged between systems but not stored on combatants (e.g. `CombatCommand`): plain
  unmanaged structs; the owning system wraps them in its own ECS storage.
- Presence flags that change during a battle: `IEnableableComponent`, not add/remove, to avoid
  structural changes for up to 400 combatants. Default `EntityQuery` matching skips disabled ones.
- Enums declare an explicit unmanaged underlying type: normally byte/sbyte; CommandButtons uses ushort (#52).
- No `MonoBehaviour`, `Animator`, `Rigidbody`, `NavMeshAgent` or managed components for combat state.

## Space
- World units. X/Z is the horizontal ground plane (Z = depth), Y = altitude.
- `CombatPosition` is the authoritative position. `LocalTransform`/rendering is derived from it,
  never the other way round.
- `CombatFacing` is ±X only (`Left = -1`, `Right = +1`); 0 is invalid and must not survive spawn.

## Identity and lifetime
- Immutable identity = `CombatantId` + `CombatTeam`, separate from mutable state
  (`CombatPosition`, `CombatFacing`, `InCombat`).
- `CombatantId.Value` is unique per ECS `World`, never reused across battles (also not after defeat or
  despawn); `0` is invalid. The battle lifecycle spawner (#5) is the only allocator. A fresh `World`
  may restart the ID sequence, while an ordinary battle restart in the same World must not.
- `Entity` handles are valid only inside one `World` and are recycled after destruction; anything
  persistent, ordered, addressed from outside or replayed uses `CombatantId`.
- Deterministic iteration order, when required, is ascending `CombatantId`, not chunk order.
- Defeat disables `InCombat` (entity stays, no respawn). Despawn destroys the entity; both are
  "not in combat" for every consumer.

## Ownership (single writer per component)

| Data | Created / initialised by | Written during battle by | Readers |
|---|---|---|---|
| `CombatantId` | Spawner (#5) | nobody | all |
| `CombatTeam` | Spawner (#5) | nobody | all |
| `CombatPosition` | Spawner (#5) | `CombatMovementSystem` (#31); Stage 04 extends this owner | all |
| `CombatMotion` | same private Spawn (#5/#31) | `CombatMovementSystem` (#31), ground support / vertical velocity / grounded byte | movement, replay / diagnostics |
| `CombatFacing` | Spawner (#5) | `CombatFacingSystem` (#33), sign of accepted `MoveX`, before movement; not #4 | all |
| `InCombat` (enableable) | Spawner (#5), enabled | single defeat owner (Stage 08), disable only | all simulation queries |
| `CombatCommand` values | producers: `PlayerInputCommandSystem` (#22, human) or AI | — (values, not state) | command processor (#4) only |

Producers never write combat state directly; their only output is `CombatCommand`. The record of
which command was accepted, and any processor history, is owned by #4 under `Commands/`.

## Command contract
`CombatCommand` = intention of one combatant for one tick:

| Field | Meaning |
|---|---|
| `Combatant` | Target `CombatantId`. |
| `Tick` | Simulation tick this command is for. |
| `MoveX`, `MoveZ` | Desired direction, each `-1`, `0` or `+1` (8 directions + idle). |
| `Held` | `CommandButtons` held at sampling time. |
| `Pressed` | Buttons pressed at least once since the producer's previous command for this combatant (may include a tap already released). |
| `Source` | `Player` / `AI`. Diagnostics only. |

Rules (the processor #4 implements them; their outcome must be observable for tests):
1. **Key.** A command is identified by (`Combatant`, `Tick`). At most one command is accepted per
   key, and it is applied only on that tick, never earlier or later.
2. **None.** A command with `IsNone` (no movement, no buttons) is a valid accepted command meaning
   "no new action". A missing command for an in-combat combatant on a tick is processed exactly like
   an accepted None command.
3. **Duplicate / conflict.** Commands are collected in a fixed producer order inside the tick's
   command-collection phase. The first command for a key is accepted; every later command with the
   same key is rejected — never merged and never overwriting the accepted one. A rejected command
   that differs from the accepted one (including a different `Source`) is reported as a conflict.
4. **Stale / future.** `Tick` lower than the tick being simulated is stale and rejected. A higher
   `Tick` is never applied before its tick; whether it is kept until due (bounded) or rejected is
   #4's policy. `Tick == 0` is always rejected.
5. **Unknown / removed combatant.** A command whose `Combatant` is invalid, has no entity, or whose
   `InCombat` is disabled is rejected.
6. **Validation.** `MoveX`/`MoveZ` outside `-1..1` are rejected by the processor. Action legality
   (stamina, locks, injuries) belongs to later rule owners, identical for player and AI.
7. **Edges.** Producers emit `Pressed` so taps between ticks are not lost; when several ticks run in
   one frame, a press is reported in the first command only (implemented for the player by
   `Input/INPUT.md`, #22: presses are consumed by the first produced tick; pause, focus/device loss and a
   new battle/human binding drop them). Derived edges such as the "fresh
   direction press" for a future separately bound dash are computed by the processor from its own previous-tick
   state, not by producers.

## Compatibility with dependent issues
- **#3 fixed-step simulation.** Owns the single tick counter. Requirement from this contract: the
  counter is unsigned and the first simulated tick is `1` (0 reserved for "no tick"). The group order
  "command collection → state update → publication" matches rules 1–4: producers write commands for
  the tick being simulated before #4 consumes them in the same step. The clock reads/writes no Core
  component.
- **#4 command processor.** Single consumer of `CombatCommand`, keyed by `CombatantId` + tick.
  Chooses the queue storage (e.g. a singleton `DynamicBuffer` / Native container wrapping
  `CombatCommand`), resolves `CombatantId` → entity without O(N²) scans, implements rules 1–7 and
  owns the accepted-command record. Does not write position, facing or `InCombat`.
- **#5 bootstrap.** Allocates `CombatantId`, sets `CombatTeam`, `CombatPosition`, a valid
  `CombatFacing` and enabled `InCombat`; human and AI combatants carry identical components.

## Stage 05.1 additions (PR #48)

- `CommandButtons.Kick = 1 << 3` (foot strike); `Attack` is the hand strike. The processor accepts it like the others.
- `CombatAttackState` (`CombatAttack.cs`): current strike kind, start tick, series length, one buffered follow-up press,
  last series end tick. Spawned free; written only by `CombatActionSystem`.
- `CombatHealth` (`Current`/`Max`, spawned 100/100) and the `CombatHitRecord` buffer (per-strike ledger): written only by
  `CombatHitSystem` (the ledger is cleared by `CombatActionSystem` when a strike starts). 0 HP does not defeat yet.
- `CombatMotion` gains `LandTick`, `LastMoveTick`, `MoveStreakStartTick`, `MoveStreakDistance` (movement owner only).
