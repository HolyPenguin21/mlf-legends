# Stage 03 final polish — owner handoff, 2026-10-28

## Post-merge correction: landing frames skipped in the saved scene

After PR #50 merged as `72f48ce`, the owner reported a skipped landing pose. Inspection found that
`Assets/Scenes/CombatSmoke.unity` assigned CharacterAnimationConfig to the renderer but left **Combat Bootstrap →
Animation Config** empty. Without the bootstrap publisher, the ECS timing table retained its default zero landing
recovery; the renderer therefore immediately selected run/idle at ground contact. This correction assigns the SAME
existing config asset to the bootstrap. No timing, art, roster or runtime code is changed.

Frame indices start at zero: Landing Start Frame **4** means `jump_004` (the FIFTH picture), followed by `jump_005`
when End Frame is 5. With the current curve, the derived window is 27 ticks: frame 4 for 8 ticks (~0.1333s), frame 5 for
19 ticks (~0.3167s). This is a numerical schedule check, not Unity visual acceptance. In an existing locally edited scene,
assign `Assets/MLFLegends/Combat/PresentationData/CharacterAnimationConfig.asset` to **Combat Bootstrap → Animation Config**
as well as the renderer, then start Play Mode again. The bootstrap log should report `Landing recovery 27 ticks`.

Validation of this correction: saved scene bootstrap and renderer config references match the existing asset GUID;
the scene diff is exactly that one reference; `git diff --check` passes. **NOT RUN:** Unity compilation, PlayMode and
visual confirmation. Stage 03 remains pending the owner's manual acceptance.

## Original implementation handoff

Base: `main` **31765c658da9df6191ac03ed82f8b7cc3c34eff7**. One branch `fix/stage03-final-polish`, one PR, no merge.
**Stage 03 remains unaccepted until the owner confirms the Unity check.** No Stage 04 work is included.
This document supersedes older descriptions of Preparation-only movement and cancellable visual landing.

## Full canvas and the clipping cause

The renderer used the manifest's useful `rect` for BOTH geometry and UV. That is correct for genuinely packed/trimmed
content, but it excludes pixels repainted outside the old rect on a full-cell sheet. The existing #48 evidence already
records exactly this problem: the owner's repainted `kick_right_002` extended beyond its rect into its extrusion
(`Docs/Evidence/48/README.md`, working-PNG diagnostic). There is no runtime 128×128 clipping box: `nominal_height_px = 128`
is only the pixel/world scale calibration. Reducing that scale or enlarging culling bounds alone cannot restore excluded UVs.
The current external PNG is absent here; reproducing/visually confirming its current pixels is **NOT RUN**.

`MageFemale01SpriteAtlasCatalog.asset` now explicitly selects **Frame Storage = SourceCanvasCells**. Each actual page
region is `(rect.xy - trim_offset, source_size)`. All 50 current regions are complete, disjoint 192×192 cells inside the
1728×1536 page. This restores the ENTIRE existing source cell, including pixels outside stale useful rects, without editing
JSON, replacing PNGs, reading texture pixels or rescaling the body. UVs and geometry use the same region. No neighbouring
cell is sampled. Full-cell geometry is still the one shared two-quad mesh, with alpha clipping and batch culling enabled.
The shader clips only by alpha, never by nominal body size.

The default **TrimmedRects** mode remains for truly packed atlases: `rect` is sampled and restored at `trim_offset` on the
virtual `source_size` canvas. Do not enable SourceCanvasCells on genuinely packed pages. Cell bounds/overlap validation
rejects incompatible content; it does not guess a layout or auto-centre a silhouette. A packed export must include all
visible pixels in its useful rect; pixels discarded by an exporter cannot be recovered by the renderer.

Frame geometry/UV and their union bounds are compiled once on catalog load. The renderer adds the shared artistic offset
to the shader, anchor conversion, projected overlap footprints and the culling envelope once each. The envelope covers both
facings, all sampled poses, their offset, jump support Y and the existing bounded depth push. No culling is disabled, no
per-fighter GameObject/texture/material/mesh is introduced, and no new per-frame allocations or shared-table rebuilds are added.

## Fixed world support versus artistic alignment

The world support is **source_size × [0.5, 0.85]**, independent of pose/clip/pixel bounds. For 192×192 it is **[96, 163.2]**
in top-left-origin canvas pixels. It stays fractional; there is no integer rounding. `CombatPosition` remains the physical
world position, and ECS jump height moves it. Artist settings never change collision positions, strike reach or hit boxes.

Configure the drawing in **Assets/MLFLegends/Combat/PresentationData/CharacterAnimationConfig.asset → Characters →
mage_female_01 → Image Offset Pixels**. This is ONE offset for all clips of that character, in pixels (+X right, +Y down).
Keep the same character entry and catalog for every animation. Do not use separate support points for jump or strikes.

Compatibility is explicit: the manifest's old `pivot` becomes the baseline ART alignment. On the fixed virtual canvas,
the image translation is `WorldSupport - JSON pivot + ImageOffsetPixels`. Thus old `[96,160]` data gets a baseline
translation `[0,+3.2]` and preserves its previous world drawing/anchor placement at zero extra offset. The fixed support
is still `[96,163.2]`; the art pivot is not a second physical position. If the owner wants the source image origin exactly
on the reference canvas, use Image Offset Pixels `[0,-3.2]` with the old JSON. New exports may use pivot `[96,163.2]`
and zero offset. No batch JSON migration or automatic source-file overwrite occurs.

Algebraically, source point `p` has authored card position `(p.x - pivot.x + offset.x, pivot.y - p.y - offset.y)`.
For left facing the complete X result changes sign, including the offset; hand/effect points use exactly that transform.
The source-image translation is applied once. Hand/effect direction vectors are mirrored but never translated.

