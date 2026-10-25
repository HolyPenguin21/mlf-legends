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
| 01 | ECS simulation, fixed-step scheduler, entity data, command buffer, replay tests and 2/100/400 ECS baseline | All six main tasks delivered, Stage 01 light-combatant baseline recorded with Burst on/off; verify and resolve Stage 01 debt before handoff. No movement/AI/collisions/renderer yet. | MAIN DELIVERABLES 6/6 MERGED — TECHNICAL DEBT IN PROGRESS |
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

## First visible gameplay scene — owner decision (2026-10-25)

- Preserve stage order: finish **Stage 01** baseline ECS profiling (#7), then **Stage 02** movement and fixed-angle camera, then **Stage 03** sprite rendering.
- **Do not** create a new visually playable/demo battle scene in Stage 02 using capsules, placeholder GameObjects or other substitute fighter graphics. Movement and camera in Stage 02 are verified through ECS state/integration tests and existing technical smoke infrastructure, not a new user-facing scene.
- **After the sprite renderer is working in Stage 03**, assemble the first visible battle test scene: orthographic camera, simple 3D arena and sprite-rendered fighters reflecting canonical ECS state. Integrate the Stage 02 movement/camera there; no separate gameplay model or render-driven simulation.
- Existing `Assets/Scenes/CombatSmoke.unity` is a **technical ECS smoke scene**, not a visual gameplay milestone, and can remain for automated/debug checks. No request to remove it.
- The first attack-capable fight remains a later milestone (Stage 05+); the complete 1v1 prototype is Stage 09. Do not promise visual attacks or finished combat at Stage 03.

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
- [x] **01.4 / Issue #5 — Two combatants bootstrap**: merged [PR #13](https://github.com/HolyPenguin21/mlf-legends/pull/13), commit `e64976f91ab032c6af00c900b9c0334834c89bc2`. One lifecycle owner, 2 identical ECS combatants, World-scoped non-reusing allocator, cleanup/restart in same World, scene bootstrap with owner token, new relative battle tick = 1. Agent-reported Unity **71/71 EditMode** and **6/6 PlayMode PASS**; manager read production code and test scenarios. Status: MERGED / AGENT-REPORTED PASS. Follow-up lifecycle hardening #14 scheduled in stabilization.
- [x] **01.5 / Issue #6 — Replay/behavior tests: MERGED.** [PR #20](https://github.com/HolyPenguin21/mlf-legends/pull/20), merge commit `41a8540c97aaca0c8c1c4354558abf33db9224a7`. Test-only isolated ECS replay driver and 16 integration cases; tested real clock, command queue, lifecycle, role-normalized same-World/different-World replay and catch-up step order; no production runtime changes. Agent reports **98/98 EditMode, 6/6 PlayMode PASS**, plus deliberately broken runtime negative controls; manager inspected driver/tests but did not run Unity. Frame-paced input limitation tracked for Stage 02 in Issue #22.
- [x] **01.6 / Issue #7 — Baseline ECS 2/100/400-lite benchmark: MERGED / MANAGER REVIEWED.** [PR #24](https://github.com/HolyPenguin21/mlf-legends/pull/24) merged as `0ef2e24f904d7fdc0adb9e961bfd4efce114c2f9`; #7 closed after static review. Only existing Bootstrap owner spawns up to 200+200 with one ID reservation; default 1v1 unchanged. Dedicated test-only benchmark (no replay string recorder), 1000 ticks/load and 3 measured passes, Burst on/off with named hardware. Agent reports **114/114 EditMode, 6/6 PlayMode PASS** (manager did not run Unity). At N=400 Burst on: idle ≈16.6 µs/tick, 400 commands ≈46 µs/tick, full 12,800 pending ≈726 µs/tick. This is light ECS, **not** full battle performance. Named-entity pool follow-up is consolidated into #19 (implementation prepared, Unity verification pending); full pending performance watchlist is #25.
- **Architectural ownership:** only Issue #3 writes the authoritative World tick. Only Issue #4 consumes and clears commands; its `ResetForBattle` API is invoked by #5, never copied. Issue #5 owns battle lifecycle and monotonic non-reusable-in-World `CombatantId`. Future movement/collisions own authoritative `CombatPosition`, not Stage 01 clock. Tooling issue #10 and Commands Temp cleanup issue #12 are scoped follow-ups, not blockers for bootstrap.
- Unknown Unity execution status must remain explicit; **not a blocker** for delivering approved PRs.

## Stage 01 closure gate — owner decision (2026-10-25)

**Do not start Stage 02 immediately after completing the main Stage 01 performance benchmark.** The owner requires the following sequence, with each implementation reviewed against actual `main`:

1. **Issue #7 COMPLETED** — 2/100/400 light ECS baseline, Burst on/off, hardware/results in `Docs/PERFORMANCE_BASELINE.md`, PR #24 manager-reviewed and Issue closed.
2. **Issue #17 — DIAGNOSTICS DELIVERED / WINDOWS QA OPEN.** [PR #27](https://github.com/HolyPenguin21/mlf-legends/pull/27) merged as `9c98220dc53ed0a76db87b92035f95dbda8e44af`; agent added safe managed test logs and reproducible Windows path/cache protocol. **Root cause unknown; Unity/Windows test NOT RUN; issue stays OPEN.** Windows reproduction can proceed independently of #19 and remains mandatory to decide before Stage 01 signoff.
3. **Issue #19 (consolidated implementation: #19 + former #26) — IMPLEMENTED / MANAGER STATIC PASS / ISSUE CLOSED.** [PR #28](https://github.com/HolyPenguin21/mlf-legends/pull/28), merge `4075b154583a5088ac74a7d41b3c3d7ddf6761fc`, implemented the sole dual-group lifecycle owner (Initialization and FixedStep) for pause-safe Start/Restart/End, bounded two diagnostic names and regression coverage. [PR #29](https://github.com/HolyPenguin21/mlf-legends/pull/29), merge `c14d011824ed92a660919256d336e78638df72b8`, corrected ordering constraints: FixedStep-only Clock runs after Lifecycle, Begin ECB remains OrderFirst, phases retain explicit order. **No runtime method bodies changed in #29.** #19 closed as implementation completed after bottom-up static review, **NOT Unity-verified**: compile, EditMode/PlayMode, sorting logs, no-domain-reload QA and post-change frame profiling NOT RUN. Final audit #23 must check partial test World clock-only/commands-only group sorting (Clock references absent Lifecycle, possible warning) and explicitly disposition unrun QA.
4. **Issue #23** — final Stage 01 technical-debt audit (Core → Simulation → Commands → Bootstrap → Replay → Performance), registry of any further issues, final QA and performance summary, and preparation of a **standalone handoff message for a new Stage 02 conversation** with exact `main` commit and contractual context. Measured but not currently blocking Stage 01: full pending command cost #25 (future Stage 12/if bulk producers arrive).

**Independent Stage 01 manager audit update (2026-10-26):** Source audit of current `main` `5bf4493a` is **PASS** with no critical ECS architecture faults (single owners, clock, queue, replay, lifecycle and 2/100/400 roster contracts reviewed). Implementation Issue #19 is closed. The code audit does **not** require Unity Editor; Unity compile/EditMode/PlayMode, post-change real ordering, Domain Reload disabled manual QA and Unicode-path Burst reproduction are **NOT RUN**, and the historical 114/114 tests and benchmark precede PR #28/#29. #23 now represents final **evidence/disposition gate**, not an additional implementation assignment. One nonblocking test-fixture mismatch is documented: partial `CombatClockTests` and `CombatCommandProcessorTests` Worlds omit the Lifecycle system referenced by `CombatClockSystem.UpdateAfter` (warning possible, not reproduced without Editor). #17 stays open until actual Windows evidence or explicit owner decision. No new early visual scene; Stage 02 work only after the user's decision and in a new session.

**Completion criterion:** Stage 01 technical debt must be explicitly resolved and reviewed; if a manual check cannot be performed, record it as outstanding and seek the owner's decision rather than silently marking it complete. Stage 02 **Issue #22**, frame-rate-independent input sampling, is planned work that requires a future real input adapter; it is not preemptively implemented as Stage 01 debt. The first visible user-facing fight scene remains **after Stage 03 sprite rendering**, not Stage 02.

## Stage 01 stabilization window — manager scheduling decision (2026-10-25)

After merged Issue #5 (PR #13), execute narrow follow-ups **before final validation and performance baselining**:

1. **Issue #12 — Commands Temp Native-container lifetime: COMPLETED.** [PR #15](https://github.com/HolyPenguin21/mlf-legends/pull/15) merged as `4ccc4222c58307ca9164f453de9c8d7bbb2ef1b9`. Native Temp container lifetimes made explicit; real persistent `EntityQuery` retention in `ResetForBattle` fixed. Agent reports **72/72 EditMode, 6/6 PlayMode PASS**; manager static review confirms six Temp scratch containers and all three dynamically created queries disposed. Native Temp allocator's per-frame reclamation still determines actual frame peak, to be profiled in #7.
2. **Issue #10 — PowerShell Unity test runner: COMPLETED.** [PR #16](https://github.com/HolyPenguin21/mlf-legends/pull/16) merged as `7e6238bc94cf9a12ad5b17d76c9af475edad76df`. Uses direct `System.Diagnostics.Process` wait/timeout, fresh XML parsing and reliable exit status; agent reports **72/72 EditMode** and **6/6 PlayMode PASS** in supported ASCII paths, plus extensive negative controls. Manager static-reviewed PowerShell and confirmed no runtime/gameplay changes.
3. **Issue #14 — Battle lifecycle safety: COMPLETED.** [PR #18](https://github.com/HolyPenguin21/mlf-legends/pull/18) merged as `6983ad874e604f9a22baeadc2b63b8c6b7b23ad7`. The same lifecycle owner now runs before the clock on **each** fixed tick (including catch-up), checks `CanResetForBattle` through canonical queue validation before destroying actors, and releases ephemeral Bootstrap queries. Agent reports **82/82 EditMode, 6/6 PlayMode PASS**; manager inspected code and tests. Pause/Domain Reload scenarios remain tracked separately in #19.
4. **Issue #6 — Replay and integrated behavior tests: COMPLETED.** PR #20 merged; 16 scenarios, agent-reported 98/98 EditMode and 6/6 PlayMode pass. Replay coverage is limited to existing clock, commands and lifecycle; no fake RNG or combat mechanics added.
5. **Issue #7 — 2/100/400-light-actor performance baseline: COMPLETED.** PR #24 merged and code reviewed, Issue closed. 114/114 EditMode and 6/6 PlayMode agent-reported PASS; full methodology and hardware in `Docs/PERFORMANCE_BASELINE.md`. Full pending performance risk tracked for later in #25.
6. **Issue #17 — Unicode Windows path/Burst (STAGE 01 DEBT / WINDOWS QA PENDING):** historical EditMode 71/72 under Cyrillic path, ASCII 72/72. Diagnostic-only PR #27 merged, root cause unknown, all controlled Windows experiments NOT RUN; continue external Windows evidence in parallel with #19. Do not close until confirmed fix/workaround or explicit owner disposition.
7. **Issue #19 — Consolidated Bootstrap: CODE COMPLETE / ISSUE CLOSED.** PR #28 + sorting follow-up PR #29 both merged, static review PASS. PlayMode/Domain Reload/manual Unity testing **NOT RUN**; verification and any unexpected sorter warnings are tracked explicitly in final audit #23. Issue #17 Windows Unicode/Burst remains OPEN; Stage 01 is not signed off.
8. **Issue #22 — Tick-aligned input sampling (STAGE 02):** replay tests show per-render-frame `Tick+1` producers are pacing-dependent. Define Held/Pressed handling and command scheduling when real input adapters arrive; no Stage 01 benchmark blocker.
9. **PR #21 — URP shader import/cache recovery and serialized Editor settings (MERGED):** agent reports cached failed `.urtshader` imports were repaired by reimporting 16 shaders locally and that subsequent 98/98 EditMode, 6/6 PlayMode tests passed. Serialized URP/ProjectSettings updates are in `main`; review rendering settings when Stage 03 work starts. Does not change combat ECS runtime. This evidence does not yet prove Unicode Burst Issue #17 is fixed.

**Issue consolidation / execution decision (2026-10-26):** former #26 + #19 consolidated into single Bootstrap [Issue #19](https://github.com/HolyPenguin21/mlf-legends/issues/19), with one PR and distinct code/test sections. Closed-as-duplicate #26 is **not fixed** until #19 is implemented. **Diagnostic PR #27 is MERGED** but #17 Windows test/root cause are unresolved; dispatch #19 now while Windows QA runs later/in parallel. #23 stays an independent manager final Stage 01 QA/architecture gate that must reconcile both issues. #25 is future conditional performance work.

**Post-benchmark follow-up policy:** #26 ECS debug-name exhaustion is a small correctness/stability defect to close during Stage 01; #25 full pending command cost is a measured *future performance risk*, not a failed benchmark or a proven Stage 01 gameplay bug. Review #25 before bulk 32-tick AI command producers or full-scale performance work, with the same Commands owner and the existing benchmark/replay suite.

**Operating policy:** manager tracks every nonblocking follow-up as an Issue and decides the integration window. Truly independent polish/low-impact debt can wait until end of the stage; a fix that affects a subsequent test, measurement, integration boundary or correctness must go **before** that consumer. Keep one Issue per focused PR, prohibit competing writers/file overlap, perform available static checks, report Unity execution status honestly. These follow-ups do **not** retroactively block delivery of #5 or require a new ECS layer. Core `CombatantId` comments and `Core/CONTRACTS.md` were reconciled to World-scoped uniqueness by manager (documentation-only changes).

## Delivery policy
Existing Unity-generated project content is preserved. Code changes belong in focused commits/PRs and must not be merged before authorization. **Project-owner rule (2026-10-25): Unity Editor tests must never block commit/merge/push when the owner cannot run them from the current environment.** Perform available static checks, report risks, mark runtime tests **NOT RUN**, and leave them outstanding for a later verification opportunity. Do not mislabel delivery as VERIFIED. Update status and QA together whenever delivering milestones.
