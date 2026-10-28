# MLF Legends — Combat specification

**Approval status:** approved high-level mechanics. All unapproved numbers, exact hotkeys and timing parameters remain provisional.

## Fundamentals
A moderately paced Little Fighter 2 / Nekketsu-inspired 2.5D action brawler on true 3D arenas with sprite characters. Fixed camera angle independent of zoom; player directly controls **one fighter**, remaining combatants use AI. One world scales from 1v1 to 200v200. Death lasts until encounter end; no respawn in match. Map size and winning conditions are scenario-dependent: annihilation, defense, capture or survival.

## Controls (proposed bindings)
- WASD: movement X/Z; character attacks toward left/right with limited depth hitbox allowance.
- J: primary attack; heavy/combination mapping remains open.
- K: jump.
- L: hold front-facing block.
- L + **fresh press of direction**: one short directional defensive dash; forward/back, depth, diagonals allowed. No repeated dashing merely from holding a direction.
Controls should be easily remappable. All input enters the same action command validation pipeline as AI.

**Implemented input policy (Stage 02.3, #22; bindings provisional):** WASD = world X/Z (W = +Z depth, not
camera-relative), J = Attack, K = Jump, L = Guard (also Space = Jump, Enter/left mouse = Attack; gamepad left stick,
West, South, right shoulder). Stage 05.1 adds U / gamepad North = Kick and executes Attack/Kick as the strikes below;
Guard is still an intention only; guard and L + direction dash are not implemented. Sampling: one player command per real fixed simulation tick, never per render frame; Move and
Held are the live device state on every tick (also every catch-up tick of one frame); a press reaches exactly one
tick — the first produced after it — so a tap shorter than a tick is never lost and holding a button never repeats it.
Several presses of one button before a tick are one press. Analog sticks are quantized to the digital
{-1, 0, +1} axes (dead zone 0.25, eight 45° sectors). **Pause** (`Time.timeScale = 0`) collects no combat presses;
resuming uses the buttons actually held, with no synthetic press. **Focus loss** stops the fighter on the next tick and
drops pending presses; so do device loss, End/Restart and a change of controlled fighter. Details: `Input/INPUT.md`.

**Camera (Stage 02.4, #22; values provisional):** orthographic, one fixed configurable angle (40° pitch, 0° yaw by
default) at every zoom level; follows the controlled fighter's authoritative position (ground plane; jump height is not
followed by default), pan relative to follow (arrow keys / right stick, clamped, Home recenters), zoom by orthographic
size within limits (`=`/`-`, mouse wheel, d-pad). Pan and zoom work while paused and never change the simulation; a
new battle drops the pan. The camera never decides which fighters are simulated. Details: `Presentation/CAMERA.md`.

## Facing and directional movement (approved, 2026-10-26)

The current 2.5D combat facing has two directions: **Left (world X negative)** and **Right (world X positive)**. Fighters automatically turn toward horizontal movement: on a fixed simulation tick with accepted `MoveX = +1`, they face Right; with accepted `MoveX = -1`, they face Left. This also applies during diagonals and jumps. **Pure depth movement (`MoveX = 0, MoveZ ≠ 0`) and standing still do not turn the character**; the previous horizontal facing remains. There is no automatic facing toward the closest enemy or backward-striding mode. Player and AI share the same rule, derived only from `AppliedCombatCommand`.

Stage 05.1 defines the first action-state restriction explicitly: a running strike locks the turn except on its first tick (see Attacks); Guard still does not lock it. The sprite renderer (#38) mirrors the art by ECS `CombatFacing` but never decides it. Implementation tracked in [Stage 02.2 Issue #33](https://github.com/HolyPenguin21/mlf-legends/issues/33).

## Attacks
A canonical attack has Windup → Active → Recovery, attack shapes, interrupt/cancel windows and optional cooldown. Light attack is faster/weaker, heavy attack slower/stronger with greater guard pressure; jump attacks enabled. Attack queries consider horizontal facing, target depth Z and height Y; individual attacks can strike several enemies. Each intended impact is recorded only once per active event per target unless a move is explicitly multi-hit.

### Implemented minimal strike set (Stage 05.1, PR #48; rules PROPOSED, numbers provisional — owner approval pending)

The spec leaves the heavy/combination mapping, series, air strikes and movement during attacks open. Stage 05.1
implements the smallest coherent set below; every item marked *proposed* is a decision for the owner to confirm or change
(all numbers live in `Simulation/CombatAttackTuning.cs`, one authoritative source).

- **Controls (proposed):** J / Enter / left mouse / gamepad West = `Attack` (hand strike, existing button); new logical
  button `Kick` = **U** / gamepad North (foot strike). Both go through the one command queue; AI presses the same bits.
- **Strikes:** `punch_left`, `punch_right`, `kick_left`, `kick_right`, `kick_spin`. Each has Windup → Active → Recovery in
  fixed ticks (60 Hz). Hits resolve only on Active ticks, in the fixed simulation tick, never from animation or markers.

| Strike | Windup | Active | Recovery | Chain after recovery tick | Damage | Reach (forward) |
|---|---|---|---|---|---|---|
| punch_left | 13 | 5 | 11 | 4 | 4 | 0 … 1.0 |
| punch_right | 9 | 9 | 11 | 4 | 4 | 0 … 1.0 |
| kick_left / kick_right | 11 | 5 | 17 | 6 | 7 | 0 … 1.3 |
| kick_spin (finisher) | 19 | 6 | 20 | — | 12 | 0 … 1.45 |

  All boxes: |ΔZ| ≤ 0.4, |ΔY| ≤ 0.9 (world units; 2 units = nominal body height). Windows follow the delivered art's frame
  proportions; reach follows the drawn fist/foot. Every combatant spawns with 100 HP.
- **Start (proposed):** a fresh press (Held never repeats) starts a series when the fighter is free and grounded at the
  start of the tick. Attack wins over Kick when both are pressed on one tick; Attack + Jump on one grounded tick strikes.
  **Air strikes are not implemented**: an airborne press is dropped (no jump-attack clip exists yet), contrary to the
  approved "jump attacks enabled" goal — deferred to a later stage with its own clip.
- **Series (proposed):** the first strike is left; each follow-up uses the other side; Kick after `kick_right` is the
  `kick_spin` finisher. A press during Windup is dropped; from the first Active tick to the end of Recovery the latest
  press is buffered in one slot. The follow-up starts on the first tick at/after the strike's chain point (Windup + Active +
  chain delay), cutting the rest of Recovery; without a buffered press Recovery runs out and the fighter is free on the next
  tick. `kick_spin` and the 4th strike of a series cannot chain: they always play the full Recovery and drop the buffer.
  Examples: J J J J = L R L R; U U U = kick L, kick R, spin; J U U = punch L, kick R, spin; U J J = kick L, punch R, punch L.
