# Stage 05.3 — damage duration, strike effects and auto focus (#77)

Base main `d5e97dea7326fa6bc6ccd28ebb44f823c601a92d` includes #73/#74 review fixes and owner smoke tuning.
One branch/PR for #77. Stage04/05 owner acceptance stays open. Merge requires a separate owner instruction.

## Execution and numerical ownership

Existing order: Lifecycle → Clock → command collection/processor → Action → Facing → Movement → final index → Hit.
No new system, queue, grid, action owner, Position writer or clock. Action copies the existing table's Timing and Strike
at every accepted start. Two added integer handles in frozen Strike select immutable prepared effect bindings; jobs never
read a Unity asset. Bootstrap prepares resources outside ticks on config/catalog revision. Invalid authoring rejects the
publication with a labelled error, keeping the previous table. Optional null/Disabled bindings disable only that effect.
Body, phases, T/curves, pivots, owner values and character atlas artwork/metadata are preserved.

### One damage interval

`CombatStrikeTiming.DamageStart/DamageEnd` are the execution's sole half-open damage interval in elapsed fixed ticks.
Default: raw animation-derived Active start/end (or integer ActiveStart/RecoveryStart for explicit tick fixtures).
An explicit duration leaves the start unchanged and sets end = start + seconds × 60. Authoring must be positive/finite
and end by the body clip's actual duration, including any fractional last tick; invalid input is rejected, never clamped.
This override changes gameplay. It does not change graphic phases, 50/25/25 movement, chain points, self launch, Height
profile, landing or O's no-chain rule. A replaced/ended execution has no independent hitbox left behind. Editing the asset
acts on the next execution; the current ledger is never cleared by a duration edit or a new tick.

Hit's eligibility is overlap of the simulated interval `[elapsed, elapsed+1)` with this damage interval; it no longer
requires PhaseAt == Active. Ordinary point attacks retain their existing final-point geometry and valid empty trace
NativeArray; their numerical confirmation time is the admitting slice's lower boundary. Swept O clips both sides of every
accepted synchronized segment to the damage interval, includes its start, excludes its end, and computes conservative
query bounds only over that clipped attacker path with each segment's accepted Height. The final snapshot/epoch/membership
guard still precedes HP/ledger writes. Waist, HitOffsetY, occlusion, stable ordering and the full-execution once-per-target
ledger remain; authored on-hit impulse still queues once for the next Movement boundary. No self impulse is re-issued.

### F / desired auto-focus mode

Input action `Player/FocusToggle`, default `<Keyboard>/f`, remappable using the existing Input System asset/override path.
Logical bit10 (ushort); retired bits4..8 stay rejected. IsNone and the processor's allowed mask include it. The existing
capture coalesces multiple presses before one accepted tick into one Pressed bit; Held never repeats. Catch-up consumes
that bit once, and Facing also guards LastToggleTick against repeated polling of the same accepted tick. Pause/focus/device
and battle changes retain the existing producer edge-discard contract. Rejected/stale/unknown/foreign-battle commands
cannot alter the addressed fighter's mode. Player and AI share exactly this path.

Lifecycle creates CombatAutoFocus per fighter, zero/default = ON. Facing is the sole runtime writer of this desired mode
and of Facing. It applies accepted toggle intent before its one facing pass. ON retains #74 nearest enemy by X/Z distance,
low stable-ID ties, no radius/retained target and MoveX fallback without an opponent. OFF uses MoveX; idle and pure Z keep
the old side. WASD/speed are unchanged. Strike direction stays locked after its first tick and during landing lock, while
the desired mode can change. The first permitted boundary (including a chained start) uses the new mode. Same-tick F +
new strike resolves one final direction before LaunchTick0, hitbox and Swing publication. Restart/new World do not inherit
mode state. This is not selected-target cycling or a camera lock.

## Facts, time and lifecycle

CombatHitSystem owns one `CombatEffectStream` singleton, one preallocated 8192-element numeric fact buffer, and its
managed World-scoped resource cache. Swing facts are emitted for accepted starts, sorted by attacker ID, after final
Facing/Movement; rejected, jump-prioritized and Held-only presses do not generate starts. Repeated Hit on the same tick
cannot duplicate a start. Impact facts are emitted only after the entire confirmation/write job successfully completes.
Candidate overlap, body push or HP inspection are not events. Damage0 / HP0 accepted Hit produces Impact; positive-only
red tint is an independent existing fact. N unique targets produce N Impacts; attacker + StartTick + target distinguishes
simultaneous actors and each 1–1–2 execution. Exceptions do not publish speculative accepted Impacts.

