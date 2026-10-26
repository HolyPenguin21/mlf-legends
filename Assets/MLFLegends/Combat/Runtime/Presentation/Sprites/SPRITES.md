# Combat sprites (Stage 03.1 + 03.2, Issue #38)

One scene-level GPU renderer draws every in-combat fighter of the active battle from a shared atlas. It reads ECS
(`CombatantId`, `CombatPosition`, `CombatFacing`, enabled `InCombat`, `BattleSession`) and writes **nothing** to ECS.
Input contract for art: `Docs/SPRITE_ATLAS_CONTRACT.md`. State-driven animation (idle/walk/jump), the final mage and
the visible scene are Stage 03.3; here frames are selected explicitly.

## Pieces and owners

| Type | Kind | Owns / responsibility |
|---|---|---|
| `SpriteAtlasManifest` | static parser | JSON v1 → `SpriteAtlasDefinition`, all-or-nothing, errors name path + id. Uses the installed `com.unity.serialization` reader (no second JSON framework); its object view keeps duplicate keys visible. |
| `SpriteAtlasDefinition` | immutable data | Pages, frames (index = runtime frame id), clips, markers, transitions; string ids resolved once. No Unity objects. |
| `SpriteAtlasCatalog` | ScriptableObject (content) | Manifest `TextAsset` + explicit `file → Texture2D` bindings. Owned by the project; never destroyed by consumers. |
| `SpriteAtlas` | `IDisposable` | Validated binding (name, size, uncompressed), CPU frame table (`NativeArray`, 32 B/frame), lazily one GPU frame `GraphicsBuffer`. Does **not** own page textures. |
| `TechnicalAtlasFixture` | static generator | Code-generated technical atlas (2 pages 128×128 / 96×160, 3 trims, holes, shared frames, magenta sentinel padding). Caller destroys the textures. Not a character. |
| `SpriteCardMath`, `SpriteCardBasis` | static math | Canvas → card → world for bodies, anchors, bounds, tests; mirrored by the shader. |
| `CombatSpriteExtraction` | `IDisposable` | Two queries per World, reused native lists, depth tie-break, per-instance frame, page grouping. Read-only; no structural changes. |
| `CombatSpriteRenderer` | `MonoBehaviour` (scene) | Atlas load/reload, material, card mesh, instance `GraphicsBuffer`, one `MaterialPropertyBlock` per page, submission, owner arbitration, debug gizmos. |
| `CombatSprite.shader` | URP HLSL | Passes `UniversalForwardOnly`, `DepthOnly`, `DepthNormalsOnly`; alpha clip; ZWrite On / ZTest LEqual; `Cull Off`. |

`CombatSmoke.unity` → *Main Camera* carries `CombatSpriteRenderer` (source: technical fixture) next to the existing
`CombatCameraController`. No per-fighter GameObject, Transform, SpriteRenderer, MeshRenderer or Animator.

## Frame order

`PreUpdate` input → `Update`: MonoBehaviours, then the default World's `SimulationSystemGroup` with all fixed ticks of
the frame (Begin ECB → Lifecycle → Clock → Collection → Facing → Movement → Publication → End ECB, unchanged) →
`PreLateUpdate`: `CombatCameraController.LateUpdate` (order 0) places the camera → `CombatSpriteRenderer.LateUpdate`
(`[DefaultExecutionOrder(100)]`, explicit) reads ECS and the camera rotation and calls `Graphics.RenderMeshPrimitives`
→ URP renders those draws for that camera in this frame. The renderer has no tick, clock, accumulator or ECS writer.
Paused (`timeScale = 0`): LateUpdate still runs, positions/frames stay, camera pan/zoom keep working.

## Geometry (sprite card)

Basis from the camera (`SpriteCardBasis.FromCamera`): `Right` = horizontal screen right; `DepthAxis` = horizontal camera
forward. With `s = NominalWorldHeight / nominal_height_px` (start 2 / 96 world units per pixel):

- canvas point `(x, y)` → card offset `(f·(x − pivot.x), pivot.y − y)` pixels, `f = ±1` facing (whole drawing and trim
  offset mirrored around the pivot; UVs stay, so flip ≠ UV-only flip);
- card y ≥ 0 → `support + Right·x·s + (0, 1/cos pitch, 0)·y·s` (**vertical card**);
- card y < 0 → `support + Right·x·s + (−DepthAxis / sin pitch)·|y|·s` (**on the ground towards the camera**).

Orthographically every source pixel becomes a screen square of `s`, so the drawn angle is shown as drawn (no second
tilt), while depth is that of an upright body standing at `CombatPosition`: walls behind never cover the head, pixels
below the pivot (near foot) lie on the floor instead of sinking into it. The mesh is two quads split at the pivot line
(a quad collapses when the frame lies on one side). Jump height is only `CombatPosition.Y` (no `CombatMotion`, no
root motion). Pitch scale factors are clamped to ≥ 0.1 (camera pitch range 5…90°).

UV: the manifest's top-left rect becomes Unity's bottom-left UV once (`SpriteCardMath.FrameUv`). The shader samples with
URP's `sampler_PointClamp` at mip 0, independent of the page's import filter/wrap/mipmaps; rects exclude extrusion.

## Depth policy

**Manager correction #41 OPEN (2026-10-26):** the current roster-wide cumulative push below can pull a fighter through nearby opaque geometry when unrelated fighters share its depth, even far apart in X/outside the camera. This is a known defect pending correction, not accepted 3D occlusion. The following formulas describe the shipped code under review.

- Body: alpha clip (cutoff 0.5) with depth write/test in every pass; transparent pixels write nothing. Translucent VFX
  will be a separate future material/pass.
