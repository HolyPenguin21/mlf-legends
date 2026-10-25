# MLF Legends — Combat development roadmap

## Governing rules
- Focus **only on combat** until the owner explicitly reopens campaign design.
- One authoritative ECS combat architecture from 1v1 through 200v200, without a separate duel/mass code path.
- Stage states: NOT STARTED / READY / IN PROGRESS / BLOCKED / ACCEPTED (owner report) / VERIFIED (evidence independently checked). **Delivery and verification are independent**: lack of Unity Editor access is not a gate for committing, merging, pushing or continuing implementation when authorized. Owner-reported tests may justify ACCEPTED, not independently VERIFIED.
- Each change: inspect current code and call sites → define owner and boundaries → implement → all **available** static/automated tests → bottom-up dependency audit → commit/PR/merge when authorized → update QA and roadmap. Unavailable Unity tests are recorded as NOT RUN, not invented as passing.
- Don't silently introduce duplicate systems, unjustified architecture or redundant per-entity MonoBehaviours.
- Unity version: **6000.5.4f1**; repository: https://github.com/HolyPenguin21/mlf-legends; main branch: `main`.
- Template: **Universal 3D (URP)**, chosen for 3D terrain with sprite characters. URP 17.5.0 in current manifest.
- Performance promises need measurements on named hardware; never claim 400 combatants already work.

## Stages and verified deliverables
| Stage | Work | Acceptance | Status |
|---|---|---|---|
| 00 | GitHub/Unity/URP bootstrap, dependencies, folders, test harness, CI feasibility | Source tracked; editor/packages committed; tests owner-reported PASS; CI and independent evidence deferred | ACCEPTED (OWNER) |
| 01 | ECS simulation, fixed-step scheduler, entity data, command buffer, seeded tests | Two simulated combatants reproduce same results with same inputs/seed; no render-state authority | IN PROGRESS — #2 merged; #3 ready |
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
- [x] Entities **6.5.0** and Entities Graphics **6.5.0** are declared and locked; Burst **1.8.29** and Collections **6.5.0** also appear in lockfile (configuration verified).
- [ ] Confirm the Editor imports and resolves all core/registry packages without errors (execution pending).
- [ ] Record and commit dependency versions after Package Manager resolution.
- [x] Add separate EditMode/PlayMode test assemblies, ECS/URP unit smoke tests and existing SampleScene load test on Stage 00 feature branch.
- [x] Add a Windows PowerShell test runner and ignore generated results.
- [x] Owner confirmed that all Stage 00 tests passed on 2026-10-25 (OWNER-REPORTED PASS; logs unavailable for independent review).
- [x] Owner-reported test pass implies Stage 00 test assemblies compiled and SampleScene smoke test succeeded; independent Console audit remains pending.
- [ ] Evaluate GitHub Actions Unity test runner and licensing after local tests pass; CI currently deferred, not configured.
- [x] Stage 00 **ACCEPTED by owner** based on reported PASS for all tests, 2026-10-25. Retain missing XML/CI as follow-up; don't imply independently VERIFIED. Stage 01 can proceed.

## Stage 01 progress and integration order (2026-10-25)
- [x] **01.1 / Issue #2 — Core contracts**: merged as [PR #8](https://github.com/HolyPenguin21/mlf-legends/pull/8), commit `74de551e7bc4e1f213e7af1c688c1a2db78212cb`. Single runtime asmdef, `CombatantId`, `CombatTeam`, `CombatPosition`, `CombatFacing`, `InCombat`, `CombatCommand` and `Core/CONTRACTS.md`. Static architecture review passed; Unity runtime tests not independently rerun. Status: **MERGED / RUNTIME UNVERIFIED**.
- [x] **01.2 / Issue #3 — Single fixed-step clock**: PR #9 merged as `1c98d1f982bea8e7776101382b2585fa16a89fd3`. One World-scoped 60Hz fixed-step clock, separate phase order, tick 1 first, no double writer. Agent reported Unity EditMode **15/15** and PlayMode **2/2 PASS**; manager inspected code and tests but did not run Unity independently. Status: MERGED / AGENT-REPORTED PASS.
- [x] **01.3 / Issue #4 — Command processor**: merged [PR #11](https://github.com/HolyPenguin21/mlf-legends/pull/11) as `cca88b38e3167f4b0e1329a5128b640a49227fc3`. One queue, one Burst ECS consumer, `AppliedCombatCommand`, bounded future commands and `ResetForBattle` contract. Agent-reported **48/48 EditMode, 3/3 PlayMode PASS**. Manager static review completed; Temp-native disposal improvement logged as Issue #12. Status: MERGED / AGENT-REPORTED PASS.
- [ ] **01.4 / Issue #5 — Two combatants bootstrap**: **READY** (#4 merged). Re-use existing World; stop/despawn old actors → call `CombatCommandQueue.ResetForBattle(EntityManager, SimulationClock.Tick)` at safe boundary → spawn new actors with non-reused IDs and valid Team/Position/Facing. New battle relative tick starts at 1. Manager detailed handoff posted to Issue #5.
- [ ] **01.5 / Issue #6 — Replay/behavior tests**: BLOCKED until #5 merged. Verify `relativeTick = absoluteTick - BattleStartTick` and two sessions in one World without leaked actors/commands.
- [ ] **01.6 / Issue #7 — Baseline ECS 400-lite benchmark**: BLOCKED until #5 merged and Temp cleanup Issue #12 is addressed before final benchmarking; can run in parallel with #6 once ready.
- **Architectural ownership:** only Issue #3 writes the authoritative World tick. Only Issue #4 consumes and clears commands; its `ResetForBattle` API is invoked by #5, never copied. Issue #5 owns battle lifecycle and monotonic non-reusable-in-World `CombatantId`. Future movement/collisions own authoritative `CombatPosition`, not Stage 01 clock. Tooling issue #10 and Commands Temp cleanup issue #12 are scoped follow-ups, not blockers for bootstrap.
- Unknown Unity execution status must remain explicit; **not a blocker** for delivering approved PRs.

## Delivery policy
Existing Unity-generated project content is preserved. Code changes belong in focused commits/PRs and must not be merged before authorization. **Project-owner rule (2026-10-25): Unity Editor tests must never block commit/merge/push when the owner cannot run them from the current environment.** Perform available static checks, report risks, mark runtime tests **NOT RUN**, and leave them outstanding for a later verification opportunity. Do not mislabel delivery as VERIFIED. Update status and QA together whenever delivering milestones.
