# Character animation — Stage 05.1 / PR #48 (current)

Eight clips of `mage_female_01` are bound to authoritative ECS state through the existing renderer and the one shared
`CharacterAnimationConfig`: `idle_combat`, `run`, `jump`, `punch_left`, `punch_right`, `kick_left`, `kick_right`,
`kick_spin`. Relaxed idle, walk, dash and cast are neither required nor read; their absence never blocks loading.
`CombatAnimationSet` (one per selected character/config revision, shared by the roster) selects each fighter's frame from
`CombatAttackState`, `CombatMotion` and the clock only — no per-fighter history, Animator, blending or second clock.

| Priority | State (authoritative source) | Clip | Timing |
|---|---|---|---|
| 1 | strike running (`CombatAttackState.Kind`) | that strike | fitted to Windup / Active / Recovery ticks of `CombatAttackTuning` |
| 2 | airborne (`CombatMotion.Grounded == false`) | `jump` rise / fall frames | ascent progress `1 − vy/JumpImpulse`, descent progress `−vy/JumpImpulse` |
| 3 | moved within the last `MoveStreakGraceTicks` (6) and the movement streak is alive | `run` | stride phase = streak distance / (MoveSpeed · T) |
| 4 | landed, no move or strike since, within the landing window | `jump` land frames | config share of `jump` T (16 ticks with N6/T0.8 flat) |
| 5 | otherwise | `idle_combat` | cycle T from the tick idle was entered (strike end, landing end, run stop, battle start) |

**How N / T / curve interact with simulation windows.** Loops (idle, run) behave as before: N first frames, cycle T, the
curve distributes the cycle. Run's T is the cycle at full `MoveSpeed`; diagonal motion (0.707 speed) strides slower and a
stop holds the stride. Play-once clips are *fitted*: the config N/T/curve still fix each frame's share of the clip, and each
marker segment is stretched over its authoritative window. For strikes the frames before `strike_pose` span Windup, the
`strike_pose` frames span exactly the Active ticks (when hits resolve), the rest span Recovery; T therefore only sets
relative shares, and a T different from the strike's total (29/29/33/33/45 ticks) is reported as a warning — it can never
move a hit. For `jump` the air segments follow the movement owner's velocity, so the drawing never changes the trajectory
and the landing pose never appears in the air; T and the curve set the landing window and frame shares. N must keep at least
one windup and one recovery frame around `strike_pose` and must cover every jump marker, otherwise the set is rejected.
Frames are selected per simulation tick (no interpolation between ticks, no image blending), so the result is identical at
any render rate.

**Markers.** Strikes use the delivered `strike_pose` markers. The delivered `jump` clip had only `entry`/`return`; the
phase markers `rise [1,2]`, `fall [3]`, `land [4,5]` were added to the manifest from visual inspection (1 push-off, 2 apex
tuck, 3 descent, 4 landing crouch, 5 recovery) and are flagged in `meta.jump_phase_markers` as pending artist
confirmation. Frame `jump_000` (ground anticipation) is in no marker and is never shown, because ECS takeoff is immediate;
the renderer reports this once per revision. Frame `jump_002` is drawn 23 px above the pivot (tucked legs); world height
still comes only from `CombatPosition`. Kick recoveries end on the frame declared compatible with `idle_combat_000`, and
idle restarts at frame 0 on the next tick.

**Config.** The owner's `idle_combat` (6 / 1 s) and `run` (5 / 0.5 s) entries are unchanged. Added: `jump` 6 / 0.8 s,
punches 6 / 29⁄60 s, kicks 6 / 33⁄60 s, `kick_spin` 9 / 0.75 s, all flat curves. All eight are required; a missing or
invalid clip, setting or marker rejects the whole set with field-path diagnostics (nothing is drawn, the battle continues).
The Inspector lists every clip's frame holds, each strike's combat windows and the combined set diagnostics.

**Removed semantics.** The #43/#45 shared battle-relative phase for idle/run and run-by-current-command are replaced by
the state table above (Stage 04 collisions can now change actual movement without touching presentation).
The text below documents #43/#45 and is historical where it contradicts this section.

# Character animation — Stage 03.3b / #45

Current #45 scope is one real `mage_female_01`, with `idle_combat` and `run` loops. Grounded current-tick accepted X/Z movement uses run; airborne, implicit None, zero or stale movement uses idle.
The two-clip presentation selector reads existing state only. No transitions, action FSM, root motion, gameplay events or per-fighter visual objects.

## Setup and ownership

