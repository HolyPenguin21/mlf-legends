# MLF Legends — Quality assurance and evidence

## #38 bottom-up recheck after the merge of PR #39 (2026-10-26, branch `fix/38-recheck`)

Owner-requested re-audit (manifest/texture → geometry/UV/anchors → buffers/shader/depth → ECS reads/World lifetime →
camera/URP → tests/benchmark → docs). Tested `8ea534f`, raw files in `Docs/Evidence/38/8ea534f/`: **EditMode 329/329 ×2,
PlayMode 30/30 ×2 (8 GPU cases Ignored = NOT RUN without device), D3D12 Graphics+Performance 8/8 ×2**, no C#/shader
errors or warnings, no leaks.

Findings and fixes:
1. **Defect:** a page imported without alpha (e.g. RGB24) was accepted — every rect would draw as an opaque box. Now
   rejected (`has no alpha channel`); test with an RGB24 page.
2. **Defect (found by the new test, run on `d3caf83`: 327/329):** the `com.unity.serialization` reader stops at the end
   of the root value and silently ignores trailing content (`{…} x`, `{…} }`). Now rejected by a strings/escapes-aware
   end-of-root check. A second root object, comments and a UTF-8 BOM (now tolerated) are covered by tests.
3. A superseded renderer extracted ECS every frame for nothing; it now neither reads nor draws (binding/selections kept).
4. Extraction and submission CPU time are measured separately (`RenderStats`, TSV): submission ≈ 18 µs at every count;
   extraction median 12.3 / 52.7 / 189.1 µs for 2 / 100 / 400 (Editor batchmode; total 30.9 / 71.1 / 209.1 µs).
   Draw calls 6 → 8, GC.Alloc 0, one instance buffer — unchanged.
5. Test gap: depth crossing was only checked with static poses. Added real fixed-tick movement (Z towards/through each
   other, then X pass at equal depth: exactly one front change) and a GPU step-by-step pass-through across pages.
6. Docs: contract states binary body alpha (alpha < 128 not drawn), BOM/comments/trailing content, anchors may lie
   outside the rect; SPRITES.md limits (exact for pitch ≈ 5.8°–84.3°, below-pivot pixels as a floor decal, no fog/TAA
   motion vectors/shadow caster, target 4.5, no XR, not pixel-perfect); BOOTSTRAP.md scene contents; PERFORMANCE_BASELINE
   pointer.

Process note: an intermediate commit `95eb8d7` did not compile (a test string literal broken by the editing tool; fixed
in `d3caf83`); its and `d3caf83`'s runs are superseded and not kept. The NOT RUN list of #38 below is unchanged.

## Stage 03.1 + 03.2 / #38 atlas contract and GPU sprite renderer (2026-10-26, branch `feature/stage03-atlas-renderer`)