The config Inspector's **Full source canvas preview** selects any clip/local frame and facing, including frames outside
the configured playback range. Cyan = fixed full canvas; white + = fixed world support; amber = aligned source-image
canvas; blue/magenta = hands; green = effect anchor. Fit/scale is derived from source size and the shared offset, never
the current silhouette. Scene renderer anchor gizmos show the same cyan/amber outlines and white support sphere.
Missing external PNG is reported; outlines and anchors still display. Frame-to-frame drawing errors remain artistic work:
the renderer deliberately does not normalize each pose to its bounding box.

## Attack movement

**Assets/MLFLegends/Combat/Runtime/Simulation/CombatMovementTuning.cs** contains
`PreparationMoveMultiplier = 0.5`, `ActiveMoveMultiplier = 0.25`, `RecoveryMoveMultiplier = 0.25`.
`CombatMovementSystem` reads the running strike's frozen `CombatAttackState.Timing.PhaseAt(...)` and multiplies planar
displacement once. Ordinary speed returns on the first free tick. Diagonal normalization, strike-facing lock, action
eligibility, existing combo buffer, hit timing and once-per-target strike ledger remain in their existing owners.
The strike keeps visual priority over run. Jump impulse/gravity/vertical integration and phase durations are not scaled.

## Landing frames and recovery

In the same config, **Animations → jump → Landing Start Frame** sets the first landing frame; **End Frame** is the last,
inclusive. Current range: **4..5**. `-1` preserves old settings by using the existing manifest `land` marker (no manual
editing of old configs). `rise`/`fall` still come from the manifest. Ranges must be consecutive, inside playback, disjoint
and ordered rise → fall → land. Explicit Landing Start Frame works even if there is no `land` marker.

The duration is the sum of those frames' holds from the existing `TotalDurationSeconds` / `SpeedCurve` timing table.
It is rounded UP to complete simulation ticks (60 Hz); the same range is fitted to those ticks for display. The Inspector
shows range, ticks and seconds. The owner's existing jump curve/duration are preserved: with the current curve and T=0.8s,
frames 4..5 occupy approximately **0.44468942s**, so recovery is **27 ticks / 0.45s**. A flat curve at T=0.8s would give
16 ticks. There is no independent delay to tune.

`CombatSmokeBootstrap` publishes this count in the existing `CombatStrikeSchedule` timing table along with strike windows,
once per config/catalog/World change; there is no new table/system/config layer. It now validates the complete combat set,
so a broken jump range also rejects publication (last valid table stays). Worlds without a published animation config
have zero landing recovery rather than a hidden fallback delay.

`CombatMovementSystem` resolves physical Y contact first, stamps `LandTick` and copies the duration into
`CombatMotion.LandingRecoveryTicks`. The contact tick is the first recovery tick: planar movement is suppressed even on
that tick. The window is `[LandTick, LandTick + LandingRecoveryTicks)`. Facing stays fixed during recovery. The run streak
is cleared, landing beats run, and the first free tick uses the live held direction (a fresh run streak) or `idle_combat`.
New Attack/Kick and Jump presses during recovery are consumed/dropped, not replayed afterwards. Input sampling, command
collection and other fighters' simulation continue; the existing strike-only combo buffer is unchanged.

Pause freezes the shared simulation clock and therefore recovery/playback. Restart/despawn destroys the actor's motion
with its entity; spawn initializes fresh motion with zero recovery. A table edit affects the next landing; an existing
landing keeps its copied tick count. Presentation fits the current configured landing range into that frozen window,
just as running strikes retain their own simulation windows. Runtime edits use Inspector/Config.Set, not mutation of
the exposed character list. Disable/unload retains the renderer's existing GPU/native-resource cleanup path.

## Checks and manual acceptance

Performed here:

- Inspected current main renderer/shader, UV/trim loader/validator, pivot/anchors, extraction/footprints/culling,
  authoritative attack timing, movement/action/facing order, input consumption, clock and spawn/despawn paths.
- `git diff --check`; independent metadata check: all 50 full source cells are in-page and pairwise disjoint.
- Tree-sitter C# syntax parsing of every changed C# file, no syntax errors (this is NOT a Unity compile).
- Independent transform/flip/culling-envelope model: 10,800 points over all 50 source regions, both facings, three offsets
  including fractional/large shifts, camera pitches 5°/40°/85° and yaws 0°/35°; bounds contained every sampled corner/bend.
- Unique Unity `.meta` GUIDs (213), new documentation links, and exact preservation of all owner clip durations/curves/ranges.
- Independent numerical integration of the CURRENT jump curve (SciPy): range 4..5 = 0.4446894194s → 27 ticks.
- No new automatic tests added. Existing movement/landing assertions invalidated by these semantics are updated;
  fixed-duration landing fixtures explicitly publish their timing and preserve their original 8+8-frame expectations.

**NOT RUN:** Unity compilation/import/Burst (no Unity Editor or Unity dependencies installed), EditMode/PlayMode tests,
GPU/GC/performance measurement, Game/Scene/Inspector visual acceptance, external art PNG inspection and player build.
Historical PASS logs refer to older commits and do not validate this change. Other project-config tests contain older
hardcoded tuning assumptions; a fresh Unity suite may need the owner's current tuning reflected in those expectations.

Owner check in Unity: provide the existing external 1728×1536 page preserving `.meta`; scrub extreme kicks and jump in
both facings; confirm full canvas, fixed support and offset/anchor alignment; check bodies near camera/frustum edges;
hold a direction through all three attack phases and through landing; tap Attack/Kick/Jump during landing and verify no
deferred start; check pause/resume and restart during recovery. At 2/100/400 fighters verify batching/resource counts and
culling in Frame Debugger/profiler. Confirm Stage 03 only after this manual acceptance.
