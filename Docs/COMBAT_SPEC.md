## Current #64 — Stage 04.3 body contacts (2026-10-29)

PR #62 and #63 were merged on the owner's explicit command; integrated main base is
`adf98cd1c9ca4df2b2d2e96aa65d3a7e17eb3a5a`. Branch `feature/64-body-contacts`; the owner subsequently explicitly authorized commit, push and merge of #64.
[Current body-contact contract, execution boundary, tuning, limits, checks and benchmark](STAGE04_BODY_CONTACTS.md).
Movement remains the only Position/Motion writer: gather all proposals → same owner's working swept grid → sorted
body/world constraints → write all results → current-tick final index → unchanged Hit. Managed orchestration explicitly
schedules/completes a Burst job; actual execution is diagnosed, not assumed. Mass and controlled-role priority are separate.
Free allies yield, ordinary locomotion does not bulldoze enemies, trapped/unresolved islands stop with diagnostics.
Stage 03 OWNER-ACCEPTED; Stage 04 IN PROGRESS. Unity/C#/Burst/scene/performance NOT RUN; source/model checks are separate.
Historical notes below retain their original SHA/status and do not override this update.

## Current #61 — Stage 04.2 static arena (2026-10-29)

Dependent branch `feature/61-static-arena` starts from corrected #60 PR #62 head
`e26e668435b9cb55bcf5bced163710b6c2b61230`; main at dispatch is `180b4121792e68931f27c109b78cd0d24c21a4d4`.
The owner authorized proceeding to physics while #62 remains open; neither PR is merged by the implementing agent.
[Owners, supported geometry/limits, scene setup, leases, fallback, tests and evidence](STAGE04_ARENA.md).
Stage 03 OWNER-ACCEPTED; Stage 04 IN PROGRESS. Only 04.2: no actor contacts/yield, impulses or Hit migration.
Geometry publication → lifecycle/clock/commands → Action → Facing → Movement contacts → final index → Hit.
Only Movement publishes runtime Position/Motion; lifecycle initializes actual spawn support. Core collision body stays
independent of images/pivots; current animation settings and combat rules are retained. Run stride now records actual
planar distance; the old diagonal one-axis distance expectation is corrected without changing physical speed.
**Unity/C# compilation/suites/Burst/GPU/runtime performance NOT RUN**; source/model results are separate and do not
certify runtime acceptance. Historical dated evidence below applies only to its recorded SHA/scope.

# MLF Legends — Combat specification

**Approval status:** approved high-level mechanics. The keyboard layout is owner-specified in #52; unapproved balance values and other future mechanics remain provisional.

## Fundamentals
A moderately paced Little Fighter 2 / Nekketsu-inspired 2.5D action brawler on true 3D arenas with sprite characters. Fixed camera angle independent of zoom; player directly controls **one fighter**, remaining combatants use AI. One world scales from 1v1 to 200v200. Death lasts until encounter end; no respawn in match. Map size and winning conditions are scenario-dependent: annihilation, defense, capture or survival.

## Controls — current owner layout (#52)

Physical keys, without Shift. Movement remains WASD in world X/Z. Combat action bindings live only in
`Assets/InputSystem_Actions.inputactions` → Player; camera/UI maps and existing movement bindings are unchanged.

| Key | Logical intention | Executed strike |
|---|---|---|
| Y | PunchLeft | punch_left, always |
| U | PunchRight | punch_right, always |
| I | Attack | existing alternating hand series |
| H | KickLeft | kick_left, always |
| J | KickRight | kick_right, always |
| K | KickSpin | kick_spin, including from free grounded stance |
| L | Kick | existing kick_left → kick_right → kick_spin series |
| Space | Jump | jump |

One press produces one intention, holding never repeats. Simultaneous strikes use the same deterministic rule for
player and AI: **PunchLeft → PunchRight → KickLeft → KickRight → KickSpin → Attack → Kick**. Jump + strike from a free
grounded state starts the jump and drops the strike. All intentions use the common command queue and fixed simulation.
The single existing follow-up slot stores the exact/general intention until execution; exact sides never alternate.

Old combat aliases (Enter, mouse, gamepad/other-device buttons) are removed. Guard is unbound and unimplemented; its
future key is decided before Stage 06. Crouch is excluded from the project. Dash and Cast are deferred and unbound;
unused Player template actions are removed. These decisions do not change Camera/UI controls.

Input is sampled once per real fixed tick: live Move/Held every catch-up tick, Pressed consumed once. Pause,
focus/device interruptions and battle/World rebinding retain their existing press cleanup. During landing recovery,
all strike/jump presses are dropped rather than executed after the lock; device polling and other simulation continue.

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