**Agent evidence, IN REVIEW ([PR #39](https://github.com/HolyPenguin21/mlf-legends/pull/39)), not merged.** Tested code: `2d1f41f` (clean checkout, ASCII worktree, warm Library); raw XML,
Editor.log, TSV and PNG readbacks committed in `Docs/Evidence/38/` (index: `Docs/Evidence/38/README.md`). Machine: Windows 11,
Ryzen 5 3600, 32 GB, RTX 2060 SUPER, Direct3D 12. The manager has not re-run Unity.

| Run | Result |
|---|---|
| EditMode run 1 / run 2 (`-nographics`) | **PASS 323/323**, **PASS 323/323** (79 new #38 cases; existing suites and #7/#22 benchmarks unchanged) |
| PlayMode run 1 / run 2 (`-nographics`) | **PASS 30/30**, **PASS 30/30**; 7 Graphics/Performance cases **Ignored = NOT RUN** (no device), by design |
| PlayMode `-testCategory "Graphics;Performance"` with the D3D12 device, run 1 / run 2 | **PASS 7/7**, **PASS 7/7** |
| Negative control (three temporary mutations: UV-only flip, no tie-break, camera-plane card) | **GPU 0/4, EditMode 8 of 21 targeted cases FAIL** — the tests detect the defects |
| Logs | no C# errors/warnings, no shader errors, no `Leak Detected` |

What the graphics tests prove (RenderTexture readback, whole-image comparison against an independently computed expected
image; tool: `Camera.Render` + `Texture2D.ReadPixels`, PNGs in the evidence): real URP shader compile/render on D3D12;
colours, transparency (hole shows the background/geometry behind), UV/trim/pivot and flip for asymmetric frames; frames
hanging below the pivot; different frames in one batch and two pages of different sizes; nearer fighter covers the farther
one across pages in both ID orders; equal depth and identical position → lower CombatantId in front, stable over frames;
airborne fighter, pan and zoom (1/2/3 target pixels per source pixel); opaque box in front hides the lower body, a wall
behind never covers the head, transparent pixels do not occlude, the floor neither hides the pivot row nor the part below
the pivot; no magenta page padding sampled anywhere.

**Measurements** (`stage03-sprites-n*.tsv`, run 2; Editor batchmode, 512² target, one atlas page):

| Fighters | Refresh median / p95 (µs) | GC bytes (120 frames) | Draw calls without → with renderer | Instance buffers created | Instance buffer | Atlas pages + frame table |
|---|---|---|---|---|---|---|
| 2 | 30.8 / 37.6 | 0 | 6 → 8 | 1 | 1 536 B | 126 976 + 96 B |
| 100 | 70.9 / 88.0 | 0 | 6 → 8 | 1 | 3 072 B | 126 976 + 96 B |
| 400 | 209.7 / 266.2 | 0 | 6 → 8 | 1 | 12 288 B | 126 976 + 96 B |

Draw calls do not grow with fighters (+2 per camera render for one page: the forward pass plus, presumably, the SSAO
DepthNormals prepass — per-pass attribution by Frame Debugger is NOT RUN). One `RenderMeshPrimitives` submission per page,
no buffer re-creation and zero GC.Alloc per frame after warm-up. Batches: NOT RUN (ProfilerRecorder not published in
batchmode). GPU time, full-frame cost and FPS: **NOT RUN / not claimed**; batchmode Editor CPU numbers are not a frame
budget.

**NOT RUN / open:** art smoke with the artist's first real frame + JSON (no package yet; the contract and example are
ready for the handshake); owner GUI look in the Game view, Frame Debugger per-pass breakdown, GPU timing, player build;
Domain Reload disabled manual check; other GPUs/APIs (Vulkan/Metal/mobile renderer asset). #30/#17/#25 unchanged.

**Known limits:** main-thread managed extraction (~0.2 ms at 400 in the Editor); Scene view does not show fighters (draws
target the selected camera); only yaw 0° calibrated; depth tie-break worst case 0.8 units when 400 fighters coincide;
import settings are validated (size, compression, warnings for mipmaps/linear) but not enforced by a postprocessor.

## Manager re-review of merged PR #37 (2026-10-26)

**PASS — source and supplied raw evidence independently inspected; Unity not re-run by manager.** Reviewed main `72bb712920920f0f0ea742a4a86076baf904ff68`, merge `fc5fb675ee1a13de7ee839e40337e1cb78adcbb0`. Compared tested `4d9e431bc7474cf7233f8edaec60f0c14df65ab0` to reviewed main: only documentation, evidence and .gitattributes differ; no runtime/test/scene changes.

The three confirmed defects are corrected: End preserves the panned view on its first refresh; Attach binds the current session without clearing fresh post-attach input on the first tick; the enabled adapter follows a late/replaced default World while enable-order arbitration preserves one source. Focus tests now drive InputManager focus handling and restored device state, rather than only calling the adapter callback. No new simulation owner, clock, queue or per-fighter GameObject was introduced.

Read all five final NUnit XML files: EditMode **244/244 twice**, PlayMode **21/21 twice**, Burst-off Performance **7/7**; no skipped/failed cases. Final logs report exit 0 and no C# compiler errors/warnings or Leak Detected in these warm runs. The known clock-less fixture sorter warning, no-graphics PlayMode warning and explicit Burst-disabled warning remain qualified. Read the mutation diff and negative-control XML: all **4 EditMode + 4 PlayMode** selected cases fail at the intended reviewed behaviours. Negative-control mutation compilation has CS0162; this is not a warning in the final code.

**Performance evidence qualification:** all attached/held input series have GC.Alloc 0; the known detached Burst-on run 1 N=400/k1/rep3 has one sample. Additionally, `4d9e431/burst-off/stage02-33-burst-off-n2.tsv` records **one GC.Alloc in each Start rep 2 and rep 3**. The earlier statement that every existing-benchmark series had zero samples was too broad. These samples are unattributed; they do not establish a producer regression or a leak. Burst-on paired median mean-per-tick input overhead is approximately **+6–7 us at k1**, **+4 us at k10**, for N=2/400. Camera and full-frame performance are not measured.

**Known focus limitation, not hidden by the PASS:** a button released while unfocused, with no release state reported by the device, can lose its first press after focus returns; the following release unblocks it (documented in INPUT.md). Actual OS Alt-Tab/hardware validation, owner GUI checks, Domain Reload disabled with these components, player build and camera/full-frame measurements remain **NOT RUN**. #30/#17/#25 stay open in their existing scope. No blocking defect established in the requested fixes; Stage 02 input/camera implementation is manager-reviewed and Stage 03 planning may proceed.

## Stage 02.3 + 02.4 / #22 after manager review (2026-10-26, PR #37, branch `feature/stage02-22-input-camera`)

**Merged on the owner's instruction as main `fc5fb675ee1a13de7ee839e40337e1cb78adcbb0` (2026-10-26).** Merged code equals the
tested `4d9e431` (later commits: Markdown and evidence files only). Manager re-review: PASS with the evidence qualifications above.

**Tested code: `4d9e431bc7474cf7233f8edaec60f0c14df65ab0`** (its code equals `4768386`; only Markdown differs), clean detached
checkout, same machine/versions/runner as below. **Raw XML, Editor.log and TSV are committed in `Docs/Evidence/22/`**
(index: `Docs/Evidence/22/README.md`). Agent-run; the manager has not re-run Unity.

| Run | Result |
|---|---|
| EditMode run 1 / run 2 | **PASS 244/244**, **PASS 244/244** (5 new review regressions) |
| PlayMode run 1 / run 2 | **PASS 21/21**, **PASS 21/21** (3 new review regressions incl. the focus case ×2) |
| Performance, `--burst-disable-compilation` | **PASS 7/7** (probe: Burst not executed) |
| Negative control: the 8 review regressions against `4768386` + four mutations restoring the old behaviour | **0/4 EditMode, 0/4 PlayMode** — every regression FAILS on the reviewed defect (`Docs/Evidence/22/4768386-negative-control/`) |

Review findings and their fixes (all in this PR, no new subsystems):
1. **End moved a panned camera** (pan reset on losing the target). Now only a new valid target drops the pan; End/Idle keep
   position, size and pan exactly. Regressions: EditMode `EndKeepsThePannedViewExactlyUntilANewTargetBinds` (first refresh
   after End without input), PlayMode `CameraRebindsOnRestartAndHoldsItsViewWhenTheBattleEnds` (view captured before End).
2. **A fresh tap right after attaching to a running battle was lost** (Attach unbound, the first tick re-cleared). Now Attach
   binds to the current session. Regression: `AFreshTapRightAfterAttachingToARunningBattleReachesTheFirstTickOnce` (pre-attach
   tap still dropped, Restart still drops old presses).
3. **The adapter bound only in OnEnable.** It now follows `World.DefaultGameObjectInjectionWorld` (once per World, checked
   each frame); the producer keeps the highest enable order. Regressions: PlayMode
   `AdapterFollowsTheDefaultWorldWhenItAppearsOrIsReplaced` (no World → A → B → A, stale presses, replaced adapter does not
   retake, destroy detaches), EditMode `TheHigherEnableOrderWinsWhicheverCaptureAttachesFirst`.
4. **Focus regain with device reset/sync.** Buttons held across a focus change or device disable/enable/reset are blocked
   until released. Regressions: PlayMode `RealFocusLossAndRegainWithDeviceResyncTurnsNoRestoredHoldIntoAPress`
   (InputTestFixture's runtime focus → the Input System's real `OnFocusChanged`, runInBackground true and false, device
   re-reporting held K/J/L: Held, no Jump/Attack; release → press jumps), EditMode
   `KeysHeldAcrossAResetOrDisableEnableCycleAreHeldNotPressed` (soft reset; disable/enable). Real OS alt-tab: NOT RUN.

Logs: no `error CS`/`warning CS`; one known intentional clock-less sorter warning per EditMode run; no `Leak Detected`;
PlayMode only `No Graphics Device found` (environment). `ProjectSettings/TimeManager.asset` batchmode reformat reverted,
not committed. Benchmark: GC.Alloc 0 in every held input series; the Burst-off existing-benchmark N=2 Start reps 2 and 3 each record one sample (unattributed). **One** GC.Alloc sample in
one *detached* (inert producer) series (Burst on, run 1, N = 400, 1 tick/frame, rep 3, max frame 67.5 us), not reproduced
in run 2; origin not attributed (the detached producer returns before any work).

**NOT RUN:** owner GUI manual check (`Input/INPUT.md`); real hardware devices and real OS focus switching; Domain-Reload-
disabled Play Mode with the new components; player build; cold import / Cyrillic path for this branch (#30/#17
unchanged); camera and Initialization/full-frame cost.

The earlier section below (`d879d4c`) is superseded: those runs did not cover the reviewed defects.

## Stage 02.3 + 02.4 / #22 agent verification, first PR revision (2026-10-26) — SUPERSEDED by the section above

**Tested code SHA: `d879d4cb47917bb72ed4d48a056487314a1818e3`** (later branch commits change Markdown only). Clean detached
checkout in the ASCII worktree `D:/Unity/Project/mlf-s7` (warm Library), no local patches; Windows 11 Pro 10.0.26100, AMD
Ryzen 5 3600, 32 GB; Unity **6000.5.4f1**, Entities 6.5.0, Collections 6.5.0, Burst 1.8.29, URP 17.5.0, **Input System
1.19.0** (unchanged); `Tools/Run-UnityTests.ps1` (batchmode `-nographics`), each run a separate Unity process.

| Run | Result |
|---|---|
| EditMode run 1 | **PASS 239/239** (195 previous + 44 new, incl. a 7th Performance test) |
| EditMode run 2 | **PASS 239/239** |
| PlayMode run 1 | **PASS 18/18** (10 previous + 8 new) |
| PlayMode run 2 | **PASS 18/18** |
| EditMode `-testCategory Performance`, `--burst-disable-compilation` | **PASS 7/7** (probe: Burst code not executed, as intended) |
| Burst-on Performance | inside both full EditMode runs; probe: Burst code executed |

Evidence (XML, Editor.log, TSV) is now committed in `Docs/Evidence/22/d879d4c/` (and the failed `1408887` series in
`Docs/Evidence/22/1408887-failed/`).
Logs: no `error CS`/`warning CS`; one `Ignoring invalid [UpdateAfter] ... CombatClockSystem` per EditMode run, the known
intentional clock-less fixture (`CombatClockTests.CombatPhasesDoNotRunWithoutAClock`); no new sorter warnings; no
`Leak Detected` (warm Library); PlayMode logs only the environmental `No Graphics Device found` (Entities Graphics,
`-nographics`). Batchmode rewrote `ProjectSettings/TimeManager.asset` to Unity's serializedVersion 2 rational timestep
(0.02 = 2822399/141120000, `m_TimeScale` 1) in the worktree; reverted, not committed (same class of side effect as the
known URP asset churn).

An earlier full series on `1408887` failed 2 new tests (EditMode 237/239 twice, Burst-off Performance 6/7; PlayMode 18/18
twice): an asset without maps made `InputActionAsset.FindActionMap(..., throwIfNotFound: true)` return null (runtime fix:
explicit `ArgumentException`), and the producer benchmark's virtual key state did not survive EditMode editor frames
under InputTestFixture (test fix: the hold is re-reported untimed before each frame). Both fixed before `d879d4c`.

**What the new tests cover (real Input System events, real asset, real producer/processor/facing/movement):**
- EditMode `PlayerInputProducerTests` (InputTestFixture virtual devices): 30/60/120 FPS and uneven pacing with 0-10 ticks
  per frame; 0/1/2/3/10 ticks per frame; Held/Move on every catch-up tick, Pressed once; tap between ticks (Pressed with
  Held = None); several presses -> one bit; holding K jumps once; WASD diagonal and stick quantization; gamepad; Restart
  between catch-up ticks and between frames; End/idle, despawn, disabled InCombat; pause; focus and device loss;
  attach/replace/detach/dispose (one Player command per tick); replay worlds without adapter get no hardware command;
  player-vs-AI parity of identical canonical commands (bit-identical position/motion/facing); 200 vs 200 (all 400 ticked).
- EditMode `CombatCameraControllerTests`: follow after movement/jump, fixed angle, pan/zoom clamps, invalid input, yaw,
  Restart rebinding, End/Despawn/defeat/World disposal, and a 200 vs 200 snapshot (all combatants, session, queue, clock,
  entity count) identical before/after 50 camera-only frames.
- PlayMode `PlayerInputPlayModeTests` (default world, CombatSmoke): real PlayerLoop order PreUpdate input -> Update
  `SimulationSystemGroup` -> PreLateUpdate `LateUpdate`; one producer before the processor; taps reach the first tick after
  their frame and holds the ticks of their frames at 30/60/120 FPS (`Time.captureFramerate`, both 0- and 1-tick frames at
  120); pause (no tick/position change, pan/zoom work, no jump on resume); focus loss; additive scene switch and adapter
  re-enable (one producer, one Player command per tick); camera rebinding on bootstrap re-enable.

**NOT RUN / limits:** owner GUI manual check (`Input/INPUT.md`, Manual check); real hardware keyboard/gamepad, real OS
focus switching (tests drive `OnApplicationFocus` / the capture API) and Domain-Reload-disabled Play Mode with the new
components; player build; cold import and Cyrillic path for this branch (#30/#17 unchanged). Initialization/full-frame
cost (#30) and the camera's frame cost are unmeasured. The earlier 195/195 and 10/10 are not evidence for this code.

## Manager review of merged PR #36 (2026-10-26)

Reviewed main `013cf238a1ecdfe5f50fca9660dc8585aed9cc42` bottom-up: the sole Lifecycle/session/ID/spawn/reset owner, Initialization proxy, clock/phase order, default-world bootstrap, replay/manual fixtures, movement/facing and benchmark call sites. **Implementation accepted; no corrective runtime change identified. #35 remains CLOSED. Stage 02.3 input work may proceed; #30 remains OPEN for the residual checks below.**

Independently downloaded and inspected the [evidence archive](https://gist.github.com/HolyPenguin21/1cfd8ac9ca4b34206be07572c69297b8): **11 NUnit XML, 11 Editor logs, 12 benchmark TSV (34 files)**, correcting the earlier 12/16/6 inventory. Six full EditMode runs passed **195/195** (including all six repaired End-in-catch-up cases), three PlayMode runs passed **10/10**, and two Performance runs passed **6/6**. GitHub comparison confirms the tested `f841bbfbba2569a369a0967ccc2955274313686b` and current main differ only in Markdown documentation. The manager reviewed supplied evidence, not a new local Unity execution.

**Residual evidence findings (not silently PASS):**
- Both cold EditMode logs (`f912f37-ascii-cold-editmode.log:12788`, `f912f37-cyrillic-cold-editmode.log:12763`) report **`Leak Detected : Persistent allocates 4 individual allocations.`** at shutdown. Warm logs do not contain that message. Allocation stacks are absent; ownership is UNKNOWN, and this is not proof of a Lifecycle leak or of an upstream Unity defect. Repeat cold/warm with allocation stack tracing, first import without tests, then relevant suites; identify the owner before changing code/packages. Retained in #30.
- The dedicated N=400 memory TSVs contain series deltas **-576 B** (Burst on, cmd k10 rep 2) and **-27,546 B** (Burst off, lead32 k18 rep 3). Timing TSVs also contain nonzero total-memory deltas, including **+135,152 B** in Burst-off cmd k1 rep 3. Thus “native drift 0 in every series” is false. These process-counter fluctuations alone establish neither a growing leak nor its absence; GC.Alloc samples are zero. See corrected PERFORMANCE_BASELINE.md.
- `f912f37-ascii-warm-playmode.log:778` reports modification of the immutable package asset `Packages/com.unity.render-pipelines.core/Runtime/Debugging/Runtime UI Resources/RuntimeDebugWindow_PanelSettings.asset`. Qualify clean-checkout/source equivalence with this import side effect; do not edit package sources as a workaround.
- Only the intentional clock-less fixture retains an invalid ordering warning; no C# compiler error/warning was found in supplied logs. No-graphics and licensing notices are separate environment messages.
- Initialization-proxy/full Unity-frame performance is **NOT MEASURED** by this fixed-step harness. Start/end/restart timings measure the direct Lifecycle call. Domain/Scene Reload GUI checks remain **OWNER-REPORTED PASS**, not independently logged.
- #17: ASCII/Cyrillic cold/warm XML all show `Done=1; RanManaged=0; BurstEnabled=True`. Historical failure **NOT REPRODUCED** on this machine/version; root cause UNKNOWN and the broader original path/protocol matrix is not complete. #17 stays OPEN, nonblocking.

**Scope note:** GitHub repository and serialized Unity project settings can be reviewed, but Editor tests have **not** been performed directly by this assistant. On **2026-10-25**, the project owner reported **all Stage 00 tests PASSED**. This is an **owner-reported PASS**, without attached XML/log evidence; it is not an independently audited run.

**Non-blocking delivery rule (approved 2026-10-25):** The owner currently cannot run Unity tests from their environment. This MUST NOT block committing, merging or pushing requested changes, nor prevent moving on to subsequent implementation tasks. Perform every feasible static check, label Unity checks **NOT RUN**, record the residual risk and verify later when execution becomes possible. Delivery is not the same as test approval.

## #35 + #30 Unity verification on the corrective branch (2026-10-26)

**Status: Unity-verified on branch `fix/35-lifecycle-proxy` ([PR #36](https://github.com/HolyPenguin21/mlf-legends/pull/36), merged to main on the owner's instruction), no local patches.**
Platform: Windows 11 Pro 10.0.26100, AMD Ryzen 5 3600, 32 GB; Unity **6000.5.4f1**, Entities 6.5.0, Collections 6.5.0,
Burst 1.8.29, URP 17.5.0; `Tools/Run-UnityTests.ps1` (batchmode `-nographics`) on separate git worktrees.

**Fixes in this branch**
- CS0579: `BattleLifecycleSystem` keeps one `[UpdateInGroup(FixedStep)]`; new `BattleLifecycleInitializationProxy`
  (Initialization, OrderFirst) updates the same existing instance. Default world auto-creates both,
  `ReplayWorld.RuntimeSystems`/benchmark list both, manual lifecycle test worlds add the proxy.
- NUnit: `EndRequestedInsideAFixedStep…` takes `int allies`, converted with `checked((ushort)allies)`; all 6 cases run.
- Order tests now assert: one lifecycle instance per World (by system type), Initialization holds the proxy and not the
  lifecycle, FixedStep holds the lifecycle and not the proxy, the proxy has only `UpdateInGroup(Initialization, OrderFirst)`,
  the proxy really runs that instance (its `LastSystemVersion` advances) and drains the one buffer on a paused frame
  with no tick or command phase; the proxy is inert without/after the lifecycle. Existing catch-up End/Restart tests unchanged.
- Sorter warnings removed at their source (test registration; no Unity code touched): ReplayWorld now lists
  `BeginSimulationEntityCommandBufferSystem` (target of Unity's FixedStep `[UpdateAfter]`) and sorts both test
  producers together; clock/processor fixtures include the inert lifecycle (target of the clock's `[UpdateAfter]`).
  Deprecated `FindObjectsByType(…, FindObjectsSortMode)` replaced (CS0618).

**Unity runs (XML + Editor.log attached in #30)**

| Run | SHA | Library | EditMode | PlayMode |
|---|---|---|---|---|
| ASCII worktree `D:/Unity/Project/mlf-s7`, run 1 | `f841bbf` | warm | 195/195 | 10/10 |
| same, run 2 (new Unity process) | `f841bbf` | warm | 195/195 | 10/10 |
| ASCII copy `D:/Unity/Project/mlf-s35-cold` | `f912f37` | cold import | 195/195 | — |
| same, second process | `f912f37` | warm | 195/195 | 10/10 |
| Cyrillic copy `D:/Unity/Project/млф-s35-кириллица` | `f912f37` | cold import | 195/195 | — |
| same, second process | `f912f37` | warm | 195/195 | — |
| Performance category, Burst on / off (`mlf-s7`) | `f912f37` | warm | 6/6 and 6/6 | — |

`f841bbf..f912f37` changes documentation only. Compile: no `error CS`, no `warning CS` in project code.
Remaining Entities warning: one `Ignoring invalid [UpdateAfter] on CombatCommandCollectionSystemGroup targeting
CombatClockSystem` from `CombatClockTests.CombatPhasesDoNotRunWithoutAClock`, whose premise is a clock-less World
(documented in the test). PlayMode logs `No Graphics Device found. Deformation systems will not run.` (Entities
Graphics under `-nographics`; environment). The cold batchmode import again rewrote
`Assets/Settings/UniversalRenderPipelineGlobalSettings.asset` in the test copy (known `-nographics` side effect, not
committed). "Errors" in logs are the expected `LogAssert` clock-exhaustion messages and a licensing token notice.

**#17 Unicode/Burst:** physical copies of one commit at an ASCII and a Cyrillic project path, each cold then warm:
`ResetFromABurstSystemInTheCollectionPhaseRejectsThatTicksCommands` passed in all four runs with
`BurstEnabled=True; Done=1; RanManaged=0` (Burst code executed); no failed imports in the Cyrillic cold log. The
historical failure is **not reproduced** on this machine and Unity version; the root cause of the old report stays
unknown (the user profile/temp path is Cyrillic in every run). Closing #17 is the manager's decision.

**Manual GUI (owner, project `mlf-legends` on `fix/35-lifecycle-proxy`, Unity 6000.5.4f1 Editor):** reported
**PASS** for: 1v1 start, bootstrap disable/enable repeated without entity accumulation; End/Restart at
`Time.timeScale = 0`; duplicated bootstrap / late End of the previous owner (NotOwner) and additive scene switch;
Enter/Exit Play Mode ×3 with *Do not reload Domain or Scene* and with *Reload Scene only*. Owner-reported; no log
attached. 200v200, restart during movement/jump and several fixed ticks per frame have no GUI entry point: covered
by the EditMode/PlayMode suites above (2/100/400 rosters, catch-up k = 2…18), not by manual checks.

**Performance:** see `Docs/PERFORMANCE_BASELINE.md`, "Stage 02.2 + #35 measurement". GC.Alloc samples 0; nonzero native deltas and cold-import leak warnings are qualified in the manager review above.

## Actual Unity results from Stage 02.2 PR #34, 2026-10-26 — compile blocker

The implementer reports a real run under Unity 6000.5.4f1 / Entities 6.5.0 on ASCII Windows worktree `D:/Unity/Project/mlf-s7`. **Current unpatched main/PR #34 FAILS TO COMPILE:** `BattleLifecycleSystem.cs(28,6): CS0579 Duplicate 'UpdateInGroup' attribute`, introduced by PR #28. Manager independently confirmed two annotations in source (not independently run in Unity). This is a CONFIRMED SOURCE-LEVEL / AGENT-REPORTED COMPILE FAILURE, not a mere outstanding NOT RUN.

Only a **TEMPORARY LOCAL UNCOMMITTED patch** replacing the initialization attribute with a thin proxy enabled an experimental run: **186/193 EditMode passed, 7 failed** (6 existing NUnit `TestCase` int-to-ushort bindings and 1 now-obsolete double-attribute expectation), **10/10 PlayMode passed**. Do **NOT** treat these as pass results for merged main. Logs/XML have not been independently reviewed here. Facing implementation in PR #34 is STATIC PASS, not verified clean-build PASS.

Blocker correction [#35](https://github.com/HolyPenguin21/mlf-legends/issues/35) accepted as one narrow PR: only one lifecycle owner in FixedStep; tiny Initialization proxy delegates to same system; adjust Default/Replay/manual fixture order tests; fix six NUnit bindings; retain all pause/catch-up assertions and full EditMode+PlayMode verification. Earlier test sorting warnings: project-authored `PlayerCommandProducer` invalid ordering in isolated fixture; separately Unity-provided `FixedStepSimulationSystemGroup` warning. Distinguish their origin and never claim zero engine warnings without logs.

QA debt #30 stays OPEN until actual clean-code tests are attached and verified. Separate Windows Unicode/Burst #17 remains OPEN.

## Owner disposition: Stage 02 begins with deferred Stage 01 Unity QA (2026-10-26)

**Current status: Stage 01 CODE ACCEPTED / STATIC AUDIT PASS; UNITY VERIFIED = NO.** The owner explicitly approved starting Stage 02 immediately rather than waiting for external Unity tests. [Issue #23](https://github.com/HolyPenguin21/mlf-legends/issues/23) independent bottom-up code audit has been CLOSED as completed. **Do not reclassify unexecuted runtime tests as passing.**

Open [Issue #30](https://github.com/HolyPenguin21/mlf-legends/issues/30) is the **single verification-debt tracker** for actual project import/compile, full EditMode/PlayMode on current HEAD after PR #28/#29, fixed-step sort warnings (including partial Clock and Commands test Worlds that omit Lifecycle), paused scene lifecycle and manual Enter/Exit Play Mode with Domain Reload disabled, and (if feasible) updated full-frame ECS performance. Status **NOT RUN** until XML, Editor.log and/or manual QA evidence from the owner's next Unity session arrives. Historical 114/114 EditMode, 6/6 PlayMode and 2/100/400 measurements remain agent-reported on older code; not a current Unity PASS.

Original [#17](https://github.com/HolyPenguin21/mlf-legends/issues/17) Unicode/Cyrillic Windows-path Burst regression remains OPEN and **NOT RUN** in controlled trials (PR #27 diagnostics only); it is separate from general QA #30. [#25](https://github.com/HolyPenguin21/mlf-legends/issues/25) full-pending cost remains a future conditional performance concern. [#22](https://github.com/HolyPenguin21/mlf-legends/issues/22) is future Stage 02 input pacing.

**Stage 02 first task:** [#31](https://github.com/HolyPenguin21/mlf-legends/issues/31) canonical fixed-tick ECS movement X/Z plus jump Y; when the first PR arrives manager reviews bottom-up and records Unity tests as RUN/PASS/FAIL/NOT RUN exactly as evidenced. Stage 02 may progress with `NOT RUN` only when no observed critical compiler/runtime defect blocks dependent work.

## Manager review of merged Stage 02.1 PR #32 (2026-10-26)

- **Delivery:** [PR #32](https://github.com/HolyPenguin21/mlf-legends/pull/32) merged to main as `e5c58afcc40188ece29f4d296ae18d486be547ce`; [Issue #31](https://github.com/HolyPenguin21/mlf-legends/issues/31) closed. Actual latest source inspected bottom-up: CombatMotion, one CombatMovementSystem, initial component via existing BattleLifecycleSystem.Spawn, queue/clock and phase scheduling, replay snapshots, 39 authored movement EditMode cases, PlayMode paused restart and 2/100/400 benchmark harness.
- **Verdict: STATIC PASS / no established runtime correctness or ownership defect.** Fixed dt, 8-direction normalized movement, Grounded+Jump Pressed, no infinite hold jumping, semi-implicit gravity and flat per-spawn-Y support, 1v1/100/400 same ECS path; End/Restart/Despawn removes motion with the actor. No second World, clock, command consumer or position writer.
- **Test limitations: NOT RUN:** Unity import/Entities codegen/C# or Burst compilation; EditMode/PlayMode; warning-free default-world order; manual Domain Reload; post-change 2/100/400 timing and memory. Old Stage 01 figures do not verify Stage 02 motion. Append actual test evidence to QA debt #30 when available.
- **Next Stage 02.2 test debt (not a blocker for #31):** `CombatReplayTests.RealSystemsRunInTheirDeclaredOrderInsideOneFixedStep`, `DefaultWorldBattleLifecycleTests.DefaultWorldHasOneLifecycleOwnerAndTheCompleteFixedStepOrder`, `CombatBaselineTests.AssertMovementOwner` each require the **whole** State group to contain exactly one movement system. The next facing/action-state writer will make this obsolete. Update them within that next scoped implementation to test Movement's uniqueness and phase ordering while allowing the additional explicit owner. See [#31 review comment](https://github.com/HolyPenguin21/mlf-legends/issues/31#issuecomment-6410821524). No premature test weakening.
- **Future performance watchpoint:** the movement loop writes `CombatPosition` and `CombatMotion` each tick even when grounded/idle. This is correct today; profile effects on ECS change versions/render synchronization at Stage 03 before claiming an optimization is needed.

## Stage 02.1 implementation evidence (#31, 2026-10-26)

Base main: `7ae5459d5b6a5ff9b37bc3750d452c8ce0ea7d41`. Separate PR branch; no autonomous merge.

- Source review: one State-phase motion writer, one existing lifecycle Spawn/cleanup, same runtime
  system in default world, replay and benchmark; Collection -> State movement -> Publication unchanged.
- Prepared EditMode regression cases: 8 directions/idle and distance, both sources and every actor at
  2/100/400, k=2/3/10 catch-up replay, jump Pressed/Held/airborne presses, gravity/landing at nonzero Y,
  rejected/duplicate/stale/unknown/deferred inputs, disabled InCombat, zero-tick paused frames,
  restart during jump, old pending/submission clearing, motion removal on End/Despawn, fresh/repeated
  World jump replay and running-clock guard. Replay records motion and position per completed tick.
- Existing command-matrix expectations change only for actual accepted movement; identity, facing,
  command outcomes, pending ordering, lifetime and timeline assertions remain. Performance archetype
  check includes motion; output scope/names distinguish Stage 02 from the retained historical baseline.
- Prepared PlayMode checks: automatic State-system registration and a real default-world synthetic jump,
  freeze at `Time.timeScale=0`, paused Restart/End cleanup. No visual fighters or new scene.
- **NOT RUN:** Unity import/C# source generation/compilation, Burst compilation, complete EditMode and
  PlayMode execution, sorter warnings in Editor, Domain Reload manual checks, and renewed 2/100/400
  timings/allocations. No Unity Editor, C# compiler or PowerShell runner is installed in this environment.
- **Executed static checks: PASS** — `git diff --check`; pinned Unity/Entities/Burst/URP versions; all
  asmdef JSON files; unique GUIDs and new asset .meta companions; runtime position/motion writer searches
  (existing Spawn plus CombatMovementSystem), canonical dt/enabled-InCombat and shared harness registration.
  These source checks do not establish C# compilation or runtime correctness.
- General post-PR #28/#29 Unity debt remains #30; Windows Unicode/Burst remains separately open #17.
  Strict floating point and replay coverage are same-platform contracts, not cross-platform lockstep proof.

## Stage 02.1 bottom-up re-review (#31 / PR #32, 2026-10-26)

Reviewed from component layout/write sites through Spawn/destruction (including disabled actors),
command validation/deferred input, Collection/State/Publication and both lifecycle boundaries, default
world/replay registration, snapshots and performance harness. No runtime ownership, scheduling or
cleanup defect was found in this source review; it is not execution evidence. Runtime code is unchanged
by this follow-up. Existing #30 Unity/sorter/manual debt and #17 Windows/Burst remain open.

Three verification gaps were corrected:
- The 2/100/400 roster test now compares the **complete per-tick raw replay** between single-step frames
  and one 10-step catch-up frame, for every actor and both command sources, instead of checking only
  final roster motion.
- Added 12 cases for airborne End/Restart requested from Collection, State or Publication at N=2/400.
  The requesting tick must finish with its old actors; cleanup occurs before the next catch-up tick;
  disabled airborne actors/allies disappear, pending old input is cleared, and fresh actors start
  grounded with zero velocity and new IDs at relative tick 1.
- Performance checks now require every declared actor component (including CombatMotion), rather
  than only rejecting unexpected components. Both timing and memory harnesses assert the single
  motion owner's registration in State outside measured regions.

Prepared movement EditMode cases: **39 total**; existing default-world PlayMode coverage retained.
Execution remains **NOT RUN** for Unity/C# source generation, Burst, all runtime tests and profiling.
Only source/asset consistency checks and `git diff --check` were executed successfully.

## Stage 02.2 implementation evidence (#33, 2026-10-26)

Base main: `46af296`. Branch `feature/stage02-33`, [PR #34](https://github.com/HolyPenguin21/mlf-legends/pull/34), merged to main on the project owner's explicit instruction after a bottom-up re-check. Status **MERGED / UNITY-UNVERIFIED** (compile blocker below).

- **Runtime:** `CombatFacingSystem` in `CombatStateUpdateSystemGroup`, `[UpdateBefore(CombatMovementSystem)]`;
  sole post-Spawn writer of `CombatFacing` (searched: Spawn + this system only). Reads only
  `AppliedCombatCommand` of enabled `InCombat`; sign of MoveX, 0 keeps facing. No new component/clock/queue.
- **Tests:** new `CombatFacingTests` (spawn L/R kept, turn sequence incl. repeat/idle/Z-only/diagonals/jump/
  airborne/Attack+Guard, player = AI, 1/2/3/10 catch-up raw replay, 2/100/400 rosters, rejected/stale/
  deferred, disabled InCombat, paused frames + Restart/Despawn/End, restart inside catch-up, clock guard).
  `CombatMovementTests` direction matrix expects facing by X sign + isolated writer-separation test.
  `CombatReplayTests` matrix has explicit per-tick facing; mirror test asserts equal facing. State-order checks in
  replay, default world and benchmark: exactly one facing and one movement owner (by system type), facing first,
  neither in Collection/Publication.
- **Unity run (2026-10-26, Unity 6000.5.4f1, separate ASCII worktree `D:/Unity/Project/mlf-s7`):**
  - **Unpatched branch: FAIL to compile — pre-existing on main, not caused by #33.**
    `BattleLifecycleSystem.cs(28,6): error CS0579: Duplicate 'UpdateInGroup' attribute`
    (Entities 6.5 `UpdateInGroupAttribute` has no `AllowMultiple`; introduced by `6acf1fc`, PR #28).
    So current `main` does not compile in Unity; Stage 01 post-#28 and Stage 02.1 have never run. Needs an
    owner/manager decision on the lifecycle's Initialization registration (Bootstrap scope, not #33).
  - **Local, uncommitted QA patch** (Initialization attribute replaced by a proxy system updating the same
    lifecycle handle, plus the two tests' Initialization-membership lines pointed at the proxy):
    EditMode **186/193 passed**; all 36 facing/movement-matrix/order cases passed, Baseline 2/100/400 and
    FrameNativeMemory passed. 7 failures, none in #33 scope: 6 × `BattleLifecycleTests.EndRequestedInsideAFixedStep…`
    (`[TestCase(..., 0/199)]` int cannot bind the `ushort allies` parameter — pre-existing test defect) and
    `OnlyTheFixedStepClockDeclaresTheLifecycleOrderingDependency` (checks the attribute the patch removed).
    PlayMode **10/10 passed**, incl. default-world State order and paused restart facing.
    These results are evidence for the facing logic only, **not a PASS of the branch or main**.
  - First run also exposed `NativeList.ToArray()` resolving to LINQ (`NotImplementedException`) in State-order
    assertions; the #33 tests now use `AsArray().ToArray()` (old main code had the same pattern).
  - No sorter warning for `CombatFacingSystem`. Pre-existing sorter warnings remain (#30): `PlayerCommandProducer`
    UpdateBefore `AiCommandProducer` and `FixedStepSimulationSystemGroup` UpdateAfter
    `BeginSimulationEntityCommandBufferSystem` are ignored as invalid.
- **NOT RUN:** official 2/100/400 timings (the patched full-suite run wrote local `stage02-33-*` TSVs, not a
  controlled baseline), Burst-off, Domain Reload manual checks, #17 Unicode path. `git diff --check` PASS;
  new `.meta` GUIDs unique.

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


## Issue #17 Windows Unicode/Burst investigation (2026-10-26; INCOMPLETE)

- Inspected fresh `main` `7c17081f76f1425f1ed5a45c176b71290649146d`, Issue #17, PRs #16/#21 and the actual reset test, queue and test runner. **Root cause UNKNOWN; URP/Burst shared cause NOT PROVEN.** Prior spaces-path tests used junctions, so the next comparison uses independent physical clones.
- Available host: Linux x86_64, no Unity Editor or Windows execution environment found. Original XML/Editor/Burst logs are not in the checkout; GitHub Actions returned no runs. Historical PASS/FAIL reports are not rerun evidence.
- Added test-side NUnit output for Editor/assets path/current directory, Burst enabled state and the existing `Done`/`RanManaged` signals. Original assertions, `[BurstCompile]`/`[BurstDiscard]` probe, runtime, ECS architecture, packages and `Tools/Run-UnityTests.ps1` are unchanged.
- Full environment record, historical evidence limits, exact targeted command, physical-path/cache matrix, import diagnostics and remaining acceptance gate: [BURST_PATH_INVESTIGATION.md](BURST_PATH_INVESTIGATION.md).
- Static checks **PASS**: diff whitespace, local document links/fences, pinned package versions and exact source comparison after removing diagnostic writes; no assertions/probe or protected runtime files changed. These are not Unity execution results.
- Bottom-up review corrected reproduction controls: independent cold/warm full-suite and target runs, separate main/diagnostic cache baselines, explicit launch working directory, project-local outputs and the original Cyrillic Windows profile. Diagnostic logging can alter timing/cache state; it is not a fix. No further C# change was justified.
- **NOT RUN:** Unity compilation, targeted Burst test and full EditMode/PlayMode on ASCII, ASCII+spaces and Cyrillic+spaces; existing/clean/warm Library, isolated Burst-cache and Core RP reimport comparisons. This delivery is diagnostic only; #17 and the Stage 01 debt gate remain open.
- **Manager static review and delivery (2026-10-26):** [PR #27](https://github.com/HolyPenguin21/mlf-legends/pull/27) merged into main, commit `9c98220dc53ed0a76db87b92035f95dbda8e44af`. Reviewed all three changed files and current test: exactly two managed `TestContext.WriteLine` additions; Burst probe, test assertion, queue, runtime, packages, asmdefs and runner unchanged. The targeted `-testFilter` pattern is consistent with Unity's documented support for regex; targeted command and Windows path scenarios **not executed**. **Diagnostic PR accepted, Issue #17 remains OPEN/UNVERIFIED.** This is no evidence of Burst compatibility at a Cyrillic project path.
- **Work ordering:** next development is consolidated [Issue #19](https://github.com/HolyPenguin21/mlf-legends/issues/19) (paused End, Domain Reload QA, bounded names formerly #26) independent of Burst/Unicode. **Issue #17 still requires actual Windows evidence or owner disposition before final Stage 01 signoff #23.** No stage transition or retroactive PASS granted.


## Editor error "Host type is not matching any asset type … TraceVirtualOffset.urtshader" (2026-10-25; RESOLVED locally)

- **Symptom:** on every Editor start and batchmode run since ~15:52: `Host type is not matching any asset type at Path Packages/com.unity.render-pipelines.core/Editor/Lighting/ProbeVolume/VirtualOffset/TraceVirtualOffset.urtshader`, logged by `RenderPipelineResourcesEditorUtils.TryReloadContainedNullFields` while URP populates `ProbeVolumeBakingResources`. Not present in the Issue #12 and earlier logs; tests still passed.
- **Root cause reported by the PR #21 agent (local `Library/`, not repository content):** the import results of all 16 Core RP `.urtshader` files were **failed imports** (`DirectoryNotFoundException`) made while the project was located at a Cyrillic scratchpad path (`C:\Users\<Cyrillic>\…\scratchpad\mlf legends proj\`). The import is cached by dependency hash, which does not include the absolute project path, so Unity kept reusing the failed artifacts: each `.urtshader` loaded as `DefaultAsset` with only an `ImportLog`, the `ComputeShader`/`RayTracingShader` references in `UniversalRenderPipelineGlobalSettings.asset` resolved to null, and the reload by `[ResourcePath]` failed. Only the first field (`traceVirtualOffsetCS`) is reported. A shared cause with the Burst failure in #17 is **not established**; see the separate investigation above.
- **Fix:** force-reimport every `*.urtshader` (`AssetDatabase.ImportAsset(path, ForceUpdate)` from a temporary batchmode editor method, not committed; in the Editor: *Project > Packages > Core RP > right click > Reimport*). Before: 16 assets with import errors out of 11202; after: 0. No package, settings reference or code change needed.
- **Verified (agent-run, Unity 6000.5.4f1, `D:\Unity\Project\mlf-legends`):** EditMode **98/98**, PlayMode **6/6**, 0 occurrences of the message in both logs; reproduced and fixed on a disposable copy first.
- **Prevention:** do not open, copy or test the project with its `Library/` under a non-ASCII path. If the message comes back, reimport the Core RP package folder or delete `Library/` (full reimport).
- **Editor data upgrades committed together:** URP global settings `m_RuntimeSettings`, `PC_RPAsset` shader prefiltering, `DefaultVolumeProfile` new fields, Standalone batching entry, new `EntitiesClientSettings.asset` and `SceneTemplateSettings.json`. These are automatic migrations by Unity 6000.5.4f1 / URP 17.5 on project open, unrelated to the error.

## Stage 01.6 ECS performance baseline (Issue #7 / PR #24; manager-reviewed, MERGED)

- **Runtime change (Bootstrap, manager-authorized narrow extension):** `BattleSetup.HumanAllies` / `AiAllies` (default 0 = unchanged 1v1). `Start` reserves `Setup.CombatantCount` IDs in one `TryReserve` (human = first, AI = first + 1, then allies) and spawns allies through the same private `Spawn`, same archetype, leader's team and facing, rows of 10 behind the leader; > 200 per team = `InvalidSetup` before any side effect. Contract in `Bootstrap/BOOTSTRAP.md`, "Rosters". No new system, factory, clock, queue or World.
- **Defect found by the benchmark and fixed:** `Spawn` named every entity `Combatant <id>`; Entities' process-wide name store holds 16 384 unique names until a domain reload and IDs are never reused, so ~40 restarts of 200 vs 200 logged `Max unique Entity Name capacity exceeded` (0.25 s stall) and then no entity got a name. Only the two leaders are named now. Residual: 2 names per battle, ~8 000 battles per Editor domain.
- **Harness:** `Assets/Tests/EditMode/Performance/` (category `Performance`): isolated World from `ReplayWorld.RuntimeSystems`, self-timed test producer, memory probe; timed regions contain only the fixed-step group update or the lifecycle update, with an untimed editor frame between simulated frames. Method, environment and results: [`Docs/PERFORMANCE_BASELINE.md`](PERFORMANCE_BASELINE.md).
- **Measured (agent-run; AMD Ryzen 5 3600, 32 GB; Unity 6000.5.4f1 Editor batchmode; Entities 6.5.0; Burst 1.8.29 on; mean µs per tick, N = 2 / 100 / 400):** idle 12.6 / 13.6 / 16.6; one command per combatant 13.0 / 21.6 / 46.0; full 32-tick pending store 15.4 / 170 / 726; battle start 22 / 66 / 201 per request; GC.Alloc 0 in every series; per-frame native growth 0 B (median and p95) up to 18-tick catch-up frames. Burst off at N = 400: command tick 184 µs, start 682 µs.
- **Open risk for the Commands owner (#4), not changed here:** a full pending store (400 × 32) costs ≈ 726 µs per tick and ≈ 7.4 ms per 10-tick catch-up frame (Editor, Burst on): the processor walks every pending command and rebuilds its key map on every tick. Tracked in [Issue #25](https://github.com/HolyPenguin21/mlf-legends/issues/25) as a **future scale/producer calibration** (not a failed Stage 01 baseline).
- **Tests (agent-run, ASCII worktree path `D:/Unity/Project/mlf-s7`, `Tools/Run-UnityTests.ps1`):** EditMode **114/114 PASS** (98 existing + 10 roster + 6 performance), PlayMode **6/6 PASS**. Performance category alone, Burst on and `--burst-disable-compilation`: 6/6 each. Mutation controls (temporary, reverted): naming every ally → 2 failures; reserving 2 IDs → 8; no AI allies → 9; no team-size bound → 3.
- **Not covered / NOT RUN:** player build measurement; worker-thread scaling (no jobs in Stage 01); movement, collisions, spatial queries, AI, rendering; GC bytes (counter returns 0 in the Editor's Mono); Temp scratch inside Unity's reserved allocator (not observable).
- **Manager acceptance / delivery (2026-10-25):** [PR #24](https://github.com/HolyPenguin21/mlf-legends/pull/24) merged as `0ef2e24f904d7fdc0adb9e961bfd4efce114c2f9`; #7 closed. Static code review covered `BattleSetup` and `AllyOf`, the sole `BattleLifecycleSystem.Spawn`, ID allocator/command consumer, 10 roster regression tests, the benchmark World/producer/timers/memory probe, 6 benchmark cases, and documented hardware/limitations. **No critical architecture/correctness finding**. Unity runs, native/managed memory samples and raw TSV were **not independently executed or opened by manager**; figures and PASS statuses remain agent-reported.
- **Additional Stage 01 stability debt, CONSOLIDATED:** [Issue #26](https://github.com/HolyPenguin21/mlf-legends/issues/26) is closed **as duplicate**, not fixed; all bounded debug-name acceptance criteria are now **Part B of [Issue #19](https://github.com/HolyPenguin21/mlf-legends/issues/19)**. Agent should fix it in one Bootstrap PR along with safe End on pause and Domain Reload QA, after #17.
- **Additional measurement qualification:** reported 0 `GC.Alloc` samples ≠ measured zero managed allocation *bytes*, and reported 0 B native memory growth ≠ no Temp allocations. A prior 1000-update synchronous single-Editor-frame probe observed ~0.7 GB counter growth, but the final realistic frame-paced benchmark **did not reproduce this**; no leak is proven. #25 retains a condition-based performance/memory follow-up.
- **Stage 01 main tasks: 6/6 merged, but Stage 01 is NOT CLOSED.** The mandatory debt sequence is **#17 → consolidated #19 (including former #26) → manager final audit #23** → new-session Stage 02 handoff. #25 full pending store is future performance watchlist, not a closure blocker.


## Stage 01 closure policy (owner consolidation, 2026-10-26; PENDING)

**Issue consolidation (2026-10-26):** #19 is now the sole remaining Bootstrap implementation Issue after #17, including former #26. Closing #26 as `duplicate` is task housekeeping, **not successful QA or a code fix**. #23 stays a separate manager-owned evidence and dependency review; #25 remains a future conditional performance issue. No runtime code has been changed by this issue consolidation.

- **Stage 01 is not yet closed.** #7 benchmark is implemented and manager-reviewed (PR #24, agent-reported 114/114 EditMode / 6/6 PlayMode). **#17 Unicode/Burst** remains unresolved; draft PR #27 provides diagnostic instrumentation but no controlled Windows execution. After #17, deliver one implementation PR for **#19** covering End on pause + Domain Reload QA + permanent-bounded ECS debug names (former #26), then perform #23 final manager audit before Stage 02.
- **#17 remains unresolved** despite PR #21 local reimport success: a genuine retest of the original Burst failure on Unicode-path is still needed.
- **#19 manual QA remains NOT RUN** until an agent or the owner actually performs Enter/Exit Play Mode with domain reload disabled. A recorded `NOT RUN` does not mean verified.
- **Issue #23 final audit:** manager checks actual `main`, every single owner/clock/queue/spawner/ID, cleanup and pending command lifetimes, replay equivalence, measured perf and regression suite evidence. New Stage 01-specific failures become tracked fixes, not ignored notes.
- **Stage 02 handoff:** prepare a complete Russian context message for a **new session** only after the closeout review, with exact merge HEAD, outstanding future-stage Issues, proven test status and first planned Stage 02 work. Tick-aligned input Issue #22 belongs to Stage 02 and is not considered unresolved Stage 01 implementation.
- The visual gameplay scene remains scheduled **after Stage 03 sprite rendering**.

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
| 2026-10-25 | Stage 01.6 performance baseline 2/100/400 | MANAGER STATIC REVIEW PASS / MERGED | PR #24 / #7 closed; reports real 2/100/400 light ECS timings, not full gameplay |
| 2026-10-25 | Issue #7 baseline: EditMode 114/114, PlayMode 6/6, Performance 6/6 Burst on/off, 4 mutation controls | AGENT-REPORTED PASS / MEASURED | Branch `feature/stage01-7`; results in `Docs/PERFORMANCE_BASELINE.md`; manager has not executed Unity or inspected raw TSV/XML |
| 2026-10-25 | Issue #7 lifecycle/benchmark review and closure | PASS (STATIC) / MAIN TASKS 6/6 MERGED | PR #24 commit 0ef2e24, one Bootstrap spawner/allocator, tests/perf harness and documented limitations |
| 2026-10-25 | Full pending 12,800 commands × 400 fighters | MEASURED PERFORMANCE RISK / FUTURE | 726 µs/tick agent-reported; Issue #25; not a gameplay or Stage 01 correctness defect |
| 2026-10-25 | Leader name pool after many World restarts | OPEN / STAGE 01 DEBT | Issue #26, narrow fix before #19 and final Stage 01 closeout |
| 2026-10-25 | Stage 01 completion gate | NOT YET COMPLETE | #17 Unicode/Burst → #26 bounded names → #19 pause/Domain QA → #23 final review; Stage 02 in a new session |
| 2026-10-26 | Issue #17 controlled Windows path/cache matrix | NOT RUN / INCOMPLETE | Linux host without Unity/Windows; diagnostic test output and reproducible protocol only; root cause unknown, issue remains open |
| 2026-10-26 | PR #27 diagnostic code/docs | MERGED / MANAGER STATIC PASS | `9c98220`; no gameplay/rules/probe assertion changed; Windows Unity tests remain NOT RUN |
| 2026-10-26 | Unicode/Burst Issue #17 | OPEN / WINDOWS QA PENDING | Repro on physical ASCII/Cyrillic Windows paths remains mandatory for Stage 01 closure |
| 2026-10-26 | Bootstrap lifecycle + bounded names Issue #19 / PR #28 | MERGED / STATIC REVIEW, MINOR ORDERING FIX OPEN | `4075b154`; same lifecycle across Init/fixed, bounded names, tests authored; Unity tests NOT RUN; sorting annotations need follow-up |
| 2026-10-26 | Stage 01 closeout Issue #23 | BLOCKED BY OUTSTANDING QA | Require review/disposition for both #17 and #19 before new-session Stage 02 |

## Manager bottom-up review of merged PR #28 (2026-10-26; static evidence only)

- [PR #28](https://github.com/HolyPenguin21/mlf-legends/pull/28) **MERGED** to `main` as `4075b154583a5088ac74a7d41b3c3d7ddf6761fc`, while Issue #19 is **OPEN** pending a narrow correction. Manager read actual `BattleLifecycleSystem`, `BattleSession`, `CombatClockSystem`, phase group sorting, `CombatCommandQueue.ResetForBattle`, `CombatSmokeBootstrap`, test fixtures (EditMode frame boundary/replay/ECB/roster, PlayMode pause/scene), benchmark integration and docs. **No conclusive static correctness fault in canonical End/Restart, owner token, ID lifetime or bounded names.**
- **Specific ordering risk (code inspection):** the dual-group `BattleLifecycleSystem` has `[UpdateBefore(typeof(CombatClockSystem))]`, but Clock is NOT in Initialization; `CombatClockSystem` now resides in the normal FixedStep sorting bucket but retains `[UpdateAfter(typeof(BeginFixedStepSimulationEntityCommandBufferSystem))]` where BeginFixedStep is `OrderFirst`. Entities sorting attributes are only valid among common group/bucket peers; the settings can generate `Ignoring invalid UpdateBefore/After attribute` warnings. **WARNING NOT RUNTIME-OBSERVED**; this is a source/documentation risk. Proposed micro-fix in Issue #19 comment: place `[UpdateAfter(typeof(BattleLifecycleSystem))]` on fixed-only Clock, remove lifecycle's `UpdateBefore(Clock)` and redundant Clock `UpdateAfter(BeginFixedStep)` while keeping BeginFixedStep's implicit OrderFirst and collection's explicit ordering. Validate both default World and isolated-world final order and Editor warning output.
- **Unity validation status:** Agent reports static syntax parse 32 C# files, asmdef/JSON/meta checks and diff checks PASS. Unity compile/source-generation/EditMode/PlayMode/performance rerun, `Time.timeScale=0` actual PlayMode and Domain Reload disabled GUI **NOT RUN**. Static checks must not be equated to runtime pass. Evidence in PR #28 and Bootstrap QA matrix.
- **Stage 01 closeout gate:** after ordering micro-fix, decide #19 implementation status on actual static evidence and `NOT RUN` disclaimer; #17 Unicode/Burst still needs Windows QA or owner decision; final independent audit #23 remains required before a new Stage 02 session.

## Issue #19 — pause-safe lifecycle, implementation checks (2026-10-26)

Base: `main` `d817b88` (PR #27 already merged). Scope: Bootstrap plus existing clock/phase ordering
annotations needed to retain `BeginFixedStep ECB → Lifecycle → Clock → Collection`; no clock logic,
command rules, #17 Burst tests/import diagnostics/runner, #25 optimization or Stage 02 work changed.

**Before:** requests only drained before fixed steps; a paused End/OnDisable could leave the old session,
actors and future commands indefinitely. **After:** the same lifecycle handle also drains at Initialization
on every rendered frame, including pause. Start/Restart/End apply at the next safe boundary, not
synchronously in a scene callback; no fake tick, second queue consumer, session owner or destructor.
The buffer is cleared once per batch; repeated empty boundary calls do not repeat a reset. Owned End
still cannot end another scene's battle. Start/Restart retain full preflight and non-reused IDs.

| Check | Evidence / status |
|---|---|
| Bottom-up ownership/dependency review | Source reviewed: Core IDs/InCombat → queue validation/reset/processor → clock and phases → allocator/session/lifecycle → bootstrap OnEnable/OnDisable → EditMode, PlayMode, replay and benchmark world construction. Single writers retained. |
| 1v1 + 200v200 paused-frame Start/Restart/End, pending/submitted/result cleanup, disabled actors, ID and first-relative-tick invariants | Added `BattleFrameBoundaryTests`; **NOT RUN — Unity Editor/runtime absent**. |
| Repeated lifecycle calls do not reprocess Restart / clear newly submitted command | Added sentinel-command regression at a second Initialization followed by fixed tick; **NOT RUN**. |
| Old-owner End with replacement Restart in same / separate paused frames; invalid Restart atomicity | Added frame-boundary regressions; **NOT RUN**. |
| End from all 3 phases / Restart during catch-up, duel and 400 actors | Extended existing `BattleLifecycleTests`; **NOT RUN**. Existing ordinary tick, queue rejection, clock exhaustion, ID exhaustion, cleanup tests retained. |
| Canonical replay with extra lifecycle-only paused frames vs fixed boundaries | Added 1v1/200v200 replay comparison over 3 Restart sessions and final catch-up End; **NOT RUN**. Existing full command/replay suites retained. |
| Actual Time.timeScale=0, unpause, bootstrap disable/enable, real additive CombatSmoke replacement/unload | Extended default-world PlayMode tests with unscaled waits and finally restoration; **NOT RUN**. |
| Enter/Exit Play Mode with Domain Reload disabled (both scene reload modes) | **NOT RUN**. Environment probe: no Unity/Unity Hub executable or Unity assemblies found; no display (`DISPLAY` unset). A GUI session could not be launched. Follow `Bootstrap/BOOTSTRAP.md`, record evidence before #23 disposition. |

Future actor ECB writes must play back in the producing tick's EndFixedStep, because paused-frame cleanup
can precede the next BeginFixedStep. No runtime ECB producer exists today. Review this contract when later
stages add jobs/structural writes. Real Unity 6000.5.4f1 / Entities 6.5.0 compilation, automatic dual-group
registration/order, all EditMode/PlayMode suites and the Domain Reload GUI scenario remain verification
risks, not claimed PASS. #17 remains OPEN and its Windows evidence is independent; Stage 01 is not closed.

### Bounded diagnostic names (part B / former #26)

**Before:** two unique `Combatant <ID>` names per battle accumulated globally even after entity destruction.
**After:** only `Combatant Human` / `Combatant AI`, with unnamed allies; IDs remain monotonic and authoritative.
Updated every `GetName` expectation and the Domain Reload smoke instructions. Added a regression that
inspects every actor over 45 alternating 1v1/200v200 Start/End and Restart sessions, requiring exactly the
same two nonempty names and fresh IDs on every iteration. **Unity execution: NOT RUN**. No global store
mutation/reset, mutable naming counter, additional singleton, spawner or identity lookup was introduced.

### Checks actually executed for this delivery

- **PASS — static syntax only:** tree-sitter C# parser, all 32 C# source files, zero syntax errors.
  This does not compile Unity references, source generators, Burst or tests.
- **PASS — JSON / Unity identity metadata:** all 5 asmdef/package JSON files parse; all 69 asset `.meta`
  GUIDs are valid and unique; the new test has its own `.meta`.
- **PASS — diff hygiene:** `git diff main --check` has no whitespace errors. Fresh `git fetch origin main`
  confirmed base `d817b88bdbf9b7eefb86b81dc3321ed92ad330c6` still current.
- **PASS — source preservation comparison:** lifecycle `OnUpdate`, `Apply`, `DestroyCombatants`, `Despawn`,
  `OnDestroy` method bodies and every clock method body equal the base. Queue/processor/ID allocator,
  original replay tests/driver, #17 command test source, Windows runner and Burst investigation document
  are byte-for-byte unchanged. No runtime class/struct added. Checked the two lifecycle memberships,
  explicit clock/collection ordering annotations and exactly two literal actor-name callsites.
- **NOT RUN — actual Unity compilation, all EditMode/PlayMode regressions (including existing ID/queue/replay
  suites), performance rerun and manual Domain Reload GUI scenario.** No Unity executable, assemblies or
  display are available. No stubbed substitute was used and no runtime test result is inferred from source checks.

Review risk: automatic scheduling of the same handle in both groups and the ordering annotations must be
confirmed in Unity 6000.5.4f1 / Entities 6.5.0. The default-world membership/order test and attribute-driven
replay worlds cover this when run. Cleanup is next-frame bounded while paused, not synchronous at unload.

### Bottom-up follow-up review of PR #28 (2026-10-26)

Re-read the complete production path: Core identity/enabled state → allocator → queue preflight/reset,
processor buffers and AppliedCombatCommand → clock/phase ordering → lifecycle batch copy/clear and
structural changes → bootstrap enable/disable/owner tokens → manual, attribute-driven replay and
benchmark Worlds. No additional runtime defect was established by source inspection. Runtime code,
queue/ID rules, #17 sources and the original replay/benchmark sources are unchanged by this follow-up.

Two test defects were corrected:

1. Default-world polling helpers created EntityManager-owned persistent EntityQueries without disposing
   them; the new pause waits amplified the pre-existing resource leak. All queries in the affected
   PlayMode fixture now have an explicit using lifetime, as does the new EditMode RequestCount helper.
   The temporary entity array used during smoke-scene inspection is also disposed immediately.
2. The previous PR revision changed the real smoke-scene switch test into a pause-only test, losing its
   ordinary-time counterpart. Both normal and paused scene replacement/final unload now run through
   the same scenario in separate UnityTests. Async scene operations use an unscaled bounded wait.

Additional missing integration coverage was prepared:

- End/Restart requested on the **last** catch-up tick, then Initialization without any intervening tick:
  1v1 and 200v200, pending/submitted/results cleanup, owner/ID replacement and first resumed relative tick 1.
  Earlier tests requested on the first catch-up tick, so the next fixed boundary could mask this case.
- Restart + owned End before the first World tick: no live battle, no retained request and no revival.
- Real EndFixedStep ECB playback before frame-boundary cleanup for 1v1/200v200. A test-only, disabled-auto-
  creation probe records an actor position write plus an End request; the write must play back in its
  producing tick before Initialization destroys the actor. No new runtime writer/destructor was added.

**Actually executed:** all 32 C# files parsed without syntax errors; JSON/meta identity checks and
`git diff --check` passed; source comparison confirms this follow-up changes tests/docs only and #17
remains untouched. **NOT RUN:** Unity compilation, the additional/retained EditMode/PlayMode scenarios,
profiles and the Domain Reload GUI scenario. Unity remains unavailable; source review is not runtime PASS.

## Independent Stage 01 code audit #23 (2026-10-26; STATIC PASS, Unity NOT RUN)

The manager audited current `main` `5bf4493a3280628a93be1841997682a3a6f6f981` bottom-up across all 18 combat runtime C# files, core contracts/asmdefs, command queue/processor/reset, World clock/phase sorting, Bootstrap/start/end/restart/owner tokens, 2/100/400 actors, replay, benchmark and their test fixtures. **No critical architectural or implementation defect established by source review.** In particular, there is one lifecycle spawner, one clock writer, one command consumer/reset path, World-scoped monotonic IDs and constant diagnostic leader names. Pause-safe Initialization and fixed-tick paths use the same lifecycle system and request buffer.

**Non-blocking source-level test fixture discrepancy:** `Assets/Tests/EditMode/Simulation/CombatClockTests.cs` and `Assets/Tests/EditMode/Commands/CombatCommandProcessorTests.cs` assemble isolated FixedStep Worlds with Clock but no Lifecycle. Current Clock carries `UpdateAfter(BattleLifecycleSystem)`; the target is absent during their `SortSystems()`. There **may** be an invalid-ordering warning in Unity; no editor output exists to substantiate it. Inspect test output and fix fixtures minimally if confirmed; avoid runtime architectural expansion. Default/replay/benchmark Worlds include both systems.

**Evidence boundary:** 114/114 EditMode and 6/6 PlayMode tests are historical agent-reported results from **before** PR #28/#29. **NOT RUN on current HEAD:** Unity compilation/Entities codegen/Burst, full EditMode/PlayMode, actual sorter logs, paused/scene tests, Domain Reload disabled GUI and post-change full-frame profiling. The benchmark baseline is light ECS overhead, **not** gameplay frame time. Windows Unicode/Burst investigation remains open as #17 (root cause unknown; original failure not retested). Future full-pending cost #25 and Stage 02 tick-input pacing #22 remain separate.

**Status:** #23 independent **source-code audit complete / PASS**, but final Stage 01 acceptance as `Unity verified` is **not** supported. #23 is an explicit owner-disposition/checkpoint for missing external Unity evidence, *not* a reason to redo architecture blindly. Full audit details in [issue comment](https://github.com/HolyPenguin21/mlf-legends/issues/23#issuecomment-6405137109).

## Manager acceptance of PR #29 and Issue #19 (2026-10-26; STATIC PASS)

- [PR #29](https://github.com/HolyPenguin21/mlf-legends/pull/29) merged in `main` as `c14d011824ed92a660919256d336e78638df72b8`; full patch and final Runtime attributes inspected. `BattleLifecycleSystem` has no `UpdateBefore/After` constraint, is still in Initialization (OrderFirst) + normal FixedStep, and `CombatClockSystem` in FixedStep declares `UpdateAfter(BattleLifecycleSystem)`. BeginFixedStep/EndFixedStep have their built-in OrderFirst/OrderLast; Collection after Clock; State after Collection; Publication after State. Runtime method bodies, ID/clock/queue and tests of original #17 are unchanged.
- [Issue #19](https://github.com/HolyPenguin21/mlf-legends/issues/19) **CLOSED as implementation completed**. Pause-safe End/Restart and bounded leader naming from PR #28; ordering annotation follow-up PR #29. **Not Unity verified**: compilation, EditMode/PlayMode, no-domain-reload manual, actual Unity sorter logs and post-refactor whole-frame performance are NOT RUN. Delivery verified by static inspection only.
- **Final audit #23 additional check:** both `CombatClockTests.CreateClockWorld` and `CombatCommandProcessorTests.SetUp` construct incomplete Worlds with Clock but without Lifecycle, yet call `FixedStep.SortSystems()`. Clock's new `UpdateAfter(BattleLifecycleSystem)` may be ignored and produce `Ignoring invalid [UpdateAfterAttribute]` warnings because the target is absent. These warnings are **NOT OBSERVED** without Unity; they are not shown to break the real default World, which contains both systems. In first available Unity EditMode test run inspect full Editor.log and test failures, and if warnings are emitted fix only the responsible fixture/contract and retest. Do not invent a second clock/lifecycle or new owner.
- #17 remains OPEN; controlled Windows Unicode/Burst reproduction NOT RUN. #23 remains OPEN as independent final Stage 01 gate: unresolved manual QA and missing Unity runtime test evidence need explicit disposition before describing Stage 01 as verified.

## Issue #19 — fixed-only ordering annotation follow-up (2026-10-26)

Base: fresh `main` `d6acf7b104f7165b2347cd4d5a6097ab87fd25b9`, after manager review of merged PR #28.
Removed Lifecycle's `UpdateBefore(CombatClockSystem)`. Fixed-only Clock now declares
`UpdateAfter(BattleLifecycleSystem)` instead of the redundant cross-bucket dependency on BeginFixedStep ECB.
BeginFixedStep `OrderFirst` and EndFixedStep `OrderLast`, the two memberships of the same lifecycle handle,
Collection/State/Publication dependencies and every runtime method body are unchanged. No new systems,
BattleRequest processing, queue rules, identities, names or pause behavior were introduced.

Bottom-up source review: queue preflight/reset and actor cleanup → lifecycle drain/session/allocator →
clock and phase dependencies → bootstrap owner tokens → isolated/manual, replay/benchmark and default
World construction. Only annotations/comments changed in runtime source. A new annotation regression
rejects the old declarations even when their resulting relative order happened to be correct. The
attribute-driven replay order test and default-world PlayMode test verify the complete seven-system
chain and the same Lifecycle handle in Initialization with no Clock there:

`BeginFixedStep ECB → Lifecycle → Clock → Collection → State → Publication → EndFixedStep ECB`.

Actually executed:
- **PASS, static only:** all 32 C# files parsed with zero syntax errors; all runtime method bodies compared
  byte-for-byte to base; no processing change. The annotation graph permits exactly the required chain
  among 5,040 permutations, using Unity's documented built-in OrderFirst/OrderLast bucket contract.
- **PASS:** 5 asmdef/package JSON files parse; 69 unique valid meta GUIDs; `git diff --check` clean.
  #17 test source, Windows runner and investigation document unchanged.
- **NOT RUN:** Unity compilation/source generation/Burst; annotation and actual order tests; retained
  1v1/200v200 lifecycle, ID, queue, catch-up, pause/unpause, scene, naming and replay regressions;
  performance rerun; manual Domain Reload GUI scenario. No Unity executable/assemblies/display available.

Actual Editor sorting logs, including intentionally partial clock/command test Worlds that omit Lifecycle,
remain unobserved; source checks are not proof of warning-free runtime sorting. #17 remains open and #23
independent manager acceptance remains required. This follow-up **was merged as PR #29**, but its actual Unity test execution remains NOT RUN.

## Acceptance history supersession (2026-10-26)

Earlier entries calling #23 a blocker of Stage 02, or describing #19/#26 as not yet merged, are historical dated notes. **Current controlling status:** #19 fixed and closed via merged PR #28 + #29; #23 static audit completed and closed by owner-approved transition; #30 deferred Unity evidence OPEN; #17 Cyrillic path Burst OPEN; Stage 02 #31 implemented and manager STATIC PASS via merged PR #32 (Unity NOT RUN). These statuses supersede the earlier sequence without rewriting historical agent claims.
