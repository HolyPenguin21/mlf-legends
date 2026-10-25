# Combat Core contracts (Stage 01.1, Issue #2)

Data-only contracts shared by every combat system. This folder contains no systems and no
behaviour. All combat simulation code lives in the single assembly `MLFLegends.Combat.Runtime`
(`Assets/MLFLegends/Combat/Runtime/`); future features add folders (`Simulation/`, `Commands/`,
`Bootstrap/`, …) to this assembly instead of creating new runtime asmdefs.

## Type conventions
- Per-combatant state that has exactly one value per entity: `IComponentData` (unmanaged, blittable,
  no `bool`, no managed references, Burst-compatible).
- Variable-length per-entity data (future hit ledgers, contact lists): `IBufferElementData` with an
  explicit `[InternalBufferCapacity]`. None is needed yet.
- Presence/absence flags that change during a battle: `IEnableableComponent`, never add/remove, to
  avoid structural changes for up to 400 combatants.
- Enums declare an explicit `byte`/`sbyte` underlying type.
- No `MonoBehaviour`, `Animator`, `Rigidbody`, `NavMeshAgent` or `SharedComponentData` for combat state.

## Space
- World units. X/Z is the horizontal ground plane (Z = depth), Y = altitude.
- `CombatPosition` is the authoritative position. `LocalTransform`/rendering is derived from it,
  never the other way round.
- `CombatFacing` is ±X only (`Left = -1`, `Right = +1`); 0 is invalid.

## Identity
- `CombatantIdentity { Id, Team }` is immutable after spawn.
- `Id` is unique per battle, never reused (also not after defeat), `0` is invalid.
- `Entity` handles are valid only inside one world session and may be recycled after destruction;
  anything persistent, ordered or replayed uses `CombatantIdentity.Id`.
- Deterministic iteration order, when required, is by `Id`, not by chunk order.

## Ownership (single writer per component)

| Component | Created / initialised by | Written during battle by | Readers |
|---|---|---|---|
| `CombatantIdentity` | Spawner (#5) | nobody | all |
| `CombatPosition` | Spawner (#5) | movement/collision integration (Stage 02/04) | all |
| `CombatFacing` | Spawner (#5) | action-state owner (#4, later stages) | all |
| `InCombat` (enableable) | Spawner (#5), enabled | defeat/removal owner only (disable, no re-enable) | all simulation queries |
| `CombatCommand` | Spawner (#5), `default` | the one producer of that combatant: player input or AI | command processor (#4) only |

A combatant has exactly one command producer at a time. Player and AI write the same struct; the
`Source` field is diagnostic only and must not change any rule.

## Command contract
`CombatCommand` is a single slot on the combatant entity (not a queue):

| Field | Meaning |
|---|---|
| `Tick` | Simulation tick this command is for. `0` = no command. |
| `MoveX`, `MoveZ` | Desired direction, each `-1`, `0` or `+1` (8 directions + idle). |
| `Held` | `CommandButtons` held at sampling time. |
| `Pressed` | Buttons pressed at least once since the producer's previous command (may include a tap already released). |
| `Source` | `Player` / `AI`, diagnostics only. |

Rules:
1. **Identification.** A command is identified by (combatant `Id`, `Tick`). The processor applies
   the slot only when `Tick` equals the tick currently being simulated.
2. **None.** `CombatCommand.None` (= `default`) means no movement and no buttons. A missing command,
   a stale command (`Tick` ≠ current tick) and `Tick == 0` are all treated exactly as `None`.
   Tick 0 is never simulated, so a freshly spawned combatant is idle until its producer writes.
3. **Duplicates.** Writing again for the same combatant and tick overwrites the slot (last write
   wins) before processing. The processor runs once per tick, so a command is applied at most once;
   re-reading the same slot on a later tick is stale and therefore `None`.
4. **Edges.** `Pressed` is produced by the input/AI layer so short taps between ticks are not lost.
   Derived edges the processor needs (e.g. "fresh direction press" for L + direction dash) are
   computed and stored by the processor (#4) from its own previous-tick state, not by producers.
5. **Validation.** Out-of-range `MoveX`/`MoveZ` values are rejected/clamped by the processor (#4).
   Action legality (stamina, locks, injuries) is decided only by the processor and later rule owners.

## Compatibility with dependent issues
- **#3 fixed-step simulation.** Must provide the current tick as an unsigned counter whose first
  simulated tick is `1` (tick 0 reserved). Producers run before the processor inside the same tick
  step and stamp `Tick` with the tick being simulated. The clock does not own or read any component here.
- **#4 command dispatch.** Is the only reader of `CombatCommand`; reads enabled-`InCombat`
  combatants, applies the slot when `Tick` matches, otherwise `None`. Any processor-private history
  (previous held buttons/direction) is a component owned by #4 under `Commands/`, not added here.
