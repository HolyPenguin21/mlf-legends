# Issue #56 — effective image pivot and Inspector correction

Base main: **311c3d7d06b5320b9df701ae6f21f519d07c6af1** (fresh checkout). Delivery branch:
`fix/56-sprite-pivot-inspector`. Exact delivered head is recorded in the PR. **No merge; Stage 03 is NOT accepted.**
The owner's previous manual checkout SHA is unknown; its result is not attributed to this base. Stage 04.1 is excluded.

## Where to edit the image pivot

Open the existing atlas page in **Sprite Editor**, select the Sprite for the intended frame, change **Pivot**, then
**Apply**. The catalog's **Image Pivots** array explicitly maps each manifest `FrameId` to that imported Sprite subasset.
The shipped mage catalog now binds all **50** frame IDs to the original page `.meta` GUID/internalIDs, checked against
`nameFileIdTable` and each full-cell rect. Names such as `mage_female_01_00_0` are not interpreted as frame indices.
Do not edit the standalone `frames/` PNG imports expecting them to control the atlas: the catalog references page Sprites.

All importer metadata remains byte-identical to base. The six idle Sprites retain custom normalized **[0.535,0.205]**,
which becomes **[102.72,152.64]** on a 192×192 source canvas. The other **44** Sprites retain the chosen **Center** pivot,
**[96,96]** in canvas pixels. Center is valid imported data and now affects drawing; it is not replaced with JSON/support
coordinates to imitate the previous bug. Use the Sprite Editor to deliberately choose a different pivot, including group
editing. Full-cell normalized **[0.5,0.15]** gives the reference **[96,163.2]**. No automatic silhouette centering occurs.

## One image transform

At `SpriteAtlas.Load`, catalog bindings resolve by exact `frame_id`. For a Sprite on the frame's page:

- Full-canvas origin on the page: `C = frame.rect.xy - frame.trim_offset` (top-left axes).
- Sprite top-left on the page: `S = [sprite.rect.x, page.height - sprite.rect.yMax]`.
- Effective canvas pivot: `P = S - C + [sprite.pivot.x, sprite.rect.height - sprite.pivot.y]`.

`Sprite.pivot` is already rect-local pixels with Y up. Convert it once; keep fractional values. The Sprite rect must match
the frame's useful rect OR its complete source canvas, on the bound page. Trim restoration is included in `S - C`.
Invalid/missing Sprite references, unknown/duplicate frame IDs and wrong page/rect bindings reject atlas loading before
native/GPU allocation. An invalid binding never silently falls back or selects another Sprite.

For a canvas point p, additional ImageOffsetPixels A (+X right, +Y down) and facing f = ±1:
`card = [(p.x - P.x + A.x) * f, P.y - p.y - A.y]`, followed by the existing `CardToWorld` transform.
At A=0, the image pivot coincides with CombatPosition. Moving the pivot right moves the right-facing image left; moving
it up moves the image down. Facing mirrors the entire X result, including A. World position, nominal scale and physics
are unchanged. The reference canvas support stays `source_size × [0.5,0.85]`; it is not an extra image translation.

`SpriteAtlas` caches P by runtime frame index and builds GPU CardRect from it. `TryGetAnchor` and optional scene canvas
gizmos read that same current-frame P. Anchor directions use the tangent at the resulting card height, so the existing
vertical/ground split remains consistent. Extraction footprints use those CardRects; culling uses their cached union
plus the single artistic offset and the existing bounded depth push. Shader/table layout, full 192×192 cells, packed
TrimmedRects sampling, alpha clipping, batching and resource ownership are unchanged. The obsolete derived
LegacyPivotOffsetCard property is removed; there is no second alignment offset.

## Legacy fallback, migration and invalidation

A frame with **no Image Pivots entry** explicitly uses the JSON pivot. Thus a legacy texture-only catalog or the string
`SpriteAtlas.Load` fixture API keeps its old drawing/anchors without JSON edits. Imported P and JSON pivot are never
summed. ImageOffsetPixels remains the single optional additional translation.

Editor-only `SpriteAtlasPivotImporter` fills missing bindings at editor initialization, relevant asset imports and build
preparation, using exact page plus useful/full-canvas rect matching. Asset enumeration order and Sprite names do not
matter. Existing entries win and are never automatically remapped. No matching Sprite means legacy fallback; multiple
matches persist an invalid binding and report an error requiring explicit selection. Changing a catalog to different
content requires repairing its explicit bindings; stale references are rejected rather than guessed.

Migration saves only catalog references, never `.meta`, JSON, Sprite pivots/rects/names or PNG pixels. The current mage
references were migrated directly from the already tracked internalIDs because the external PNG is absent here; their
resolution during Unity import still needs the owner check. Preserve the original page `.meta` when supplying the PNG.

