# Issue #41 — correction evidence and required Unity rerun

Base: `a5e29a5fe4619f509b75e9cbce53106437f342d0`. Branch: `fix/38-depth-and-measurement`.
Status: source implementation only, manager acceptance pending. No automatic merge.

| Check | This environment |
|---|---|
| Source review and independent numerical wall/depth-slot check | Performed; not a shader/device test |
| Full EditMode and headless PlayMode | NOT RUN (Unity/dotnet/Mono unavailable) |
| Real D3D12 Graphics and Performance, 2/100/400, both modes | NOT RUN (no Unity/graphics device) |
| Temporary legacy-policy negative control on new GPU wall test | NOT RUN; mutation supplied below |
| New measured GC bytes/samples, CPU split, XML/Editor.log/TSV/readbacks | NOT RUN; no results fabricated |

## Run on the Unity machine

Use a clean ASCII checkout of the exact branch SHA being tested, Unity 6000.5.4f1, current locked packages. Record
`git rev-parse HEAD` before any mutation. Start separate processes, twice per series, and retain each fresh XML and log:

```powershell
$testedSha = git rev-parse HEAD
$evidenceDir = "Docs/Evidence/41/$testedSha"
New-Item -ItemType Directory -Force $evidenceDir
# Full suites (headless skips Graphics/Performance; report pass + skipped counts separately).
& $editor -batchmode -nographics -projectPath $project -runTests -testPlatform EditMode -testResults "$evidenceDir/editmode-run1.xml" -logFile "$evidenceDir/editmode-run1.log"
& $editor -batchmode -nographics -projectPath $project -runTests -testPlatform PlayMode -testResults "$evidenceDir/playmode-run1.xml" -logFile "$evidenceDir/playmode-run1.log"
# Actual graphics device: NO -nographics. Verify Direct3D12/device in Editor.log.
& $editor -batchmode -force-d3d12 -projectPath $project -runTests -testPlatform PlayMode -testCategory "Graphics;Performance" -testResults "$evidenceDir/gpu-run1.xml" -logFile "$evidenceDir/gpu-run1.log"
```

Set `$editor` to Unity.exe and `$project` to the absolute checkout path; use an absolute `$evidenceDir` for Unity CLI
outputs. Wait for each process to exit before the next one. Preserve `TestResults/performance/*stage03-sprites*.tsv`
(summary + both raw modes + controls for 2/100/400) and `TestResults/graphics/41-*.png` into separate `gpu-run1/` and
`gpu-run2/` folders before another run can overwrite them. Repeat the commands with run2 filenames. Record process exit
codes, XML pass/fail/skipped/inconclusive totals, device, profiler controls and any compiler/shader/leak diagnostics.
The old test-runner wrapper expects passed=total and may call intentionally skipped headless cases FAIL: use/read the
actual NUnit XML and do not relabel skipped tests as passed.

## Temporary negative control

`legacy-depth-negative-control.diff` replaces only the depth policy with the shipped global recurrence; it ignores
footprints and has no budget. Shader, wall geometry and independent expected wall colour are unchanged. In a separate
clean checkout of the tested SHA:

```powershell
git apply Docs/Evidence/41/legacy-depth-negative-control.diff
# Same GPU command, but -testFilter "CloseWallOcclusionIsIndependentOfDistantRoster" instead of -testCategory.
# At 400 the body-colour assertion must FAIL: the close wall is crossed. Keep the fresh XML/log/readbacks + this diff.
git apply -R Docs/Evidence/41/legacy-depth-negative-control.diff
```

Then run the identical filter unmutated and require PASS at 2/100/400. Never update the expected colour/image from
calculated offsets. Final full suites and GPU/Performance must run against the unmutated code. Keep negative-control
results under a separate folder bearing the same base tested SHA and the mutation diff; never commit the mutation to runtime.

## Measurement definitions

See PERFORMANCE_BASELINE.md: each raw row has synchronous Refresh total/extract/submit, separate Camera.Render CPU,
validated allocation byte delta/sample count, and separate render counters. Unsupported allocation columns are NOT
MEASURED; if both counters miss the live 4096-byte control, validation is Inconclusive / NOT RUN. The control test must
detect the allocation and verify that the zero-allocation assertion rejects it. Allocation regions exclude test plumbing.

Art handshake, owner Game-view look, Frame Debugger, GPU timing, player build and Domain Reload disabled remain
NOT RUN. #30/#17/#25 remain separate and OPEN. Historical evidence under #38 is not amended retroactively.
