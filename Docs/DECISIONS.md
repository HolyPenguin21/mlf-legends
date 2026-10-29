## Current #70 — Stage 05.1a decisions (2026-10-29)

Only this section is current; #69 below is history where it disagrees. [Contract](STAGE05_1A_ATLAS_MIGRATION.md).
- **D70-1 Wall velocity = exact cone projection.** Final velocity is the Euclidean projection onto {v : dot(v,n) ≥ 0} of a
  minimal set of active normals (last blocked substep of every move + walls touched at the final position); redundant
  normals are dropped exactly, overflow stops the body with a diagnostic. Replaces two passes over at most two normals.
- **D70-2 Velocity-pass budget.** Exhaustion inside the velocity pass publishes the solved positions with zero contact
  velocity (`VelocityComplete = false`, `VelocityDiscarded`); no impulse retry, no re-integration.
- **D70-3 Scene obstacle.** The owner's `Arena Cube (1)` is an explicit arena source; no automatic collider scanning.
- **D70-4 v2 art as data.** source_size/page/pivots come from the manifest and the imported Sprites; no square or 192×192
  assumption. PhysicalAbility default clip `physical_ability`; `kick_spin` is a legacy id with a declared successor, used
  only by the explicit migration and diagnostics.
- **D70-5 Pivots and custom positions.** Feet line [96,136] for every frame plus the owner's per-frame horizontal custom
  positions; a same-size Sprite rect shifted ≤ half a canvas is a custom image position (pivot shift, manifest pixels).
- **D70-6 Explicit config migration.** Renamed slot clips and new clips are migrated by an owner command with a report;
  valid T/curves/phases/Combat values are never reset; the project asset was migrated in Git with an old → new table.
- **D70-7 Change policy.** Atlas/config changes apply on the next frame; a running strike keeps its windows and is drawn
  only with frames of its simulated phase; new art is installed between battles / Restart.

## History — #69 — Stage 05.1 decisions (2026-10-29)

Only this section was current; every older "History" section keeps its own SHA/scope and is superseded where it disagrees (old key layouts, exact-limb intentions, velocity = displacement/dt, series limit 4, stage states).

- **D69-1 Final contact velocity.** Velocity after contact is the physical final velocity, never achieved displacement / dt:
  inward normal parts against surfaces that blocked the body are removed (time of impact implicit in the move), touching
  pairs exchange approaching normal velocity as a perfectly inelastic mass-weighted collision (held bodies immovable),
  then deceleration. One integrator, one Position/Motion writer; positions/holds/slop unchanged. [STAGE05_1_CHARACTER_SERIES.md](STAGE05_1_CHARACTER_SERIES.md) §1.1.
- **D69-2 Disabled cleanup.** Whole-entity `Disabled` fighters lose ExternalVelocity like a disabled InCombat bit.
- **D69-3 Test isolation, not production relaxation.** The ordered PlayMode FAIL came from a test World without the arena
  owner and an unrun `finally`; the fixture restores replaced Worlds in `[UnityTearDown]`. The arena authoring reports a
  frozen-movement error only after its own staged geometry is published.
- **D69-4 Per-character data in the existing dataflow.** Body (Radius/Height/Mass) and per-slot clip/damage/reach/depth/
  height live in `CharacterAnimationEntry.Combat` next to the animation timing (the only Active source) and are published
  through the existing `CombatStrikeSchedule` (now the character table). No second config, no physics/attack/ability
  managers. Values are frozen per strike execution (`CombatAttackState.Strike`) and per fighter at spawn.
- **D69-5 Semantic slots and migration.** `CombatAttackKind` 1..5 keep their values as Hand1/Hand2/Foot1/Foot2/
  PhysicalAbility; default clips punch_left/punch_right/kick_left/kick_right/kick_spin. Versioned, idempotent data migration.
