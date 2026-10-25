# MLF Legends — Quality assurance and evidence

**Scope note:** GitHub repository and serialized Unity project settings can be reviewed, but Editor tests have **not** been performed directly by this assistant. On **2026-10-25**, the project owner reported **all Stage 00 tests PASSED**. This is an **owner-reported PASS**, without attached XML/log evidence; it is not an independently audited run.

**Non-blocking delivery rule (approved 2026-10-25):** The owner currently cannot run Unity tests from their environment. This MUST NOT block committing, merging or pushing requested changes, nor prevent moving on to subsequent implementation tasks. Perform every feasible static check, label Unity checks **NOT RUN**, record the residual risk and verify later when execution becomes possible. Delivery is not the same as test approval.

## Stage 00 findings and checklist
- [x] `HolyPenguin21/mlf-legends` exists; GitHub App repository access repaired and write validated.
- [x] `Assets/`, `Packages/` and `ProjectSettings/` exist in `main`.
- [x] `ProjectSettings/ProjectVersion.txt` reads `6000.5.4f1`.
- [x] `Packages/manifest.json`: `com.unity.render-pipelines.universal` **17.5.0**, `com.unity.inputsystem` **1.19.0**.
- [x] `Packages/packages-lock.json` includes Burst **1.8.29** and Collections **6.5.0** as transitive dependencies.
- [x] `ProjectSettings/GraphicsSettings.asset` points to a render pipeline asset; QualitySettings PC quality also points to a render pipeline asset.
- [x] Initial project documentation added (README, Docs/ROADMAP, ARCHITECTURE, COMBAT_SPEC, DECISIONS, QA).
- [ ] Confirm renderer asset and smoke scene instantiate correctly in Unity Editor.
- [x] User added `com.unity.entities` **6.5.0** and `com.unity.entities.graphics` **6.5.0** to manifest and lockfile. Both are Unity 6000.5 core packages.
- [ ] Verify Unity Editor imports the two core packages without package or compiler errors.
- [ ] Verify package dependency resolution in the editor.
- [x] Created EditMode and PlayMode test assemblies with committed `.meta` GUIDs; staged ECS lifecycle, URP asset, and SampleScene load smoke tests on feature branch.
- [x] **OWNER-REPORTED PASS (2026-10-25):** all Stage 00 EditMode and PlayMode tests passed, per project owner. XML/Editor logs not provided; independent verification not performed.
- [ ] Retain optional independent scene/Console review for later; owner-reported passing PlayMode test includes existing SampleScene load.
- [ ] CI remains pending local tests and a viable Unity license/runner strategy; no GitHub Actions workflow currently configured.
- [x] Record owner-reported test PASS and accept Stage 00 for planning purposes. CI and independent log review remain optional follow-ups; do not represent them as completed.

## How to run tests (Windows; Unity Editor must be closed)

In Unity GUI: open **Window > General > Test Runner**; run **EditMode** tests, then **PlayMode** tests. Use Unity 6000.5.4f1.

In PowerShell from repository root:

```powershell
.\Tools\Run-UnityTests.ps1 -EditorPath 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' -Mode EditMode
.\Tools\Run-UnityTests.ps1 -EditorPath 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' -Mode PlayMode
```

The script requires a nonzero NUnit test count with all tests passing; it saves XML and Editor log to ignored `TestResults/`. Command-line testing can require an available Editor license. **Running these commands is optional for delivery**; when unavailable, keep Unity tests marked NOT RUN and continue via static review. If either suite fails when eventually executed, log the failure and fix it; do not mark the stage VERIFIED before actual evidence.

## Required review/check method
1. Inspect code and existing callers before modifying.
2. Search for competing authoritative processors and duplicate functionality.
3. Trace effects: input → commands → actor state → collision → attack/guard → injury → render and AI.
4. Cover edge cases, interruptions, cleanup, multi-hit and simultaneous events.
5. Separate EditMode automated tests from PlayMode/full integration tests; list NOT RUN clearly.
6. Use seed-controlled replay where possible; disclose limits of deterministic floating-point across platforms.
7. Record CPU/GPU profiles including actor count, environment, target hardware and frame/tick timing.
8. Write regression findings with test command/Unity version/commit SHA.