Fact identity includes owning World, stream epoch, BattleNumber, sequence, attacker ID, StartTick and optional target ID.
Clock convention: simulation tick T integrates `[T−1,T]`; a strike beginning on StartTick starts at `StartTick−1` on this
axis. Render samples the completed `SimulationClock.Tick`, with no Time.time, coroutine, extrapolation or accumulator.
Swing end = start + raw end of the last graphic Active frame (T/curve), not the first instant of that frame and not the
damage override end. Its own graphic clip frame shares are normalized once over that entire range; its DurationSeconds
field is deliberately ignored. A whole sub-tick Swing may already be expired before the next refresh; its start remains
in history, never replayed from frame0. Impact time = T−1 + accepted segment contact fraction, position = target's accepted
waist at that fraction. It stays at that world point and uses its own duration, even after target/attacker despawn.

Swing follows its still-running execution's current accepted Position. Its facing and resource/anchor tables stay frozen;
replacement/end/despawn/Disabled membership stops it. A selected hand/cast anchor uses the frozen body timing at the
sampled moment; Impact uses the historical contact moment, never current renderer extraction. Initial event time maps to
local frame0. Position uses effective image pivot plus ImageOffsetPixels through SpriteCardMath; anchor direction, frame
geometry and offset are reflected once. Missing/hidden anchors either use the explicit fallback Offset or skip, counted.
Anchor layer supplies front/back when present; fallback uses binding Layer. Gameplay coordinates stay numeric and art-free.

Pause freezes the same clock and therefore age/frame. Resume continues. End/Restart at the existing safe lifecycle boundary
(including paused Initialization proxy) clears logical history and increments epoch; old facts never enter another battle.
Readers never remove facts. Repeated Refresh/Camera.Render, offscreen/re-enable and a second consumer see the same ages.
An already-created world contact flash can finish after ordinary despawn without dereferencing the destroyed entity.

Retention: expired prefix facts retire after `end+120` ticks. A long-lived older fact may retain expired newer facts behind
it; the hard capacity is always 8192. Overflow overwrites the oldest fact in stable publication order, counts HistoryDropped
and never stops HP/ledger/physics. At most 2048 retained live instances are drawn; oldest retained live facts win, excess is
counted in Dropped on each refresh. This affects presentation only. World resources retain up to 640 binding slots (64 full
five-slot revisions), sharing atlas/catalog revisions. Handles are never reused or changed under live executions or scene
publication leases. At capacity, newly published strikes get disabled VFX handles, a warning and ResourceDropped count;
valid gameplay settings still publish. A new World releases this version cache. Failed preparation rolls back new resources.

Renderer World changes clear old property blocks; disable disposes its shared effect material/buffer/queries and body mesh.
World/Hit teardown disposes cached atlas tables/frame buffers and generated fixture textures, never imported assets. Older
valid atlas versions can finish live executions; no old entity references are held in the resource cache. End/Restart clear
battle history while keeping reusable, valid World resources for the unchanged published table.

## GPU and diagnostics

One helper owned by the existing CombatSpriteRenderer, one shared effect material and fixed-capacity instance buffer;
no per-event GO/Material/ParticleSystem. Uses existing SpriteAtlas parser/validation/frame buffers, shared card mesh and
SpriteCardMath, with its own 32-byte instance layout (position, depth, frame, facing, world-units-per-pixel scale, rotation).
Batches group by effect atlas/page; pages/trim/frame shares can differ from the 192×160 character canvas. Independent
conservative rotated-card bounds include the support bend. Distant body/page membership does not change effect depth:
Swing reads only its matching actor's bounded tie push. Front/back is ±0.004 world units around the body bias. Impact depth
is based on its fixed contact point; Front reserves the existing 1/16-unit body tie budget, Back uses the unpushed side,
so a later target/roster/page change cannot alter its depth. Transparent alpha blend, ZTest LEqual, no depth writes; opaque body/arena provide
occlusion. Effects do not inherit body damage tint; body alpha/depth policy and ground shadows are unchanged.