- **D69-6 Layout and series (manager's working interpretation for review).** U hand series, I foot series, O ability,
  Space jump; priority U → I → O; 1-1-2-1-2 by started strikes in a bounded step; hand↔foot switch restarts at 1;
  Recovery end resets; O free-state only, never buffered, never interrupts; no length limit. Exact-limb command bits 4..8
  are retired (invalid), so pre-#69 recordings using them are not compatible.
- **D69-7 Debug view.** Play Mode Scene gizmos on the bootstrap draw the simulated body and the frozen strike box.

## History — #66 — Stage 04.4–04.5 impulses and indexed Hit (2026-10-29)

Agent implementation choices, pending manager review and owner acceptance ([contract](STAGE04_IMPULSES_HIT.md)):
- One impulse input: `CombatImpulse` buffer on a Movement-owned singleton, epoch = BattleNumber; consumed exactly once by
  the Movement call that sees it (Hit-phase producers → T+1). No event framework, queue owner or physics clock.
- Physical X/Z velocity lives in `CombatMotion.ExternalVelocity`; vertical stays `VerticalVelocity`. Δv = J/CombatBody.Mass.
- Physics before control within a tick (two world moves only when there is external motion; otherwise the exact 04.3 move).
  Strike factors and landing lock act on control only; positive lift leaves support without Jump Pressed.
- Contacts split each violation by cause; the physical share is a perfectly inelastic, mass-weighted normal response for
  any teams and becomes velocity (= achieved physical displacement / dt). Walls are frictionless; ground/air Coulomb
  deceleration 24 / 2 u/s², rest below 0.05 u/s; max external speed 24 u/s, beyond is rejected (never clipped).
- Rolled-back islands publish zero external velocity and count it; consumed J is never retried.
- Disabled InCombat loses its external velocity and its pending impulses (dropped); re-enable releases nothing.
- Hit: managed orchestration + one atomic index batch + Burst kernel; ulp-padded point-target box as broadphase; stale
  snapshot with Active attackers throws before writes, no brute-force fallback. The 04.1 "disabled index" replay control
  is replaced by an independent brute-force oracle.
- Owner (2026-10-29): Burst-off benchmark runs are excluded; production code runs with Burst. Owner (same day): fix the
  pressing-front convergence in #66 — uncontrolled ally pairs by pressing command, initial-overlap split, 5 % radius slop,
  bounded hold escalation instead of whole-island rollback (STAGE04_IMPULSES_HIT §9).
- 04.3 blockers found by the first real compile/run: missing `using Unity.Mathematics` in the replay driver (CS0246), 04.2
  arena lifecycle tests calling SendMessage in EditMode, and a sprite fixture walking enemies through each other.
  Fixed test-side in the first commit; no runtime 04.3 change was needed.

## History — #64 — Stage 04.3 body contacts (2026-10-29)

PR #62 and #63 were merged on the owner's explicit command; integrated main base is
`adf98cd1c9ca4df2b2d2e96aa65d3a7e17eb3a5a`. Branch `feature/64-body-contacts`; the owner subsequently explicitly authorized commit, push and merge of #64.
[Current body-contact contract, execution boundary, tuning, limits, checks and benchmark](STAGE04_BODY_CONTACTS.md).
Movement remains the only Position/Motion writer: gather all proposals → same owner's working swept grid → sorted
body/world constraints → write all results → current-tick final index → unchanged Hit. Managed orchestration explicitly
schedules/completes a Burst job; actual execution is diagnosed, not assumed. Mass and controlled-role priority are separate.
Free allies yield, ordinary locomotion does not bulldoze enemies, trapped/unresolved islands stop with diagnostics.
Stage 03 OWNER-ACCEPTED; Stage 04 IN PROGRESS. Unity/C#/Burst/scene/performance NOT RUN; source/model checks are separate.
Historical notes below retain their original SHA/status and do not override this update.

## History — #61 — Stage 04.2 static arena (2026-10-29)

Dependent branch `feature/61-static-arena` starts from corrected #60 PR #62 head
`e26e668435b9cb55bcf5bced163710b6c2b61230`; main at dispatch is `180b4121792e68931f27c109b78cd0d24c21a4d4`.
The owner authorized proceeding to physics while #62 remains open; neither PR is merged by the implementing agent.
[Owners, supported geometry/limits, scene setup, leases, fallback, tests and evidence](STAGE04_ARENA.md).
Stage 03 OWNER-ACCEPTED; Stage 04 IN PROGRESS. Only 04.2: no actor contacts/yield, impulses or Hit migration.
Geometry publication → lifecycle/clock/commands → Action → Facing → Movement contacts → final index → Hit.
Only Movement publishes runtime Position/Motion; lifecycle initializes actual spawn support. Core collision body stays
independent of images/pivots; current animation settings and combat rules are retained. Run stride now records actual
planar distance; the old diagonal one-axis distance expectation is corrected without changing physical speed.
**Unity/C# compilation/suites/Burst/GPU/runtime performance NOT RUN**; source/model results are separate and do not
certify runtime acceptance. Historical dated evidence below applies only to its recorded SHA/scope.

## History — #58 — Stage 04.1 (2026-10-28)

**Stage 03 OWNER-ACCEPTED**, by the owner's direct message recorded in #58: «Stage 03 закрыли. Идем дальше».
Exact owner manual-test SHA/protocol was not supplied; this is not a blanket Unity/GPU PASS. **Stage 04 IN PROGRESS**;
04.1 bodies/spatial snapshot delivered for review from fresh main `a6e96295c0a1e008327e28e8ff89205caf0a4f77`.
One branch `feature/58-combat-spatial-index`, one PR to main; merge requires a separate owner instruction.
[Implementation, owners, API, settings, tests and limitations](STAGE04_SPATIAL_INDEX.md).
#46/#53 and QA #17/#25/#30 remain separate. Older dated status/evidence entries below are historical and superseded
where they say Stage 03 awaits acceptance or Stage 04 has not started; their original test results/SHA remain unchanged.

04.1 choices: logical vertical cylinder at CombatPosition's base; single CombatBodyTuning spawn default
(radius 0.3 / height 2 / mass 1, calibration only); CellSize 1 in CombatSpatialTuning. Closed bounds/closed cell coverage,
floor on negative coordinates, Id-sorted unique AABB candidates, explicit huge-query full scan and build-range diagnostics.
One managed SystemBase owns reusable native containers; exact membership validation is O(N) once per atomic AABB batch
(or per convenience single query). No borrowed view escapes. Cleanup includes whole-entity Disabled fighters; partial
World checks require all enabled IDs to have Body/Position/Team, including newly enabled incomplete entities. No contacts/impulses/Hit migration;
04.2–04.5 remain separate. The merged PR also includes pivot/config tuning commit 68c43c; #60 preserves its two exact
assets and corrects tests/evidence. The earlier claim of no tuning changes was inaccurate; see STAGE04_SPATIAL_INDEX.

## Owner correction #56 — working Sprite Editor pivot, no embedded raster preview

Imported per-frame Sprite pivot is the image positioning source; JSON pivot is used only without a binding. Center is
valid. Preserve pivots/.meta and use stable frame_id → Sprite references, never array order. Reference support stays
[96,163.2] for 192×192; ImageOffsetPixels is the single additional art translation. Anchors/footprints/bounds use the
same transform. Remove raster preview entirely and cache Inspector parsing/timing, with frame details opt-in.
[Migration, invalidation and QA](STAGE03_PIVOT_CORRECTION.md). Stage 03 remains unaccepted; no Stage 04.1 or merge.

## Owner decisions #52 — current controls and command representation (2026-10-28)

[Authoritative keyboard table and validation](STAGE03_CONTROLS.md). Physical Y/U/H/J/K select exact hand L/R, foot L/R/spin;
I and L retain general hand/foot series; Space jumps. Exact intentions persist through the same one-slot buffer. Priority:
exact hand L/R → foot L/R/spin → general hand → general foot, identical for player/AI. Jump wins when free/grounded.
CommandButtons is ushort; existing 1/2/4/8 values retained, exact bits 16..256 added, valid mask 0x1ff. Guard is logically
reserved but unbound. Crouch is excluded; Dash/Cast deferred, no new bindings. Camera/UI and movement retained.
Attack speed 50/25/25 and authoritative landing lock from PR #50/#51 remain. These decisions supersede older hotkey,
Preparation-only movement and cancellable-landing proposals below; dated evidence remains tied to its original SHA.

## Stage 03 final polish — owner-requested corrections (2026-10-28)

The [final-polish handoff](STAGE03_FINAL_POLISH.md) supersedes older movement/pivot/landing decisions where they conflict:
fixed support at [50%,85%] of source size; legacy pivot is art alignment; extra image offset is character-wide; mage uses
explicit full-source cells; attacks move at 50%/25%/25%; landing blocks controlled movement/new starts for the configured
landing frames' quantized duration. No separate delay, auto-centering, config layer or ECS writer. Stage 03 acceptance
still belongs to the owner after Unity review. No merge authorized.

# MLF Legends — Decision log

## Stage 03 tuning — Inspector roster size, frame ranges and strike phases (2026-10-28)

Owner instruction before Stage 03 acceptance (Stage 04 not started; Stage 03 is NOT declared accepted). One branch
`feature/stage03-inspector-tuning`, one PR, no implementing-agent merge.

| # | Decision | Where |
|---|---|---|
| T1 | Team sizes on the existing `CombatSmokeBootstrap` (leader included, 1..200 each, total shown); applied only by its owned Restart (enable / explicit *Restart battle*); invalid sizes rejected, never clamped | `CombatSmokeBootstrap`, `BattleSetup.HumanAllies/AiAllies` |
| T2 | `FrameCount` replaced by read-only `StartFrame` (0) / editable `EndFrame` (inclusive) / derived `FrameCount`; local 0-based clip indices | `CharacterAnimationSettings` |
| T3 | Strike phases stored as two inclusive ends (`PreparationEndFrame`, `ActiveEndFrame`); all other bounds derived, so no gaps/overlaps; each phase ≥ 1 frame | `CharacterAnimationSettings`, `SpriteAnimationTiming` |
| T4 | Phases + T + curve are the simulated windows (supersedes P10 "frames fitted to fixed tuning windows"); `CombatAttackTuning` windows remain only the default table of worlds that publish nothing | `CombatStrikeSchedule`, `CombatSmokeBootstrap` |
| T5 | Quantization: a tick is Active when its clip interval `[e, e+1)/60` overlaps the Active time (never skipped); Preparation = earlier ticks (floor); end = `ceil(T·60)`; 0-tick Preparation/Recovery only warned | `CombatStrikeTiming.TryFromSeconds` |
| T6 | Timing copied into `CombatAttackState.Timing` at every strike start; table changes affect new strikes only | `CombatActionSystem` |
| T7 | Planar movement allowed in Preparation (supersedes the Preparation part of P5); jump still locked; facing rule unchanged | `CombatMovementSystem` |
| T8 | Chain point = `min(RecoveryStart + ChainDelay, End)` so a short configured Recovery keeps the series (P4 otherwise unchanged) | `CombatStrikeTiming.FromTicks` |
| T9 | The bootstrap restores the table it replaced on disable unless someone published after it (no leak across scenes/tests) | `CombatSmokeBootstrap` |
| T10 | Migration: legacy `FrameCount = N` → `EndFrame = N − 1` on load (hidden slot, then 0); project phases initialised from `strike_pose`; unmarked phases are an error with an explicit Inspector button, never guessed | `CharacterAnimationSettings`, config editor |

Known limits: one strike table per World (the roster uses one character type until #46); the table is World state outside
the command stream (replays must publish the same table); manual Game-view tuning by the owner is still required.

## Stage 05.1 + 03.3c / PR #48 — five strikes and state-driven combat animation (2026-10-28)

Base main `00310c3`. One branch `feature/stage05-strikes-combat-animation`, one PR; **MERGED on the owner's instruction as `b828ccb`** (2026-10-28). Proposed rules P1–P12 still await approval.
**Simulation:** `CombatActionSystem` (sole writer of `CombatAttackState`) → `CombatFacingSystem` → `CombatMovementSystem` →
`CombatHitSystem` (sole writer of `CombatHealth` and the per-strike `CombatHitRecord` ledger) in State. Five strikes
(punch_left/right, kick_left/right, kick_spin) with Windup/Active/Recovery, reach/depth/height boxes, damage once per target
per strike, series with one buffered follow-up and fixed chain points; timing/reach/damage in `CombatAttackTuning` (single
source). Player J = Attack, new logical `Kick` = U / gamepad North; AI uses the same queue. Rules not fixed by the spec are
**proposed** in `COMBAT_SPEC.md` and await owner approval (controls, series, air strikes deferred, movement/facing locks).
**Presentation:** `CombatAnimationSet` binds all eight clips (idle_combat, run, jump, five strikes) of the existing config to
authoritative state: strike frames fitted to the combat windows (`strike_pose` = Active ticks), jump rise/fall by vertical
velocity, landing only on the ground, run stride by distance, idle restarted on entry. N/T/curve keep their meaning as frame
shares; a strike T that differs from the combat total is reported. Owner idle/run settings preserved. Jump phase markers
`rise/fall/land` were added to the manifest (flagged, pending artist confirmation). No new renderer, clock, GameObject,
texture or material per fighter. Defeat at 0 HP, guard, stagger/knockback, combo bonuses, cancels and air strikes are not
implemented. Evidence (Unity EditMode/PlayMode/GPU runs, raw XML/logs/TSV, static art checks): `Docs/Evidence/48/README.md`.

**Proposed by the implementing agent, PENDING OWNER APPROVAL (implemented provisionally, each one isolated in data/one owner):**

| # | Decision | Where | Alternative if rejected |
|---|---|---|---|
| P1 | Superseded by owner #52: final Y/U/I/H/J/K/L/Space layout above; no combat aliases | input asset, CommandButtons | historical provisional binding removed |
| P2 | Series: first strike left, sides alternate, Kick after kick_right = kick_spin finisher, max 4 strikes | `CombatAttackTuning.Select`, `MaxSeriesLength` | other order/length; directional variants |
| P3 | One buffered follow-up press (latest wins) accepted from Active to end of Recovery; Windup presses dropped | `CombatActionSystem` | buffer during Windup; first press wins |
| P4 | Follow-up starts at Windup+Active+ChainDelay (cuts the rest of Recovery); finisher/4th strike never chains | `CombatAttackTuning` | longer delays; per-pair cancel tables |
| P5 | Superseded by final polish: planar speed 50%/25%/25%, no jump; turning only on a strike's first tick | Facing/Movement owners | slow walk during windup; turn anytime |
| P6 | Strikes only from the ground; airborne presses dropped (no jump-attack clip) — the spec's "jump attacks enabled" is deferred | `CombatActionSystem` | air strike with a dedicated clip |
| P7 | Jump + strike pressed on one grounded tick: jump wins, strike dropped | `CombatActionSystem` | strike wins |
| P8 | Box: forward 0…reach along facing, abs(ΔZ) ≤ 0.4, abs(ΔY) ≤ 0.9; all enemies inside hit (no cap); allies never | `CombatAttackTuning`, `CombatHitSystem` | target cap; body radius when Stage 04 adds bodies |
| P9 | 100 HP, damage 4/4/7/7/12, clamp at 0, no defeat yet | `CombatAttackTuning` | defeat owner in Stage 08 |
| P10 | Strike/jump clips fitted to simulation windows; config T of a strike only sets frame shares (mismatch = warning) (**strike part superseded by T4**; jump unchanged) | `CombatAnimationSet` | reject mismatching T |
| P11 | Jump phase markers rise [1,2] / fall [3] / land [4,5]; jump_000 unused | manifest `meta.jump_phase_markers` | artist re-marks or re-exports |
| P12 | Run stride by distance; ≤ 6-tick stops keep run | `CombatMovementTuning.MoveStreakGraceTicks` | time-based phase; other grace |

## Stage 03.3b / #45 — idle + run (2026-10-27)

Base main `97b8c188` includes owner Unity import/scene/package changes and the owner's idle duration 1s; these are preserved.
Delivery adds only `run` (5 frames, config default 0.5s) to the existing character atlas/config. Grounded actors use run
when their current-tick accepted AppliedCombatCommand has nonzero MoveX or MoveZ; airborne/None/stale commands use idle.
Both clips retain independent N/T/SpeedCurve settings and the existing common battle-relative clock phase; no clip-entry
clock, per-id history, gameplay writer, new ECS system, Animator or transition FSM. Selector runs before footprint/depth/page
grouping; manual partial Worlds keep their original component query. Stage 04 collisions will revisit actual-motion criteria.

Input is the latest corrected PNG version4 (1152x384) with compatible revision3 manifest from the idle_run archive.
PNG SHA256 `d22b2a5950b7902cf2d3730a98022723cfd9ba374b7a6c6cb2ec9ac71195423d`; manifest SHA256
`27d9ffbbab436c619706673be17e92bf5b6d7634a3b8a0cc8041d89de0b3b573`. Idle row/frame metadata are unchanged.
PNG remains external/ignored; no art was regenerated. Anchors are estimates, not independently verified anatomy.
Independent manager bottom-up source review STATIC PASS; raw checks/source hashes are in Evidence/45/manager-static-review.json. Unity compilation/import/Burst,
authored EditMode/PlayMode/Graphics, actual GC/performance/Game view/Frame Debugger/Domain Reload off remain NOT RUN.
Evidence and rerun instructions: `Docs/Evidence/45/README.md`. #30/#17/#25 remain separate. One PR, no implementing-agent merge.
Older #43 idle-only/1.2s dispatch and evidence entries below are historical and do not override the current delivery.

Multi-character contract: the shared config supports many CharacterId entries, each with its OWN SpriteAtlasCatalog,
manifest/pages and per-clip N/T/curve. Clip IDs are character-scoped; switching the renderer's selected CharacterId
rebinds its atlas/frame indices/pixel scale/timing and clears all old page property-block references. Synthetic A→B→A
regression covers different frame counts, page textures/sizes, source/pivot/trim/nominal height and durations, with an
unrelated invalid entry. Current renderer still selects ONE default character type for the whole roster. Simultaneous
mixed-character/multi-atlas fighters require separate follow-up #46; this change does not claim that feature.


## Shared character animation configuration — owner requirement, 2026-10-27 (#43)

Owner authorized implementation after first idle package became ready. One CharacterAnimationConfig contains nested
character/animation settings: N first clip elements, total seconds T and normalized relative-speed AnimationCurve.
Config timing takes precedence over JSON duration_ms in this mode; JSON remains the authoritative content geometry/
frame order. Curves integrate/normalize so changing speed shape does not change T. Scope supports finite unweighted
Hermite curves, endpoint keys0/1, N within available frames and 0<T<=86400; weighted/Constant infinite tangents reject
explicitly. Inspector displays derived frame intervals. Config edits apply by revision; no second authoritative clock.
Only mage_female_01/idle_combat configured now; movement/jump still show idle. Common phase is derived from the existing
battle tick boundary. Manual technical mode is retained explicitly. No PNG/art archive/source enters Git; stable PNG
.meta and matching JSON/catalog/config/scene bindings do. No implementing-agent merge.

Current base fe324f83 already merged PR #42 after manager STATIC PASS and closed #41 as implementation delivered.
Its runtime/GPU/measurement checks remain NOT RUN; historical decisions below do not supersede that disposition.


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
  DepthNormals; no URP feature changed). Original roster-wide ≥0.002 gap is superseded by corrective #41:
  overlapping projected rectangles form local components, requested 0.002 gap compressed into a fixed 0.0625-unit
  total budget per component. Lower ID in front on ties across pages/chunks; bias 0.02 unchanged. `CombatPosition`
  untouched. See SPRITES.md for precision/occlusion rationale and remaining device-validation limits.
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


## Stage 05.2 — #73 physical ability

Implementation and numerical contracts: [STAGE05_2.md](STAGE05_2.md). Order remains Action → Facing → Movement (accepted shape/path) → final SpatialIndex → Hit. Next: #74, then 05.3 VFX. Stage 04/05 owner manual acceptance is still open. Unity/Burst/GPU/benchmark execution for this change is NOT RUN; available source/model evidence does not replace it.


## Stage 05.2a — #74

After #73 / PR #75 (main 78b473b), the current hit reference is the **accepted body waist on both sides**, Facing selects the nearest opponent before Movement, and presentation reads Hit-owner feedback plus numeric ground projection. See [STAGE05_2A.md](STAGE05_2A.md) for defaults, ownership, intentional rule changes and exact NOT RUN limits. Next: 05.3 VFX. Stage 04/05 owner visual/manual acceptance remains open.
