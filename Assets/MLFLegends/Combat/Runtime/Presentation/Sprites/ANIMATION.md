# Current #52 control integration

[Current keyboard/intent contract](../../../../../../Docs/STAGE03_CONTROLS.md). Exact hand/foot/spin and general-series
requests feed the existing CombatAttackState; this renderer selects the existing named clips and never reads keys.
PR #51 already supplies the shared bootstrap config binding and empty-clips preview guard; neither is duplicated here.
Authoritative selection remains strike → airborne jump → landing recovery → run → idle. Phase speed is 50/25/25;
landing is uninterruptible by controlled move/new actions for the configured frame holds (currently 12 ticks after 68c43c).
Older dated animation descriptions below are historical where they state fixed strike tuning, run-before-landing,
Preparation-only movement or old input keys. Stage 03 is NOT accepted until the owner confirms the Unity check.

# Stage 03 final polish — pending owner acceptance (2026-10-28)

Current settings and compatibility: [Stage 03 final polish handoff](../../../../../../Docs/STAGE03_FINAL_POLISH.md).
Reference world support is source_size × [0.5,0.85]. Imported Sprite pivots position each image; JSON is fallback only.
One ImageOffsetPixels per character adds a translation. Raster Inspector preview is removed; diagnostics are cached.
[Current pivot and Inspector contract](../../../../../../Docs/STAGE03_PIVOT_CORRECTION.md).
Attack planar speed is 50% Preparation, 25% Active, 25% Recovery. Landing now has an authoritative motion recovery window:
physical contact → jump landing frames (movement/new actions locked) → run if held, otherwise idle_combat. In jump settings,
LandingStartFrame..EndFrame sets the range; -1 uses the old land marker. T/curve frame holds set the duration (ceil to ticks),
published with the existing combat timing table. Current flat curve, T=1s, EndFrame=4 and LandingStartFrame=4
give one landing frame (4) for 12 ticks / 0.2s; no separate delay. Older 27-tick evidence describes the pre-68c43c config.
These rules supersede old Preparation-only movement and run-before-landing text below. Stage 03 is NOT accepted yet.

# Character animation — Stage 03 tuning: frame ranges and strike phases (current)

**Where.** `Assets/MLFLegends/Combat/PresentationData/CharacterAnimationConfig.asset` → *Characters* → `mage_female_01` →
*Animations* → one entry per clip. The scene object *Combat Bootstrap* (`Assets/Scenes/CombatSmoke.unity`,
`CombatSmokeBootstrap`) references the same asset in **Animation Config** and the character in **Strike Character Id**: it
turns the strike phases into the simulated strike windows. The renderer (*Main Camera*, `CombatSpriteRenderer`) reads the
same asset for drawing.