The postprocessor delays until import completes, then bumps the affected catalog Version, including Sprite Editor Apply
where references remain identical. Manifest/page/catalog reimport, moved/deleted assets and catalog Inspector edits also
invalidate consumers. A renderer sees the version change on refresh, disposes the old CPU/GPU frame table through the
existing unload path, clears selections/page blocks and compiles the new table. No asset lookup/metadata parse/table
rebuild happens per fighter or per unchanged render frame. Import hooks queue paths only and retain no global asset
references. Build preparation ensures migrated references are saved; player builds read serialized Sprite rect/pivot
through normal runtime APIs, with no `.meta`/PNG disk reads or UnityEditor dependency in the runtime assembly.

World/renderer/character/catalog switching keeps the existing extraction ownership and unload/rebind paths. OnDisable
releases native/GPU resources. Domain-reload-disabled play continues to use the existing renderer static reset and
OnEnable/OnDisable lifecycle; the importer owns no runtime table. Actual repeat-play/build validation is NOT RUN.

## Inspector changes and cache ownership

Removed Full source canvas preview, its clip/frame/facing selection, raster drawing, coloured outlines/anchors and all
preview state/helpers. No hidden preview work or replacement window remains. The existing property drawer still edits
Characters/catalog, ImageOffsetPixels, read-only StartFrame/FrameCount, EndFrame, T/SpeedCurve, strike phase boundaries
and LandingStartFrame. Marker-copy remains an explicit undoable action.

`CharacterAnimationConfigEditor` owns its manifest cache and per-character diagnostic cache. An unchanged GUI event
returns before asset dependency lookup, manifest parsing, settings snapshots or timing/set creation. Manifest results
(including errors) are reused by catalog/manifest/dependency hash; multiple clips/characters sharing a catalog do not
parse it again. Config Version/changes and Undo trigger a settings snapshot comparison; offset-only edits do not rebuild
timing. A changed timing input or manifest rebuilds diagnostics using the existing runtime CombatAnimationSet and
SpriteAnimationTiming implementations. Valid set timings are reused for individual clip details. Invalid sets retain
per-clip and set error diagnostics, including landing/phase failures.

Undo/Redo and project changes request dependency/settings checks and repaint. A pivot-only catalog revision does not
reintegrate unchanged curves in this Inspector. Removed catalogs are pruned, and OnDisable unsubscribes events and
clears all references when the target/Inspector changes. Detailed frame-hold strings/UI are collapsed by default and
materialized only on first expansion of that revision. Landing and strike summaries/errors remain immediately visible.

The owner's Editor slowdown observation prompted removal and caching. Without a same-asset Unity profile, the share
attributable to raster preview versus timing integration/other Editor work is unknown; no measured speedup is claimed.

## Verification of this delivery

**PASS — source/model checks only:**

- All 84 repository C# files parsed with tree-sitter C#; zero syntax errors (not a compiler/API check).
- All 50 saved frame_id/GUID/internalID bindings match unique full 192×192 cells and the existing nameFileIdTable;
  shuffled Sprite enumeration 100 times preserves every match. Six custom and 44 Center pivots preserved.
- Independent numerical model checked 43,200 card corner/bend points across every frame, both facings, four pivot
  variants (including large/fractional changes), three offsets, three pitches and two yaws: conservative world bounds
  contain every point. GPU CardRect interpolation and anchor canvas transform agree for hands/effect positions.
- 1,000 synthetic trimmed-pivot/legacy-placement cases; no JSON double translation; movement sign and fractional
  reference-support checks. These are mathematical/source checks, not execution of Unity/GPU code.
- 134 protected config/scene/manifest/importer/simulation/input/command/bootstrap files byte-identical to base, including
  all owner curves/T/ranges, #52 bindings, movement phase factors and landing recovery implementation.
- Embedded preview code/state absent; runtime code contains no UnityEditor API; `.meta` GUIDs unique;
  documentation links resolve; `git diff --check` clean. No art PNG/source files added.

**FAIL:** none observed in performed checks. This does not establish Unity runtime correctness.

**NOT RUN:** Unity compilation/import/Burst, EditMode/PlayMode, Sprite Editor Apply/Undo and real GUI responsiveness/profile,
Game/Scene view idle/run/jump/five strikes in both facings, GPU/Frame Debugger/resource/GC measurements, actual clipping at
extreme offsets/limbs, catalog/World switching and repeated Play with Domain Reload disabled, player build. This environment
has no Unity Editor/dependencies or external PNG. Historical PASS logs certify their own SHAs only.

Owner acceptance sequence: supply the original page preserving `.meta`; edit one pivot and then a frame group with Apply;
confirm unchanged CombatPosition/scale and image/anchors moving together; exercise all eight clips and both facings;
check extra offsets and a separate JSON-only catalog; reimport/switch catalogs and Worlds/repeat Play/build; inspect extreme
poses at frustum edges and shared draw resources; edit curves/phase/landing settings, Undo/Redo and reopen the Inspector
with frame details closed/open. Compare the same asset's Editor profile before/after if possible. Verify #52 controls,
50/25/25 movement and landing recovery. Stage 03 stays open until the owner accepts these scenarios.