`CombatSpriteRenderer.Stats.Effects`:
- Spawned: cumulative facts with an enabled numeric binding in the current epoch.
- Alive: currently extracted instances (≤2048); Expired: expired enabled facts still retained this refresh.
- Retired: cumulative history-prefix retirements (all fact kinds/bindings); HistoryDropped: ring overwrites.
- Dropped: live instance-capacity omissions this refresh; MissingAnchors/MissingBindings: explicit failures this refresh.
- ResourceDropped: cumulative binding requests disabled by version capacity; Submissions/BufferCapacity: actual GPU draws/capacity.
- Stats.EffectsMicroseconds: CPU extraction + effects submit; body/shadow timings and counters keep their old meaning.
These are diagnostics, not assertions that GPU/GC/performance were measured here.

## Inspector and visible O example

`Assets/MLFLegends/Combat/PresentationData/CharacterAnimationConfig.asset` → Characters → mage_female_01 → Combat →
Hand1 / Hand2 / Foot1 / Foot2 / PhysicalAbility:
- Override Damage Duration + Damage Duration Seconds. Read-only footer “Strike windows” displays exact seconds/ticks for
  damage and Swing beside the existing graphic phase counts. Default override is OFF for all five slots.
- Swing / Impact: Source Disabled/Catalog/Technical, Catalog, ClipId, Scale, Offset (world forward X/up Y/depth Z), Mirror,
  optional Anchor, Missing Anchor policy, Layer; Duration Seconds is Impact-only (positive, ≤2 s).
- Current smoke config explicitly enables procedural **Technical** `swing` and `impact` for all five slots. This is a cyan
  sweep and yellow flash produced by TechnicalEffectFixture, a pipeline diagnostic rather than approved art. Replace Source
  with Catalog and assign its clip to install final art without a simulation change. No PNG/art was added or redrawn.

`Assets/Scenes/CombatSmoke.unity` → CombatSpriteRenderer → Strike Effects: Draw Effects and retained Effect Shader.
`Combat Bootstrap` keeps existing physical gizmos: the hitbox is red on the admitting damage interval, independent of phase.
For swept damage ticks the gizmo shows the conservative AABB of only the clipped accepted path, rather than an inactive
final endpoint. Game-view smoke label shows Auto focus ON/OFF [F]. `Assets/InputSystem_Actions.inputactions` → Player → FocusToggle changes F.

For O, first keep the owner's values/override OFF and compare with override ON, choosing a duration that ends by the
read-only execution end. Walk/launch past a late target after the graphic Active has ended: a long damage window may admit
it while Swing is already gone. One target recontacting still loses damage only once. Repeat identical free group positions
with weak/strong self J, then a wall/trapped group: inspect distinct victims, physical displacement, holds/discards and effect
contact positions. Test U/I miss = Swing only; multi-target = one flash per target; F OFF retreat left with enemy right,
F during U/I/O and landing, and F+O LaunchTick0. These visual/runtime scenarios remain NOT RUN here.

## Reproduction and evidence

Available source/model scripts (install tree-sitter, tree-sitter-c-sharp, PyYAML):

```
python Tools/Verify-Stage05Ability.py
python Tools/Verify-Stage05Feedback.py
python Tools/Verify-Stage05Effects.py
```

These parse/check source/data and run independent arithmetic models; they do not execute C#, jobs or shaders.
Authored EditMode coverage: CombatStrikeEffectsTests; new CombatFacingTests and real-input PlayerInputProducerTests;
CombatFeedbackTests multi-attacker identity and CombatPhysicalAbilityTests historical contact point; replay records now
include focus mode/last toggle and the frozen numerical damage interval. Keep all #73/#74 regression suites.
PlayMode Graphics: CombatStrikeEffectsGraphicsTests, separate clips/pages, pixels ON/OFF, repeated refresh and re-enable.
Performance: CombatSpritePerformanceTests.StrikeEffectsCost2_100_400 at 2/100/400, ordinary/mass × VFX OFF/ON with identical
commands, a separate warm-up flag, full tick, extraction/body/effect submit, total synchronous CPU and host frame time,
validated allocation controls, actual Burst markers, native motion/contact/effect-frame capacity, effect GPU frame/instance bytes and live/draw capacity.
Writes TestResults/performance/stage05-3-effects-*.tsv; host frame includes the Editor/default World. GPU milliseconds are
NOT MEASURED. Benchmark has no claimed measurements until actually run on named hardware.

Unity import/compilation, EditMode/PlayMode/Graphics/Performance, actual Burst/GPU/GC and owner manual acceptance are
NOT RUN in this environment. [Exact tested source and raw checks](Evidence/77/README.md). Source review and models do not
replace the remaining wall/front/back/offscreen/anchor/GPU checks or weak/strong crowd demonstration.