**Frame numbers are LOCAL clip indices from 0** (the clip's first frame in the manifest is 0), never page cells or global
atlas frame numbers. The Inspector says so above every entry.

| Field | Stored | Meaning |
|---|---|---|
| `StartFrame` | no, read-only | first played frame = the source clip's first frame, always 0 |
| `EndFrame` | **yes, editable** | last played frame, inclusive; must be inside the clip (`0..clip frames − 1`) |
| `FrameCount` | no, read-only | `EndFrame − StartFrame + 1` |
| `TotalDurationSeconds` | yes | playback time of exactly `StartFrame..EndFrame` |
| `SpeedCurve` | yes | X = normalized time 0..1 of that range, Y = relative speed ≥ 0; distributes only the played frames |
| `PreparationEndFrame` | yes (strike clips only) | Preparation = `StartFrame..PreparationEndFrame` |
| (Active start) | no | `PreparationEndFrame + 1` |
| `ActiveEndFrame` | yes (strike clips only) | Active = `PreparationEndFrame + 1..ActiveEndFrame` |
| (Recovery) | no | `ActiveEndFrame + 1..EndFrame` |

Only `EndFrame`, `PreparationEndFrame` and `ActiveEndFrame` are stored: every other boundary is derived, so phases are
always consecutive, without gaps or overlaps, and cover the played range. Each phase needs ≥ 1 frame (a strike needs ≥ 3).
Nothing is clamped: an out-of-range `EndFrame`, an empty phase or a too-short clip is an error with the field path
(e.g. `mage_female_01.punch_left.ActiveEndFrame: 5; Recovery 6..5 must hold at least one frame (EndFrame 5)`), the whole
set is rejected (nothing drawn) and the bootstrap keeps the previous strike windows. Changing `EndFrame` re-validates the
phases. After an atlas update that shortens a clip the error names the clip's current range ("was the atlas clip
shortened?"). Phase fields are drawn only for the five strike clips. JSON and PNG are never modified.

**One timing source.** Range + T + curve give each frame's start time (`SpriteAnimationTiming`, computed once per config
revision). For a strike, Active starts at the start time of `PreparationEndFrame + 1` and Recovery at the start time of
`ActiveEndFrame + 1`; the clip ends at T. `CombatStrikeTiming.TryFromSeconds` quantizes this to fixed 60 Hz ticks: tick `e`
of a strike simulates clip time `[e, e + 1) / 60`; a tick is **Active when it overlaps the Active time**, so an Active phase
shorter than a tick, or several boundaries crossed inside one tick, still give at least one Active tick (never skipped).
Preparation = ticks before it (`floor`), Recovery = ticks from `ceil(RecoveryStart · 60)` to `ceil(T · 60)`. A Preparation or
Recovery shorter than a tick may get 0 ticks: reported as a warning. The five timings form `CombatStrikeSchedule`, published
into ECS by the bootstrap; the follow-up chain point is Recovery start + `CombatAttackTuning` chain delay (capped at the
strike's end, so a short Recovery never removes a series). Reach, damage, chain delays and series rules stay in
`CombatAttackTuning`.

**Drawing follows the simulation.** A strike copies its timing into `CombatAttackState.Timing` when it starts. Drawing
reads that frozen phase for the tick and shows the frame at clip time `elapsed / 60`, kept inside that phase's frames; the
drawn phase always equals the simulated one, at any render rate, while paused or after catch-up ticks. Hits are resolved
only by `CombatHitSystem` from the frozen timing — never from the renderer or a drawn frame. Moving during Preparation
keeps the strike clip (strike has drawing priority over run).

**Example — `punch_left` (migrated project values).** Range `0..5` (6 frames), T = 0.48333 s (29 ticks), flat curve
(each frame 0.08056 s):

| Phase | Frames | Clip time | Ticks |
|---|---|---|---|
| Preparation | 0..2 | 0 – 0.2417 s | 0..13 (14) — no hits, the fighter may move |
| Active | 3..3 | 0.2417 – 0.3222 s | 14..19 (6) — hits resolve, each target at most once |
| Recovery | 4..5 | 0.3222 – 0.4833 s | 20..28 (9) — no hits; a buffered follow-up starts at tick 24 |

Resulting project windows (Preparation/Active/Recovery ticks): punch_left 14/6/9, punch_right 9/11/9, kick_left and
kick_right 11/6/16, kick_spin 20/5/20. The previous fixed `CombatAttackTuning` windows (13/5/11, 9/9/11, 11/5/17, 19/6/20)
remain only as the default table of worlds that publish nothing (tests, benchmarks). The config Inspector lists each
strike's phase frames, seconds and ticks and the whole table; the bootstrap logs every published table.

**Migration.** Old entries stored `FrameCount = N` ("first N frames"). On load the hidden migration slot moves it to
`EndFrame = N − 1` (StartFrame 0), so the played range, `TotalDurationSeconds` and `SpeedCurve` are unchanged; the slot is 0
afterwards and never read again. The project asset was migrated in Git: every clip keeps its range/T/curve; strike phases
were initialised from the manifest `strike_pose` markers (punch_left 0..2/3/4..5, punch_right 0..1/2..3/4..5, kicks
0..1/2/3..5, kick_spin 0..3/4/5..8). An old asset elsewhere gets phases 0/0, which is reported as an error (never guessed);
the Inspector offers the explicit button *Set Preparation/Active from manifest strike_pose*. A config Active range that
differs from `strike_pose` is only a warning: the config decides.

**Play Mode edits.**
- Range, T, curve, phases: Inspector edits bump the config `Version`; the renderer rebuilds its frames on the next frame and
  the bootstrap re-derives and publishes the strike table on its next `Update`. **Strikes started afterwards** use it; a strike
  already running keeps the timing it started with to its end (no mid-strike change). An invalid edit logs one error and keeps
  the previous table (and draws nothing until fixed).
- The atlas catalog `Version` (re-import) also triggers re-validation.
- Team sizes on the bootstrap apply only on the next start/restart (enable, or *Restart battle* in its Inspector).
- Disabling the bootstrap restores the table it replaced (unless someone published after it).

The section below (Stage 05.1 / PR #48) is historical where it contradicts this one: N is now `EndFrame`, and strike
frames are no longer "fitted" to fixed `CombatAttackTuning` windows — the config phases define those windows.

# Character animation — Stage 05.1 / PR #48

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
idle restarts at frame 0 on the next tick. `entry`, `return`, `chain_exit` and `compatible_transitions` are not read at runtime; a test
checks that each strike's chain point (Windup + Active + ChainDelay) is reached only after its `chain_exit` frame shows.
The art marks `kick_spin` with `chain_exit [5]`, but by the proposed rule P4 the spin is a finisher and never chains.

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
