# Issue #17 — Windows project paths and Burst

## Status and evidence boundary (2026-10-26)

**INCOMPLETE: Windows reproduction blocked by the available environment. Root cause UNKNOWN.**
This is a diagnostic handoff, not a fix or a confirmed Unity/Burst limitation. Do not close
[#17](https://github.com/HolyPenguin21/mlf-legends/issues/17) or advance the Stage 01 debt gate on this report.

Inspected `main` **7c17081f76f1425f1ed5a45c176b71290649146d**, Issue #17 and its three
manager comments, PRs #16/#21, QA and the actual test/runtime/runner sources.
The checkout specifies Unity **6000.5.4f1 (d550df8bd089)**, Entities **6.5.0**, Burst
**1.8.29**, URP/Core RP **17.5.0**, Test Framework **1.7.0**. Preserve these versions.

The current host is Linux x86_64. No Unity Editor, Windows host/mounted Windows installation,
PowerShell, .NET SDK or Mono compiler was found in the inspected executable/install locations.
There are no committed original Editor logs, NUnit XML or Burst compiler logs in this checkout.
The connected repository's Actions run list returned zero runs; no CI result substitutes for reproduction.
Historical results below are **previous agent reports**, not newly executed or independently verified results.

| Evidence | Reported result | What it establishes |
|---|---|---|
| [PR #16](https://github.com/HolyPenguin21/mlf-legends/pull/16), Windows 11 / PowerShell 5.1 | ASCII EditMode 72/72, PlayMode 6/6 | Historical working configuration |
| PR #16, spaces in project and Editor paths **via junctions** | EditMode 72/72 | Spaces through those aliases worked; not a controlled physical-copy comparison |
| PR #16, Cyrillic profile + spaces | EditMode 71/72; reset probe assertion failed | Managed probe execution observed while Burst was enabled; cause not diagnosed |
| [PR #21](https://github.com/HolyPenguin21/mlf-legends/pull/21), ASCII project | 16 failed `.urtshader` imports → 0 after force reimport; EditMode 98/98, PlayMode 6/6 | Recovery of that local import state; no Unicode Burst retest |
| [PR #24](https://github.com/HolyPenguin21/mlf-legends/pull/24), ASCII project | EditMode 114/114, PlayMode 6/6 | Historical current-suite baseline; not this branch's result |

## What the current code does

`CombatCommandProcessorTests.ResetFromABurstSystemInTheCollectionPhaseRejectsThatTicksCommands`
creates an isolated World, submits commands, and inserts `BurstBattleResetProbe` between
the test producers and the real command processor. Its `OnUpdate` already has
`[BurstCompile(CompileSynchronously = true)]`. On tick 2 it calls the real
`CombatCommandQueue.ResetForBattle`, sets `Done = 1`, calls `[BurstDiscard] MarkManaged`
and stores the request. That discarded method writes `RanManaged = 1` only on the managed path.

The assertion `RanManaged == 0` runs when `BurstCompiler.IsEnabled` is true. Its historical
failure indicates managed execution, **not** why compilation/dispatch fell back. A run with
Burst disabled is not acceptable evidence for this issue even if the existing test passes.
The reset method clears canonical command buffers and applied state; it does no path/file IO.
Static inspection cannot exclude compiler/source-generation/dispatch or environment defects.

The diagnostic change adds only managed `TestContext.WriteLine` output before probe creation
and before assertions: Editor version, `Application.dataPath`, current working directory,
Burst enabled state, `Done` and `RanManaged`. Collect the NUnit test-case output as well as
Editor logs. No assertion, probe body, Burst option, runtime code, package or runner changes.

**URP relationship remains unproved.** PR #21 describes cached failed imports produced in a
Cyrillic-path copy. That does not establish whether a common path failure also affects Burst,
whether imports prevent Burst initialization, or whether they are independent. No upstream
bug is identified as the cause of #17.

## Controlled Windows reproduction — NOT RUN here

Use one Windows machine, the same user/session, the exact Editor above and identical package
lockfile. Keep Burst compilation enabled throughout. Before any cleanup, preserve the original
failing project, its Library, logs and settings outside the project. Never share a Library between paths.

1. Record OS build, CPU, Windows display language and system locale, active UTF-8 system-locale
   option, PowerShell version, Editor executable path/revision, package versions, and the values
   of `USERPROFILE`, `TEMP`, `TMP` and relevant `UNITY*`/`BURST*`/`UPM*` cache overrides (redact secrets).
   Record Asset Import/Accelerator settings, Burst menu options and resolved package/cache paths.
   Do not change locale, user profile, temp location or caches in the initial comparison.
2. Prepare three **physical, independent** Git clones on the same local volume, not junctions,
   symlinks or `subst`: `D:\MLF-QA\mlf-ascii`, `D:\MLF-QA\mlf spaces`,
   `D:\MLF-QA\млф проект`. Record full paths and lengths; if only Unicode fails, also repeat
   with a similarly long ASCII path to separate character content from path length.
3. Check out the exact same commit in all three and record `git rev-parse HEAD`, `git status
   --porcelain`, and SHA256 of `Packages/manifest.json`, `Packages/packages-lock.json` and
   `ProjectSettings/ProjectVersion.txt`. First reproduce on the inspected main SHA; then use
   the diagnostic PR head (record its actual SHA) in all three copies to obtain probe output.
   Do not compare one path's older main against another path's PR head.
4. Close all Editors using those projects. For each path run the targeted test first, before
   any full suite or reimport; archive XML/logs immediately. Run once on the fresh clone
   (no Library) and again after restarting the Editor with that Library unchanged.
   Repeat the comparison in reverse path order to check process/global-cache order effects.
5. Retain a separately reproduced failing-state copy for each intervention below. A full
   Library reset changes multiple caches and cannot by itself identify which cache caused a failure.

### Targeted command

Use **Windows Command Prompt** (not PowerShell) for this example. Create a new, empty output
directory for each path/condition/attempt first. Replace project/output paths consistently.
`start /wait` waits for the actual Editor process; retain its exit code and validate XML.

```bat
mkdir D:\MLF-QA\evidence\ascii-cold-01
start "" /wait "C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe" -batchmode -nographics -projectPath "D:\MLF-QA\mlf-ascii" -runTests -testPlatform EditMode -testFilter "^MLFLegends\.Tests\.EditMode\.CombatCommandProcessorTests\.ResetFromABurstSystemInTheCollectionPhaseRejectsThatTicksCommands$" -testResults "D:\MLF-QA\evidence\ascii-cold-01\target.xml" -logFile "D:\MLF-QA\evidence\ascii-cold-01\Editor.log"
echo UnityExit=%ERRORLEVEL%
```

Do not add `-quit` or Burst-disabling arguments. Do not infer success from exit code alone:
require fresh NUnit XML with the **exact target test**, total=1, passed=1, failed=0 and no
skip/inconclusive result, plus exit=0. On the diagnostic head require `BurstEnabled=True`
at both output points, `Done=1`, `RanManaged=0`. Missing output or a hang is not PASS.
Archive stdout/exit status; if no XML exists, retain Editor/compiler logs and report run error.
Cross-check a failing command-line case by selecting that same test in the Editor Test Runner
on the **same physical path**, recording launch route, actual dataPath and working directory.
GUI/CLI disagreement warrants a controlled launch/quoting comparison, not an ECS change.

`Tools/Run-UnityTests.ps1` has only `-EditorPath`, `-Mode`, `-TimeoutMinutes` and derives the
project root from its own location. **It has no `-TestFilter`.** Use it unchanged for full suites
from each clone, with the Editor closed. Archive its `TestResults/stage00-editmode.*` before
another EditMode run overwrites them; do the same for PlayMode outputs.

```powershell
.\Tools\Run-UnityTests.ps1 -EditorPath 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' -Mode EditMode
.\Tools\Run-UnityTests.ps1 -EditorPath 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' -Mode PlayMode
```

### Separate the cache interventions

| Condition | Change from a preserved, reproducible state | Evidence required |
|---|---|---|
| Existing Library | None; restart Editor | Preserve original logs/artifacts before repair |
| Clean Library | With Editor closed, move Library outside project and reopen | Record global cache/Accelerator state; clean Library is **not** proof of clean global caches |
| Warm Library | Restart without cleanup after the clean run | Cold/warm difference with unchanged source/settings |
| Burst cache only | Identify the JIT cache from installed Burst 1.8.29 source/compiler logs, archive it, close Editor, move only that cache aside | Actual cache path and evidence it was rebuilt; retain asset import state; if not independently isolatable, say so |
| Core RP imports only | In the failing path's Editor, Project > Packages > Core RP > Reimport; then rerun target | Per-asset import errors/types before/after, exact assets reimported, compiler output and probe result; note any incidental Burst rebuild |
| Global import/cache source | If implicated, repeat with Accelerator download disabled and an isolated documented package-cache location, one change at a time | Resolved cache locations/settings before/after; do not clear unrelated projects' global caches |

For import diagnostics inspect **all** Core RP `*.urtshader` assets, not just the first
`TraceVirtualOffset` message. Record imported types and per-asset errors using the installed
Editor's import diagnostics; zero occurrences of one Console message is not zero failed imports.
Preserve Asset Import Worker logs (`Logs/`), import diagnostics and Burst compiler diagnostics
at their actual emitted paths alongside the complete Editor log. A Burst-cache intervention that
also reimports assets, or a reimport that recompiles Burst, is a confounded run; repeat independently.

After each isolated intervention rerun the target. After a demonstrated fix/workaround, run
the full current EditMode and PlayMode suites on the chosen configuration and the target again
under Unicode. Record actual discovered totals, not assumed historical counts (114 and 6).

### Results on this investigation's host

All entries below are **NOT RUN**, not failures or successes. No Editor process was launched.

| Physical Windows path | Existing Library | Clean Library | Warm Library | Burst cache only | Core RP reimport | Full EditMode | Full PlayMode |
|---|---|---|---|---|---|---|---|
| ASCII | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NOT RUN |
| ASCII + spaces | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NOT RUN |
| Cyrillic + spaces | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NOT RUN |

For each eventual result attach: commit, path/condition/attempt, UTC timestamps, launch route
and arguments, working directory, environment/settings, resolved versions, exit code, XML,
both probe outputs, per-asset import-error count (or NOT MEASURED), first Burst error with full
stack/inner exception, cache actions, and SHA256 of evidence files. Keep original logs; use
redacted copies when sharing personal profile paths or environment data.

## Interpretation and remaining gate

- Only a controlled intervention which changes the result and is repeatable can establish a cause.
  A clean-Library success alone establishes dependence on local state, not a specific Burst defect.
- If shader imports recover but the probe still fails, repairing those imports is insufficient
  to repair the Burst failure. If both recover together, that is correlation until isolated.
- If Unicode fails consistently with clean caches, correct incoming paths, Burst enabled and
  a captured native/compiler path error, reduce to a disposable minimal Unity project with
  a Burst-discard sentinel before attributing the limitation to Unity/Burst. No such minimal
  repro has been executed here. Retain the original project as the regression test.
- Interim working practice from previous reports: fresh checkout on a physical ASCII path,
  rebuild Library there, and verify Burst stays enabled. Reimport Core RP there if the historical
  cached-import symptom occurs. **Not newly verified, not Unicode support and not a confirmed
  engine workaround.** A junction alone does not prove Unicode paths are supported.

No runtime fix is justified by available evidence. Windows/Unity compilation and tests, cache
isolation, the original failure and its relationship to URP remain outstanding. Manager/owner
disposition is still required before closing this debt; delivery of this PR does not satisfy it.

## Checks actually performed

- PASS: `git diff --check` and local Markdown link/fence/conflict-marker checks.
- PASS: source comparison against inspected main after removing only the two diagnostic writes
  and their comment is byte-for-byte equal (UTF-8 text). Assertions, conditional Burst check,
  probe and behavior are preserved; no new tests or assemblies were added.
- PASS: package-lock version checks and changed-file scope review. Runtime, clock, spawner,
  performance harness, packages, project settings, asmdefs, test `.meta` and runner are unchanged.
- **NOT RUN:** C# compilation, NUnit/Unity execution and every Windows path/cache experiment.
  Static preservation checks do not demonstrate Burst compilation or a regression-free Editor run.

Reference mechanics: Unity's [Test Framework command-line reference](https://docs.unity.com/en-us/engine/7000.0/manual/programming-environment/test-framework-introduction/reference-command-line)
documents test selection/results and the unsupported `-quit` combination; its current page is
not version-pinned to the project's 1.7.0. Check the installed package documentation if behaviour
differs. Burst's [BurstDiscard reference](https://docs.unity3d.com/Packages/com.unity.burst@1.8/manual/compilation-burstdiscard.html)
documents the managed-execution sentinel technique; the rolling 1.8 docs currently show 1.8.31,
not proof of a defect or fix in the pinned 1.8.29.