> **Stage 03 tuning update (owner instruction).** Strike windows are no longer fixed numbers: each strike clip's
> Preparation (= Windup) / Active / Recovery frame ranges in `CharacterAnimationConfig`, with its duration and speed curve,
> define them (quantized to 60 Hz ticks, Active never shorter than one tick; see `Presentation/Sprites/ANIMATION.md`). The
> current project values after 68c43c are punch_left/right 14/6/9, kicks 12/6/12, kick_spin 20/5/5 ticks; the table
> below is the headless default only. **During attacks planar movement is 50% Preparation / 25% Active / 25% Recovery** (the strike keeps
> playing, no jump); facing keeps the strike lock described below. A strike keeps the windows it started with. The chain point is
> Recovery start + the chain delay below, capped at the strike's end.

The spec leaves the heavy/combination mapping, series, air strikes and movement during attacks open. Stage 05.1
implements the smallest coherent set below; every item marked *proposed* is a decision for the owner to confirm or change
(reach/damage/chain rules/default windows live in CombatAttackTuning; live phase windows come from CharacterAnimationConfig).

- **Controls:** the owner-approved table above; the same exact/general flags are available to AI.
- **Strikes:** `punch_left`, `punch_right`, `kick_left`, `kick_right`, `kick_spin`. Each has Windup → Active → Recovery in
  fixed ticks (60 Hz). Hits resolve only on Active ticks, in the fixed simulation tick, never from animation or markers.

| Strike | Windup (default) | Active (default) | Recovery (default) | Chain after recovery tick | Damage | Reach (forward) |
|---|---|---|---|---|---|---|
| punch_left | 13 | 5 | 11 | 4 | 4 | 0 … 1.0 |
| punch_right | 9 | 9 | 11 | 4 | 4 | 0 … 1.0 |
| kick_left / kick_right | 11 | 5 | 17 | 6 | 7 | 0 … 1.3 |
| kick_spin (finisher) | 19 | 6 | 20 | — | 12 | 0 … 1.45 |

  All boxes: |ΔZ| ≤ 0.4, |ΔY| ≤ 0.9 (world units; 2 units = nominal body height). Windows follow the delivered art's frame
  proportions; reach follows the drawn fist/foot. Every combatant spawns with 100 HP.
- **Start (proposed):** a fresh press (Held never repeats) starts a series when the fighter is free and grounded at the
  start of the tick. Simultaneous strikes use the priority above; Jump + strike from free grounded stance starts Jump and drops the strike.
  **Air strikes are not implemented**: an airborne press is dropped (no jump-attack clip exists yet), contrary to the
  approved "jump attacks enabled" goal — deferred to a later stage with its own clip.
- **Series (proposed):** general requests start left and alternate sides; general Kick after `kick_right` is the
  `kick_spin` finisher. A press during Windup is dropped; from the first Active tick to the end of Recovery the latest
  press is buffered in one slot. The follow-up starts on the first tick at/after the strike's chain point (Windup + Active +
  chain delay), cutting the rest of Recovery; without a buffered press Recovery runs out and the fighter is free on the next
  tick. `kick_spin` and the 4th strike of a series cannot chain: they always play the full Recovery and drop the buffer.
  Examples: I I I I = hand L R L R; L L L = kick L, kick R, spin. Exact Y/U/H/J/K keep the selected strike even when buffered; spin remains a full-Recovery finisher.
- **During a strike:** planar speed = 50% Preparation (Windup), 25% Active, 25% Recovery; never a jump; facing is locked except on the first tick of each strike (series start or follow-up), where
  that tick's MoveX may still turn the fighter, before the hit box is tested. Moving never cancels the strike.
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
Future Stage 06, not implemented or bound; key undecided (L is general Kick). Blocking protects only from front. Regular guard drains stability according to hit and defender's guard strength; stability recovers by defined rules. Heavy strikes may push defending fighter. Stability reaching zero triggers guard break and a vulnerable interval. **Timely guard** reduces loss and opens a temporary advantage for player/AI to use; **does not perform counter automatically**. Attacks from rear bypass normal front guard.

## Dash and jump
Dash is deferred/unbound; no L+direction action exists. Future directional defensive dash creates rapid repositioning about **1–1.5 fighter widths** as a starting tuning estimate. A brief defined segment may avoid ordinary attack hit registration, but dash is *not universally invulnerable* to magical/environmental hazards. Has punishable recovery, command gating and physical collision throughout, with no phasing through crowds or walls. Jump changes altitude and attack eligibility, vulnerable air state and landing.

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
- Future defense acceptance: a separately chosen guard/dash binding must not repeat from holding or clip through actors/obstacles.
- Damage/stagger applies exactly once per intended hit; multi-target hits all valid separate targets.
- Broken arm/leg/eye changes executable actions, perception or movement, and death stops further action.