`CombatSmoke.unity` Main Camera uses `CombatSpriteRenderer.AtlasSource.CharacterConfig` (2),
`Combat/PresentationData/CharacterAnimationConfig.asset`, character `mage_female_01`.
The one shared config has Characters → CharacterId / AtlasCatalog / Animations → ClipId / FrameCount /
TotalDurationSeconds / SpeedCurve. It points to the existing per-character `SpriteAtlasCatalog`, which binds
JSON and exact PNG basenames. The renderer has no second active catalog reference in this mode.

Install the latest corrected external PNG version4 at
`Assets/MLFLegends/Art/Characters/MageFemale01/Atlas/mage_female_01_00.png`, preserving the tracked `.meta`.
The matching JSON and catalog/.meta are included. PNG SHA256:
`d22b2a5950b7902cf2d3730a98022723cfd9ba374b7a6c6cb2ec9ac71195423d`;
JSON SHA256: `27d9ffbbab436c619706673be17e92bf5b6d7634a3b8a0cc8041d89de0b3b573`.
The PNG is intentionally ignored in Git. It is not generated or substituted at runtime.
Import RGBA32/sRGB, Point, Clamp, no mipmaps/compression/NPOT resize, max 2048, not Read/Write.

Owner idle settings preserved: 6 frames, 1 second, speed 1. Added run defaults: 5 frames, 0.5 second, speed 1. The 1152x384 page has idle in row 0, run in row 1 (sixth cell empty); RGBA8 mip0 storage 1769472 bytes. Nominal 128 px = 2 world units (`2/128` per source pixel),
canvas 192x192, common pivot [96,160]. All source bodies fit 128x128; idle bottom is y160. Run has authored airborne stride poses; no per-frame normalization of their bounds.
Transparent guards inside idle 136x136 / run 152x152 useful rects keep extrusion outside body pixels. A row is an art packing
convention; runtime reads rects/pages, never assumes a fixed grid. Point is not pixel-perfect at arbitrary
fractional camera zoom; no pixel snapping/morphing system was added.

## Timing contract

FrameCount N selects the FIRST N clip elements in JSON order (1..available). No new/interpolated pictures;
truncating the sequence can change artistic loop continuity. TotalDurationSeconds T is the complete cycle,
0<T<=86400, finite. JSON duration_ms remains validated contract metadata; it does not multiply config timing.
SpeedCurve X is normalized cycle time 0..1, Y relative speed >=0. A flat 1 gives T/N frame duration.
Tangents alter acceleration smoothly, not raster blending. Lower/higher speed holds/advances frames longer/faster.
Curves require >=2 ordered keys, exact first/last times 0/1, finite values/tangents and no negative overshoot.
Weighted tangents and Constant/step (infinite) tangents are rejected with a diagnostic. Endpoint speed mismatch
warns about the loop's speed discontinuity. The Inspector shows available count, average FPS and all frame holds.

Let `t=(SimulationClock.Tick-BattleSession.BattleStartTick)/60` (using the existing TickRateHz constant),
`u=(t % T)/T` and `P(u)=integral(0,u,speed)/integral(0,1,speed)`; local frame is floor(N*P(u)), clamped to N-1.
`SpriteAnimationTiming` analytically integrates unweighted Hermite cubics in double; derivative roots validate
all interior minima. Amplitude and local polynomial scaling prevent tiny/huge relative speed from hiding errors.
80 bisection iterations prepare lower-bound frame starts once; runtime uses modulo + binary search with half-open
intervals. A zero-speed plateau exactly on a frame boundary belongs to the NEXT frame from plateau start.
Within the supported duration domain the boundary target is <0.1ms; independent Python/SciPy checks are numerical
source-model evidence, not execution of this C# or Unity. Frames shorter than a tick/render interval can be skipped.

Both clips use the same elapsed battle time and their own T/curve, including at switches; switching never restarts at frame0. Everyone shares the battle phase; no per-id timing state is needed. The fixed clock freezes on pause, repeated
Refresh never advances phase, resume retains it. Restart uses the new BattleStartTick, even while paused. World
change/disposal rebinds read-only queries; invalid/missing session/clock or Tick<start draws nothing. Offscreen,
disabled renderer and zero active roster do not stop ECS or elapsed time. Reappearance uses the current phase.

## Integration, caching and edits

The existing extraction keeps three read-only queries per World. With a run table it also obtains a temporary entity array and reads optional CombatMotion/AppliedCombatCommand; manual fixtures need no new components. It selects the current
frame BEFORE projected footprint/depth offsets/page grouping, so changed trim/pages/anchors use the same frame.
The bounded #42 depth policy and shader are unchanged. Renderer manual `Configure`/SetFrame/SetClipFrame/
SetDefaultFrame remain for technical tests; Set* returns false in CharacterConfig mode. Explicitly configure
manual mode to preview individual frames; it cannot silently override the animated mode.

