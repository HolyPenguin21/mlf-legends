## Current #64 — Stage 04.3 body contacts (2026-10-29)

PR #62 and #63 were merged on the owner's explicit command; integrated main base is
`adf98cd1c9ca4df2b2d2e96aa65d3a7e17eb3a5a`. Branch `feature/64-body-contacts`; the owner subsequently explicitly authorized commit, push and merge of #64.
[Current body-contact contract, execution boundary, tuning, limits, checks and benchmark](STAGE04_BODY_CONTACTS.md).
Movement remains the only Position/Motion writer: gather all proposals → same owner's working swept grid → sorted
body/world constraints → write all results → current-tick final index → unchanged Hit. Managed orchestration explicitly
schedules/completes a Burst job; actual execution is diagnosed, not assumed. Mass and controlled-role priority are separate.
Free allies yield, ordinary locomotion does not bulldoze enemies, trapped/unresolved islands stop with diagnostics.
Stage 03 OWNER-ACCEPTED; Stage 04 IN PROGRESS. Unity/C#/Burst/scene/performance NOT RUN; source/model checks are separate.
Historical notes below retain their original SHA/status and do not override this update.

# Stage 04.2 / #61 — static arena

Work starts from #60 PR #62 head `e26e668435b9cb55bcf5bced163710b6c2b61230`, whose correction includes
both review follow-ups. Fresh main at dispatch is still `180b4121792e68931f27c109b78cd0d24c21a4d4`;
#62 is open. The owner's subsequent instruction «Переходи к физике арены» authorizes proceeding on this dependent
branch, not merging #62 or bypassing its manager review. Branch `feature/61-static-arena`; PR is stacked on
`fix/60-animation-checks-evidence` until #60 is merged on the owner's instruction, then must be retargeted to fresh main.
No merge is performed. Stage 03 remains OWNER-ACCEPTED; Stage 04 remains IN PROGRESS.

[Second bottom-up review and corrections](STAGE04_ARENA_REVIEW.md) supersede the earlier solver/lifecycle details below where noted. Unity/C# remains NOT RUN.

## Responsibilities and boundaries

- `CombatArenaShape`: numeric static box/ramp, no GameObject or actor references. `CombatArenaGeometry`: allocation-free
  contact/support math and bounded displacement integration, returns values only. It never writes ECS.
- `CombatArenaSystem`: one World owner of an entity with active/pending dynamic buffers, lease/revision/state and Movement
  diagnostics. ECS owns their native storage and disposes it with the entity/World. No static registry or second grid.
- `CombatArenaAuthoring`: one scene adapter, explicit Sources; reads Transforms and optional solid BoxColliders only at
  enable/explicit Rebuild. Generates one scene-level ramp mesh per ramp on rebuild when a shared RampMaterial is supplied;
  owns/destroys only its own preview objects/meshes, reuses the material. No per-fighter visual or physical object.
- `BattleLifecycleSystem`: same sole spawn path; initializes Grounded by the published geometry at the supplied spawn Y.
  Does not integrate/correct positions. Thus a spawn in midair cannot start a ground attack or invent a personal floor.
- `CombatMovementSystem`: sole runtime writer of Position/Motion; commands and frozen attack phase determine desired X/Z
  once, then contact math determines final position, velocity and support. Publishes only that result.
- Existing Action/Facing/Hit, command/input paths, art/pivots/T/curves/ranges, hit shapes, HP, combo/buffer unchanged.
  Hit remains its prior full search with no wall occlusion rule. Bodies do not block each other yet.

Boundary: BeginFixed ECB → **Arena publication** → Lifecycle → Clock → Collection →
**Action → Facing → Movement (contacts) → final SpatialIndex → Hit** → Publication → EndFixed ECB.
A tiny Initialization proxy forwards to the **same arena owner** before the existing lifecycle Initialization proxy.
This supports geometry setup before paused Start/Restart without a second publication implementation or clock. The same
owner also commits pending geometry between catch-up ticks; without pending changes it only reads its state and returns.
Default World and replay/benchmark use the same owners. Existing owner/order assertions remain, with arena boundary added.

## Authoring and supported geometry

`CombatArenaAuthoring.Sources`, on Combat Bootstrap in `Assets/Scenes/CombatSmoke.unity`:

| Source | Numeric shape |
|---|---|
| Arena Floor | Solid box, top Y=0, bottom -0.5, 60 × 30 world footprint |
| Existing Cube | Exact BoxCollider center/size and world yaw 33.32°, original transform retained |
| Arena Platform | Solid box, top Y=1.25, bottom 0.75, footprint X=5..7, Z=6..8 |
| Arena Ramp | Solid wedge, X=2..5, Z=6..8, flat underside Y=0, top rises 0..1.25 along +X |
| Arena Ceiling | Box at X=-7..-3, Z=5.5..8.5, underside Y=2.45 (body height 2 permits walking, jump contacts roof) |

