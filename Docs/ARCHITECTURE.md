## Current #69 — Stage 05.1 data flow (2026-10-29)

Only this section is current; every older "History" section keeps its own SHA/scope and is superseded where it disagrees (old key layouts, exact-limb intentions, velocity = displacement/dt, series limit 4, stage states).

```
CharacterAnimationConfig ─ Animations (timing) + Combat (body, 5 slots: clip, damage, reach, depth, height)
   └─ CombatSmokeBootstrap (main thread, on change): validate → CombatAnimationSet(slot clips) → ApplyTo
        └─ CombatStrikeSchedule.Publish  ── the World's one character table (Revision)
input capture (U/I/O/Space) → PlayerInputCommandSystem ─┐
AI producers ───────────────────────────────────────────┴→ CombatCommandQueue → processor (retired bits → InvalidValue)
fixed tick: BattleLifecycle (spawn: table.Body → CombatBody) → clock → commands
  → CombatActionSystem (U→I→O, SeriesStep, copies Timing + Strike) → Facing
  → CombatMovementSystem (single integrator; contact job: positions, then final contact velocity)
  → CombatSpatialIndexSystem (final snapshot) → CombatHitSystem (frozen Strike, index batch, Burst kernel)
presentation: renderer (slot clips) and bootstrap gizmos read ECS only
```
Owners are unchanged: Action writes CombatAttackState, Movement Position/Motion, Hit HP/ledgers, lifecycle spawns;
details [STAGE05_1_CHARACTER_SERIES.md](STAGE05_1_CHARACTER_SERIES.md).

## History — #66 — Stage 04.4–04.5 impulses, indexed Hit, 2/100/400 (2026-10-29)

