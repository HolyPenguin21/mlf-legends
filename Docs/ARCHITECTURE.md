# MLF Legends — Combat architecture

## Stage 03.3a / #43 — current integration (2026-10-27)

The existing renderer gains an explicit CharacterConfig source. One shared project ScriptableObject maps character
IDs to existing SpriteAtlasCatalog assets and per-clip N/T/relative-speed curves. SpriteAnimationTiming compiles
curve integrals/frame boundaries once per revision; extraction reads the existing BattleSession + SimulationClock,
selects idle_combat BEFORE footprints/depth/page grouping, and writes nothing to ECS. All fighters share the battle
phase, including while moving/jumping and offscreen. Pause/resume/restart require no new clock/state owner.
Manual fixture APIs are retained in manual sources. No per-fighter object/Animator/material/texture or second render
path. Scene uses the real external PNG + tracked JSON/catalog/config. Current nominal mage height128px maps to2units.
Only idle is integrated; state-driven clip selection/transitions remain deferred. Details: Presentation/Sprites/ANIMATION.md.
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
auto-facing and no action locks yet; future attack/guard restrictions belong in this owner. The processor,
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
