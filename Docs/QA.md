# MLF Legends — Quality assurance and evidence

**Scope note:** GitHub repository and serialized Unity project settings can be reviewed, but compilation or play-mode execution has **not** been performed by this assistant. Never confuse configuration inspection with passed Editor tests.

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
- [ ] Execute test suites successfully in Unity Editor; capture results XML/Console.
- [ ] Open a gameplay smoke scene and check Console/Play Mode.
- [ ] CI remains pending local tests and a viable Unity license/runner strategy; no GitHub Actions workflow currently configured.
- [ ] Log test evidence; only then promote Stage 00 to VERIFIED.

## How to run tests (Windows; Unity Editor must be closed)

In Unity GUI: open **Window > General > Test Runner**; run **EditMode** tests, then **PlayMode** tests. Use Unity 6000.5.4f1.

In PowerShell from repository root:

```powershell
.\Tools\Run-UnityTests.ps1 -EditorPath 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' -Mode EditMode
.\Tools\Run-UnityTests.ps1 -EditorPath 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' -Mode PlayMode
```

The script requires a nonzero NUnit test count with all tests passing; it saves XML and Editor log to ignored `TestResults/`. Command-line testing can require an available Editor license. If either suite fails, send the Unity Console/Editor log and test result XML; do not mark stage 00 complete.

## Required review/check method
1. Inspect code and existing callers before modifying.
2. Search for competing authoritative processors and duplicate functionality.
3. Trace effects: input → commands → actor state → collision → attack/guard → injury → render and AI.
4. Cover edge cases, interruptions, cleanup, multi-hit and simultaneous events.
5. Separate EditMode automated tests from PlayMode/full integration tests; list NOT RUN clearly.
6. Use seed-controlled replay where possible; disclose limits of deterministic floating-point across platforms.
7. Record CPU/GPU profiles including actor count, environment, target hardware and frame/tick timing.
8. Write regression findings with test command/Unity version/commit SHA.

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