Sources with a BoxCollider use its local center/size, not the visual mesh bounds. Otherwise Center/Size explicitly specify
local dimensions, transformed once. Ramp is the wedge filling Bottom..Top(x), not a rotated solid box. Its disk support
is the **highest top point over the disk intersected with the finite rectangle**, not center-height or infinite plane.
A radius-0.3 actor at the center of a slope 0.5 ramp therefore has base 0.15 above the ramp's center-height.

World rotation is yaw-only. Nonuniform positive world basis lengths are supported; a right-handed orthogonal X/Z basis
is required. Reflected/sheared/pitched/rolled matrices, MeshCollider, Rigidbody, trigger/disabled BoxCollider, missing
Transforms, invalid dimensions/kinds/non-finite/out-of-domain values are rejected explicitly. No silent AABB substitution.
All supported dimensions/thickness ≥0.01, X/Z bounds and Y bounds within ±1024. Body Radius/Height in 0.01..32 for contacts;
Mass remains finite positive but has no contact response role. The body index's wider numeric validation remains unchanged.

Walkable ramp slope ≤1 (45°), inclusive. Steeper ramp is a blocker, not climbable ground. No step-up, auto-climb, actor
pushing, impulses, dash/cast/crouch or new hit-through-wall rule. Following a continuous walkable slope preserves Grounded
and does not manufacture a landing every tick. Leaving finite support makes the actor airborne; spawn height is never a
production support. Ceiling uses body Height and cancels only upward velocity; gravity resumes on the next tick.
If a ceiling prevents takeoff completely, support remains grounded and no air/landing recovery transition is invented.

`DrawGeometry` is an optional Scene gizmo switch; no heavy Inspector preview. Actual source transform edits require
**Rebuild static arena**. Visual transforms can change before a new numeric revision is staged; moving obstacles are not
supported by this static contract. Rebuild caches numeric data; fixed updates never enumerate scene objects or Unity Physics. The adapter caches its diagnostic
EntityQuery per World bind and disposes it on a live-World release; Update and Rebuild do not create replacement queries.

## Numeric policy and limitations

One central `CombatArenaTuning` owns skin=0.0002, max slope, dimensions/domain and guards. Exact disk/rectangle nearest
point handles rounded exterior corners; yaw is transformed into local coordinates and back. Ramp solid cross-section at
foot height is a clipped rectangle; head contact uses the flat underside. Stable source order breaks equal-depth ties.

Substep length ≤one quarter of the smallest body dimension and every obstacle thickness/width; additionally
≤sqrt(4 × Radius × Skin), bounding rounded-corner grazing chord error conservatively below skin. Vertical and planar
travel share one work budget, ≤256 planar substeps per tick; ≤12 contact/depenetration/roof-clip refinement iterations.
A vertical interval sweep precedes control, preserving contact-tick movement suppression. Ground-follow rise is clipped
against ceilings before side resolution; support is rechecked after horizontal corrections. Excess per-step correction or
an uncleared volume rejects the tick instead of ejecting the actor sideways. Steep tops block a fall but do not grant
walkable Grounded/landing recovery. This is the existing 60 Hz tick,
not extra commands, hits, Pressed edges, clocks or landing events. A complete wall cannot be skipped; tangency within skin
is numerical contact tolerance. Huge displacement/velocity rejects the entire tick with WorkLimit, never clips a path
through geometry. This is a deliberately bounded solver, not arbitrary-speed CCD or an unbounded void-fall simulation.

Initial overlap correction is bounded by min(0.5, Radius), uses exact separating corrections and counts no run distance.
Deep/trapped overlap or iteration exhaustion freezes the **original** position/kinetics for that tick and records a failure.
`CombatArenaDiagnostics` has Tick/FailedBodies/LastFailure; the scene adapter logs a failure once per revision. Invalid
production geometry also freezes rather than falling back. No resolved result is published on failure.

LandingRecovery is copied from the existing table **once per physical air→support transition**. Existing Action/Facing
landing gates still drop new presses and buffering. Movement drops controlled movement during recovery, then resumes from
live input without a new delay. Contact clears the run streak; the initial vertical sweep suppresses the whole landing tick's planar control when recovery
is positive. Edge-fall fractions remain bounded inside the tick; they never replay commands or gravity. The contact solver does not restart a strike or
change facing. GroundHeight is current support when grounded, last recorded support when airborne.

