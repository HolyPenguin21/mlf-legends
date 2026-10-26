# Issue #38 — raw Unity test evidence (Stage 03.1 + 03.2)

**Manager review qualification (2026-10-26), [#41](https://github.com/HolyPenguin21/mlf-legends/issues/41):** all six final XML/logs and six TSVs read; counts agree. The zero byte deltas are unvalidated allocation evidence until the PlayMode counter passes a live-allocation control. Old extract/submit stats can come from a different Refresh because they were sampled after yield. Raw files remain exactly as recorded; new evidence is required after the correction. Source review also established cumulative depth-push occlusion failure with many equal-Z actors outside the local screen region; passing the existing duel GPU tests does not cover it. See QA.md.

Raw files exactly as Unity and the tests wrote them (NUnit XML, Editor.log, TSV, PNG readbacks). Recorded by the
implementing agent; the manager has not re-run Unity. Machine: Windows 11 Pro 10.0.26100, AMD Ryzen 5 3600, 32 GB,
NVIDIA GeForce RTX 2060 SUPER (Direct3D 12, feature level 12.2). Unity 6000.5.4f1, Entities/Collections/Serialization
6.5.0, Burst 1.8.29, URP 17.5.0, Input System 1.19.0. Clean checkout of the named commit in the ASCII worktree
`D:/Unity/Project/mlf-s7` (warm Library), one Unity process per run. Logs contain local paths and the Windows user name
in licensing channel names; no serial/licence IDs or e-mail.

| Folder / files | Commit | Command | Result |
|---|---|---|---|
| `2d1f41f/*-editmode-run{1,2}.*` | `2d1f41f` (all #38 code/tests) | `Tools/Run-UnityTests.ps1 -Mode EditMode` (batchmode `-nographics`) | **PASS 323/323, 323/323** (includes the existing #7/#22 benchmarks) |
| `2d1f41f/*-playmode-run{1,2}.*` | `2d1f41f` | `Tools/Run-UnityTests.ps1 -Mode PlayMode` (`-nographics`) | **PASS 30/30, 30/30**; 7 Graphics/Performance tests **Ignored = NOT RUN** (no device), by design |
| `2d1f41f/*-gpu-graphics-performance-run{1,2}.*` | `2d1f41f` | `Unity.exe -batchmode -projectPath <wt> -runTests -testPlatform PlayMode -testCategory "Graphics;Performance"` (graphics device, no `-nographics`) | **PASS 7/7, 7/7** on Direct3D 12 |
| `2d1f41f/gpu-run{1,2}/graphics/*.png` | `2d1f41f` | written by the Graphics tests (RenderTexture → `Texture2D.ReadPixels`) | readbacks that were compared pixel by pixel |
| `2d1f41f/gpu-run{1,2}/stage03-sprites-n{2,100,400}.tsv` | `2d1f41f` | written by the Performance test | see QA.md (#38) |
| `8ea534f/*` (bottom-up recheck, branch `fix/38-recheck`) | `8ea534f` | same three series | **EditMode 329/329 ×2; PlayMode 30/30 ×2 (8 Ignored = NOT RUN without device); GPU Graphics+Performance 8/8 ×2**; TSVs add `extract_median_us`/`submit_median_us` |
| `2d1f41f-negative-control/` | `2d1f41f` + `mutations.diff` (temporary, never committed) | Graphics category (GPU) and EditMode `SpriteCardMathTests|CombatSpriteExtractionTests` against three simultaneous mutations: UV-only flip, no depth tie-break, camera-plane card | **GPU 0/4, EditMode 13/21 (8 FAIL)** — the tests detect each defect (first failing assertion per test shown in the XML) |

Column meanings of the TSVs: `refresh_*_us` = `CombatSpriteRenderer.Refresh` (extraction + submission) CPU time;
`camera_render_median_us` = `Camera.Render` CPU time; `gc_bytes_measured` = `GC.GetAllocatedBytesForCurrentThread` delta
around Refresh over 120 measured frames after 30 warm-up frames; `draw_calls_median`/`setpass_median` =
`UnityEditor.UnityStats` after each `Camera.Render` of a 512×512 target; `batches_median` = NOT RUN (only available via
`ProfilerRecorder`, which batchmode does not publish); `submissions` = `RenderMeshPrimitives` calls (atlas pages).
GPU time is not measured.
