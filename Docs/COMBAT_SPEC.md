## Current #69 — controls, series and per-character data (2026-10-29)

Only this section is current; every older "History" section keeps its own SHA/scope and is superseded where it disagrees (old key layouts, exact-limb intentions, velocity = displacement/dt, series limit 4, stage states).

Controls and strike rules below ("Controls", "Attacks") are the #69 state; contract [STAGE05_1_CHARACTER_SERIES.md](STAGE05_1_CHARACTER_SERIES.md).

## History — #66 — Stage 04.4–04.5 impulses, indexed Hit, 2/100/400 (2026-10-29)

Fresh main base `cf2b5bcf57c2b81b635074a0322a85ecf50879b3` (PR #65); branch `feature/66-impulses-indexed-hit`, one PR.
[Contract: impulse input/epoch, integration, mass/contacts, rollback, Hit migration, checks, manual checklist](STAGE04_IMPULSES_HIT.md);
[exact SHA and PASS/FAIL/NOT RUN results](Evidence/66/README.md).
Movement remains the only Position/Motion writer and integrator: pending `CombatImpulse` → Δv=J/Mass → physics then
control proposals → contacts (physical share = mass-weighted inelastic response for any teams, 04.3 rules for the rest)
→ velocity from achieved physics → deceleration → final index → Hit via one atomic index batch + Burst kernel.
The first real Unity compile of the 04.3 base failed (CS0246 in the replay driver); fixed first, with two test-side
EditMode fixes. Per-attack Impact/stagger/guard remain Stage 05/06. Stage 03 OWNER-ACCEPTED; Stage 04 IN PROGRESS —
manager review and owner manual acceptance pending; merge only on owner instruction.

## History — #64 — Stage 04.3 body contacts (2026-10-29)

PR #62 and #63 were merged on the owner's explicit command; integrated main base is
`adf98cd1c9ca4df2b2d2e96aa65d3a7e17eb3a5a`. Branch `feature/64-body-contacts`; the owner subsequently explicitly authorized commit, push and merge of #64.
[Current body-contact contract, execution boundary, tuning, limits, checks and benchmark](STAGE04_BODY_CONTACTS.md).
Movement remains the only Position/Motion writer: gather all proposals → same owner's working swept grid → sorted
body/world constraints → write all results → current-tick final index → unchanged Hit. Managed orchestration explicitly
schedules/completes a Burst job; actual execution is diagnosed, not assumed. Mass and controlled-role priority are separate.
Free allies yield, ordinary locomotion does not bulldoze enemies, trapped/unresolved islands stop with diagnostics.
Stage 03 OWNER-ACCEPTED; Stage 04 IN PROGRESS. Unity/C#/Burst/scene/performance NOT RUN; source/model checks are separate.
Historical notes below retain their original SHA/status and do not override this update.

## History — #61 — Stage 04.2 static arena (2026-10-29)

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

**Approval status:** approved high-level mechanics. The keyboard layout is owner-specified in #67 and implemented in #69 (the #52 layout is historical); series rules are the manager's working interpretation for review; balance values and future mechanics remain provisional.

## Fundamentals
A moderately paced Little Fighter 2 / Nekketsu-inspired 2.5D action brawler on true 3D arenas with sprite characters. Fixed camera angle independent of zoom; player directly controls **one fighter**, remaining combatants use AI. One world scales from 1v1 to 200v200. Death lasts until encounter end; no respawn in match. Map size and winning conditions are scenario-dependent: annihilation, defense, capture or survival.

## Controls — current owner layout (#69, owner requirements from #67)

Physical keys, without Shift. Movement remains WASD in world X/Z. Combat bindings live only in
`Assets/InputSystem_Actions.inputactions` → Player (`HandSeries`, `FootSeries`, `PhysicalAbility`, `Jump`); camera/UI maps unchanged.

| Key | Logical intention | Executed |
|---|---|---|
| U | HandSeries | Hand1, Hand1, Hand2, Hand1, Hand2, … |
| I | FootSeries | Foot1, Foot1, Foot2, Foot1, Foot2, … |
| O | PhysicalAbility | the character's physical ability (v2 art #70: physical_ability clip; v1 kick_spin; no self impulse) |
| Space | Jump | jump (kept until the owner explicitly cancels it) |
| K | — | reserved for the Stage 06 block, unbound |
| double-tap WASD | — | future Stage 06 dash, not implemented |
| 7 / 8 / 9 / 0 | — | future magic slots (Stage 13), unbound |

One press produces one intention, holding never repeats. Simultaneous presses use the stable technical priority
**U → I → O** for player and AI alike (manager's starting rule, #69). Jump + strike from a free grounded state starts the
jump and drops the strike. The old exact-limb keys Y/U/H/J/K/L of #52 are not extra gameplay buttons any more; their
command bits are retired (rejected as invalid values). Crouch is excluded. All intentions use the common command queue.

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

### Implemented strike set (Stage 05.1 / #69; rules = manager's working interpretation, values provisional)

Five semantic slots with per-character data (`CharacterAnimationConfig` → character → **Combat**): **Hand1, Hand2, Foot1,
Foot2, PhysicalAbility** — each with its clip, Damage, ReachMin..ReachMax (forward), DepthHalfWidth (±ΔZ) and
HeightTolerance (±ΔY), world units, validated ranges, never clamped. The slot clip's Preparation (= Windup) / Active /
Recovery frames with its duration and speed curve define the windows (quantized to 60 Hz, Active ≥ 1 tick; see
`Presentation/Sprites/ANIMATION.md`). Body per character: vertical cylinder Radius/Height/Mass from CombatPosition. A strike
copies its timing AND damage/box at its start (frozen per execution); a body applies at spawn. Details and Inspector path:
[STAGE05_1_CHARACTER_SERIES.md](STAGE05_1_CHARACTER_SERIES.md).

| Slot | Default clip | Default windows (headless) | Chain delay | Damage | Reach (forward) |
|---|---|---|---|---|---|
| Hand1 | punch_left | 13/5/11 | 4 | 4 | 0 … 1.0 |
| Hand2 | punch_right | 9/9/11 | 4 | 4 | 0 … 1.0 |
| Foot1 / Foot2 | kick_left / kick_right | 11/5/17 | 6 | 7 | 0 … 1.3 |
| PhysicalAbility (finisher) | physical_ability (v1: kick_spin) | 19/6/20 | — | 12 | 0 … 1.45 |

All default boxes: |ΔZ| ≤ 0.4, |ΔY| ≤ 0.9; target point = CombatPosition. Current project windows (v2 art, #70): punch
14/6/9, kicks 15/5/10, physical_ability 15/4/11 ticks ([migration table](STAGE05_1A_ATLAS_MIGRATION.md#4-config-migration-old--new-characteranimationconfigasset)). Every combatant spawns with 100 HP. **Planar movement during a strike: 50% Preparation /
25% Active / 25% Recovery**, never a jump; facing locked except on each strike's first tick.

- **Start:** a fresh press (Held never repeats) starts a strike when free and grounded at the start of the tick.
  Airborne presses are dropped (no air strikes yet). Jump + strike → jump.
- **Series:** U and I each run **1, 1, 2, 1, 2, …** (first two started strikes use clip 1), counted by started strikes in a
  bounded step — no length limit. A Windup press is dropped; from Active to the end of Recovery the latest series press
  is buffered in one slot and starts at the chain point (Windup + Active + chain delay). Switching hand ↔ foot starts the
  other sequence at 1; Recovery ending without continuation resets the series; no hidden timeout. Dropped, unexecuted or
  replaced presses never advance a sequence.
- **Physical ability (O):** starts only from the free legal state, plays its full Recovery, is never buffered, never
  interrupts a strike, never ends a foot series automatically.
- **Targets:** every in-combat enemy inside the box, several per strike, each at most once per strike (ledger); allies and
  self never. Resolved on the shared final spatial index after Movement (#66).
- **Not implemented:** guard damage, impact/stagger/knockback per strike, self impulse, swept/path hits, dynamic Height,
  defeat at 0 HP, combo bonuses, cancels, cooldowns, heavy/light, air strikes, VFX.

Each impact separately resolves:
- **HP Damage**: injury/health reduction.
- **Guard Damage**: drain of defender's current stability.
- **Impact**: stagger, launch, knockback or other physical response.

Attack animation follows state and simulation windows, not Animator events.

## Guard
Future Stage 06, not implemented; K is reserved for it (unbound in 05.1). Blocking protects only from front. Regular guard drains stability according to hit and defender's guard strength; stability recovers by defined rules. Heavy strikes may push defending fighter. Stability reaching zero triggers guard break and a vulnerable interval. **Timely guard** reduces loss and opens a temporary advantage for player/AI to use; **does not perform counter automatically**. Attacks from rear bypass normal front guard.

## Dash and jump
Dash is a future Stage 06 double-tap of a WASD direction; not implemented in 05.1. Future directional defensive dash creates rapid repositioning about **1–1.5 fighter widths** as a starting tuning estimate. A brief defined segment may avoid ordinary attack hit registration, but dash is *not universally invulnerable* to magical/environmental hazards. Has punishable recovery, command gating and physical collision throughout, with no phasing through crowds or walls. Jump changes altitude and attack eligibility, vulnerable air state and landing.

## Body collisions
All characters interact physically. Ally yield creates slight movement priority, and user-controlled fighter passes through allied clusters almost effortlessly **without** disabling collision. Enemies physically obstruct movement. Knockback into other fighters/environment can have chain effects.
Since #66 (provisional calibration): an external impulse J changes velocity by J/Mass; ordinary walking never shoves an
enemy, but physical motion transfers to any body by mass (inelastic, momentum-conserving, chains through groups); walls
stop the normal part and keep sliding; ground/air deceleration 24/2 u/s²; max 24 u/s. Strike speed factors and landing
lock do not reduce it; it never turns the fighter, cancels a strike or counts as running. No attack applies an impulse yet.
Since #69 the velocity after a contact is the physical final velocity, not the tick's average displacement: the normal part
into a blocking surface is removed at the time of impact (tangent kept), and bodies that came into contact share their
approaching normal velocity as a perfectly inelastic, mass-weighted collision. Nothing hidden is released later.

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