Run distance now sums actually achieved X/Z segment lengths, excluding depenetration. Wall pressure adds no distance and
no LastMoveTick; the existing six-tick run grace is retained. Previously a normalized diagonal moved the same physical
speed as straight input but accumulated only one axis into the animation stride. The related test now expects equal
actual distance; physical speed itself is unchanged. No spatial/reference assertion is removed or relaxed.

## Revision, lease, lifetime, cache

Acquire increments a **per-World** lease; only that lease can stage/release. Stage validates/copies into the pending buffer;
active rows/revision change only when the owner publishes at a safe boundary. Invalid/released sources stage an invalid
revision and retain Required=true for the current battle, without resurrecting a spawn floor. Released leases are retired
and cannot stage again. Only after an actual End/idle boundary does the owner release the
requirement so a reused default World can host legacy fixtures again. Restart alone cannot bypass an unavailable arena. A production bootstrap always acquires a required
arena before its new Start, including an invalid lease/error if its adapter is missing; it cannot inherit this opt-out. Old adapters cannot release/rebuild a successor's
geometry; callbacks retain their own World and check disposal before touching its EntityManager. Replacement World/system
rebinds through setup; scene holders also check owner-instance identity, so repeated raw tokens cannot authorize an old
callback. Automatic rebind does not steal a successor; an explicit owned Start/Restart may claim the correct scene data.
No Entity/native view is transferred. Acquire/Stage/Release are methods of that owner instance; disposed owners reject Stage/Release without touching an
EntityManager, even when raw numbers repeat in a replacement. Raw lease numbers are scoped to their acquired owner and World. Pending and active capacities are reused, freed by ECS on
owner/World destruction; Stage never exports buffers. Geometry can survive battle End/Restart in the same scene/World.

Bare/partial Worlds or new isolated battles with no required arena retain their legacy GroundHeight flat support **inside the same math/
Movement path**. Historical flat-only fixtures trust their supplied Grounded/edited Y and use one planar step with technical work dimensions;
this does not rewrite their actual indexed Body or apply contact-size limits to unrelated bare fixtures. Real production
actors require their actual Body and real support. The production CombatSmoke bootstrap explicitly requires an enabled arena
adapter; missing source emits a visible error and freezes movement. This compatibility fallback is not a duel-only path.

## Checks and evidence

`Tools/Verify-Stage04Arena.py` checks a clean committed head, exact SHA/tree, syntax (not compilation), scene references/
metadata, protected code and tuning identity, and independent Python disk/support arithmetic. `Docs/Evidence/61` stores
its exact tested-source report. The #60 verifier intentionally retains its original scope/protection and is not a PASS
claim for changed Stage 04.2 production owners. Final-head rerun goes to /tmp and is recorded in the PR to avoid a
self-referential report SHA. Source/model results do not execute Unity/C#.

Authored C# suite `CombatArenaTests`: axis/rotated/scaled authoring, head-on/parallel/away/diagonal walls, both sides and Z,
rounded corner/inner corner, grazing/overlap/deep failure, thin wall/work limit, ceiling, edge→lower floor, finite disk ramp
support/slope boundary, project-duration recovery/input suppression/resume, strike phase/hit ledger at wall, actual run
distance/final index, player/AI parity at 1/2/3/10 catch-up, 2/100/400 restart/revision/invalid geometry and independent
Worlds, paused End/Restart/disposal, retired scene leases/next bare-battle compatibility and elevated-spawn action gating. Existing spatial tests are preserved byte-for-byte.

Existing benchmark class/harness extended by `CombatArenaBenchmarkTests`: few/many walls, sparse/dense 2/100/400,
sustained wall pressure and periodic jumps/strikes. Cold setup outside measurement; warmup separate; Movement, index,
Hit owner calls and real fixed-group cost sampled separately. Contact math is a **separate managed wall-pressure
microbenchmark**, not additive, a live contact split or a Burst timing claim. Allocation positive control/restoration,
active/pending capacity stability and process native delta are included. No actual CPU/GC/native/GPU figures or FPS claims.

**NOT RUN:** Unity compile/import, Entities sourcegen, Burst, C# EditMode/PlayMode, scene/manual GPU, runtime performance/
allocation/leak runs, Domain Reload off and player build. Unity/C# toolchain is absent here. Source findings and authored
tests are not runtime acceptance. The real-art smoke roster assertion is also corrected to the retained scene setup (1 human + 2 AI = 3), a source
mismatch already present before #61; no executed Unity FAIL is claimed. The manager must review source and the owner
must authorize merge separately; #62
acceptance and final Unity acceptance remain independent gates. Actor contacts/yield, impulses and Hit-index migration
remain 04.3/04.4/04.5.
