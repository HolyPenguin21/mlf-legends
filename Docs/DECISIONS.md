# MLF Legends — Decision log

## Stage 03 starts in parallel with character art — owner decision (2026-10-26)

- Owner approved the next ordered task: combine 03.1 atlas contract/loading and 03.2 first GPU instanced renderer/depth in [#38](https://github.com/HolyPenguin21/mlf-legends/issues/38), one branch/PR. Final female-mage art is produced independently; use a generated technical atlas for code validation.
- Reuse the existing ECS authority and CombatSmoke/camera. One shared render path for 2/100/400; render reads position/facing, owns only presentation/cache/GPU resources. No per-fighter GameObjects, gameplay writers or duplicate queues.
- Match the art manifest v1: source_size/pivot/trim, stable frame/clip ids, multiple pages and reflected anchors. #38 specifies the exact markers representation before production export; dimensions are data. The definitive machine-readable example/schema is the implementing agent's first deliverable.
- Explicit frame selection and graphics depth tests are in this task; automatic idle/walk/jump selection, animation transitions and final character integration follow in 03.3. Art clip availability does not authorize implementing attacks/dash/magic early.
- Scope is authorized, implementation is not yet delivered or verified. The artist and code agent must validate a first real frame/JSON when available. Existing QA debt stays qualified.

## Stage 03.1 + 03.2 implementation choices (#38, agent PR, 2026-10-26)

- **Contract:** manifest v1 exactly as dispatched (`Docs/SPRITE_ATLAS_CONTRACT.md` + validated example). Clarified for the
  artist: frames reference pages by zero-based index; markers are `name → array of local indices`; unknown keys are
  rejected (optional top-level `meta` is ignored); each rect needs 2 px extrusion inside its page, grown rects must not
  overlap; only `authored_facing: "right"`.
- **JSON:** the installed `com.unity.serialization` 6.5.0 `SerializedObjectReader` (runtime asmdef reference added; no
  Newtonsoft, no package change). Its object view enumerates every member, so duplicate keys are detected; numbers are
  checked against the strict JSON grammar by the validator.
- **API:** `Graphics.RenderMeshPrimitives` with a custom URP shader and structured buffers (instance 24 B, frame 32 B),
  one call per atlas page (the installed overload has no start instance → `_InstanceOffset` per page block). Rejected
  for now: `RenderMeshInstanced` (matrix/property-array instancing, batch limits), indirect/compute culling and an
  Entities Graphics path (second renderer) — no measured need. Pages may differ in size, so no Texture2DArray.
- **Geometry:** vertical card above the pivot (world up / cos pitch) and ground card below it (/ sin pitch): exact
  screen image of the art, upright-body depth, feet on the floor. Start calibration 96 px = 2 world units (one
  configurable scale). Flip mirrors geometry around the pivot (trim offset and image together), never only UVs.
- **Depth:** alpha clip + ZWrite/ZTest in forward, DepthOnly and DepthNormalsOnly passes (PC renderer's SSAO uses
  DepthNormals; no URP feature changed). Equal depth: render-only push of ≥ 0.002 units per card ordered by
  (depth, CombatantId), lowest ID in front; constant bias 0.02 units towards the camera. `CombatPosition` untouched.
- **Order/ownership:** scene `CombatSpriteRenderer` in `LateUpdate` with `[DefaultExecutionOrder(100)]` after the camera;
  no ECS system/writer/tick. Later-enabled renderer owns a World + Camera; the other stays idle with one warning.
  Technical fixture generated in code for CombatSmoke (no PNG committed); art plugs in through a `SpriteAtlasCatalog`.
- **Frame selection:** explicit preview seam only (`SetFrame`/`SetClipFrame`/default frame); no animation clock or state
  binding (Stage 03.3). Import settings are documented and validated, not enforced by a project-wide postprocessor.

## Combined Stage 02.3 + 02.4 delivery — owner decision (2026-10-26)

- Owner requested combining real input (02.3) and fixed-angle orthographic camera (02.4). Expand existing [Issue #22](https://github.com/HolyPenguin21/mlf-legends/issues/22); one branch, one PR, joint acceptance. Substage numbers remain traceability labels. This supersedes any earlier requirement to deliver the camera in a separate PR.
- Preserve separate responsibilities inside that delivery: Input System/raw device state → one producer in existing Collection/queue; presentation camera reads HumanCombatant/CombatPosition after simulation and writes only camera state. Held each tick, Pressed consumed once; pause/focus/Restart clear stale edges. Camera follow/pan/zoom keeps one fixed configurable angle; simulation stays independent of visibility.
- Reuse existing InputSystem_Actions asset and CombatSmoke Main Camera where appropriate. Bindings/numeric camera settings are provisional and configurable. No alternate command queue, clock, combatant views, Cinemachine framework or early visual scene; the first sprite-backed visible scene remains Stage 03.
- PR #36 / #35 is manager-accepted. #30 and #17 remain separate nonblocking QA; their latest findings are in QA.md. This decision prepares the combined implementation, it does not mark either substage implemented.

## Stage 02.3 + 02.4 implementation choices (#22, agent PR, 2026-10-26)

- **Producer placement:** `PlayerInputCommandSystem` (`SystemBase`) inside the existing `CombatCommandCollectionSystemGroup`,
  before the `OrderLast` processor, auto-created in the default world only. It reads an attached
  `PlayerInputCapture`; no capture → inert. No frame-rate `Tick + 1` path, no accumulator, no manual `InputSystem.Update()`.
- **Input update mode:** the Input System default `ProcessEventsInDynamicUpdate` (no settings asset). Verified in the real
  PlayerLoop: `PreUpdate.NewInputUpdate` → `Update` (MonoBehaviours, then `SimulationSystemGroup` with N fixed ticks) →
  `PreLateUpdate` (camera `LateUpdate`). `ProcessEventsInFixedUpdate` would follow MonoBehaviour `FixedUpdate`, unrelated
  to the ECS tick, and was rejected.
- **Sampling:** Move = `ReadValue` + one quantization policy (dead zone 0.25, 8 × 45° sectors); Held = device state of
  the bound buttons (so a button held before enabling is Held but never Pressed); Pressed = `performed` callbacks, OR-ed,
  consumed by the first produced tick. Presses are dropped (never replayed) on pause (`Time.timeScale = 0`), focus loss
  or gain, device removal/reset, End/Restart/new human (binding = battle number + human ID) and adapter attach/detach.
- **Ownership:** each capture and the camera clone their action map from the shared asset and dispose the clone; the
  shared/project-wide asset is never enabled or disabled by them. The latest enabled `PlayerInputAdapter` attaches
  (bootstrap-like ownership); a replaced adapter cannot detach its successor. Static process-global state: none.
- **Bindings:** existing asset extended: Guard action (L, gamepad right shoulder), J → Attack, K → Jump; arrow keys moved
  from Player/Move to the new Camera map (Pan) to avoid conflicts; Camera map Pan/Zoom/ZoomStep/Recenter. UI map unchanged.
- **Camera:** `CombatCameraController` on CombatSmoke's existing Main Camera (now orthographic, 40° pitch); `LateUpdate`,
  unscaled presentation delta, read-only queries; pan dropped on a new battle, zoom kept; no Cinemachine, packages,
  smoothing pipeline, per-fighter objects or new scene. Altitude follow defaults to 0 (configurable).
- **Manager review corrections (PR #37, 2026-10-26):** (1) losing the camera target (End/Idle) keeps pan and view exactly;
  only a new valid target drops the pan. (2) `Attach` binds to the session current at that moment (presses before it are
  dropped, fresh ones reach the next tick). (3) `PlayerInputAdapter` checks the default World every frame and moves its
  binding when a World appears or is replaced; competition is resolved in the producer by enable order (latest enabled
  wins whichever attaches first; each World tried once). (4) Buttons held across a focus change or a device
  disable/enable/reset are blocked until the device reports them released, so a restored hold is Held, never Pressed;
  conservative edge: a button released while unfocused and never re-reported loses its first press after regain.
- **Assemblies:** `Unity.InputSystem` added to the runtime asmdef (Input/ and Presentation/ folders, no new runtime
  assembly); test asmdefs add `Unity.InputSystem` + `Unity.InputSystem.TestFramework`. Pure ECS suites stay device-free.

## Compile blocker remediation decision (2026-10-26)

- Owner's project was compiled in Unity 6000.5.4f1 by the Stage 02.2 implementer. Current main failed with CS0579 from two `UpdateInGroup` attributes on `BattleLifecycleSystem` introduced by PR #28; the manager verified the duplicated annotations in source. This is a real, presently blocking build issue, not historical `NOT RUN`.
- **Manager accepted option A:** preserve one `BattleLifecycleSystem` declared only in FixedStep, with its existing sole request-drain/session/ID/queue/spawner ownership and `CombatClockSystem.UpdateAfter(BattleLifecycleSystem)`. Create a minimal Initialization-only forwarding proxy to update the SAME lifecycle handle when fixed ticks are paused; no separate request processing, no duplicate actor owner or custom bootstrap. Verify real Entities 6.5 update API and all Default/Replay/manual test Worlds in Unity.
- Also fix six pre-existing `BattleLifecycleTests.EndRequestedInsideAFixedStep...` NUnit TestCase int-vs-ushort binding failures in that same narrow PR. New blocker [#35](https://github.com/HolyPenguin21/mlf-legends/issues/35), acceptance logs in #30. Experimental agent patch with 186/193 EditMode and 10/10 PlayMode is not proof of main passing. Before accepting Stage 02.3 runtime/input integration, restore a clean Unity build.
- **Implemented in #35 (agent PR):** `BattleLifecycleInitializationProxy` (`ISystem`, no Burst, `RequireForUpdate<BattleSession>`, looks up the lifecycle each frame via `WorldUnmanaged.GetExistingUnmanagedSystem` and calls `SystemHandle.Update`). Entities' `UpdateSystem` restores the executing-system state around the nested update and honours the lifecycle's `Enabled`/`RequireForUpdate`. Partial clock/processor test worlds now include the inert lifecycle so the clock's `UpdateAfter` target exists.
- This is a technical compatibility exception to the general ability to defer unavailable Unity tests: a known reproducible compile error is NOT a mere unexecuted check. #17 Unicode Windows path Burst stays separate.

## Approved / fixed
- Repository: `HolyPenguin21/mlf-legends`, branch `main`.
- Owner's editor: **Unity 6000.5.4f1**. Use **Universal 3D (URP)** for 3D environment, not 2D template.
- Current work is **only combat**; do not implement or expand campaign design.
- Upgraded LF2/Nekketsu appearance: ~4-head-high expressive 2D sprites in a real 3D scene, 2.5D.
- Orthographic camera at a **fixed angle** over all zoom levels, plus panning and follow.
- ECS/DOTS + Burst-based simulation, specialized GPU sprite rendering, custom collisions and custom navigation. No full per-fighter Animator, Rigidbody or NavMeshAgent simulation.
- One authoritative combat system for a duel and full-scale **200 vs 200** battle. Human controls one fighter; others are AI.
- Attack facing mainly left/right with configurable Z-depth tolerance; attacks can hit multiple enemies.
- Moderate combat tempo, with windup/active/recovery, blocking and counters important.
- **Front-facing guard with stability**, guard break and **timely block advantage** without automated counter.
- Short directional defensive dash bound to **L (guard) + new direction press**, including depth and diagonals, with collision intact.
- All combatants collide. Allies yield somewhat; human fighter can press through allies with slight resistance. Enemies obstruct.
- Basic direct attacks don't hurt allies; skill-generated environmental/physical consequences can affect friend and enemy.
- Defeated fighters do not respawn before battle ends.
- **S.P.E.C.I.A.L.** and anatomical injuries (eyes, arms, legs, head/torso) with functional consequences.
- Prepared magic spells with several type-dependent resources, and environmental effects, postponed until later stages.
- Implementation roadmap 00–13, quality gates, maintainable clear ownership and systematic dependency checks.
- **Delivery policy (2026-10-25): Unity runtime/EditMode/PlayMode checks are NOT a blocking condition for commit, PR merge, push or continued implementation when the user cannot execute them.** Always run feasible static checks, mark unexecuted Unity checks **NOT RUN** in QA, and leave them for later verification. Never claim unexecuted tests passed; distinguish delivery from verified completion.

- **Stage 00 (2026-10-25):** owner confirmed all prepared Unity tests passed. Record **OWNER-REPORTED PASS**, without asserting the assistant independently reviewed run logs.
- **Agent management (2026-10-25):** assistant prepares GitHub Issues with technical scope, dependencies, acceptance criteria and PR review; Stage 01 is broken down into Issues #2–#7. Other agents work on separate branches, do not merge independently. Manager keeps ROADMAP and QA current.

## Stage 02 start and outstanding Unity QA (owner decision, 2026-10-26)

- **Stage 02 is AUTHORIZED to start now**, overriding the former requirement to first run all Stage 01 Unity/Windows QA. [#23](https://github.com/HolyPenguin21/mlf-legends/issues/23) independent source-code audit is **completed/CLOSED, STATIC PASS**; this is not a claim that Unity compiled or executed after PR #28/#29. This new owner decision is the controlling schedule rule; the earlier Stage 01 gate below is historical.
- All **not-yet-run Unity verification** is kept as separate, open [Issue #30](https://github.com/HolyPenguin21/mlf-legends/issues/30): compile/import, real EditMode/PlayMode, system sorting (including incomplete Clock/Commands test fixtures), paused lifecycle, Domain Reload disabled manual smoke, and refreshed post-change 2/100/400 perf. Status remains **NOT RUN** until evidenced. If actual tests reveal a material regression, fix the responsible subsystem with a narrow Issue/PR.
- The original Windows Cyrillic-path Burst discrepancy [#17](https://github.com/HolyPenguin21/mlf-legends/issues/17) stays **OPEN** and unverified; PR #27 was diagnostics, not repair. Use ASCII project paths as the known historical working setup for ongoing development until controlled Windows comparison can be run.
- Stage 02 starts with [#31](https://github.com/HolyPenguin21/mlf-legends/issues/31) deterministic fixed-tick ECS X/Z movement + Y jump reading existing `AppliedCombatCommand`. This task must not introduce visual proxies, a second simulation architecture, a new command queue or input adapter, camera or collisions. Facing/action-state owner, [#22](https://github.com/HolyPenguin21/mlf-legends/issues/22) real frame-to-tick input and read-only fixed-angle orthographic camera follow/zoom/pan follow in distinct reviewed Stage 02 scopes.
- Earlier owner preference for a standalone Stage 02 conversation **is not a technical start gate**. Manager prepares a complete handoff for the new session, while the first Stage 02 GitHub Issue may be issued and implemented immediately. Stage 03 remains the **first visible user-facing gameplay scene** once GPU sprite rendering works; no Stage 02 capsule/proxy demo.

## Stage 02.2 facing-on-movement rule (owner decision, 2026-10-26)

- **Owner approved: fighters automatically face their direction of movement.** `CombatFacing` is currently **horizontal world X only**. On the authoritative fixed simulation tick, an accepted `AppliedCombatCommand.MoveX = +1` faces `Right` and `MoveX = -1` faces `Left` (including diagonal and airborne movement).
- `MoveX = 0` preserves existing facing: idle, vertical-on-screen/Z-only navigation, jump in place, implicit None and no accepted command do not turn the actor. Future Stage 03 sprites mirror this authoritative facing; no 360° orientation or enemy-autofacing/backpedal mode.
- Same rule for human and AI. Current guard/attack held/pressed fields do not lock the facing while their action-state mechanics are not implemented; future guarding/attack states may add explicit action-lock rules at their responsible layer, with approval and tests.
- Keep `BattleLifecycleSystem.Spawn` as initial facing writer, add exactly **one** fixed-tick facing owner in `CombatStateUpdateSystemGroup` after command collection; explicitly order it before existing `CombatMovementSystem`. Neither input producer nor command processor nor renderer nor movement system itself becomes another facing writer. Original component remains signed `FacingDirection.Left=-1, Right=+1`.
- Implementation issue: [#33](https://github.com/HolyPenguin21/mlf-legends/issues/33). It also repairs pre-existing tests that incorrectly freeze facing for every movement direction or assume exactly one State-system; no other gameplay systems, action state expansion or visible scene in this PR.

## Stage 02.1 motion implementation choices (#31, 2026-10-26)

- One `CombatMovementSystem` in State owns runtime position and compact vertical kinetics; existing
  lifecycle Spawn initialises motion for every roster actor. No separate input path or orientation writer.
- Temporary landing support is each actor's initial Y, preserving all legal finite BattleSetup heights.
  Grounded Jump Pressed sets upward velocity; semi-implicit gravity runs through None; landing clamps
  to support and zeros velocity. Held never initiates/repeats jumps; airborne presses are not buffered.
- Shared provisional tuning is 4 world units/s movement, 8 world units/s jump impulse, 20 world units/s²
  downward gravity. No acceleration/inertia; full horizontal control in air. These are implementation
  starting values for calibration, not newly approved balance. See Simulation/SIMULATION.md.
- Future Stage 04 terrain/body resolution extends this position owner and replaces spawn-height support.
  No visual scene or proxy actors. No Unity execution/performance PASS claimed for this implementation.

## Visual-scene milestone (owner decision, 2026-10-25)

- Follow the existing roadmap in order: Stage 01 ECS, Stage 02 movement/camera, Stage 03 GPU sprite renderer.
- **No early temporary gameplay scene, capsule/proxy visual fighters or separate demo/vertical slice during Stage 02.** Camera and movement are implemented against the existing ECS authority and testable without an early display scene.
- Create the first **visible** scene only **after the sprite renderer is implemented and usable in Stage 03**, integrating the already-built movement and camera with actual sprite-backed combatants. This is not a request to defer all technical scenes: `CombatSmoke.unity` remains as the Stage 01 test/smoke scene.
- Do not create gameplay parallel paths solely to make the visual scene work. Stage 03 should display ECS state; combat attacks are not promised until Stage 05.

## Historical Stage 01 hold — superseded by owner decision 2026-10-26

- Stage 01 performance Issue #7 **is integrated and manager-reviewed** (PR #24, commit `0ef2e24f904d7fdc0adb9e961bfd4efce114c2f9`); the six main Stage 01 tasks are delivered, **but Stage 02 must not start yet**. Stage 01 follow-up **#17** (Unicode Windows path/Burst) has **diagnostic-only PR #27 MERGED**, but its Windows repro remains **NOT RUN / root cause UNKNOWN**. To avoid unnecessarily idling independent work, **consolidated Bootstrap PR #28 for Issue #19 was MERGED** (`4075b154`) and statically reviewed: paused End/scene exit and bounded two-name EntityNameStore policy are implemented, and its cross-group/cross-bucket ordering correction was **delivered in PR #29** (`c14d011824ed92a660919256d336e78638df72b8`). **Issue #19 CLOSED as implementation complete**, based on manager's static code/ownership/order review only. Unity compilation, EditMode/PlayMode, full sorting logs, benchmark rerun and Domain Reload GUI remain **NOT RUN** and must be explicitly reviewed/dispositioned in #23. **#17 Windows evidence remains a separate mandatory/owner-disposition requirement**. Conduct an **independent manager-led** Stage 01 audit **#23** only after both #19 and #17 have been resolved or the owner explicitly disposes of the unavailable Windows verification. No original test may be called PASS without being run; unresolved manual QA needs the owner's explicit disposition. #25 full-pending-command CPU cost is a measured conditional future scale risk, deferred until bulk input/AI producers or Stage 12, not a failed Stage 01 baseline.
- Only once Stage 01 closeout has been reviewed, the manager prepares **one full self-contained Russian context handoff for a new ChatGPT session**: repository, `main` HEAD, Unity/Entities/Burst/URP versions, ECS writer/owner contracts, integration and benchmark evidence, remaining later-stage Issues, first Stage 02 agent task, architectural constraints and PR workflow. Stage 02 will be planned and executed **in the new session**, not silently in the current thread.
- Input pacing Issue **#22** is a Stage 02 feature dependency, not pre-emptive Stage 01 implementation. Do not create an early playable visual scene in Stage 02; after Stage 03 GPU sprite renderer, assemble the first visible fight scene, keeping ECS authoritative.

## Battle restart and time policy (manager decision, 2026-10-25)

- **Do not recreate or reset the default Unity ECS World when restarting an individual fight.** A real World teardown is reserved for ending the application/game World, not for ordinary fight restart. World-scoped `SimulationClock` stays the single fixed-step authority and its unsigned `Tick` stays monotonic.
- **Battle session is not a second clock:** the battle bootstrap (#5) records an absolute `BattleStartTick` at a fixed-tick-safe boundary. Relative fight time is `SimulationClock.Tick - BattleStartTick`; the first simulated action belongs to relative tick 1. Replay tests (#6) record relative ticks and seed so the same sequence does not depend on World uptime.
- On restart, the battle lifecycle owner must stop/clear old actors and battle-only state, call the canonical command processor (#4) to clear queues/accepted history, then spawn new actors. No leftover future command, cached Entity handle or battle-local result may leak between fights.
- **Combatant IDs are monotonic and non-reusable throughout one World**, including repeated battles, allocated by one bootstrap owner (#5); this prevents late commands targeting a new fighter by reusing an ID. The allocator must reject exhaustion rather than wrap to `0`.
- Avoid implementing a second world, second clock, a session-time accumulator or duplicate command processor. Test repeated fights in the same World, plus separate-new-World isolation.
- If `SimulationClock` reaches `uint.MaxValue`, treating the World as exhausted and requiring a deliberate restart of the real World is an exceptional case, not a normal battle-restart mechanism.

## Deferred but previously discussed — campaign
- Strategic campaign is turn-based with months or years per turn.
- Individual character age, illness, injury, potential growth/decline, inheritance, childbirth and recruitment.
- Player controls one campaign character; after death player chooses ending campaign or continuing as a different character.
- No campaign work now; preserve these choices as background only.

## Open questions / values not yet approved
- Compatible Entities, Entities Graphics and other DOTS package releases for 6000.5.4f1, to be resolved within editor.
- Fixed combat tick and runtime performance budget/hardware.
- Attack/combo/heavy strike key binding and cancel windows.
- Guard stability formulas, timely block window, dash frames, reactions and body injury thresholds.
- Rendering method and asset format, equipment visual layers, terrain deformation implementation.
- GitHub Actions/Unity licensing and test runner configuration.

Any change to accepted decisions requires explicit conversation and an entry here.

## Pause-safe battle lifecycle (#19, implementation 2026-10-26)

- Schedule the same existing `BattleLifecycleSystem` in Initialization and before each fixed tick.
  Apply all request kinds at the next safe boundary, including paused frames; never invoke fake ticks.
- `BattleStartTick` records the last completed absolute tick at either boundary. A paused Start/Restart
  remains at relative tick 0 until the first real tick after resume (relative tick 1).
- Preserve the one request drain, owner-token checks, atomic preflight, ID allocator and queue reset path.
- Combatant ECB writes must play back at EndFixedStep in their producing tick. Next-tick BeginFixedStep
  playback can outlive paused-frame actor cleanup; no current runtime producer needs migration.
- Unity execution and Domain Reload GUI verification remain NOT RUN in this environment; #17's Windows
  gate and #23's independent manager review remain open. Delivery is not verified Stage 01 completion.

- Diagnostic actor names are the two constants `Combatant Human` and `Combatant AI`; allies have no name.
  Never encode IDs/counters into Entity names. `CombatantId` alone identifies actors; names are reusable
  display labels. This closes the implementation scope of former #26, subject to Unity verification.