- Cards are planes perpendicular to `DepthAxis`: one view depth per fighter (`dot(position, DepthAxis) / cos pitch`),
  independent of X and jump height.
- **Tie-break (render only):** fighters are ordered back to front by (depth, CombatantId descending); each card is
  pushed towards the camera until it is at least `DepthSeparation` (default 0.002 units) in front of the previous one.
  Equal depth → lowest `CombatantId` in front; independent of chunk order, packing or pages; pushes are only applied
  where cards are closer than the separation. Bound: `count × separation` (0.8 units for 400 equal-depth fighters (their X positions need not coincide),
  typically ≤ 0.04 for formation rows).
- **Bias:** every card additionally moves `DepthBias` (default 0.02 units) towards the camera along the view direction
  (never changes its screen image), so the ground part wins against a coplanar floor.
- `CombatPosition` is never changed. Visibility comes from the GPU depth test, not from CPU or buffer order.

## Draws and resources

- One shared card mesh (8 vertices), one runtime material from the shader, one instance buffer (24 B/instance; capacity
  grows to the next power of two ≥ 64, never per frame), one frame buffer per atlas (32 B/frame).
- One `RenderMeshPrimitives` call per atlas page that has instances: the instance buffer is grouped by page and
  `_InstanceOffset` selects the page's range (the API has no start instance). Calls depend on pages, not fighters.
  Measured: 6 → 8 draw calls per camera render with the renderer at 2, 100 and 400 fighters (one page).
  `RenderStats.ExtractMicroseconds`/`SubmitMicroseconds` give the CPU split of the last refresh.
- Bounds per page: support points' box + the atlas' real extents (widest frame both ways, highest top, deepest part
  below the pivot, push range). No culling of the simulation, ever.
- Memory of an atlas: Σ page `W × H × 4` bytes (no mipmaps/compression); fixture 126 976 B, frame table 96 B. PNG size
  on disk is not VRAM.
- Without a graphics device (`-nographics`) extraction still runs; no GPU object is created and nothing is submitted.

## World, lifecycle and owners

- Follows `World.DefaultGameObjectInjectionWorld` (or an explicitly bound World); queries are created on the first
  refresh against a World and released on change/disable (a disposed World released them already).
- Zero fighters → zero instances and zero submissions. End, Restart in the same World, Despawn, disabled `InCombat`,
  late/replaced/disposed World and scene unload leave no geometry and no Entity handles (IDs only, per refresh).
- Explicit frame selections (`SetFrame`, `SetClipFrame`, `SetDefaultFrame`) are a preview/test seam: per CombatantId,
  dropped on a new battle, another World or atlas. No clock, no state binding, no animation FSM.
- Two enabled renderers on the same World + Camera: the later enabled one draws, the earlier logs one warning and stays
  idle while superseded (no ECS read, no draw; its World binding and selections are kept); other cameras/Worlds are separate owners. The static list of enabled renderers
  is presentation-only and reset on `SubsystemRegistration` (Domain Reload off).
- Disable/destroy releases instance buffer, material, mesh, atlas (frame buffer, native tables), generated pages, queries
  and native lists. Replay/benchmark Worlds have no renderer and get no GPU resource or structural change.
- A rejected manifest/binding logs one error naming the fields and draws nothing; the battle keeps simulating. Changing
  the catalog (`SpriteAtlasCatalog.Version`) reloads it on the next refresh.

## Debug

Gizmos (Scene/Game view with Gizmos on): support point, `hand_left` (blue), `hand_right` (orange), `cast_origin`
(magenta); filled = visible, wire = hidden; dimmed = back layer; line = direction. Positions come from
`TryGetAnchor`, the same card transform as the body (flip, scale, push).

## Tests

EditMode `Tests/EditMode/Presentation`: manifest contract and violations, card math with concrete numbers, depth
tie-break permutations, page binding, extraction on real replay Worlds (2/100/400, End/Restart/Despawn/defeat, World
replacement, read-only identical replay). PlayMode `Tests/PlayMode/Presentation`: default World lifecycle, pause,
owners, rejected catalog, CombatSmoke; `[Category("Graphics")]` RenderTexture readback (Ignored as NOT RUN without a
device); `[Category("Performance")]` 2/100/400 measurements → `TestResults/performance/stage03-sprites-n*.tsv`.

## Known limits (this stage)

- Geometry is exact for camera pitch ≈ 5.8°…84.3° (|cos|, |sin| ≥ 0.1); towards a top-down view the upright card is
  seen edge-on and shrinks. The calibrated camera is 40°.
- Pixels below the pivot are depth-tested as a floor decal towards the camera: within a few tenths of a unit they may
  overlap a neighbour's legs; an airborne fighter's below-pivot part floats horizontally at its height.
- Body alpha is clipped at 0.5 (no soft edges); no fog, motion vectors (TAA) or shadow caster pass; `#pragma target 4.5`
  (structured buffers in the vertex stage: no GLES 3.0/WebGL 1); XR single-pass instancing is not supported.
- 96 px = 2 units at orthographic size 6 is not pixel-perfect (non-integer screen pixels per source pixel); pixel-perfect
  calibration belongs to 03.3.

- Main-thread managed extraction (~0.2 ms for 400 in the Editor); Burst/jobs only if later profiles need it.
- No indirect/compute culling, Entities Graphics path, shadows, lighting, translucent VFX, equipment layers or terrain
  interaction beyond depth. Camera yaw ≠ 0 is supported by the math but only 0° is calibrated.
- The Scene view does not show the fighters (draws target the selected camera).
