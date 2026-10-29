## Current #69 — Stage 05.1 (2026-10-29)

Only this section is current; every older "History" section keeps its own SHA/scope and is superseded where it disagrees (old key layouts, exact-limb intentions, velocity = displacement/dt, series limit 4, stage states).

Fresh main `86012a0` (PR #68). Branch `feature/69-stage05-character-series`, one PR, **no merge by the implementer**.
Commit order: confirmed fixes → per-character data → input/series → checks → docs/evidence. Contract: [STAGE05_1_CHARACTER_SERIES.md](STAGE05_1_CHARACTER_SERIES.md);
results: [Evidence/69](Evidence/69/README.md). The owner's #67 agreements are recorded in ROADMAP.md (single entry point). The atlas pass
is #70 (after #69 review); 05.2 self impulse/dynamic Height/swept hits; 05.3 VFX.

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

## History — #58 — Stage 04.1 (2026-10-28)

**Stage 03 OWNER-ACCEPTED**, by the owner's direct message recorded in #58: «Stage 03 закрыли. Идем дальше».
Exact owner manual-test SHA/protocol was not supplied; this is not a blanket Unity/GPU PASS. **Stage 04 IN PROGRESS**;
04.1 bodies/spatial snapshot delivered for review from fresh main `a6e96295c0a1e008327e28e8ff89205caf0a4f77`.
One branch `feature/58-combat-spatial-index`, one PR to main; merge requires a separate owner instruction.
[Implementation, owners, API, settings, tests and limitations](STAGE04_SPATIAL_INDEX.md).
#46/#53 and QA #17/#25/#30 remain separate. Older dated status/evidence entries below are historical and superseded
where they say Stage 03 awaits acceptance or Stage 04 has not started; their original test results/SHA remain unchanged.

Current scope is only #58: use existing lifecycle/Movement/Hit, one World-owned index between Movement and Hit,
no second position writer or clock. Register the same owner in default/replay/benchmark and preserve unique-owner order
assertions. Actual new component: CombatBody. New system: CombatSpatialIndexSystem (managed native-container owner).
[Bottom-up review and corrections](STAGE04_BOTTOM_UP_REVIEW.md): atomic AABB batches, complete enabled-ID membership,
whole-entity Disabled cleanup and new edge/measurement tests. Unity-unavailable checks remain NOT RUN without blocking PR delivery; do not merge or close follow-up issues.

## History — task #56 — pivot and Inspector corrective delivery

Fresh base main `311c3d7d06b5320b9df701ae6f21f519d07c6af1`. Branch `fix/56-sprite-pivot-inspector`, one PR, no merge.
[Current implementation and verification](STAGE03_PIVOT_CORRECTION.md). Stage 03 still requires owner manual acceptance.
The older #52 input layout, phase speeds, damage/simulation, landing and owner timing settings are preserved.

## History — task #52 — final controls (2026-10-28)

Base main `1c96c6f` already contains #50/#51; do not duplicate their scene/config/preview fixes. Work in
`feature/52-final-combat-controls`, one PR, no merge without owner instruction. [Current control contract and handoff](STAGE03_CONTROLS.md).
Exact/general intentions share the existing input/queue/action path. Crouch excluded, Dash/Cast deferred, Guard unbound.
Owner art/timing/roster and Camera/UI remain unchanged. Stage 03 stays open until manual owner acceptance.

## Stage 03 final polish — implementation handoff (2026-10-28)

Follow-up PR #51, branch `fix/stage03-landing-config-binding`: [bottom-up integration review and corrections](STAGE03_BOTTOM_UP_REVIEW.md).
Includes the missing scene binding, renderer/storage cache transitions, short-landing idle epoch, and World/scene publish
ownership/late startup fixes. Implementing agent does not merge; Unity and Stage 03 owner acceptance are still pending.

Current task: branch `fix/stage03-final-polish`, one PR to main, **implementing agent must not merge**.
[Settings, compatibility, source review and manual checks](STAGE03_FINAL_POLISH.md). Stage 03 remains pending owner
confirmation; Stage 04 has not started. No new automatic tests required; no PNG/art sources in the delivery.

# Agent workflow — MLF Legends

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


## History — dispatch — #43 (2026-10-27)

Implement/review Stage 03.3a on feature/stage03-idle-combat: real mage idle_combat ONLY and shared character-animation
config (N/T/normalized speed curve), existing read-only renderer/extraction and existing simulation clock. One PR,
no implementing-agent merge, no PNG/art sources committed. Current base main fe324f83 already merged #42 after
manager STATIC PASS; #41 CLOSED as delivered. Its Unity/GPU/GC reruns and #43 runtime acceptance remain NOT RUN.
The dated correction task and older dispatch notes below are historical. See #43, ANIMATION.md and Evidence/43.


**Current manager correction task (2026-10-26): [#41](https://github.com/HolyPenguin21/mlf-legends/issues/41), after merged PR #39/#40 / #38.** Reviewed main `b997d48`; source + supplied raw evidence inspected, Unity not run by manager. Fix cumulative depth push (distant equal-Z roster members must not pull a fighter through a foreground wall), validate the allocation counter with a positive control and capture Refresh split stats before yield; restore test profiler/resources on failure. One narrow `fix/38-depth-and-measurement` PR, no simulation rewrite or new renderer. #38 is delivered but manager acceptance awaits corrections. Art continues in parallel; 03.3 follows correction review. Full evidence and qualification: QA.md.

**Current implementation dispatch (owner decision, 2026-10-26): [#22](https://github.com/HolyPenguin21/mlf-legends/issues/22) combines Stage 02.3 input and Stage 02.4 camera in ONE branch/PR.** Read the expanded Issue: one tick-aligned Player producer through the existing queue, read-only orthographic follow/pan/zoom, lifecycle/focus/pause cleanup, joint tests. Reuse existing action asset and technical smoke camera; no new visible battle scene before Stage 03. Agent opens the combined PR; manager reviews; owner alone authorizes merge. **Status: MERGED / MANAGER REVIEW PASS** - PR #37 merged on the owner's instruction (2026-10-26) as main `fc5fb67`; the four manager review items were fixed before the merge; agent Unity evidence on `4d9e431` committed in `Docs/Evidence/22/`. Manager inspected the fixed source and raw XML/log/TSV: EditMode 244/244 twice, PlayMode 21/21 twice, Performance 7/7; the 8 mutation regressions fail as intended. Manager did not re-run Unity. Owner GUI, actual hardware/OS focus, Domain Reload disabled and camera/full-frame checks remain unverified; INPUT.md documents conservative first-press loss after an unreported release.

**Current manager disposition (2026-10-26; supersedes historical dispatch below):** merged PR #37 / main `72bb712` corrections accepted after source and raw evidence review; Stage 02 input/camera implementation complete, Stage 03 GPU sprite rendering planning next. Review evidence and the corrected GC.Alloc qualification are in QA.md. #30 remains OPEN for existing QA/performance debt; #17 remains OPEN / NOT REPRODUCED; #25 remains separate. No runtime rewrite requested.

**Historical dispatch before PR #36 (2026-10-26): [#35](https://github.com/HolyPenguin21/mlf-legends/issues/35) BLOCKS clean Unity build and Stage 02.3 integration.** Stage 02.2 facing #33/PR #34 merged and statically reviewed, but actual Unity compile in the agent's Windows ASCII worktree FAILED (CS0579 duplicate UpdateInGroup on the Stage 01 lifecycle). Local temporary proxy yielded 186/193 EditMode and 10/10 PlayMode, NOT a valid main PASS. Manager approved the narrow **Initialization proxy → same sole FixedStep lifecycle instance** and NUnit `int`→`ushort` binding correction in one reviewed PR; no second writer, no runtime architectural expansion. Logs and final clean EditMode/PlayMode tracked in #30. No Stage 02.3 input adapter merge before blocker resolution. Unicode/Burst #17 remains separate.

## Manager / developer division
**Project owner** approves major scope changes and delegates GitHub Issues to developers or AI agents.

**Technical manager (ChatGPT)** is responsible for:
1. Tracking roadmap milestones against the actual `main` branch, not against verbal claims alone.
2. Authoring small, testable GitHub Issues with clear scope, dependencies and acceptance criteria.
3. Assigning integration order and preventing two agents from modifying the same runtime owner concurrently.
4. Reviewing PR files, callsites, lifecycle/cleanup, competing system writers, package compatibility, regression risks and testing evidence.
5. Requesting correction when code violates the accepted architecture; logging known risks and updating `Docs/ROADMAP.md` / `Docs/QA.md`.
6. Merging and pushing only when the project owner authorizes it; no silent approvals of material architecture changes.

**Implementing agent**:
1. Read the Issue plus `Docs/ARCHITECTURE.md`, `Docs/COMBAT_SPEC.md`, `Docs/DECISIONS.md`.
2. Pull fresh `main`, inspect code, existing structures, files, references and system writers before changing anything.
3. Implement **only the specified scope**, in a dedicated `feature/stage01-<task-number>` branch with Unity `.meta` files.
4. Perform available static checks, unit tests, lifecycle review and check error paths; runtime Unity tests can be `NOT RUN` if unavailable, **without blocking delivery**.
5. Open **one PR per Issue**, targeting `main`. The agent does not merge their own PR.
6. Report a concise list of changed files, new or removed systems, tests actually run (or NOT RUN), known risks, and PR link.

## Managing parallel nonblocking follow-ups

The technical manager must not leave discovered corrective work as informal remarks. Record each nonblocking fix in GitHub Issues with a precise owner, scope, regression checks and dependencies. The manager chooses when to dispatch it to an agent instead of asking the project owner to manually schedule each follow-up.

- **Before a consuming task:** fix if it affects correctness, lifecycle ownership, test trustworthiness, or the measurements of a subsequent step.
- **At the stage's stabilization window:** group narrow follow-ups after main implementation and before final integration/replay/performance checks, with separate PRs.
- **End of stage:** defer cosmetic/low-impact cleanup not required by downstream work, while keeping open issues visible.
- Avoid contradictory parallel edits to the same files. Only the owner-approved integration sequence determines what is ready; passing unavailable tests is never assumed.

**Update (2026-10-26):** Stage 02.2 #33 delivered as PR #34 and merged on the owner's instruction; Unity-unverified. **Blocker found:** main does not compile in Unity (`BattleLifecycleSystem.cs(28,6): CS0579 Duplicate 'UpdateInGroup'`, PR #28); a Bootstrap-owned fix must be dispatched before Unity QA (#30) or 02.3 acceptance. Details in QA.md.

**Previous dispatch (owner confirms facing semantics, 2026-10-26):** Stage 02.1 #31/PR #32 merged, manager STATIC PASS, runtime Unity NOT RUN (#30). **NEXT agent implementation = [#33](https://github.com/HolyPenguin21/mlf-legends/issues/33) Stage 02.2 CombatFacing by direction of accepted MoveX**: Right on +X, Left on -X, keep previous facing on idle or Z-only movement, applies to both player and AI and airborne. Add a single CombatFacing writer in State, explicitly before current Movement owner; no backpedal/enemy auto-face/action locks/guard or new action-state architecture. **Do not duplicate CombatPosition or Facing writers.** Update three State-membership test assertions PLUS old tests expecting unchanged Facing on negative MoveX (`CombatMovementTests` direction matrix, `CombatReplayTests` command matrix); preserve checks rather than deleting. No visual scene/capsules before Stage 03 GPU sprite renderer. Later work: Stage 02 real input adapter #22 and fixed-angle read-only camera. QA debt #30 and Unicode/Burst #17 remain OPEN / NOT RUN. Agent opens one PR and does not merge independently.

## Stage 01 integration graph

```text
Issue #2 — 01.1: data contracts, runtime assembly
  └─ Issue #3 — 01.2: simulation clock / fixed tick
       └─ Issue #4 — 01.3: canonical player+AI command application
            └─ Issue #5 — 01.4: bootstrap 2 combatants, spawn/despawn
                 ├─ Issue #6 — 01.5: reproducibility and behavior tests
                 └─ Issue #7 — 01.6: 400-lite-actor profiling harness
```

Do **not** implement dependent runtime code against a guessed Issue #2 contract. #6 and #7 are parallelizable only after #5 is integrated. Any discovered contract gap is raised to the manager and fixed at the contract owner, not patched with duplicate implementation.

## Protected scopes for parallel agents

| Issue | Primary source ownership | Must not change without coordination |
|---|---|---|
| #2 | `Assets/MLFLegends/Combat/Runtime/Core/` + runtime asmdef | Clock, command dispatch, spawn systems |
| #3 | `Assets/MLFLegends/Combat/Runtime/Simulation/` | Core contracts, command producers |
| #4 | `Assets/MLFLegends/Combat/Runtime/Commands/` | Core contracts, scene / spawner |
| #5 | `Assets/MLFLegends/Combat/Runtime/Bootstrap/` and optional smoke scene | Command processing and unrelated scenes |
| #6 | `Assets/Tests/EditMode/Combat/`, `Assets/Tests/PlayMode/Combat/` | Performance harness, original smoke tests |
| #7 | `Assets/Tests/EditMode/Performance/`, `Docs/PERFORMANCE_BASELINE.md` | Test code from #6 or alternative ECS runtime |

If an Issue needs changes outside its declared directories, the agent must describe the reason in its PR and raise the overlap before editing.

## Agent handoff
Send the agent this instruction:

> Выполни только задачу из указанного GitHub Issue проекта `HolyPenguin21/mlf-legends`. Перед изменениями проверь актуальный `main`, связанные компоненты и Docs. Не добавляй второй источник истины, отдельный бой для дуэли или необоснованные новые слои. Сохраняй Unity `.meta`. Сделай отдельную ветку, коммиты и PR в `main`, но не мержи. В отчёте укажи изменённые файлы, результаты фактически выполненных проверок, `NOT RUN` для недоступных проверок Unity, известные риски и ссылку на PR. При неясности технического контракта останови именно спорную часть, а не выдумывай альтернативную архитектуру.

## Review gates
1. **Scope gate**: only Issue scope and documented adjacent dependencies.
2. **Architecture gate**: single authoritative writers, ownership clarity, no lifecycle leaks, no hidden main-thread game-loop duplicates.
3. **Correctness gate**: data flow and state transitions, edge cases (stale commands, despawn), stable order, testability.
4. **Integration gate**: current `main` can accept the PR without conflicting definitions, asmdefs or Unity GUIDs.
5. **Evidence gate**: recorded static checks and tests; Unity runtime `NOT RUN` is a tracked risk, **not** a delivery blocker.
6. **Delivery gate**: after owner authorization, merge PR, verify head in `main`, update roadmap/QA and unblock dependent Issue.

## Status
An Issue is **READY**, **BLOCKED BY DEPENDENCY**, **IN PROGRESS**, **IN REVIEW**, **MERGED / UNVERIFIED**, or **VERIFIED**. PR merge and test pass are not synonymous. No progress is assumed between chats unless confirmed through GitHub.
