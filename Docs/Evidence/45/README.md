# #45 Stage03.3b idle/run — evidence and rerun

Base main97b8c188d3c1d2fa30a8deb5709b8cb1e834d317. One feature/stage03-run PR, no automatic merge.
Independent manager bottom-up source review: STATIC PASS (raw manager-static-review.json includes checks/source hashes). The existing user idle1s/import/package/scene changes are retained.

| Check | Status |
|---|---|
| Corrected PNG and archive metadata: dimensions, alpha, useful rect/extrusion bounds, unchanged idle | PASS static Python/PIL, raw art-static-validation.json |
| C# syntax parse/GUID/JSON/whitespace/legacy mutation applicability | PASS: syntax-only68 C# files,148 unique valid meta GUIDs,7 project JSON, whitespace and legacy mutation applicability; raw source-static-checks.json |
| Unity compile/import/Entities codegen/Burst and authored EditMode/PlayMode | NOT RUN (Unity/Mono/dotnet unavailable) |
| Actual GPU readbacks, legacy-depth negative control, fresh GC/performance | NOT RUN (no Unity graphics device) |
| Game view loop/facing/phase/3D, Frame Debugger, player build, Domain Reload off | NOT RUN |

The input report concerns latest corrected PNG version4 plus compatible revision3 metadata. Old ZIP run frame bytes differ;
only geometry compatibility and unchanged idle are claimed. No art was redrawn or committed; preserve tracked .meta GUID.

## Required Unity rerun
Use Unity6000.5.4f1 and current locked packages on exact tested SHA in ASCII checkout. Install corrected PNG matching
ANIMATION.md SHA; retain current Unity import settings and verify1152x384, RGBA8/binary alpha, Point/Clamp/no mipmaps/compression.
Run each series twice in separate processes and preserve exact command/exit code/environment/device/API/tested SHA/art hash:

```powershell
& $editor -batchmode -nographics -projectPath $project -runTests -testPlatform EditMode -testResults "$evidence/editmode-run1.xml" -logFile "$evidence/editmode-run1.log"
& $editor -batchmode -nographics -projectPath $project -runTests -testPlatform PlayMode -testResults "$evidence/playmode-run1.xml" -logFile "$evidence/playmode-run1.log"
& $editor -batchmode -force-d3d12 -projectPath $project -runTests -testPlatform PlayMode -testCategory "Graphics;Performance" -testResults "$evidence/gpu-run1.xml" -logFile "$evidence/gpu-run1.log"
```

Use absolute paths, wait for exit, repeat with run2 outputs. Inspect full XML passed/failed/skipped/inconclusive and logs;
headless skipped graphics/art means NOT RUN. Retain new45-mixed readbacks and all retained #42wall/400-layer readbacks
outside Git. Follow Evidence/41 legacy-depth mutation (expected FAIL mutated, PASS unmutated); final suites unmutated.

Performance keeps stage03-sprites technical controls and adds stage03-idle-run mixed actual-art at2/100/400:
summary+both renderer/no-renderer raw TSV+live4096-byte/empty GC controls. Test-only opt-in producer appends accepted
commands in the existing Collection queue for half the roster, outside synchronous Refresh allocation/timing scope.
At least3 cycles of both clips and all11 indices are observed outside measured samples; buffers/timing are reused.
Unsupported counters say NOT MEASURED; neither working is Inconclusive/NOT RUN. Stats captured before yield.

Manual Game view1920x1080: stationary idle,8-direction run/Z-only, move-stop, jumps/airborne facing/landing, pause/resume,
paused restart/end, offscreen/renderer disable, World replacement/disposal, config edits/invalid run recovery, overlapping
idle/run and3D walls/floor. Inspect phase continuity and both independent N/T/curves. No promise at arbitrary fractional zoom.
No new runtime XML/log/TSV/GPU PASS is fabricated. Historical #38/#43 data do not establish #45 results. #30/#17/#25 remain open.

Additional synthetic multi-character regression: A→B→A uses separate catalogs/textures/page sizes, a two-vs-three-frame
table, different pivot/trim/source/nominal height and independent durations with the same clip IDs; unrelated invalid
records do not block selected characters. Unload clears all old page bindings. This is catalog switching, not concurrent
multi-character rendering; that separate scope is tracked in #46.
