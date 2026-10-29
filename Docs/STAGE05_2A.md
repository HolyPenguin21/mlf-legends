# Stage 05.2a — waists, facing and feedback (#74)

Delivered after #73 / PR #75 on main 78b473b496a22296a169c1b1b05363174dacb69e. Stage 04/05 owner manual and visual acceptance remain open. Next functional pass: 05.3 VFX.

## Hit reference change (intentional gameplay rule)

`waistY = CombatPosition.y + accepted CombatBody.Height / 2`. Hit center is the attacker's waist plus the execution's frozen HitOffsetY. The target point is the target's waist. Inclusive tolerance checks the difference of those two points, not PNG height, canvas, image pivot, spawn height or a hurt capsule. For equal heights the old Y difference is preserved algebraically. Height 2 places the center 1 unit above support.

Both stationary Inside and conservative query bounds use the accepted heights. Swept executions use the actual height stored in each committed interval by the existing spatial owner; final height is not retroactively substituted for a different path. Occlusion rays and physical hitbox gizmos use the same waist centers. The existing O offset -0.3 is now relative to the waist; there is no additional foot correction. Reach/depth/damage/teams/ledger remain unchanged.

## Facing policy and cost

CombatFacingSystem remains the only runtime writer. It gathers enabled, non-Disabled actors with Id/Team/Position once before Movement, then a synchronous Burst nearest calculation uses distance² in X/Z. It includes HP=0; no defeat mechanic exists. Self and the same team are excluded. Equal distance selects the lower CombatantId; |ΔX| ≤ 1e-6 keeps the old side. A Z-only enemy therefore suppresses movement fallback. No enemy (or a partial fixture missing team/id/position) uses the former accepted MoveX fallback; zero MoveX preserves facing. There is no acquisition radius or retained selected-target component.

Each running strike locks direction after its first tick. A chained strike may reselect on its own first tick. The existing landing recovery orientation lock is retained. Action → Facing → Movement → final index → Hit order is unchanged. LaunchTick0 J is resolved in Movement after this rule; body/anchors/hitbox all read the same facing. WASD remains world movement; no pursuit, AI or new animation clips.

This is all-pairs O(participants × roster), bounded by the existing 400-fighter limit, with reused native gather scratch. It does not query an unbuilt final index and does not allocate a second grid. LastMicroseconds measures gather plus the synchronous kernel; LastKernelRanManaged records whether that kernel ran managed. The 2/100/400 ability benchmark now records facing cost beside full tick, with separate actual Burst markers. Timing is NOT RUN here; no linear-cost or performance claim is made.

## Damage tint

CombatDamageFeedback.LastDamageTick is initialized to zero with the lifecycle-created fighter and written only by Hit after a confirmed Damage > 0, including HP=0 targets. Damage=0 and overlap candidates do not flash. The ledger prevents another Active tick of the same strike from refreshing it. Multiple attackers/strikes refresh the same bounded window; there is no stacking or render-frame HP comparison.

Renderer reads that fact for each extracted Entity. Factor is smoothstep(1 − tickAge/duration), zero at age >= duration; it depends only on the existing simulation tick and freezes on pause. The shared sprite shader mixes only RGB after the existing alpha clip. UV, flip, depth, silhouette, culling and alpha policy are unchanged. One 32-byte structured instance record (24 before; factor plus alignment padding) and one existing shared sprite material serve the roster. No per-fighter Material/MPB/coroutine/GO was added. Entity-based extraction prevents page sorting or reused ids in another World/battle from transferring tint.

## Ground projection

CombatGroundShadows is a read-only helper owned/disposed by the one CombatSpriteRenderer, not another renderer component or geometry owner. It reads active numerical CombatArenaShape data (box/platform/ramp) and the current fighter X/Z. It selects the highest supported center surface below the fighter; no numeric support means no shadow. Legacy flat-plane fixtures with no acquired arena deliberately have no guessed shadow. Jump/self J/airborne sprite height does not lift the projection.

The disk lies on the selected plane with orthonormal ramp tangents. The shader clips it to that surface's finite footprint, avoiding floating over its edge. One shared quad, material and grow-only instance buffer submit the entire shadow roster in one extra instanced draw. Depth test LEqual, no depth write, ordinary alpha blending, a small normal bias; opaque arena and sprite depth occlude it. Its independently projected worldBounds are separate from airborne sprite bounds; culling remains enabled. Projection is read-only and is not cached across ticks/arena revisions, avoiding stale support. No Physics raycast/collider, Light or Projector.

Renderer Stats.Submissions continues counting body page submissions; ShadowSubmissions (0/1), ShadowInstances and ShadowMicroseconds are separate. Shadow instance stride is 80 bytes; capacity grows and is reused. GPU/CPU/GC deltas have not been measured. The PlayMode sprite benchmark now has groundShadows false/true cases at 2/100/400 and reports shadow CPU, buffer bytes and actual draw counters; GPU milliseconds are not measured by that harness. It keeps its validated allocation controls. No previous benchmark values are reused.

## Inspector and manual review

`Assets/Scenes/CombatSmoke.unity` → CombatSpriteRenderer:
- Damage feedback: Damage Color RGB red, Damage Intensity 0.25, Damage Duration Ticks 9 (150 ms at 60 Hz).
- Ground shadow: Draw Ground Shadows on; Radius Multiplier 1.25 × Body.Radius; Color black / alpha 0.35; Bias 0.003 world units. Shared shadow shader is serialized in the scene for build inclusion.
- Debug: Draw Anchor Gizmos only has effect in Play Mode. Viewport/source canvas/image/card/anchor guides cannot draw in Edit Mode, including selection; no selected callback reintroduces them. Physical Body/hit debug remains a separate bootstrap toggle.

In CombatSmoke: retreat left/right while attacking with U/I; move sideways; use O; watch recipient-only tint and shadows while airborne, on ramp/platform and at unsupported edges. Check sprite flip/anchors on nearest switch, strike lock, two simultaneous recipients, pause/restart/despawn and World replacement. Inspect selected/unselected Scene before Play and after exiting, then the explicit debug toggle in Play. These visual checks are NOT RUN here.

## Verification scope

Targeted tests: CombatFacingTests (retreat/source parity, stable ties, Z epsilon, HP0/membership/Disabled/despawn, chain lock, pacing, 2/100/400 fallback, rejected/deferred input, pause/restart/foreign World and partial fixtures); CombatFeedbackTests (positive/zero damage, HP0, repeated and simultaneous multi-target confirmations, lifecycle/pause, waist boundaries/offset, bounded fade, extraction across pages/World and floor/ramp/platform/no-support projection); CombatPhysicalAbilityTests retains launch/sweep coverage with the new nearest-facing contract. Existing independent final-point oracle now reads accepted heights and the explicit waist formula. Old movement-facing expectations in input/physics/presentation tests were reconciled with the intentional new policy.

A new two-instance Graphics test checks that only the receiver's pixels change within one shared batch. Shader, GPU depth/opacity, actual shadow appearance, Unity compilation, all EditMode/PlayMode executions, Burst runs and performance measurements remain NOT RUN in this environment. SOURCE syntax/schema/ownership checks and independent MODEL arithmetic are the available evidence; neither is runtime proof. A source scan also corrected the writable NativeArray test fixture in #73 (using-variable index writes replaced by try/finally ownership).