- **During a strike (proposed):** no planar movement and no jump; facing is locked except on the first tick of each strike
  (series start or follow-up), where that tick's MoveX may still turn the fighter, before the hit box is tested.
- **Targets:** every in-combat combatant of another team inside the box (forward along the attacker's `CombatFacing`).
  Allies and self are never hit. Several targets per strike are allowed (spec: "individual attacks can strike several
  enemies"). Each target is hit at most once per strike (per-strike ledger), however many Active ticks overlap it; the
  next strike starts with an empty ledger.
- **Not implemented (no current requirement or a later stage):** guard damage, impact/stagger/knockback, defeat at 0 HP
  (HP clamps at 0, the fighter keeps fighting), combo bonuses, cancels, cooldowns, heavy/light distinction, air strikes,
  hits on despawned/disabled fighters. Body collisions (Stage 04) are absent, so fighters may overlap; reach starts at 0.

Each impact separately resolves:
- **HP Damage**: injury/health reduction.
- **Guard Damage**: drain of defender's current stability.
- **Impact**: stagger, launch, knockback or other physical response.

Attack animation follows state and simulation windows, not Animator events.

## Guard
Blocking protects only from front. Regular guard drains stability according to hit and defender's guard strength; stability recovers by defined rules. Heavy strikes may push defending fighter. Stability reaching zero triggers guard break and a vulnerable interval. **Timely guard** reduces loss and opens a temporary advantage for player/AI to use; **does not perform counter automatically**. Attacks from rear bypass normal front guard.

## Dash and jump
Directional defensive dash creates rapid repositioning about **1–1.5 fighter widths** as a starting tuning estimate. A brief defined segment may avoid ordinary attack hit registration, but dash is *not universally invulnerable* to magical/environmental hazards. Has punishable recovery, command gating and physical collision throughout, with no phasing through crowds or walls. Jump changes altitude and attack eligibility, vulnerable air state and landing.

## Body collisions
All characters interact physically. Ally yield creates slight movement priority, and user-controlled fighter passes through allied clusters almost effortlessly **without** disabling collision. Enemies physically obstruct movement. Knockback into other fighters/environment can have chain effects.

## Friendly effects
Ordinary direct weapon attacks don't damage allies. Consequences of skills and environmental reactions may affect either team. Avoid a general friendly-fire subsystem beyond this until clearly required.

## SPECIAL and wounds
Strength, Perception, Endurance, Charisma, Intelligence, Agility and Luck form initial actor attributes. Derived stats depend on abilities, equipment, injuries and current states. Character body tracks head/eyes, both arms, both legs, torso/internal status, with broken limbs and eye loss imposing **functional** limits, not just percentage stat penalties. Bleeding and lethal injury processed authoritatively; death removes action eligibility. Future campaign aging/disease/genetics are deferred.

## AI fairness
AI observes available visual/physical attack windups and field state, cannot read hidden commands or future RNG. Reactions, mistakes, learning/skill and decision frequency are configurable. It uses same guarding, movement, attack, dash and injury rules.

## Spells (future)
Spells are assembled *before* battle. Each prepared spell spends resource types depending on spell structure, not just mana. Spells make systemic impacts on terrain, elements, groups and summons; effects may indirectly hurt friends. Stage 13 only, do not generalize early duel code around hypothetical full magic complexity.

## Tunables to calibrate
Fixed simulation tick, combat responsiveness, stability capacity/recovery, perfect/timely guard window, attack frame timing, hit shapes, dash immunity timing, dash distance, heavy-attack mapping, AI reaction distribution and equipment modifiers. Set in data and test with measurable duel scenarios, not as hardcoded constants.

## Initial 1v1 acceptance scenarios
- Movement/attack/guard/dash never depend on display frame rate.
- Player and AI use identical action legality and battle resolution.
- Front block works, rear hit bypasses; repeated guard pressure eventually breaks defense.
- Timely guard grants advantage without auto-counter.
- Held L + direction creates one dash, not endless dash; no clipping through actors/obstacles.
- Damage/stagger applies exactly once per intended hit; multi-target hits all valid separate targets.
- Broken arm/leg/eye changes executable actions, perception or movement, and death stops further action.