## Stage 01.1 code review (Issue #2 / PR #8; 2026-10-25)
- **GitHub delivery: PASS.** PR #8 merged into `main` with merge commit `74de551e7bc4e1f213e7af1c688c1a2db78212cb`; Issue #2 closed as completed.
- **Static contract review: PASS (manager inspection of committed files).** Exactly one `MLFLegends.Combat.Runtime.asmdef`; six named data-only types in `Core/`, command struct, and `Core/CONTRACTS.md`. No new autonomous tick system, command processor, combat logic, physics, graphics or extra runtime asmdef in this PR.
- **Ownership validation:** `CombatantId` and `CombatTeam` allocated at spawn (#5 later); `CombatPosition` and `CombatFacing` are not written by #3/#4; `CombatCommand` producer and consumer separation documented.
- **Agent-reported validation (not independently repeated):** `dotnet build` against installed Unity/Entities assemblies PASS with warnings-as-errors; reflection for unmanaged contracts PASS; GUID/meta pairing PASS. See PR #8 description for methodology.
- **Unity Editor project compile/Entities source generation/EditMode/PlayMode:** **NOT RUN by manager**. Agent did not report running full Unity tests for its new contracts. This is a documented risk but **not a blocker** to issuing #3.
- **Dependency checks for #3:** clock must use a `uint` tick, `Tick=0` is invalid, first simulated tick = 1, and must not introduce a competing queue consumer. Clarification posted to Issue #3.

## Stage 01.2 code review (Issue #3 / PR #9; 2026-10-25)
- **GitHub delivery: PASS.** PR #9 merged into `main`; merge commit `1c98d1f982bea8e7776101382b2585fa16a89fd3`; Issue #3 closed.
- **Manager static review: PASS for core design.** `CombatClockSystem` is the only writer of the World-scoped `SimulationClock` and the fixed-step timestep; `FixedStepSimulationSystemGroup` is used with no competing frame-time accumulator. Collection → state → publication are empty ordering groups. Clock starts at tick 1, saturates without wrapping, and does not own combatant state.
- **Agent-reported checks:** Unity EditMode 15/15 PASS and PlayMode 2/2 PASS; dotnet build with Entities generators PASS; metadata GUID audit PASS. Reported in PR #9, but full test XML/editor logs were not independently inspected by manager. Do not label these MANAGER-EXECUTED.
- **Accepted architecture decision:** ordinary fight restart reuses the default ECS World; `SimulationClock.Tick` stays monotonic, and bootstrap (#5) stores `BattleStartTick` to derive relative battle ticks. Persistent `CombatantId` allocation remains non-reusing within the same World. Future command queue owner (#4) must clear battle-owned commands/history on restart.
- **Minor tool defect (not a combat runtime blocker):** existing `Tools/Run-UnityTests.ps1` uses direct `& Unity.exe`; PR #9 reports PowerShell process-wait issues. Follow-up [Issue #10](https://github.com/HolyPenguin21/mlf-legends/issues/10), non-blocking.
- **Known residual risks:** fixed-step full-group allocation profile not recorded, agent states catch-up is bounded by `World.MaximumDeltaTime`, and World-level clock runs even outside active battles. No separate second clock should be introduced. Future replay tests must use relative battle tick and battle-seeded inputs, not absolute World uptime.

## Stage 01.3 code review (Issue #4 / PR #11; 2026-10-25)
- **Delivery confirmed:** PR #11 merged to `main` at `cca88b38e3167f4b0e1329a5128b640a49227fc3` and Issue #4 closed.
- **Manager static review:** looked at `CombatCommandProcessorSystem`, `CombatCommandQueue`, `AppliedCombatCommand`, `COMMANDS.md`, production ownership and command tests. Single `CombatCommandCollectionSystemGroup` processor applies player and AI commands with identical validation; no duplicate gameplay logic, no new clock or separate battle world. Bounded future pending, first valid command wins, stale/invalid/duplicate outcomes observable. `ResetForBattle` clears buffers and applied state without changing World clock.
- **Agent-reported tests (not independently executed by manager):** Unity EditMode **48/48 PASS**, PlayMode **3/3 PASS**; Burst compilation/negative-control and Unity metadata audit reported in PR #11.
- **Finding / follow-up #12:** `Allocator.Temp` Native containers in OnUpdate and the NativeArray created by `ResetForBattle` are not explicitly disposed. Unity Temp memory is automatically reclaimed, so a leak was **not proven**. Explicit lifetime/disposal and catch-up stress review recommended **before benchmark #7**, not blocking bootstrap #5.
- **Cross-module contract for #5:** call `ResetForBattle` after old actor cleanup and before spawning next session, at a safe tick boundary; pass **current** `SimulationClock.Tick`, not a future tick. `CombatantId` uniqueness must be enforced by the spawner over World lifetime; duplicate live IDs are not detected by the command system. Queue's `LastProcessedTick` is a World command-processing counter, not relative battle time.
- **Residual risks:** producer-order first-wins requires explicit scheduling by the future input/AI adapters; 400-actor performance remains unprofiled; derived fresh-direction key edges for dodge are Stage 02+ and not implemented yet.
- **Other independent follow-up:** PowerShell test runner issue #10, no ECS runtime blocker.

## Stage-specific profiling
- 1v1: response timing, invalid actions, front/back guard, dash, injury capability limits.
- 4v4, 16v16, 50v50, 200v200: sim time/tick, collision pairs, AI decisions, GPU batches, allocations, sprite sorting and camera.
- Navigation terrain edits only after stage 11–13 systems exist.

## Verification log
| Date | Check | Result | Evidence |
|---|---|---|---|
| 2026-10-25 | GitHub App access to new repository | PASS | Repository appears in installation-scoped list |
| 2026-10-25 | GitHub first write | PASS | README commit `5514e8d21c0f3020de93d582fc5daf456a3a0a9e` |
| 2026-10-25 | Unity version and package manifest review | CONFIG VERIFIED | GitHub file contents |
| 2026-10-25 | Entities and Entities Graphics package manifests | CONFIG VERIFIED | 6.5.0 declared and locked in repo |
| 2026-10-25 | Stage 00 smoke test suites and local runner | AUTHORED, NOT RUN | Feature branch `chore/stage-00-ecs-smoke-tests` |
| 2026-10-25 | Static branch audit: test assets, asmdefs, GUIDs, package lock, scene in build settings | PASS (STATIC ONLY) | 19 files inspected, 7 new unique meta GUIDs, 2 EditMode and 1 PlayMode tests authored |
| 2026-10-25 | Unity compiler, EditMode/PlayMode tests | NOT RUN | No Editor process/tool results |
| 2026-10-25 | Delivery without local Unity access | USER-APPROVED POLICY | Static checks required; unrun Unity tests explicitly tracked; delivery and stage verification separate |
| 2026-10-25 | Stage 01.1 merged core ECS contracts (#2 / PR #8) | PASS — STATIC INSPECTION | One runtime asmdef and data-only core contract; manager reviewed committed files; agent reports separate dotnet/reflection validation |
| 2026-10-25 | Stage 01.1 Unity Editor tests | NOT RUN BY MANAGER | PR #8 reports compile against DLLs, not execution inside Unity Editor |
| 2026-10-25 | Stage 01.2 PR #9 fixed-step architecture | PASS — STATIC INSPECTION | One clock writer, one fixed-step rate owner, ordered empty phase groups, World-scope lifecycle |
| 2026-10-25 | Stage 01.2 EditMode 15/15 and PlayMode 2/2 | AGENT-REPORTED PASS | Test execution described in PR #9, manager did not inspect raw logs |
| 2026-10-25 | Battle restart decision | MANAGER DECISION | Keep World and monotonic clock; battle start offset and queue cleanup, see DECISIONS.md and Issues #4–#6 |
| 2026-10-25 | Existing Windows test runner issue | OPEN / NON-BLOCKING | Issue #10: process waiting for Unity.exe |
| 2026-10-25 | Stage 01.3 command processor PR #11 | PASS — MANAGER STATIC REVIEW | One queue/consumer, documented command classification, no new clock or duplicated movement |
| 2026-10-25 | Stage 01.3 Unity EditMode 48/48, PlayMode 3/3 | AGENT-REPORTED PASS | PR #11 report; raw Unity logs not inspected independently by manager |
| 2026-10-25 | Commands temporary Native allocations | FOLLOW-UP / NON-BLOCKING | Issue #12; explicitly dispose where appropriate, before benchmark #7 |
