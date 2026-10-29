## Current #66 — Stage 04.4–04.5 impulses, indexed Hit, 2/100/400 (2026-10-29)

Fresh main base `cf2b5bcf57c2b81b635074a0322a85ecf50879b3` (PR #65); branch `feature/66-impulses-indexed-hit`, one PR.
[Contract: impulse input/epoch, integration, mass/contacts, rollback, Hit migration, checks, manual checklist](STAGE04_IMPULSES_HIT.md);
[exact SHA and PASS/FAIL/NOT RUN results](Evidence/66/README.md).
Movement remains the only Position/Motion writer and integrator: pending `CombatImpulse` → Δv=J/Mass → physics then
control proposals → contacts (physical share = mass-weighted inelastic response for any teams, 04.3 rules for the rest)
→ velocity from achieved physics → deceleration → final index → Hit via one atomic index batch + Burst kernel.
The first real Unity compile of the 04.3 base failed (CS0246 in the replay driver); fixed first, with two test-side
EditMode fixes. Per-attack Impact/stagger/guard remain Stage 05/06. Stage 03 OWNER-ACCEPTED; Stage 04 IN PROGRESS —
manager review and owner manual acceptance pending; merge only on owner instruction.

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

## Current #58 — Stage 04.1 (2026-10-28)

**Stage 03 OWNER-ACCEPTED**, by the owner's direct message recorded in #58: «Stage 03 закрыли. Идем дальше».
Exact owner manual-test SHA/protocol was not supplied; this is not a blanket Unity/GPU PASS. **Stage 04 IN PROGRESS**;
04.1 bodies/spatial snapshot delivered for review from fresh main `a6e96295c0a1e008327e28e8ff89205caf0a4f77`.
One branch `feature/58-combat-spatial-index`, one PR to main; merge requires a separate owner instruction.
[Implementation, owners, API, settings, tests and limitations](STAGE04_SPATIAL_INDEX.md).
#46/#53 and QA #17/#25/#30 remain separate. Older dated status/evidence entries below are historical and superseded
where they say Stage 03 awaits acceptance or Stage 04 has not started; their original test results/SHA remain unchanged.

## Current correction #52 — final combat controls (2026-10-28)

Base main `1c96c6f` includes merged PR #50/#51: full canvas/offset, 50%/25%/25% attack movement, ECS landing recovery,
scene config binding and safe empty preview are already delivered. Remaining #52 implementation uses one branch
`feature/52-final-combat-controls`, one PR, no implementing-agent merge. [Current layout and handoff](STAGE03_CONTROLS.md).
Y/U/H/J/K are exact strikes, I/L general hand/foot series, Space jump. Crouch is excluded; Dash/Cast deferred; Guard has
no binding and its future key is decided before Stage 06. Five strikes/hits are implemented; Stage 04 body/world collisions
are not. Stage 03 remains OPEN pending owner acceptance. Earlier dated delivery notes below are historical snapshots,
including their old key bindings and movement/landing descriptions; they do not override this correction.

# MLF Legends — Combat development roadmap

## Stage 03 tuning — Inspector settings before Stage 03 acceptance (2026-10-28)

Branch `feature/stage03-inspector-tuning`, one PR, **not merged by the implementing agent**. **Stage 03 is not declared
accepted: the owner performs the final check. Stage 04 has not started.**
- Roster size per team on the existing *Combat Bootstrap* (`CombatSmokeBootstrap`, `Assets/Scenes/CombatSmoke.unity`), total
  shown, applied on start/restart through the one lifecycle path.
- `CharacterAnimationConfig` clips: read-only `StartFrame`, editable `EndFrame`, derived `FrameCount` (local 0-based clip
  indices); strike clips: Preparation / Active / Recovery inclusive frame ranges.
- Those phases with duration and curve are the authoritative strike windows (`CombatStrikeSchedule` → per-strike frozen
  `CombatAttackState.Timing`); movement allowed in Preparation. Setup, example, migration and Play Mode rules:
  `Assets/MLFLegends/Combat/Runtime/Presentation/Sprites/ANIMATION.md`; decisions T1–T10 in `DECISIONS.md`; evidence in
  `Docs/Evidence/stage03-tuning/README.md`.

## Stage 05.1 + 03.3c / PR #48 — five strikes and state-driven combat animation (2026-10-28)

Base main `00310c3`. One branch `feature/stage05-strikes-combat-animation`, one PR; **MERGED on the owner's instruction as `b828ccb`** (2026-10-28). Proposed rules P1–P12 still await approval.
**Simulation:** `CombatActionSystem` (sole writer of `CombatAttackState`) → `CombatFacingSystem` → `CombatMovementSystem` →
`CombatHitSystem` (sole writer of `CombatHealth` and the per-strike `CombatHitRecord` ledger) in State. Five strikes
(punch_left/right, kick_left/right, kick_spin) with Windup/Active/Recovery, reach/depth/height boxes, damage once per target
per strike, series with one buffered follow-up and fixed chain points; timing/reach/damage in `CombatAttackTuning` (single
source). Player J = Attack, new logical `Kick` = U / gamepad North; AI uses the same queue. Rules not fixed by the spec are
**proposed** in `COMBAT_SPEC.md` and await owner approval (controls, series, air strikes deferred, movement/facing locks).
**Presentation:** `CombatAnimationSet` binds all eight clips (idle_combat, run, jump, five strikes) of the existing config to
authoritative state: strike frames fitted to the combat windows (`strike_pose` = Active ticks), jump rise/fall by vertical
velocity, landing only on the ground, run stride by distance, idle restarted on entry. N/T/curve keep their meaning as frame
shares; a strike T that differs from the combat total is reported. Owner idle/run settings preserved. Jump phase markers
`rise/fall/land` were added to the manifest (flagged, pending artist confirmation). No new renderer, clock, GameObject,
texture or material per fighter. Defeat at 0 HP, guard, stagger/knockback, combo bonuses, cancels and air strikes are not
implemented. Evidence (Unity EditMode/PlayMode/GPU runs, raw XML/logs/TSV, static art checks): `Docs/Evidence/48/README.md`.

## Stage 03.3b / #45 — idle + run (2026-10-27)

Base main `97b8c188` includes owner Unity import/scene/package changes and the owner's idle duration 1s; these are preserved.
Delivery adds only `run` (5 frames, config default 0.5s) to the existing character atlas/config. Grounded actors use run
when their current-tick accepted AppliedCombatCommand has nonzero MoveX or MoveZ; airborne/None/stale commands use idle.
Both clips retain independent N/T/SpeedCurve settings and the existing common battle-relative clock phase; no clip-entry
clock, per-id history, gameplay writer, new ECS system, Animator or transition FSM. Selector runs before footprint/depth/page
grouping; manual partial Worlds keep their original component query. Stage 04 collisions will revisit actual-motion criteria.

Input is the latest corrected PNG version4 (1152x384) with compatible revision3 manifest from the idle_run archive.
PNG SHA256 `d22b2a5950b7902cf2d3730a98022723cfd9ba374b7a6c6cb2ec9ac71195423d`; manifest SHA256
`27d9ffbbab436c619706673be17e92bf5b6d7634a3b8a0cc8041d89de0b3b573`. Idle row/frame metadata are unchanged.
PNG remains external/ignored; no art was regenerated. Anchors are estimates, not independently verified anatomy.
Independent manager bottom-up source review STATIC PASS; raw checks/source hashes are in Evidence/45/manager-static-review.json. Unity compilation/import/Burst,
authored EditMode/PlayMode/Graphics, actual GC/performance/Game view/Frame Debugger/Domain Reload off remain NOT RUN.
Evidence and rerun instructions: `Docs/Evidence/45/README.md`. #30/#17/#25 remain separate. One PR, no implementing-agent merge.
Older #43 idle-only/1.2s dispatch and evidence entries below are historical and do not override the current delivery.

Multi-character contract: the shared config supports many CharacterId entries, each with its OWN SpriteAtlasCatalog,
manifest/pages and per-clip N/T/curve. Clip IDs are character-scoped; switching the renderer's selected CharacterId
rebinds its atlas/frame indices/pixel scale/timing and clears all old page property-block references. Synthetic A→B→A
regression covers different frame counts, page textures/sizes, source/pivot/trim/nominal height and durations, with an
unrelated invalid entry. Current renderer still selects ONE default character type for the whole roster. Simultaneous
mixed-character/multi-atlas fighters require separate follow-up #46; this change does not claim that feature.


## Current delivery — 2026-10-27 / #43

`main` base fe324f83 includes merged #42; #41 is CLOSED as delivered, manager STATIC PASS.
Its new Unity/GPU/GC measurements remain NOT RUN. Older corrections-requested dispatch notes below are historical.
Stage 03.3a now supplies the real mage catalog and ONLY idle_combat, plus one shared character/animation config
(N frames, full duration, normalized speed curve). Existing ECS position/facing/clock are read-only. Source delivery
has independent manager STATIC PASS; runtime acceptance is NOT RUN. Full state-driven idle/walk/jump remains a separate task;
03.3 is not complete. See Evidence/43 and Presentation/Sprites/ANIMATION.md for exact boundaries/config/import.


**Current manager review (2026-10-26):** main `b997d48` includes merged PR #39/#40 for #38. Source and raw evidence inspected: EditMode 329/329 ×2, headless PlayMode 30 Passed + 8 Ignored ×2, D3D12 Graphics/Performance 8/8 ×2. **CHANGES REQUESTED: [#41](https://github.com/HolyPenguin21/mlf-legends/issues/41)** fixes roster-wide cumulative depth push that breaks 3D occlusion and validates GC/timing measurements. Stage 03.1–03.2 delivered, acceptance pending corrections; art continues in parallel. Stage 02 accepted with earlier qualifications; #30/#17/#25 stay OPEN. Manager did not run Unity. See QA.md.

## Stage 03 dispatch — owner-approved sequence (2026-10-26)

**[#38 Atlas contract + GPU sprite renderer](https://github.com/HolyPenguin21/mlf-legends/issues/38): MERGED / CORRECTIONS REQUESTED**, via PR #39 and #40. Current code = tested `8ea534f`; final raw counts and limits are in QA.md. **Next code task [#41](https://github.com/HolyPenguin21/mlf-legends/issues/41)**: bounded depth policy preserving local 3D occlusion at 2/100/400, validated allocation counter and same-call split timing; one corrective PR. Graphic work continues independently. 03.3 state-driven animation/final-art integration follows correction review.

1. **03.1 + 03.2 together:** document/validate schema v1 (rect/trim/pivot, pages, clip metadata, anchors), load shared immutable tables, one instanced URP renderer reading ECS position/facing, explicit per-instance frame selection and stable 2.5D depth including multiple pages/opaque 3D. Check 2/100/400, cache/resource ownership and lifecycle. No per-fighter GameObjects, second simulation or renderer path.
2. **03.3 follows review:** bind idle/walk/jump to existing authoritative state, integrate the final mage and calibrate visible scene/animations; profile graphics on actual hardware. Other attack/dash/cast clips remain content preview until their gameplay stages.
3. Artist/code handshake uses the same manifest before mass export; technical starting canvas 192×192, nominal body height 96, pivot (96,160), right-facing authored art + flip. Dimensions remain data, not shader constants. The exact markers form is fixed in #38 as name → local-index array.
4. Existing #30/#17/#25 qualifications remain. No merge by the implementing agent; raw graphics checks and headless test results are reported separately. No Stage 03 runtime result is claimed by this dispatch.

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
| 01 | ECS simulation, fixed-step scheduler, entity data, command buffer, replay tests and 2/100/400 ECS baseline | Six main tasks delivered, Bootstrap debt #19 implemented; independent code audit #23 static PASS, owner accepted Stage 02 transition with missing Unity proof tracked in #30 and Windows Unicode/Burst #17 separate. No movement/AI/collisions/renderer yet. | ACCEPTED FOR NEXT STAGE (OWNER) — UNITY QA DEFERRED |
| 02 | Movement and camera | Deterministic X/Z movement + jump Y, facing/action-state, tick-aligned input, fixed orthographic camera follow/zoom/pan, with one ECS authority | IMPLEMENTATION COMPLETE / MANAGER REVIEW PASS — #31/#33/#35 reviewed; #22 merged via PR #37, fixes and raw 244/244 ×2 + 21/21 ×2 + Performance 7/7 independently inspected; manual/hardware/full-frame QA remains qualified in QA.md |
| 03 | GPU sprite rendering | Atlas animation, direction/frame selection, stable depth sorting, instancing | OWNER-ACCEPTED — direct owner message in #58; exact manual-test SHA/protocol unknown, no blanket Unity/GPU PASS. #46/#53 remain separate follow-ups; historical per-SHA evidence below is retained. |
| 04 | Collisions | Custom actor/world collision, spatial index, masses, allies yield, enemies block, knockback | IN PROGRESS — 04.1 index (#58), 04.2 arena (#61), 04.3 body contacts (#64) merged; 04.4 impulses + 04.5 indexed Hit + 2/100/400 scale delivered in #66 for manager review and owner manual acceptance (Unity results in Docs/Evidence/66). |
| 05 | Attack resolution | Windup/active/recovery; fast/heavy/jump attacks; shapes, multi-hit rules, damage/impact/stagger | IN PROGRESS — 05.1 (#48, MERGED `b828ccb`, Unity EditMode/PlayMode/GPU PASS): five grounded strikes, W/A/R, box hit tests, multi-target, once-per-strike ledger, HP damage, series/buffer; rules PROPOSED pending owner approval. Since #66 Hit runs on the shared final index (no reimplementation needed). Open: per-attack Impact wired to the #66 impulse input, stagger/launch, interrupt/cancel, guard damage, heavy/light mapping, jump attacks, defeat, balance |
| 06 | Defense | Facing guard, stability, timely defense/counter opportunity, guard break; future guard key undecided, dash deferred/unbound | NOT STARTED |
| 07 | Duel AI | Range, observable windups, bounded reaction time, mistakes, defense/dash/counter via same command rules | NOT STARTED |
| 08 | Physiology | SPECIAL, body regions, injury and broken limbs, eye loss, bleeding, death, capability filtering | NOT STARTED |
| 09 | Complete 1v1 prototype | Combat feel/balance, UI, animation feedback, sound, robust regression suite | NOT STARTED |
| 10 | Group combat | 4v4 → 16v16 → 50v50, target allocation, local teamwork/formation, morale decisions | NOT STARTED |
| 11 | Navigation | Own heightfield grid, flow fields, bounded A*, avoidance, dirty-region recalculation, jump-vs-detour | NOT STARTED |
| 12 | 200v200 scale | ECS/Burst/rendering/perf benchmark on 400 actors; profile collisions, AI, render, frame pacing | NOT STARTED |
| 13 | Magic and destruction | Prepared spells, multiple resources, systemic area effects, destructible objects/terrain, nav changes | NOT STARTED |

## Historical Unity compilation blocker (resolved by reviewed PR #36)

Stage 02.2 [PR #34](https://github.com/HolyPenguin21/mlf-legends/pull/34) / #33 is **MERGED / STATIC REVIEW PASS FOR FACING**, but the implementer actually attempted Unity 6000.5.4f1 compilation in an ASCII worktree and reported **FAIL (CS0579 Duplicate UpdateInGroupAttribute)** in pre-existing BattleLifecycleSystem (two group attributes introduced by Stage 01 PR #28). This supersedes earlier blanket `NOT RUN` for compilation of this HEAD. A temporary uncommitted proxy workaround yielded **186/193 EditMode** (7 failed for pre-existing NUnit binding and old attribute assertion) and **10/10 PlayMode**; those are **NOT a clean main PASS**.

**Historical blocker, now resolved: [#35](https://github.com/HolyPenguin21/mlf-legends/issues/35)** — retain exactly one FixedStep lifecycle owner and add minimal Initialization proxy forwarding to the SAME system, preserve pause handling and catch-up End/Restart; repair six NUnit cases with int→ushort TestCase binding; rebuild/test clean branch and record real XML/log in [#30](https://github.com/HolyPenguin21/mlf-legends/issues/30). No second session owner or architecture expansion. The compile/integration gate was satisfied by PR #36 and its manager review. #17 Unicode/Burst remains independently OPEN.

## Stage 02 authorization and execution sequence — owner decision (2026-10-26)

**Update 2026-10-26 (#35/#30):** corrective branch `fix/35-lifecycle-proxy` compiles; clean-checkout Unity runs EditMode 195/195 and PlayMode 10/10 (ASCII and Cyrillic paths, cold and warm Library), owner manual GUI checks PASS, benchmark re-measured (see QA.md). [PR #36](https://github.com/HolyPenguin21/mlf-legends/pull/36) **MERGED on the owner's instruction (2026-10-26)**; manager review of the merged result is now complete; Stage 02.3 preparation may proceed.

**Stage 02 starts NOW.** The owner explicitly approved deferring actual Unity verification to technical debt. This supersedes the earlier Stage 01 closure gate that required all Editor/Windows QA before starting Stage 02. The independent Stage 01 source-code audit [#23](https://github.com/HolyPenguin21/mlf-legends/issues/23) is **CLOSED / STATIC PASS**, not Unity-verified. All pending post-PR #29 compile, full EditMode/PlayMode, ECS sorting warnings, paused scene/Domain Reload GUI and post-change profiling are tracked in **[#30 Unity QA debt](https://github.com/HolyPenguin21/mlf-legends/issues/30)**. **[#17 Unicode/Burst path](https://github.com/HolyPenguin21/mlf-legends/issues/17)** independently remains OPEN, cause unknown, actual Windows tests NOT RUN. Neither is silently promoted to PASS. An observed critical regression is fixed promptly before its Stage 02 consumer.

Stage 02 is split to protect single-authority boundaries, not to expand architecture:

1. **02.1 — [#31 ECS movement X/Z + jump Y](https://github.com/HolyPenguin21/mlf-legends/issues/31): PR #32 MERGED / MANAGER STATIC REVIEW PASS; Unity NOT RUN.** One fixed-tick motion integration owner reads canonical `AppliedCombatCommand`; sole writer of `CombatPosition` and minimal vertical motion state; native/Burst-compatible, 2/100/400, deterministic replay; no live input, camera, collision, renderer, proxy fighter objects or visual scene.
2. **02.2 — [#33 Facing by movement direction](https://github.com/HolyPenguin21/mlf-legends/issues/33): [PR #34](https://github.com/HolyPenguin21/mlf-legends/pull/34) MERGED by owner authorization (2026-10-26) / UNITY-UNVERIFIED.** ⚠️ Found during its Unity run: current main does not compile (`BattleLifecycleSystem` duplicate `[UpdateInGroup]`, from PR #28); must be fixed before any Unity verification and before 02.3 is accepted (see QA.md). Owner confirmed automatic Left/Right orientation from canonical accepted MoveX sign, Z-only/idle retains old facing; one fixed-tick CombatFacing owner before movement. No opponent autofacing, attack/guard action locks or second position writer. Update hard-coded facing assertions and tests assuming State contains only Movement, retaining system uniqueness/order checks.
3. **02.3 + 02.4 — [#22 Real input + fixed-angle orthographic camera](https://github.com/HolyPenguin21/mlf-legends/issues/22): OWNER COMBINED; MERGED / MANAGER REVIEW PASS (2026-10-26).** Branch `feature/stage02-22-input-camera`: `PlayerInputCapture` + `PlayerInputCommandSystem` (Collection, before the processor) + scene `PlayerInputAdapter`; `CombatCameraController` on CombatSmoke's existing Main Camera. Manager review of PR #37 requested four corrections (End camera view, fresh tap after attach, adapter World following, focus reset/sync); fixed in the same PR with regressions and a mutation negative control. Agent Unity runs on `4d9e431`: EditMode 244/244 x2, PlayMode 21/21 x2, Performance Burst off 7/7; raw XML/logs/TSV in `Docs/Evidence/22/` (QA.md). **MERGED on the owner's instruction (2026-10-26) as `fc5fb67`; #22 closed by the merge.** Manager re-review of the corrections and raw evidence is complete (QA.md); owner GUI and real OS/hardware focus checks remain open, including the documented conservative first-press loss after an unreported release. Next planned functional stage: Stage 03 GPU sprite rendering. Deliver one branch/PR: Unity Input System capture → one tick-aligned producer in existing Collection/queue (Held/Pressed, 30/60/120 FPS, catch-up, pause/restart cleanup); one scene-level read-only camera follows BattleSession.HumanCombatant + CombatPosition, supports pan/zoom with fixed angle. Verify both together in existing technical smoke/tests; no substitute fighter graphics or new visible battle scene before Stage 03.
4. **Stage 02 acceptance:** source-level/method evidence and replay/Unity tests where available, tagged `NOT RUN` otherwise. Default 1v1 and 200v200 share one implementation; benchmark changes measured, not guessed. Each runtime PR reviewed bottom-up before merge; no autonomous merge.

**Context transfer:** work preparation/dispatch begins in this conversation immediately. The previously requested new-session handoff remains available for continuing the Stage 02 implementation without carrying all Stage 01 chat messages. The new session is not a prerequisite for opening tasks or progressing today.

**Stage 02.1 manager review (2026-10-26):** [PR #32](https://github.com/HolyPenguin21/mlf-legends/pull/32) is merged (commit `e5c58afcc40188ece29f4d296ae18d486be547ce`), #31 closed. Inspected runtime, lifecycle, 39 authored EditMode cases, default-world PlayMode, replay and benchmark. Single movement writer, GroundHeight/VerticalVelocity/Grounded motion, input source parity and cleanup checked; **STATIC PASS, NOT UNITY-VERIFIED**. Stage 02 latency for 2/100/400 NOT RUN. **Future Stage 02.2 integration condition:** ReplayTests, PlayMode order and benchmark currently assert the *entire* State group contains only the movement system; when introducing a facing/action-state system, extend tests to assert both systems with real ordering and keep exactly one movement writer, rather than pinning `states.Length==1`. Do not call it a Stage 02.1 runtime defect. Profile future idle chunk writes before changing the motion owner.

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
- [x] **01.6 / Issue #7 — Baseline ECS 2/100/400-lite benchmark: MERGED / MANAGER REVIEWED.** [PR #24](https://github.com/HolyPenguin21/mlf-legends/pull/24) merged as `0ef2e24f904d7fdc0adb9e961bfd4efce114c2f9`; #7 closed after static review. Only existing Bootstrap owner spawns up to 200+200 with one ID reservation; default 1v1 unchanged. Dedicated test-only benchmark (no replay string recorder), 1000 ticks/load and 3 measured passes, Burst on/off with named hardware. Agent reports **114/114 EditMode, 6/6 PlayMode PASS** (manager did not run Unity). At N=400 Burst on: idle ≈16.6 µs/tick, 400 commands ≈46 µs/tick, full 12,800 pending ≈726 µs/tick. This is light ECS, **not** full battle performance. Named-entity pool follow-up is implemented in closed #19 (Unity verification deferred to #30); full pending performance watchlist is #25.
- **Architectural ownership:** only Issue #3 writes the authoritative World tick. Only Issue #4 consumes and clears commands; its `ResetForBattle` API is invoked by #5, never copied. Issue #5 owns battle lifecycle and monotonic non-reusable-in-World `CombatantId`. Future movement/collisions own authoritative `CombatPosition`, not Stage 01 clock. Tooling issue #10 and Commands Temp cleanup issue #12 are scoped follow-ups, not blockers for bootstrap.
- Unknown Unity execution status must remain explicit; **not a blocker** for delivering approved PRs.

## Historical Stage 01 closure gate — SUPERSEDED by 2026-10-26 Stage 02 authorization

**Historical plan, superseded as a scheduling gate.** Stage 01 implementation, technical debt #19 and independent static audit #23 are complete; Unity/runtime evidence was deferred by explicit owner decision on 2026-10-26. The earlier sequence is retained below for traceability, **not as a current block on Stage 02**:

1. **Issue #7 COMPLETED** — 2/100/400 light ECS baseline, Burst on/off, hardware/results in `Docs/PERFORMANCE_BASELINE.md`, PR #24 manager-reviewed and Issue closed.
2. **Issue #17 — DIAGNOSTICS DELIVERED / WINDOWS QA OPEN.** [PR #27](https://github.com/HolyPenguin21/mlf-legends/pull/27) merged as `9c98220dc53ed0a76db87b92035f95dbda8e44af`; agent added safe managed test logs and reproducible Windows path/cache protocol. **Root cause unknown; Unity/Windows test NOT RUN; issue stays OPEN.** Windows reproduction can proceed independently of #19 and remains mandatory to decide before Stage 01 signoff.
3. **Issue #19 (consolidated implementation: #19 + former #26) — IMPLEMENTED / MANAGER STATIC PASS / ISSUE CLOSED.** [PR #28](https://github.com/HolyPenguin21/mlf-legends/pull/28), merge `4075b154583a5088ac74a7d41b3c3d7ddf6761fc`, implemented the sole dual-group lifecycle owner (Initialization and FixedStep) for pause-safe Start/Restart/End, bounded two diagnostic names and regression coverage. [PR #29](https://github.com/HolyPenguin21/mlf-legends/pull/29), merge `c14d011824ed92a660919256d336e78638df72b8`, corrected ordering constraints: FixedStep-only Clock runs after Lifecycle, Begin ECB remains OrderFirst, phases retain explicit order. **No runtime method bodies changed in #29.** #19 closed as implementation completed after bottom-up static review, **NOT Unity-verified**: compile, EditMode/PlayMode, sorting logs, no-domain-reload QA and post-change frame profiling NOT RUN. Final audit #23 must check partial test World clock-only/commands-only group sorting (Clock references absent Lifecycle, possible warning) and explicitly disposition unrun QA.
4. **Issue #23 COMPLETED / CLOSED** — independent bottom-up STATIC PASS; no critical architecture defects. Actual Editor/runtime QA is NOT RUN and tracked in #30; Unicode/Burst #17 remains separate OPEN. Full pending cost #25 remains a future conditional performance concern, not a failure of Stage 01 baseline.

**Independent Stage 01 manager audit update (2026-10-26):** Source audit of current `main` `5bf4493a` is **PASS** with no critical ECS architecture faults (single owners, clock, queue, replay, lifecycle and 2/100/400 roster contracts reviewed). Implementation Issue #19 is closed. The code audit does **not** require Unity Editor; Unity compile/EditMode/PlayMode, post-change real ordering, Domain Reload disabled manual QA and Unicode-path Burst reproduction are **NOT RUN**, and the historical 114/114 tests and benchmark precede PR #28/#29. #23 has since been CLOSED after owner accepted the static audit and explicitly deferred runtime evidence to #30. One nonblocking test-fixture mismatch is documented: partial `CombatClockTests` and `CombatCommandProcessorTests` Worlds omit the Lifecycle system referenced by `CombatClockSystem.UpdateAfter` (warning possible, not reproduced without Editor). #17 stays open until actual Windows evidence or an explicitly documented workaround; owner authorized Stage 02 immediately with tests deferred to #30, and the handoff may be used in a new session. No early visual scene.

**Current disposition:** the owner has accepted a **static Stage 01 completion** and deferred non-executed Unity QA to #30 instead of blocking Stage 02. #17 Unicode/Burst remains OPEN. The first Stage 02 task is #31; real input pacing #22 will be implemented alongside the future real input adapter. The first user-facing fight scene remains **after Stage 03 sprite rendering**, not Stage 02.

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

**Historical Issue consolidation decision (completed before Stage 02):** former #26 + #19 consolidated into single Bootstrap [Issue #19](https://github.com/HolyPenguin21/mlf-legends/issues/19), with one PR and distinct code/test sections. Closed-as-duplicate #26 was **implemented by merged #19 PR #28/#29**; Unity runtime testing is deferred in #30. **Diagnostic PR #27 is MERGED** but #17 Windows test/root cause are unresolved; dispatch #19 now while Windows QA runs later/in parallel. #23 stays an independent manager final Stage 01 QA/architecture gate that must reconcile both issues. #25 is future conditional performance work.

**Post-benchmark follow-up policy:** Former #26 ECS debug-name exhaustion has been implemented and closed via merged Bootstrap #19. #25 full pending command cost is a measured *future performance risk*, not a failed benchmark or a proven Stage 01 gameplay bug. Review #25 before bulk 32-tick AI command producers or full-scale performance work, with the same Commands owner and the existing benchmark/replay suite.

**Operating policy:** manager tracks every nonblocking follow-up as an Issue and decides the integration window. Truly independent polish/low-impact debt can wait until end of the stage; a fix that affects a subsequent test, measurement, integration boundary or correctness must go **before** that consumer. Keep one Issue per focused PR, prohibit competing writers/file overlap, perform available static checks, report Unity execution status honestly. These follow-ups do **not** retroactively block delivery of #5 or require a new ECS layer. Core `CombatantId` comments and `Core/CONTRACTS.md` were reconciled to World-scoped uniqueness by manager (documentation-only changes).

## Delivery policy
Existing Unity-generated project content is preserved. Code changes belong in focused commits/PRs and must not be merged before authorization. **Project-owner rule (2026-10-25): Unity Editor tests must never block commit/merge/push when the owner cannot run them from the current environment.** Perform available static checks, report risks, mark runtime tests **NOT RUN**, and leave them outstanding for a later verification opportunity. Do not mislabel delivery as VERIFIED. Update status and QA together whenever delivering milestones.