Fresh main base `cf2b5bcf57c2b81b635074a0322a85ecf50879b3` (PR #65); branch `feature/66-impulses-indexed-hit`, one PR.
[Contract: impulse input/epoch, integration, mass/contacts, rollback, Hit migration, checks, manual checklist](STAGE04_IMPULSES_HIT.md);
[exact SHA and PASS/FAIL/NOT RUN results](Evidence/66/README.md).
Movement remains the only Position/Motion writer and integrator: pending `CombatImpulse` → Δv=J/Mass → physics then
control proposals → contacts (physical share = mass-weighted inelastic response for any teams, 04.3 rules for the rest)
→ velocity from achieved physics → deceleration → final index → Hit via one atomic index batch + Burst kernel.
The first real Unity compile of the 04.3 base failed (CS0246 in the replay driver); fixed first, with two test-side
EditMode fixes. Per-attack Impact/stagger/guard remain Stage 05/06. Stage 03 OWNER-ACCEPTED; Stage 04 IN PROGRESS —
manager review and owner manual acceptance pending; merge only on owner instruction.

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

State: Action → Facing → Movement → **CombatSpatialIndexSystem** → Hit. CombatBody is three unmanaged floats,
spawned by the existing lifecycle; the managed World-owned index reads current ECS into reusable native storage and
writes no gameplay data. Hit still performs its existing full scan; no physical response is added. Public reads reject
stale tick/epoch/membership, including paused End/Restart/Despawn. Atomic AABB batches validate live membership once,
returning caller-owned ranges without a second index or escaping view. Lifecycle includes whole-entity Disabled cleanup.
Settings: CombatBodyTuning and CombatSpatialTuning.

## History — #56 — image pivot and target-owned Inspector cache

Catalog Image Pivots stores stable frame_id → Sprite subasset references. Atlas load converts imported rect/pivot into
one effective full-canvas pivot per frame, shared by geometry/anchors; absent bindings retain JSON fallback. GPU bounds
and footprints derive from the same CardRects. Editor-only import hooks migrate missing bindings by exact page/rect and
invalidate catalog Version after Apply/reimport; player builds read referenced Sprite data with no disk/Editor API.
CharacterAnimationConfigEditor has no raster preview. Its target-owned manifest/settings/diagnostic caches are invalidated
by revisions and Undo/project events and cleared on disable. [Contract and limitations](STAGE03_PIVOT_CORRECTION.md).

## History — controls #52 — shared exact/general intentions (2026-10-28)

[Current controls and handoff](STAGE03_CONTROLS.md): the existing Input System capture emits ushort CommandButtons, through
one producer/processor into CombatActionSystem. Exact/general selection lives in CombatAttackTuning, not input/presentation.
The existing Buffered slot retains the full intention until execution. No new state owner, queue or timing/config layer.
PR #51's scene binding/preview/lifecycle corrections are already in base `1c96c6f`. Stage 03 still awaits owner acceptance.
Older dated feature notes below are historical where they describe old bindings or superseded movement/landing behaviour.

## Stage 03 final polish — pending owner acceptance (2026-10-28)

[Current handoff and settings](STAGE03_FINAL_POLISH.md): full source-cell mage rendering (packed trim retained), fixed
fractional support separate from artistic alignment, 50%/25%/25% authoritative-phase movement, ECS landing recovery tied
to jump frame holds. Existing owners/table/renderer only; no new systems, input path or per-fighter render objects.
This supersedes Preparation-only movement and cancellable visual landing described below. Unity validation NOT RUN.

# MLF Legends — Combat architecture

## Stage 03 tuning — strike windows from animation phases (2026-10-28)

Supersedes the two claims below that strike timing lives in `CombatAttackTuning` and that strike frames are fitted to it.
Data flow: `CharacterAnimationConfig` strike clip (EndFrame, Preparation/Active/Recovery frames, T, curve) →
`SpriteAnimationTiming` (frame start times, once per config revision) → `CombatStrikeTiming.TryFromSeconds` (ticks) →
`CombatStrikeSchedule` singleton (published on the main thread by `CombatSmokeBootstrap`, outside fixed steps) →
`CombatActionSystem` copies it into `CombatAttackState.Timing` on each strike start → `CombatHitSystem` (hits),
`CombatMovementSystem` (Preparation movement) and `CombatAnimationSet` (drawn phase) all read that frozen copy. The renderer
never triggers hits; render rate never changes the simulation. Reach/damage/chain delays/series stay in `CombatAttackTuning`,
whose windows are only the default table. Writers: table — `Publish` only; attack state — `CombatActionSystem` only.

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


## Stage 03.3a / #43 — current integration (2026-10-27)

The existing renderer gains an explicit CharacterConfig source. One shared project ScriptableObject maps character
IDs to existing SpriteAtlasCatalog assets and per-clip N/T/relative-speed curves. SpriteAnimationTiming compiles
curve integrals/frame boundaries once per revision; extraction reads the existing BattleSession + SimulationClock,
selects idle_combat BEFORE footprints/depth/page grouping, and writes nothing to ECS. All fighters share the battle
phase, including while moving/jumping and offscreen. Pause/resume/restart require no new clock/state owner.
Manual fixture APIs are retained in manual sources. No per-fighter object/Animator/material/texture or second render
path. Scene uses the real external PNG + tracked JSON/catalog/config. Current nominal mage height128px maps to2units.
Historical #43 state: only idle was integrated; superseded by #48 state-driven selection of all eight clips. Details: Presentation/Sprites/ANIMATION.md.
PR #42 is merged and manager STATIC PASS; its and #43's new Unity/GPU/performance evidence remain NOT RUN.


**Document type:** combat architecture blueprint and responsibility map. **Stage 01 ECS runtime is implemented and source-reviewed** (#23 static PASS); real Unity compilation/EditMode/PlayMode after PR #28/#29 remain **NOT RUN**, tracked in #30, while the owner started Stage 02 on 2026-10-26. Do not confuse delivery/static review with Unity runtime verification.

## Target platform and project state
- Unity **6000.5.4f1**, Universal 3D URP **17.5.0**, 3D environment.
- Existing repo includes Unity template project. URP renderer is assigned in GraphicsSettings and QualitySettings.
- Initial template packages include Input System 1.19.0, Burst 1.8.29 transitively, Collections 6.5.0 transitively. **Entities 6.5.0 and Entities Graphics 6.5.0 are installed in manifest and package lock**.
- Use the ECS APIs bundled with Unity 6000.5.4f1 and Entities 6.5.0; Stage 00 test pass was reported by the project owner, with logs unavailable for independent review.

**Current delivery boundary:** Stage 01 has one authoritative World-scoped `SimulationClock` and fixed-step phases; one command queue/processor publishing `AppliedCombatCommand`; one `BattleLifecycleSystem` that owns start/end/restart/IDs and runs in pre-clock FixedStep and, through the thin `BattleLifecycleInitializationProxy` (#35), in Initialization; and `BattleSetup` for 1v1 through 200v200. Stage 02.1 Issue #31 implements `CombatMovementSystem`, exactly one authoritative position/motion writer in the existing `CombatStateUpdateSystemGroup`; Stage 02.2 Issue #33 adds `CombatFacingSystem`, the single `CombatFacing` writer ordered before it. Stage 02.3 + 02.4 Issue #22 adds the real Unity Input System producer (one `PlayerInputCommandSystem` in Collection, before the processor) and one read-only orthographic `CombatCameraController` on the technical scene's existing camera (section below). Stage 03.1 + 03.2 Issue #38 adds the atlas contract and one read-only scene-level GPU sprite renderer (section below). Collisions remain a distinct stage.

## Architectural goals
One authoritative sim handles player, duel AI and hundreds of combatants; presentation, UI, VFX and animation never apply damage or dictate hit frames. Use Unity ECS/DOTS + Burst for performance-critical processing, with a fixed simulation step. Rendering frame rate is independent. The combat core must remain applicable to 200 vs 200 even during initial 1v1 tuning.

## Responsibilities and boundaries
1. **Input**: Unity Input System collects input, emits time-stamped action intentions into common actor command representation. Human and AI use same validation and action-state machine.
2. **State machine**: fixed-tick action status (idle/move/jump/dash/windup/active/recovery/guard/stagger/knockdown/dead), locks, interrupt/cancel eligibility.
3. **Movement integration**: world X/Z position, Y altitude, velocity, ground contact; no per-fighter rigidbodies.
4. **Spatial index/collisions**: authoritative actor occupancy, static/dynamic obstacles, body-body resolution, knockback and collision events.
5. **Attack/hit pipeline**: single owner generates attack queries, resolves hit events, guard mitigation, HP, poise and impulse. Exactly-once strike/event bookkeeping.
6. **Physiology/effective capabilities**: SPECIAL and injuries affect allowed actions and derived stats via one central effective-capability calculation, not scattered modifiers.
7. **AI**: sensors with observation/reaction limits; decisions issue same commands as player, subject to same stamina/guard/cooldown restrictions.
8. **Presentation**: ECS state sampled for GPU sprite frame/atlas, direction, depth order and equipment layers; particles/sounds/UI observe outcomes.

No duplicate systems writing the same canonical stat at different points. Explicitly document update dependency order and entity lifetime/cleanup. Burst-ready compact value components, pooled buffers, bounded data scans and deterministic seed handling preferred.

## Space and display
Characters are expressive 2D sprites roughly 4 heads tall in fully 3D destructible environments. Fixed orthographic camera orientation at all zoom levels. Planar X/Z axes with world Y height. Attack aiming primarily left/right with a finite Z depth and Y altitude overlap; no free 360-degree strike aiming in initial slice. Stable depth sorting required for overlapping fighters and geometry.

## Stage 02.1 motion ownership (#31)

Collection's existing processor publishes `AppliedCombatCommand`; State's `CombatMovementSystem` writes
`CombatPosition` and `CombatMotion` once per fixed step; Publication only reads. Lifecycle's existing
private Spawn initialises both, including the temporary landing support at spawn Y; lifecycle destroys
both with the entity. This applies equally to 2/100/400 actors and both command sources. Facing is written
only by the Stage 02.2 owner below. Integration, provisional units/tunables and Stage 04 replacement boundary are documented in
`Simulation/SIMULATION.md`. Unity compilation, regression execution and renewed profiling are NOT RUN.

## Stage 02.2 facing ownership (#33)

Spawn sets `CombatFacing` from `BattleSetup`; afterwards State's `CombatFacingSystem`, ordered
`[UpdateBefore(CombatMovementSystem)]`, is its only writer: sign of the accepted `AppliedCombatCommand.MoveX`,
`MoveX == 0` keeps the previous value, same rule for player, AI, grounded and airborne actors. No enemy
auto-facing; the Stage 05.1 strike lock (turn only on a strike's first tick) lives in this owner; future guard restrictions too. The processor,
movement and presentation never write facing. Details in `Simulation/SIMULATION.md`.

## Stage 02.3 + 02.4 input and camera ownership (#22)

Device events (Input System, default dynamic update, PlayerLoop `PreUpdate`) only change `PlayerInputCapture`: live
device state plus not-yet-consumed press bits, owned by the scene `PlayerInputAdapter` (its own clone of the Player
actions). On every real fixed tick, `PlayerInputCommandSystem` (Collection, before the processor) appends at most one
`Player` `CombatCommand` for the current active in-combat `BattleSession.HumanCombatant` to the one existing queue:
live Move/Held on every catch-up tick, Pressed once. It writes nothing else; there is no second queue, clock or
accumulator, and no frame-rate `Tick + 1` producer. Replay/benchmark/manual worlds have no producer unless a test adds
one. Pause, focus/device loss, End/Restart/new human and adapter replacement drop pending presses (`Input/INPUT.md`).

`CombatCameraController` (scene camera, `LateUpdate` after the Update-phase simulation) reads the session and the
human's `CombatPosition` and writes only its Transform/Camera: orthographic, fixed configurable angle, follow, pan and
zoom (`Presentation/CAMERA.md`). It never filters simulation by visibility; input is world X/Z, not camera-relative.
The chain is: actions/subscriptions → capture → producer → queue → processor → `AppliedCombatCommand` → Facing →
Movement → `CombatPosition` → camera. Player and AI commands share every step after the producer.

## Stage 03.1 + 03.2 atlas contract and GPU sprite renderer (#38)

Art enters through manifest v1 (`Docs/SPRITE_ATLAS_CONTRACT.md`): `SpriteAtlasManifest` validates it all-or-nothing,
`SpriteAtlas` binds pages explicitly and owns the shared CPU/GPU frame tables. One `CombatSpriteRenderer` per scene World +
Camera (later enabled wins, no double draw) reads `CombatantId`/`CombatPosition`/`CombatFacing` of enabled `InCombat`
fighters of the active `BattleSession` in `LateUpdate` (order 100, after the simulation and the camera) and draws them with
one shared card mesh, one URP shader and `Graphics.RenderMeshPrimitives` once per atlas page. It writes no ECS data, adds no
system, tick or entity; replay/benchmark Worlds have none. Depth: alpha-clipped depth-writing cards (vertical above the
pivot, on the ground below it), equal depth resolved by a render-only push ordered by `CombatantId`. The chain extends:
… → Movement → `CombatPosition`/`CombatFacing` → camera → sprite renderer. Details: `Presentation/Sprites/SPRITES.md`.
Explicit frame selection only; state-driven animation and the final character are Stage 03.3.

## Physical rules
Every living combatant has a lightweight body collider. Allies block and gently yield; player can push allied crowd with modest resistance. Enemies and walls remain solid. No ghost dash. Hits can cause stagger, launches, push-through waves and collisions with other bodies; custom ECS simplified physical resolution, not 400 dynamic Rigidbodies. Direct basic attacks don't damage friends; secondary magical/environment effects can.

## Rendering research
Prefer a specialized GPU-atlas/instanced renderer, not per-combatant Unity Animator or SpriteRenderer. Benchmark direct/indirect drawing approach, Entities Graphics interoperability, frame selection, transparency sort, equipment overlays, shadows and terrain occlusion before committing to an implementation. **Stage 03.2 choice (#38):** direct instanced `RenderMeshPrimitives` with structured buffers (measured constant draw calls for 2/100/400); indirect/compute culling and an Entities Graphics path are not implemented without a measured need; translucency, equipment overlays, shadows and terrain remain open.

## Navigation (Stage 11)
No Unity NavMeshAgent/NavMesh as authoritative navigator. Own heightfield-based X/Z walkability; group flow fields, local bounded A*, local avoidance and jump-vs-route decisions. Dirty-region rebuild as terrain changes. Terrain mostly gentle slopes/ramps with limited cliffs; collisions and nav must agree on traversability.

## Campaign boundary
Aging, generational growth, diseases, recruitment, campaign turns and post-death succession are **design background only**, deferred. Battle-relevant injury, blood loss and loss of capabilities are active combat scope. Avoid speculative campaign frameworks in early stages.

## Diagnostics
Per-stage tests and profiles: stable tick replay, spawned/despawned entities, structural changes, memory allocations, Burst job execution, collision pairs, hit ledger, CPU sim time, GPU frame time, batches, AI decisions. No FPS guarantee before recorded measurements on user hardware.

## Architectural acceptance
Before merging any combat feature, trace how commands → state transition → geometry and collisions → damage and guard → injury and death → presentation and AI connect. Confirm exactly one authoritative location for each effect and safe job ownership.


## Stage 05.2 — #73 physical ability

Implementation and numerical contracts: [STAGE05_2.md](STAGE05_2.md). Order remains Action → Facing → Movement (accepted shape/path) → final SpatialIndex → Hit. Next: #74, then 05.3 VFX. Stage 04/05 owner manual acceptance is still open. Unity/Burst/GPU/benchmark execution for this change is NOT RUN; available source/model evidence does not replace it.


## Stage 05.2a — #74

After #73 / PR #75 (main 78b473b), the current hit reference is the **accepted body waist on both sides**, Facing selects the nearest opponent before Movement, and presentation reads Hit-owner feedback plus numeric ground projection. See [STAGE05_2A.md](STAGE05_2A.md) for defaults, ownership, intentional rule changes and exact NOT RUN limits. Next: 05.3 VFX. Stage 04/05 owner visual/manual acceptance remains open.
