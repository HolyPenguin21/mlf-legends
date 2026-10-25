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

## Attacks
A canonical attack has Windup → Active → Recovery, attack shapes, interrupt/cancel windows and optional cooldown. Light attack is faster/weaker, heavy attack slower/stronger with greater guard pressure; jump attacks enabled. Attack queries consider horizontal facing, target depth Z and height Y; individual attacks can strike several enemies. Each intended impact is recorded only once per active event per target unless a move is explicitly multi-hit.

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
