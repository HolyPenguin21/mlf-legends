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

The script waits for the `Unity.exe` process itself (default timeout 60 min, `-TimeoutMinutes` to change), deletes the previous XML/log first, and prints `PASS` only when Unity exits with code 0 **and** a fresh NUnit `/test-run` XML has `total > 0`, `failed = 0`, `passed = total`. Every other outcome (missing or version-mismatched Editor, project open in Unity, timeout, crash, missing/invalid XML) prints `FAIL:` with diagnostics and exits with code 1. It saves XML and Editor log to ignored `TestResults/` (`stage00-editmode.*`, `stage00-playmode.*`). Paths with spaces are supported; a project path with non-ASCII characters (for example a Cyrillic user profile) made the Burst `ResetForBattle` EditMode test fail during Issue #10 validation, so keep the project under an ASCII path. Command-line testing can require an available Editor license. **Running these commands is optional for delivery**; when unavailable, keep Unity tests marked NOT RUN and continue via static review. If either suite fails when eventually executed, log the failure and fix it; do not mark the stage VERIFIED before actual evidence.

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

## Stage 01.4 code review (Issue #5 / PR #13; 2026-10-25)
- **Delivery: PASS.** PR #13 merged in `main` at `e64976f91ab032c6af00c900b9c0334834c89bc2`; #5 closed. Static review covered `BattleLifecycleSystem`, `BattleSession`, `CombatantIdAllocator`, `CombatSmokeBootstrap`, `BOOTSTRAP.md`, and test source including editor and PlayMode scene swap.
- **Ownership/flow: PASS (STATIC).** The lifecycle system is the only spawner/despawner, owns per-World non-reused `CombatantId` and `BattleSession`; uses the existing `SimulationClock` unchanged and calls the existing `CombatCommandQueue.ResetForBattle`, no duplicate command processor. Spawns identical archetypes with valid Team/Position/Facing and enabled `InCombat`. Owner-scoped End prevents a previous scene's late disable from killing the replacement scene's battle.
- **Agent-reported test execution:** Unity 6000.5.4f1 batchmode EditMode **71/71 PASS**, PlayMode **6/6 PASS** with real scene switch, plus Burst negative controls / GUID checks, as described in PR #13. Manager did not receive XML logs or run Unity independently.
- **Important follow-up #14:** End sent from inside a fixed step is currently processed next render-frame before the next fixed-step group, letting remaining catch-up ticks of that frame execute old-session logic. Harmless in current no-attacks prototype but **must be corrected before gameplay consequences**. Ensure same lifecycle owner and no duplicate tick processor.
- **Atomicity follow-up #14:** In `Start`/`End`, prior actors are destroyed before `ResetForBattle` validates the queue boundary. Current caller passes current clock tick so a failure is not reachable under contract, but any future erroneous boundary can leave a partial cleanup; require a preflight before destructive operations.
- **Domain Reload disabled:** Unity Enter/Exit Play Mode with domain reload turned off was not exercised, since the agent's tests do not toggle that editor setting. Record as **NOT RUN**; include manual QA scenario in #14, do not claim a regression.
- **Core documentation mismatch: RESOLVED.** Manager updated `Core/CombatantId.cs` XML summary and `Core/CONTRACTS.md` to state IDs are unique **within the World** and never reused across battle restarts. No code or public API behavior changed.
- **Remaining integration boundaries:** `BattleSession.LastOutcome` reports last request in a batch rather than a per-request ledger; intentional current lightweight diagnostic. Requests currently only apply at frame boundaries (superseded by #14: start of every fixed step), and `CombatSmokeBootstrap` may hold a static *owner token* but no static combat entities/state. Profiling 400 actors (#7) must reuse the real spawn/ID allocator path.

## Stage 01 stabilization — Native lifetime review (Issue #12 / PR #15; 2026-10-25)

- **Delivery: PASS.** PR #15 merged into `main` at commit `4ccc4222c58307ca9164f453de9c8d7bbb2ef1b9`; Issue #12 closed.
- **Manager static review: PASS.** Six native Temp scratch containers in `CombatCommandProcessorSystem.OnUpdate` now have explicit end-of-use `Dispose`; previous over-reservation of the processed-command list was removed. All three ephemeral `EntityQuery` objects built in `CombatCommandQueue.ResetForBattle` now have matched `Dispose`, including explicit validation-failure branches; reset scratch array is disposed too. No duplicate queue, additional time source, altered priority rules or changes to Bootstrap.
- **Agent-reported execution:** Unity 6000.5.4f1 EditMode **72/72 PASS**, PlayMode **6/6 PASS**, including a new catch-up/400-combatant scenario with up to 12,800 pending commands across several 16-fixed-tick frames. Test XML reported in PR but not independently reviewed or executed by manager.
- **Important allocation nuance:** native `Allocator.Temp` is automatically reclaimed at frame end; explicit `Dispose` is good resource-lifetime hygiene but does **not** establish that the peak per-frame Temp memory is reclaimed sooner. Persistent `EntityQuery` retention through World lifetime was the substantive corrected defect; measurements are deferred to Issue #7.
- **Non-blocking follow-up for Issue #14:** `BattleRequest.Submit` (`Bootstrap/BattleSession.cs`) and `CombatSmokeBootstrap.TryGetEntityManager` still construct persistent `EntityQuery` instances and do not dispose them. A scoped improvement is recorded as a comment on Issue #14, to be addressed in its existing Bootstrap-owned correction PR.
- **Next in stabilization: Issue #10**, dedicated Windows PowerShell test runner process-wait fix, before full Stage 01 replay and performance tests.

## Stage 01 stabilization — Unity batch test runner (Issue #10 / PR #16; 2026-10-25)

- **GitHub delivery: PASS.** PR #16 merged into `main` at commit `7e6238bc94cf9a12ad5b17d76c9af475edad76df`; Issue #10 closed.
- **Manager static inspection:** reviewed all changes in `Tools/Run-UnityTests.ps1` and `Docs/QA.md` and verified no Unity ECS runtime/packages/test assemblies changed. Process launch uses `System.Diagnostics.Process` with explicit PID waiting and timeout rather than direct `& Unity.exe`; arguments are quoted, project version/lockfile checked; previous XML/log deleted; new `/test-run` XML must contain positive total and all passed plus exit 0. Failed run, timeout and missing XML produce nonzero script result.
- **Agent-reported Windows checks:** PowerShell 5.1 and Unity 6000.5.4f1: EditMode **72/72 PASS**, PlayMode **6/6 PASS**, spaced ASCII project/editor paths **72/72 PASS**, mocked negative controls for invalid/missing XML, nonzero process exits, locked project, and timeout all correctly FAIL. Results reported in PR #16; manager did not personally run Unity.
- **Nonblocking defect (Issue #17):** Under a Windows project path containing Cyrillic characters, one Burst-related test failed (EditMode **71/72**, `ResetFromABurstSystemInTheCollectionPhaseRejectsThatTicksCommands`). Script properly returned FAIL. Root cause is **unknown**, do not call it a proved Burst engine bug. Working practice meanwhile: ASCII project path; investigate later with environment and logs. Tests under Cyrillic path are **not** PASS.
- **Next implementation: Issue #14**, battle lifecycle timing/atomicity and Bootstrap native query cleanup; #6 replay tests and #7 benchmarking remain pending its review.

## Stage 01 stabilization — battle lifecycle hardening (Issue #14 / PR #18; manager reviewed, MERGED)

- **Change:** `BattleLifecycleSystem` moved from `SimulationSystemGroup` (once per frame, before the fixed group) to the start of every fixed step, before `CombatClockSystem`; same single owner, clock and standard `FixedStepSimulationSystemGroup`. `Start`/`Restart`/`End` call the queue's own `CombatCommandQueue.CanResetForBattle` (shared checks with `ResetForBattle`) before destroying combatants and otherwise return `CommandQueueRejected` with no side effect. `BattleRequest.Submit` and `CombatSmokeBootstrap.TryGetEntityManager` dispose their `EntityQuery`.
- **Behavior change to review:** requests now apply at the start of the next fixed step, not the next render frame. Frames without a fixed step (`Time.timeScale = 0`, frame rate above 60 Hz) apply nothing until the next step; tick timeline unchanged. PlayMode tests wait for the next tick instead of one frame.
- **Agent-run (Unity 6000.5.4f1, batchmode, ASCII project path, `Tools/Run-UnityTests.ps1`):** EditMode **82/82 PASS** (72 existing + 10 new), PlayMode **6/6 PASS**. Negative control (lifecycle removed from the fixed step in the test world = old frame-boundary owner, and `CanResetForBattle` check removed from `Start`): 9 of the new tests FAIL as expected, e.g. "No combatant of the ended battle on tick 4" and `ArgumentOutOfRangeException` after the destroy. Manager did not run these.
- **Query lifetime:** no leak metric measured; covered by a 50× `Restart`/`End` regression sequence through `Submit` and the existing scene-switch PlayMode test.
- **Domain Reload disabled (manual):** checklist in `Assets/MLFLegends/Combat/Runtime/Bootstrap/BOOTSTRAP.md`, section "Manual check". **NOT RUN** (batchmode agent session; Enter Play Mode settings were not toggled). Non-blocking.
- **Manager static review (2026-10-25): PASS.** PR #18 merged at `6983ad874e604f9a22baeadc2b63b8c6b7b23ad7`, Issue #14 closed. Inspected runtime lifecycle, shared command queue `CanResetForBattle` validator/`ResetForBattle`, `BattleSession`, scene Bootstrap, clock/phase scheduling, integration tests and documentation. No second clock, processor, lifecycle owner or scene-specific fighter factory. In-step End is handled before the following catch-up step, with Session relative first tick 1 preserved. Queue rejection is validated before battle entities/ID allocator are mutated. Existing owner-scoped scene switch behavior maintained.
- **Residual limitations (nonblocking):** during `Time.timeScale=0` no fixed tick applies End; Domain Reload disabled smoke scenario is NOT RUN. Both tracked in deferred [Issue #19](https://github.com/HolyPenguin21/mlf-legends/issues/19), to resolve before pause UI / menu navigation. Recording ECB work to `EndSimulationEntityCommandBufferSystem` for live combatants is unsafe across multiple catch-up ticks, as documented in `BOOTSTRAP.md`; future Stage 02+ systems should use fixed-tick ECB APIs and test against despawn.
- **Next: Stage 01.5 / Issue #6** deterministic replay and integration verification; available code/test evidence is limited to current clock, lifecycle and command behavior (no RNG, movement, attack or physics yet).

## Stage 01.5 integration and replay tests (Issue #6 / PR #20; MERGED, manager-reviewed)

- **Scope: tests only.** New `Assets/Tests/EditMode/Combat/`: `CombatReplayDriver.cs` (test driver) and `CombatReplayTests.cs` (16 test cases). No runtime file, asmdef or existing test changed; no production system, RNG or game mechanic added.
- **Driver:** isolated `World` built by `DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups` from the real runtime systems, i.e. ordered by their own attributes as in the default world (not a hand-made order). Test-only systems: player and AI producers in the collection phase (same `CombatCommandSubmission` buffer, player first), a read-only recorder in the publication phase, optional in-step `BattleRequest` requesters per phase. Every completed tick is recorded canonically (combatants sorted by `CombatantId`, never by query/chunk order): ID/team/position/facing/`InCombat`, `AppliedCombatCommand`, `CombatCommandResult`, pending commands. Two forms: raw (absolute ticks, IDs) and normalized (roles H/A/foreign/invalid, `relativeTick = Tick - BattleStartTick`, results re-ordered in role space because processor order follows raw IDs that differ between battles).
- **Scenarios:** declared system order; player and AI mirror scripts give identical outcomes through one queue; an 8-tick command matrix with exact expected outcomes per tick (accepted, missing/implicit None, duplicate, conflict incl. cross-producer, stale, before battle start, deferred, too far ahead, pending duplicate/conflict, despawned combatant, previous battle's ID); two in-step restarts inside one 5-tick catch-up frame from each phase; two sequential battles in one World (normalized logs equal, entity count back to baseline, no queue leftovers, IDs never reused, battle-1 pending command falling on battle-2 tick 1 dropped); 4-battle timeline with Start/Restart/in-step End/in-step Restart (monotonic absolute tick, relative ticks from 1, idle ticks); fresh-World raw identity; frame pacing 1/1/…, 5-3, 2-3-3, 1-4-1-2 gives identical raw records; rejected requests (AlreadyActive, NotOwner, InvalidSetup, unknown Despawn) leave the raw record unchanged; owner-scoped late End on scene switch; two interleaved Worlds + solo reference, disposal of one World, default world untouched.
- **Determinism limits:** there is no seed consumer in Stage 01 (no RNG, movement, attack, AI); the seed is not part of the record and "reproducible" covers only the current clock/command/lifecycle state on one machine and Unity version. Cross-platform float determinism is not addressed (positions are only copied, never computed).
- **Documented exception (asserted, not hidden):** a producer that runs once per render frame and targets `Tick + 1` covers fewer ticks with catch-up frames (6 vs 2 submitted commands over 6 ticks); the same producer targeting explicit future ticks is pacing-independent. Future input/AI adapters that must not depend on frame rate belong in the collection phase.
- **Agent-run (Unity 6000.5.4f1, batchmode, ASCII path `D:\Unity\Project\mlf-legends`, `Tools/Run-UnityTests.ps1`):** EditMode **98/98 PASS** (82 existing + 16 new), PlayMode **6/6 PASS**. Negative controls (temporary runtime mutations, reverted, not committed): `ResetForBattle` keeps pending commands → 14 failures, 5 of them new replay tests; `BattleLifecycleSystem` moved after `CombatClockSystem` → 23 failures, 15 of 16 new tests. Manager has not run these.
- **Not covered / NOT RUN:** no-domain-reload Play Mode and `Time.timeScale = 0` remain in #19; no PlayMode replay test was added (default-world behavior is covered by the existing PlayMode suite); 400-combatant scale is #7.
- **Manager review / delivery confirmation:** [PR #20](https://github.com/HolyPenguin21/mlf-legends/pull/20) is merged to `main` at `41a8540c97aaca0c8c1c4354558abf33db9224a7`; Issue #6 closed. Inspected all production/test-file changes: two new test-only scripts under `Assets/Tests/EditMode/Combat/`, matching .meta; **no** production ECS class, clock, queue, movement or AI changed. Checked script command producers, root system group creation, canonically sorted snapshots, same-World relative replay, two-World isolation, concrete postcondition assertions and catch-up pacing differences. Agent reports EditMode **98/98 PASS**, PlayMode **6/6 PASS**, two mutation/negative controls. These runs were **not** independently executed by manager.
- **Accepted determinism boundary:** no production RNG or seed consumer exists in Stage 01, so no fake seeded game state was authored; deterministic claim is only repeatability of existing ECS clock/lifecycle/command state within same environment and equal explicitly tick-indexed inputs. Render-frame sampled producer (`Tick + 1`) differs across catch-up frame rates and is tracked in Stage 02 [Issue #22](https://github.com/HolyPenguin21/mlf-legends/issues/22).
- **Benchmark preparation:** test recorder builds arrays, string snapshots and LINQ on every tick. It is valuable for assertions but **invalid to include in measured ECS hot-path timings**; Issue #7 uses its own lean timing harness and existing single runtime lifecycle spawner/allocator, with minimal bootstrap extension if required. No actual 100/400 performance measurements exist yet.


## Editor error "Host type is not matching any asset type … TraceVirtualOffset.urtshader" (2026-10-25; RESOLVED locally)

- **Symptom:** on every Editor start and batchmode run since ~15:52: `Host type is not matching any asset type at Path Packages/com.unity.render-pipelines.core/Editor/Lighting/ProbeVolume/VirtualOffset/TraceVirtualOffset.urtshader`, logged by `RenderPipelineResourcesEditorUtils.TryReloadContainedNullFields` while URP populates `ProbeVolumeBakingResources`. Not present in the Issue #12 and earlier logs; tests still passed.
- **Root cause (local `Library/`, not repository content):** the import results of all 16 Core RP `.urtshader` files were **failed imports** (`DirectoryNotFoundException`) made while the project was located at a Cyrillic scratchpad path (`C:\Users\<Cyrillic>\…\scratchpad\mlf legends proj\`). The import is cached by dependency hash, which does not include the absolute project path, so Unity kept reusing the failed artifacts: each `.urtshader` loaded as `DefaultAsset` with only an `ImportLog`, the `ComputeShader`/`RayTracingShader` references in `UniversalRenderPipelineGlobalSettings.asset` resolved to null, and the reload by `[ResourcePath]` failed. Only the first field (`traceVirtualOffsetCS`) is reported. Same family as Issue #17 (Unicode project path).
- **Fix:** force-reimport every `*.urtshader` (`AssetDatabase.ImportAsset(path, ForceUpdate)` from a temporary batchmode editor method, not committed; in the Editor: *Project > Packages > Core RP > right click > Reimport*). Before: 16 assets with import errors out of 11202; after: 0. No package, settings reference or code change needed.
- **Verified (agent-run, Unity 6000.5.4f1, `D:\Unity\Project\mlf-legends`):** EditMode **98/98**, PlayMode **6/6**, 0 occurrences of the message in both logs; reproduced and fixed on a disposable copy first.
- **Prevention:** do not open, copy or test the project with its `Library/` under a non-ASCII path. If the message comes back, reimport the Core RP package folder or delete `Library/` (full reimport).
- **Editor data upgrades committed together:** URP global settings `m_RuntimeSettings`, `PC_RPAsset` shader prefiltering, `DefaultVolumeProfile` new fields, Standalone batching entry, new `EntitiesClientSettings.asset` and `SceneTemplateSettings.json`. These are automatic migrations by Unity 6000.5.4f1 / URP 17.5 on project open, unrelated to the error.

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
| 2026-10-25 | Stage 01.4 lifecycle PR #13 | PASS — MANAGER STATIC REVIEW | World preserved, clock monotonic, same command reset API, two clean combatants, owner-gated End |
| 2026-10-25 | Stage 01.4 Unity tests: EditMode 71/71, PlayMode 6/6 | AGENT-REPORTED PASS | PR #13 report; manager did not independently execute Unity tests |
| 2026-10-25 | No-domain-reload Play Mode scenario | NOT RUN | Manual scenario required; Issue #14 |
| 2026-10-25 | Catch-up End timing and reset atomicity | FOLLOW-UP / NON-BLOCKING FOR #5 | Issue #14, schedule before integration tests #6 |
| 2026-10-25 | Core CombatantId scope mismatch | RESOLVED — DOCUMENTATION | Updated class XML comment and Core/CONTRACTS.md to World-scoped uniqueness |
| 2026-10-25 | Stage 01 stabilization #12 merged | PASS — MANAGER STATIC REVIEW | PR #15; Temp scratch disposal and persistent query disposal; no ECS architecture changes |
| 2026-10-25 | Stage 01 stabilization Unity tests EditMode 72/72, PlayMode 6/6 | AGENT-REPORTED PASS | PR #15; raw XML and Unity execution not independently verified |
| 2026-10-25 | Bootstrap transient EntityQuery lifetime | FOLLOW-UP / NON-BLOCKING | Added to Issue #14 (BattleSession / CombatSmokeBootstrap) |
| 2026-10-25 | Unity test runner #10 | MERGED / MANAGER STATIC REVIEW | PR #16; PowerShell Process wait, fresh XML/exit checks; no gameplay changes |
| 2026-10-25 | Test runner supported-path execution | AGENT-REPORTED PASS | Unity EditMode 72/72, PlayMode 6/6, ASCII paths incl. spaces |
| 2026-10-25 | Burst regression on Cyrillic project path | AGENT-REPORTED FAIL / FOLLOW-UP | EditMode 71/72, one Burst-test failure, Issue #17; root cause unknown |
| 2026-10-25 | Battle lifecycle hardening | NEXT / OPEN | Issue #14, then #6 integration replay and #7 performance |
| 2026-10-25 | Issue #14 Unity tests EditMode 82/82, PlayMode 6/6 + negative control | AGENT-REPORTED PASS | Branch `feature/stage01-14`; manager has not executed or inspected raw XML |
| 2026-10-25 | Issue #14 no-domain-reload manual Play Mode checklist | NOT RUN | Checklist in Bootstrap/BOOTSTRAP.md |
| 2026-10-25 | Issue #14 integrated lifecycle fix | PASS — MANAGER STATIC REVIEW / MERGED | PR #18 commit 6983ad8; one fixed-step lifecycle, queue preflight, query disposal, shared-clock semantics |
| 2026-10-25 | Issue #14 Unity 82/82 EditMode and 6/6 PlayMode | AGENT-REPORTED PASS | PR #18, negative controls reported; manager did not execute Unity or inspect raw logs |
| 2026-10-25 | End at Time.timeScale=0 / Domain Reload disabled | DEFERRED / NOT RUN | Issue #19; no pause UI exists yet; do not imply verified |
| 2026-10-25 | Stage 01.5 replay tests | NEXT / READY | Issue #6, new manager handoff after PR #18 review |
| 2026-10-25 | Issue #6 Unity tests EditMode 98/98, PlayMode 6/6 + 2 mutation controls | AGENT-REPORTED PASS | Branch `feature/stage01-6`; manager has not executed or inspected raw XML |
| 2026-10-25 | `.urtshader` "Host type is not matching" Editor error | RESOLVED LOCALLY / AGENT-RUN | Failed imports cached from a Cyrillic-path copy; force reimport 16 → 0 import errors; EditMode 98/98, PlayMode 6/6, message gone |
| 2026-10-25 | Stage 01.5 replay PR #20 | PASS — MANAGER STATIC REVIEW / MERGED | 16 tests and test-only isolated World driver; no runtime duplication; merge 41a8540 |
| 2026-10-25 | Stage 01.5 EditMode 98/98, PlayMode 6/6, negative controls | AGENT-REPORTED PASS | Tests and controls reported by PR #20, manager did not execute Unity |
| 2026-10-25 | Rendering/editor serialized upgrades PR #21 | MERGED / AGENT-REPORTED PASS | Local cache force-reimport 16 shaders, 0 import errors; runs 98/98 and 6/6; not a fix proven for Unicode Burst #17 |
| 2026-10-25 | Per-render-frame command producer pacing | NONBLOCKING / STAGE 02 | Replay test documents 6 command deliveries vs 2 in catch-up, Issue #22 |
| 2026-10-25 | Stage 01.6 performance baseline 2/100/400 | NEXT / NOT YET MEASURED | Issue #7, real lifecycle spawner, no replay recorder allocations in timed region |
