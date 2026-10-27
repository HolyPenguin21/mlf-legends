# #43 Stage 03.3a — real idle + shared animation config

Base: `fe324f83ab0230f1f13ce88f2b6e2f9693837594`. Branch `feature/stage03-idle-combat`.
Independent manager bottom-up source review: STATIC PASS (2026-10-27); raw manager-static-review.json records checks and source hashes. No merge is authorized.

| Evidence/check | Status and limit |
|---|---|
| Input PNG/JSON geometry, RGBA8/binary alpha, source reconstruction, guards/extrusion | PASS, Python/PIL static content checks; raw `art-static-validation.json` |
| Hermite integration/frame-boundary formula against SciPy (180 curves × 5 amplitude scales) | PASS, independent Python source-model check; raw `timing-reference-check.json`; NOT execution of C#/Unity |
| Runtime/source ownership, cache invalidation, lifecycle and test review | STATIC PASS, independently reviewed after agent delivery; raw manager-static-review.json |
| Unity compile/import/Entities codegen/Burst, full EditMode/PlayMode | NOT RUN, no Unity executable/dotnet/Mono in editing environment |
| GPU readback, #42 wall and 400-layer controls, legacy-policy negative control | NOT RUN, no Unity/graphics device |
| New technical + real-idle 2/100/400 GC/timing/buffer measurements | NOT RUN; no fabricated XML/log/TSV/results |
| Game view 1920x1080, actual art loop/pivot/flip, Frame Debugger, player build, Domain Reload off | NOT RUN |

PNG stays outside Git (import it with the tracked meta). The exact provided JSON is committed; hashes and setup
are in `Assets/MLFLegends/Combat/Runtime/Presentation/Sprites/ANIMATION.md`. Artistic approval/hand anchors are not
established by static pixel checks. The source package labels estimated anchors/art review pending; no artwork
was regenerated or modified by this implementation.

## Required Unity rerun on final tested SHA

Use Unity 6000.5.4f1 and locked packages on an ASCII checkout. Record HEAD, environment/device/API and external PNG
hash, copy it to the documented Atlas path while preserving tracked `.meta`, and verify importer output RGBA32,
1152x192, Point/Clamp, no mipmaps/compression/resizing. A checkout without external PNG explicitly skips art tests
BEFORE loading CombatSmoke; ordinary malformed config/bindings emit errors and never fall back to technical art.

In separate processes, twice each (absolute paths; retain exit codes and fresh NUnit XML/full logs):

```powershell
& $editor -batchmode -nographics -projectPath $project -runTests -testPlatform EditMode -testResults "$evidence/editmode-run1.xml" -logFile "$evidence/editmode-run1.log"
& $editor -batchmode -nographics -projectPath $project -runTests -testPlatform PlayMode -testResults "$evidence/playmode-run1.xml" -logFile "$evidence/playmode-run1.log"
& $editor -batchmode -force-d3d12 -projectPath $project -runTests -testPlatform PlayMode -testCategory "Graphics;Performance" -testResults "$evidence/gpu-run1.xml" -logFile "$evidence/gpu-run1.log"
```

Use run2 names for the second series, never overwrite run1. Inspect actual XML pass/fail/skipped/inconclusive;
headless skipped GPU tests are NOT RUN. Follow `Docs/Evidence/41/README.md` for the required legacy-depth GPU
negative control (expected FAIL with mutation, PASS unmutated); keep its diff/log/XML separately and never commit
mutation to runtime. Final suites must use unmutated code. Check compile/shader/leak/sorter diagnostics.

Preserve TestResults/performance `stage03-sprites-*` (technical) AND `stage03-idle-*` (actual animated character):
summary, renderer/no-renderer raw samples and live/empty GC controls, for 2/100/400. New real-idle series also observes
at least 3 complete simulation-time cycles and all 6 frames outside the measured GC region, checking timing/atlas/
GPU buffer reuse. Unsupported counters say NOT MEASURED; neither allocation control working is Inconclusive/NOT RUN.
Refresh total and split Stats are captured synchronously before yield; Camera.Render/deferred counters separate.
Copy Graphics `43-animation-*` readbacks and retained `41-*`/existing graphics images outside Git with evidence links.

Manual: inspect all real frames and the loop seam, both facings, X/Z/diagonal movement and jump while idle continues,
pause/resume (phase retained), paused Restart/End, renderer disable/re-enable, camera offscreen-return, scene/World
replacement/disposal and repeated Play with Domain Reload off. Check Game view at 1920x1080 plus 1/2/3 source-pixel
controls; arbitrary fractional zoom is not promised pixel-perfect. Use Frame Debugger to attribute passes/calls.

Historical #38 measurements predate #42 and do not verify this code. #30/#17/#25 remain separate/open.

Independent numerical check is reproducible with Python + NumPy + SciPy: `python Docs/Evidence/43/check_timing_reference.py`. It checks the mathematical source model, not Unity/C# execution.
