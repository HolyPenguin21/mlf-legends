# MLF Legends — Combat development roadmap

## Governing rules
- Focus **only on combat** until the owner explicitly reopens campaign design.
- One authoritative ECS combat architecture from 1v1 through 200v200, without a separate duel/mass code path.
- Stage states: NOT STARTED / IN PROGRESS / BLOCKED / VERIFIED. Only VERIFIED after acceptance and regression evidence.
- Each change: inspect current code and call sites → define owner and boundaries → implement → targeted tests → bottom-up dependency audit → regression tests → commit/PR → update QA and roadmap.
- Don't silently introduce duplicate systems, unjustified architecture or redundant per-entity MonoBehaviours.
- Unity version: **6000.5.4f1**; repository: https://github.com/HolyPenguin21/mlf-legends; main branch: `main`.
- Template: **Universal 3D (URP)**, chosen for 3D terrain with sprite characters. URP 17.5.0 in current manifest.
- Performance promises need measurements on named hardware; never claim 400 combatants already work.

## Stages and verified deliverables
| Stage | Work | Acceptance | Status |
|---|---|---|---|
| 00 | GitHub/Unity/URP bootstrap, dependencies, folders, test harness, CI feasibility | Source tracked; correct Editor and URP, required packages resolved; Unity compile, smoke scene, EditMode test passed and logged | IN PROGRESS |
| 01 | ECS simulation, fixed-step scheduler, entity data, command buffer, seeded tests | Two simulated combatants reproduce same results with same inputs/seed; no render-state authority | NOT STARTED |
| 02 | Movement and camera | 2.5D move, jump, orientation, fixed angle orthographic camera, follow/zoom/pan; frame-rate-independent control | NOT STARTED |
| 03 | GPU sprite rendering | Atlas animation, direction/frame selection, stable depth sorting, optional equipment layers, instancing/indirect render assessment | NOT STARTED |
| 04 | Collisions | Custom actor/world collision, spatial index, masses, allies yield, enemies block, knockback | NOT STARTED |
| 05 | Attack resolution | Windup/active/recovery; fast/heavy/jump attacks; shapes, multi-hit rules, damage/impact/stagger | NOT STARTED |
| 06 | Defense | Facing guard, stability, timely defense/counter opportunity, guard break, dash L+direction | NOT STARTED |
| 07 | Duel AI | Range, observable windups, bounded reaction time, mistakes, defense/dash/counter via same command rules | NOT STARTED |
| 08 | Physiology | SPECIAL, body regions, injury and broken limbs, eye loss, bleeding, death, capability filtering | NOT STARTED |
| 09 | Complete 1v1 prototype | Combat feel/balance, UI, animation feedback, sound, robust regression suite | NOT STARTED |
| 10 | Group combat | 4v4 → 16v16 → 50v50, target allocation, local teamwork/formation, morale decisions | NOT STARTED |
| 11 | Navigation | Own heightfield grid, flow fields, bounded A*, avoidance, dirty-region recalculation, jump-vs-detour | NOT STARTED |
| 12 | 200v200 scale | ECS/Burst/rendering/perf benchmark on 400 actors; profile collisions, AI, render, frame pacing | NOT STARTED |
| 13 | Magic and destruction | Prepared spells, multiple resources, systemic area effects, destructible objects/terrain, nav changes | NOT STARTED |

## Early risk probes (not separate feature architectures)
- During 01 benchmark fixed-step loop and spatial query overhead at 400 basic entities.
- During 03 prove sprite instancing, sorting and equipment passes on actual target GPU before asset pipeline locks.
- During 04 stress-test dense ally collision and mass knockback.
- During 07 run AI intent updates at staggered intervals, while hit resolution remains fixed-step.
- During 11 quantify dirty nav repair under terrain modifications.

## Stage 00 checklist (live status)
- [x] GitHub repository created and accessible to connector.
- [x] Unity source uploaded to `main`; `Assets/`, `Packages/`, `ProjectSettings/` present.
- [x] Unity Editor version pinned by `ProjectSettings/ProjectVersion.txt` to 6000.5.4f1.
- [x] URP 17.5.0 referenced in manifest and URP assets selected in Graphics and PC Quality settings.
- [x] Initial documentation created in `Docs/`.
- [ ] Confirm 3D renderer asset and smoke scene are valid inside Editor.
- [ ] Install and **resolve in Unity** compatible Entities, Burst, Collections and optional Entities Graphics packages; currently Entities / Entities Graphics are missing in manifest.
- [ ] Record and commit dependency versions after Package Manager resolution.
- [ ] Define asmdefs and baseline EditMode test, set up play-mode smoke test.
- [ ] Run Editor C# compile without errors and smoke scene in Play Mode.
- [ ] Establish GitHub Actions checks with suitable Unity licensing; otherwise document blocker and local checks.
- [ ] Log all test evidence and close stage 00 only when actually verified.

## Delivery policy
Existing Unity-generated project content is preserved. Code changes belong in focused commits/PRs and must not be merged before authorization. Update status and QA together whenever completing milestones.
