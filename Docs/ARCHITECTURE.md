# MLF Legends — Combat architecture

**Document type:** technical blueprint. No combat runtime has been implemented/verified yet.

## Target platform and project state
- Unity **6000.5.4f1**, Universal 3D URP **17.5.0**, 3D environment.
- Existing repo includes Unity template project. URP renderer is assigned in GraphicsSettings and QualitySettings.
- Initial template packages include Input System 1.19.0, Burst 1.8.29 transitively, Collections 6.5.0 transitively. **Entities and Entities Graphics are not yet installed**.
- Core combat design must not assume installed ECS APIs until versions are resolved and compiled in Editor.

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

## Physical rules
Every living combatant has a lightweight body collider. Allies block and gently yield; player can push allied crowd with modest resistance. Enemies and walls remain solid. No ghost dash. Hits can cause stagger, launches, push-through waves and collisions with other bodies; custom ECS simplified physical resolution, not 400 dynamic Rigidbodies. Direct basic attacks don't damage friends; secondary magical/environment effects can.

## Rendering research
Prefer a specialized GPU-atlas/instanced renderer, not per-combatant Unity Animator or SpriteRenderer. Benchmark direct/indirect drawing approach, Entities Graphics interoperability, frame selection, transparency sort, equipment overlays, shadows and terrain occlusion before committing to an implementation.

## Navigation (Stage 11)
No Unity NavMeshAgent/NavMesh as authoritative navigator. Own heightfield-based X/Z walkability; group flow fields, local bounded A*, local avoidance and jump-vs-route decisions. Dirty-region rebuild as terrain changes. Terrain mostly gentle slopes/ramps with limited cliffs; collisions and nav must agree on traversability.

## Campaign boundary
Aging, generational growth, diseases, recruitment, campaign turns and post-death succession are **design background only**, deferred. Battle-relevant injury, blood loss and loss of capabilities are active combat scope. Avoid speculative campaign frameworks in early stages.

## Diagnostics
Per-stage tests and profiles: stable tick replay, spawned/despawned entities, structural changes, memory allocations, Burst job execution, collision pairs, hit ledger, CPU sim time, GPU frame time, batches, AI decisions. No FPS guarantee before recorded measurements on user hardware.

## Architectural acceptance
Before merging any combat feature, trace how commands → state transition → geometry and collisions → damage and guard → injury and death → presentation and AI connect. Confirm exactly one authoritative location for each effect and safe job ownership.
