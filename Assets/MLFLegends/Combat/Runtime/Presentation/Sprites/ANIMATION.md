# Character animation — Stage 03.3a / #43

Current scope is one real `mage_female_01`, one `idle_combat` loop, used even while moving/jumping.
No state-driven clip selection, transitions, root motion, gameplay events or per-fighter visual objects.

## Setup and ownership

`CombatSmoke.unity` Main Camera uses `CombatSpriteRenderer.AtlasSource.CharacterConfig` (2),
`Combat/PresentationData/CharacterAnimationConfig.asset`, character `mage_female_01`.
The one shared config has Characters → CharacterId / AtlasCatalog / Animations → ClipId / FrameCount /
TotalDurationSeconds / SpeedCurve. It points to the existing per-character `SpriteAtlasCatalog`, which binds
JSON and exact PNG basenames. The renderer has no second active catalog reference in this mode.

Install the external revision-2 PNG at
`Assets/MLFLegends/Art/Characters/MageFemale01/Atlas/mage_female_01_00.png`, preserving the tracked `.meta`.
The matching JSON and catalog/.meta are included. PNG SHA256:
`f931423030c6bc39de9f8374cd4d240fc460dda46488fa506ebbc7e21f9321c7`;
JSON SHA256: `8d7e273c80f7b7dd1e90de8301bf663e2ebcaefa2ebac867ee3cf294f3350fda`.
The PNG is intentionally ignored in Git. It is not generated or substituted at runtime.
Import RGBA32/sRGB, Point, Clamp, no mipmaps/compression/NPOT resize, max 2048, not Read/Write.

Defaults: 6 frames, 1.2 seconds, speed 1. Nominal 128 px = 2 world units (`2/128` per source pixel),
canvas 192x192, common pivot [96,160]. All six source bodies fit 128x128 and their bottom bound is y160.
Transparent guards inside 136x136 useful rects keep extrusion outside body pixels. A row is an art packing
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

Everyone shares the battle phase; no per-id timing state is needed. The fixed clock freezes on pause, repeated
Refresh never advances phase, resume retains it. Restart uses the new BattleStartTick, even while paused. World
change/disposal rebinds read-only queries; invalid/missing session/clock or Tick<start draws nothing. Offscreen,
disabled renderer and zero active roster do not stop ECS or elapsed time. Reappearance uses the current phase.

## Integration, caching and edits

The existing extraction adds one read-only clock query (three queries per World total). It selects the current
frame BEFORE projected footprint/depth offsets/page grouping, so changed trim/pages/anchors use the same frame.
The bounded #42 depth policy and shader are unchanged. Renderer manual `Configure`/SetFrame/SetClipFrame/
SetDefaultFrame remain for technical tests; Set* returns false in CharacterConfig mode. Explicitly configure
manual mode to preview individual frames; it cannot silently override the animated mode.

JSON/atlas tables load once per catalog/version. Timing resolves once per selected config revision and is shared
by the whole roster. Timing edits do not reload an unchanged atlas or its GPU frame table. Use Inspector or
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

New EditMode: timing/count/duration/curve errors and scale invariance, config IDs/copies, animated extraction pages
and 400 roster, read-only identical replay. New PlayMode: 2/100/400, pause, paused Restart/End, visibility/disable,
config error/recovery/no-fallback, atlas reuse, World disposal/rebind. Graphics compares animated page/trim/flip
with an independently selected manual frame at 1/2/3 pixels per source pixel. Existing manual graphics/depth tests
remain. Real-art smoke skips explicitly BEFORE scene loading if external PNG is absent. Performance retains
`stage03-sprites-*` technical controls and adds `stage03-idle-*` real animated series at 2/100/400, with the same
validated allocation controls and synchronous pre-yield timing. The two prefixes do not overwrite each other.

Unity compilation/import/Burst, all authored Unity tests, real GPU output, fresh GC/performance, art Game view,
Frame Debugger/player build/Domain Reload off are NOT RUN here. No historical #38 result verifies #42/#43.
See `Docs/Evidence/43/README.md` for required exact-SHA runs and collected static evidence.