JSON/atlas tables load once per catalog/version. Both timings resolve once per selected config revision and are shared
by the whole roster. Missing/invalid either clip atomically rejects the pair and clears old timing; no idle substitution for missing run. Timing edits do not reload an unchanged atlas or its GPU frame table. Use Inspector or
`CharacterAnimationConfig.Set` to replace runtime settings (defensive copy, Version++); do not mutate objects
exposed by Characters directly. Edits recompute phase from current battle time with the new N/T/curve. Invalid
settings clear timing/draw and log once per revision; corrected config/catalog Version resumes without restart.
Duplicate selected character/clip IDs, missing bindings/clip, non-loop/hold-last, invalid N/T/curve and mismatched
character geometry have field/ID diagnostics. Other unselected invalid records do not block a valid character.

No gameplay component writes, systems, clocks, queues, per-fighter materials or textures. One loaded atlas owns
its native frame table and GPU frame buffer; the renderer owns mesh/material/instance buffer, extraction and timing.
Disable/destroy releases owned GPU/native/query resources; project config/catalog/TextAsset/PNG are never destroyed.
Atlas reload invalidates indices/timing; buffers grow by capacity only, unchanged from the existing renderer.

## Validation boundary

EditMode additionally covers all eight directions and Player/AI sources, rejected input, airborne/landing/facing, stale Applied tick on re-enable, manual partial Worlds, mixed pages/trim and read-only identical replay. New PlayMode: 2/100/400, pause, paused Restart/End, visibility/disable,
config error/recovery/no-fallback, atlas reuse, World disposal/rebind. Graphics compares animated page/trim/flip
with an independently selected manual frame at 1/2/3 pixels per source pixel. Existing manual graphics/depth tests
remain. Real-art smoke skips explicitly BEFORE scene loading if external PNG is absent. Performance retains
`stage03-sprites-*` technical controls and adds `stage03-idle-run-*` mixed real animated series at 2/100/400, with a test-only producer in existing Collection/queue (allocations outside Refresh measurement), at least three cycles of both clips/all11 frames, and the same
validated allocation controls and synchronous pre-yield timing. The two prefixes do not overwrite each other.

Unity compilation/import/Burst, all authored Unity tests, real GPU output, fresh GC/performance, art Game view,
Frame Debugger/player build/Domain Reload off are NOT RUN here. No historical #38 result verifies #42/#43.
See `Docs/Evidence/45/README.md` for required exact-SHA runs and collected static evidence.

## #45 source-selection details

Grounded is read AFTER movement, so jump takeoff already selects idle and landing with nonzero movement selects run.
Command.Tick must equal SimulationClock.Tick and Command.Combatant must match the extracted ID. WasSubmitted is not a
movement criterion; raw input and held jump are never read. Current Movement has no blocking collisions: accepted MoveX/Z
is its actual planar movement input. When collision resolution arrives this graphical criterion needs explicit review.
AnimationTiming is always the idle cached table, not a per-combatant current-clip property; RunAnimationTiming exposes run.
Both share one atlas/GPU tables and one submission per occupied page. No curve integration or config lookup occurs per actor.
The latest PNG supersedes run pixels in the earlier idle_run ZIP; do not extract the old archive PNG over the correction.
Its metadata is geometrically compatible and kept unchanged, but equality to the ZIP's older separate run frames is not claimed.
User Unity package/import/scene edits from97b8c188 and idle1s curve are preserved; GPU rendering uses JSON trim/pivot rather
than Unity Sprite Editor slices. Download only the latest PNG to the existing Atlas path, keeping the existing .meta GUID.

## Multiple characters and catalogs

The common config can contain many character entries. Every CharacterId binds its own SpriteAtlasCatalog/manifest/pages
and its own idle/run settings; same clip IDs in different characters never share frame indices or timing. ConfigureAnimation
selects the desired character for this renderer. A catalog/character switch disposes old native/GPU frame data, clears
all old MaterialPropertyBlock texture/buffer references (including unused page slots), and binds the new frame table,
PixelWorldSize and timing. Imported catalogs/textures remain project-owned. Synthetic A→B→A regression checks two very
different catalogs plus an unrelated invalid entry.

Currently the renderer applies ONE selected default character type to the whole ECS roster. Different characters
simultaneously in one battle need the separate #46 per-combatant appearance/multi-atlas integration; it is not implemented
by adding config entries alone. No second real character, appearance writer or universal atlas registry was added here.
