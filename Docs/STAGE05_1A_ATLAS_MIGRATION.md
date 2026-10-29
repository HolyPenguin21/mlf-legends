# Stage 05.1a — v2 atlas migration and the #69 review corrections (#70)

Base: main `47013974c7773d2cdd565e6eabc3441003aeee0e` (PR #71, #69 delivered). Branch `feature/70-stage05-atlas-migration`, one PR,
merge only on a separate owner command. Commit order as requested: corrections first, then the art.
Results with exact SHAs: [Evidence/70](Evidence/70/README.md).

## 0.1 Final velocity against corners — every active wall at once

**Defect (confirmed by the manager's arithmetic):** `ClipWalls` made two sequential projection passes over at most two
normals. In an acute corner the second projection pushes the velocity back into the first wall: n0 = (1,0),
n1 = (−√.5, √.5), v = (−12,−12) gave (−3,−3), still into n0. `AddWall` silently dropped a third distinct normal.

**Model now** (`CombatWallSet`, `CombatArenaGeometry.cs`): the allowed final X/Z velocities form the cone
{v : dot(v, n) ≥ 0 for every active outward normal n}. `Clip` is the exact Euclidean projection onto that cone — v itself,
else the nearest single-plane projection that satisfies all the others, else zero (in 2D two active planes meet only at
the origin). Result: every published velocity satisfies dot(v, n) ≥ −1e-6·|v| for all normals, independent of their order;
the tangent along one wall is unchanged; a single wall never stops a body.

- The set is kept **minimal and exact**: a normal implied by the others is dropped (a 2D cone needs at most 3: half-plane 1,
  wedge or corridor 2, ray or point 3), so a rounded corner's sweep of normals keeps only its two extremes. Capacity 4;
  overflow (numerically degenerate input only) is explicit: the body's velocity is zero and `WallOverflowBodies` counts it.
- **Active constraints:** a move records the normals of its *last blocked substep* (sliding re-touches the wall every substep;
  a corner left behind is no longer active), all moves of the tick are merged, and every solid side within `ContactGap` of
  the final position is added (`AddTouchingWalls`; the sloped face of a walkable ramp is support, not a wall, so a body can
  still be pushed uphill). Nothing is carried to the next tick: walls release on the next tick by construction.
- Deceleration scales the velocity by a positive factor, so it never leaves the cone.

Regressions (`CombatWallSetTests`, `CombatImpulseTests`, `CombatArenaTests`): the manager's counterexample; acute/obtuse/right
corners; order independence; corridor + third constraint (ray) and point cone; rounded-corner arc; invalid input; 300 random
constraint sets against a brute-force nearest feasible velocity; rotated 45°/135° corners in the real Movement path in both
shape orders with release on the next tick; a body resting on one wall sliding into an acute second wall; the touching-wall
query (resting wall, gap, floor, walkable vs steep ramp). Mutation check: the old two-pass clip fails the formula and the
rotated-corner test (Evidence/70).

## 0.2 Work limit inside the final-velocity pass — no partial success

One `MaxPairVisits` budget covers the position solve and the velocity pass (`CombatContactJob.PairVisitBudget`, set by the
owner from `CombatContactTuning.MaxPairVisits`; an internal override exists only for the guard test). If it runs out
**inside** the velocity pass: `WorkLimit = true`, `VelocityComplete = false`, and every non-frozen body of a recorded
contact publishes **zero** external velocity (`VelocityDiscarded` counts those that had one). The positions solved before
the pass are published unchanged (`ResidualComplete` keeps describing positions only), consumed impulses are not
re-queued, time is not re-integrated. If the position solve already exhausted the budget, every body is frozen with zero
velocity and the velocity pass does not run. The separate measured 48-pass cap with its small residual is unchanged.
Guard test: `VelocityPassWorkLimitKeepsSolvedPositionsAndPublishesNoPartialVelocity`.

## 0.3 The owner's final scene

`Arena Cube (1)` (owner commit 9c2729d, Transform 2033483573) is a copy of the existing obstacle cube (same rotation,
MeshRenderer + BoxCollider, standing on the floor). It is now an explicit Box source of the existing `CombatArenaAuthoring`;
no collider scanning. Owner placement and all other owner scene edits are untouched (later owner edits — team sizes 20/20,
gizmos — were not committed by this branch). Note: that owner save also left the ramp GameObject without its name
(`Arena Ramp` → empty); not changed here. Stage 04 manual acceptance stays open; the measured 400-jam impulse discards,
corridor cost and dense-query overhead are not changed by this delivery.

## 1. The art package

| Item | Value |
|---|---|
| Package | `mage_female_01_v2_128` (`meta.art_version` v2_128), delivered outside Git to the owner's Downloads |
| Package JSON SHA256 | `8515f849728375290b808c542d640400c0f17af663cdebfe4df5f6da55498b6c` |
| Package PNG SHA256 | `b82abb7ecc9c4b923401d82d73c582596e90614ea1bbcf3d01e842d4380e5ef4` (13:14) |
| **Installed page** | owner-edited version (16:14, cells run_000/005, kick_right_002/003 touched), SHA256 `8aada5ec29dfb46ad594a0d2315d2c81201aca65c93aed6cf2fba52a85a443f7`, at `Assets/MLFLegends/Art/Characters/MageFemale01/Atlas/mage_female_01_00.png` (owner's path, existing GUID) |
| Page | 2000×2016 RGBA8, both sides ≤ 2048, imported without resize (Max Size 2048, NPOT none, Point, Clamp, no mipmaps, no compression) |
| Canvas | source_size 192×160, nominal_height_px 128, pivot [96,136], authored right |
| Clips / frames | 12 / 76: idle_combat 6, run 6, jump 6, punch_left 6, punch_right 6, kick_left 6, kick_right 6, physical_ability 8, block 4, dash 6, skill_light 6, skill_complex 10 |

**Layout:** slot 200×168 per frame — canvas at [4,4] of the slot, 2 px extrusion, 2 px separation; one clip per row.
In Sprite Editor that is **Grid By Cell Size 192×160, Offset 4×4, Padding 8×8** (a plain 192×160 grid drifts 8 px per cell).

**Measured on the installed page** (`Tools/Install-AtlasPackage.py --check`, report `Evidence/70/atlas-check.json`):
every rect is the full canvas (trim 0), inside the page with its 2 px extrusion, extrusion repeats the edge pixels, no
overlap of extruded rects, no opaque pixel outside them, markers inside their clips. 17 semi-transparent pixels in the owner's
retouch (clipped at alpha 128 like the rest). Body pixels (alpha ≥ 128) of all 76 frames fit x 23..190, y 2..136 — no frame
touches the canvas edge. Every idle frame stands on y = 136 (the reference support), standing height 126–131 px.

## 2. Import, pivots and custom image positions

- **Project manifest** = the package JSON; only `pages[0].file` is the installed basename, recorded in `meta.project_install`.
- **Sprites:** one per frame_id, rect = its full 192×160 canvas, custom pivot. **Catalog** binds all 76 frame_ids (stable
  ids derived from the frame id). Storage stays `SourceCanvasCells` (the owner repaints cells in place); a full transparent
  quad per fighter, same batching/cache, culling by the same card bounds.
- **Pivot:** Y on the feet line 136 for every frame (support source × [0.5, 0.85] = [96,136] for 192×160). X = 96 plus the
  owner's per-frame **custom horizontal position**: the owner shifted Sprite rects in Sprite Editor to keep poses whose body
  moves inside the canvas (kick_left_001–004 −11…−17 px, kick_right_003 +11, physical_ability_003/004 +9/+8, others ±1…6;
  full list `Evidence/70/owner-horizontal-offsets.json`, the owner's raw slices `owner-slices-1706.png.meta.txt`). The
  per-row vertical shifts of that edit were the plain-grid drift (all feet are on y 135) and were not kept.
- **Custom positions in the future:** a binding whose Sprite rect has the size of the frame's useful rect or canvas but sits
  shifted on the page (≤ half a canvas) is accepted; the shift moves the image pivot, the drawn pixels still come from the
  manifest region (no neighbour bleed, no crop). It is reported as a warning with the shift; a custom pivot on the exact
  cell is preferred. Automatic binding by rect stays exact.
- Reproduce: `python Tools/Install-AtlasPackage.py <package.json> <installed page.png> --pivot-x-offsets <offsets.json>`.

World height is not changed: nominal 128 px = 2 world units (`2/128` per pixel), body Radius 0.3 / Height 2 / Mass 1 and
all hit data are the owner's #69 values. Gizmos (Combat Bootstrap → Draw Combat Gizmos) show body/hit boxes on the new art.

## 3. Clips → ECS

| Clip | ECS | Notes |
|---|---|---|
| idle_combat, run | motion state | loops; run stride by distance |
| jump (one clip) | airborne / landing | rise [1,2] by ascent, fall [3] by descent, land [4,5] in the landing lock. `jump_000` (ground anticipation) is in no marker and never shown: takeoff stays immediate (reported warning, no hidden delay) |
| punch_left / punch_right | Hand1 / Hand2 (U series 1-1-2-1-2) | |
| kick_left / kick_right | Foot1 / Foot2 (I series) | |
| physical_ability | PhysicalAbility (O), finisher | replaces the v1 `kick_spin`; no self impulse (05.2) |
| block, dash, skill_light, skill_complex | **none** | configurable settings only; no input/action/physics (Stage 06/13) |

`kick_spin` is a declared legacy id (`CombatAnimationSet.SuccessorClip`): a slot still mapped to it is rejected with a hint
to the successor, never silently remapped.

## 4. Config migration (old → new), `CharacterAnimationConfig.asset`

Explicit and reported (`CharacterAtlasMigration`; Inspector button *Migrate settings to this atlas's clips*, undoable,
idempotent). The project asset was migrated in Git; T and curves of the owner are kept, Combat values untouched.

| Clip | Before (v1 art) | After (v2) | Simulated windows (Prep/Active/Recovery ticks, chain) |
|---|---|---|---|
| idle_combat | 0..5, T 0.75 | unchanged | — |
| run | 0..4 (all 5), T 0.5 | 0..5 (all 6), T 0.5 | — |
| jump | 0..4, T 1, landing 4..4 | 0..5, **T 0.6**, landing 4..5 | landing lock 12 → **12** ticks (two 0.1 s frames) |
| punch_left / punch_right | 0..5, 2/3, T 29/60 | unchanged (= strike_pose [3]) | 14/6/9, chain 24 → unchanged |
| kick_left / kick_right | 0..4, 1/2, T 0.5 | 0..5, **2/3** (strike_pose [3]), T 0.5 | 12/6/12, chain 24 → **15/5/10, chain 26** |
| PhysicalAbility | kick_spin 0..5, 3/4, T 0.5 | physical_ability 0..7, 3/4 (strike_pose [4]), T 0.5 | 20/5/5 → **15/4/11** (finisher) |
| block / dash / skill_light / skill_complex | — | 0..3 T 0.6 / 0..5 T 0.42 / 0..5 T 0.6 / 0..9 T 1.1 (JSON duration_ms), phases 0/0 | not actions |

The old kick ranges 0..4 were owner cuts of the v1 art; keeping them would silently hide the new last frames, so they
cover the whole new clips. A range shorter than its clip is now a validation warning. The tick windows change because the
new kick/ability art has a later strike pose; re-tune T/phases in the Inspector if the owner wants the old windows back.

## 5. Catalog or config change during a battle — policy

A catalog/config change applies on the next rendered frame (owner Play Mode tuning keeps working). A strike that started
before it keeps its frozen simulated windows (`CombatAttackState.Timing`) and is drawn with the new clip's frames **of the
same phase only**; a drawn frame never moves a hit. Install new art between battles (or Restart): then every strike uses
the new table. Regression: `AStrikeStartedWithTheOldArtWindowsDrawsOnlyFramesOfItsSimulatedPhase`. No clip-version history.

## 6. Limits / not in this delivery

Body height stays constant; self impulse, dynamic Height and swept hits are 05.2, VFX 05.3, block/dash 06, magic 13.
Multi-character roster/renderer remains #46. The owner's visual acceptance of the new art in the Game view (poses, series,
landing frames, flip, anchors on the new frames) is separate and NOT RUN by the implementer; anchors are the artist's
recommendations, not verified anatomy.
