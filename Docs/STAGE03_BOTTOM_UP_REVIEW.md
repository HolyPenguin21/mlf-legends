# Stage 03 bottom-up integration review — 2026-10-28

Reviewed baseline: `main` **72f48ce**, including PR #50, then the scene correction **5097403** in PR #51.
Corrections remain in **one branch / PR #51**, without merge. Stage 03 still requires owner acceptance in Unity.
This is a source/integration review with independent numerical models, **not Unity runtime acceptance**.

## Review order and ownership

| Layer | What was traced | Result / correction |
|---|---|---|
| Pixels → card → world | Fractional [96,163.2] support; legacy pivot converted once; image translation before reflection; split above/below support | Position transform is consistent. Fixed anchor directions below support to follow the ground quad rather than the upright quad. |
| Manifest / catalog / atlas | Source cells, packed trim placement, page origins, UV origin flip, import flags, shared frame table and its lifetime | All 50 source regions are inside the page and disjoint. Fixed `Catalog.Set(manifest,pages)` accidentally resetting SourceCanvasCells to TrimmedRects. Changing layout now requires the explicit third argument. |
| GPU shader / extraction / bounds | Matching frame/instance layouts, two shared quads, alpha/depth passes, flip, anchors, screen-overlap footprints, page grouping, support bounds and union extents | No additional discrepancy found in source/math checks. Atlas bounds include all sampled poses plus art offset and both facings; jump Y is in support positions. Actual GPU/occlusion/culling still needs Unity. |
| Renderer binding / cache | Config and catalog versions; character/catalog/shader switch; manual/config modes; disable/re-enable; invalid-data rejection | Fixed loss of cached ImageOffsetPixels when switching CharacterConfig → manual → the SAME CharacterConfig with unchanged Version. Fixed reconfiguration ignoring a changed/invalid shader while the old shared material remained alive. |
| Clip timing / config / preview | Local 0-based ranges; N/T/curve integration; explicit landing range and legacy marker fallback; fixed full-canvas preview | Current landing range 4..5 = 27 ticks, frame 4 for 8 ticks and frame 5 for 19. Preview describes config data; it cannot by itself prove that simulation received it. |
| State / input / movement / hits | Collection → Action → Facing → Movement → Hit; authoritative frozen strike timing; X/Z-only multiplier; physical contact; live input and press consumption | Single writers preserved. Movement is 50%/25%/25% once; gravity is unchanged. Contact starts the landing window, clears run history and blocks controlled movement/new starts. Fixed the old run grace delaying idle's epoch after a very short landing. |
| Scene → bootstrap → timing table | Both asset references/CharacterIds, full-set validation, initial publish, config/catalog/World changes | Saved scene now binds the SAME config to bootstrap and renderer. Added Inspector diagnostics for missing/mismatched bindings and explicit zero-recovery warning. Publisher also validates the manifest CharacterId. |
| Lifecycle / ownership / cleanup | Late World/session/system creation; pause; restart/despawn; World replacement; overlapping scene publishers and all disable orders | Fixed lost initial battle request before World creation, missed publish when CombatActionSystem appears later, wrong-World cleanup and stale restored tables/revisions during scene handoff. |
| Existing regression harness | Hardcoded owner tuning versus actual project asset; landing marker fixture; replay text snapshot | Updated existing expectations to current owner values, kept marker-fallback checks explicit, and included LandingRecoveryTicks in replay text. No new test cases. |

## Confirmed defects and the relevant paths

1. **Missing scene binding (reported landing skip).** `CombatSmoke.unity` had a renderer config but a null bootstrap
   config. No landing duration was published, so default zero recovery skipped landing. The scene fix is one reference;
   the owner's AI team size and other scene content are retained.
2. **Artist offset cache erased by manual mode.** `CombatSpriteRenderer.EnsureAtlas` wrote zero into the cached character
   offset. `ResolveCharacter` then reused the same config/version on return, leaving zero. The cache now survives;
   manual mode reads a zero effective offset, and character mode reads its cached value for shader/anchors/bounds/footprints.
3. **Storage mode silently reset by content replacement.** The optional `storage = TrimmedRects` argument of
   `SpriteAtlasCatalog.Set` reverted a full-cell catalog when callers only wanted to replace manifest/pages. The
   two-argument overload now preserves storage. The three-argument overload deliberately changes it.
4. **Anchor tangent on the wrong side of the card bend.** Anchor positions used the correctly aligned piecewise surface,
   but directions always used its upright axis. An art offset can move an anchor below support. Directions now use the
   tangent at the actual aligned height, with the same facing reflection.
5. **Run history leaked into a short landing's idle epoch.** Example: last move 98, contact 99, one recovery tick. The
   old idle epoch was max(98+7,99+1)=105, five ticks after the fighter was free. It is now 100; movement before landing
   cannot extend idle's initial frame. Full speed/new actions still resume exactly at the recovery boundary.
6. **Wrong-World teardown.** OnDisable previously looked up DefaultWorld, while its saved schedule belonged to the
   formerly bound World; equal revision numbers could restore an unrelated table. The bootstrap now separately remembers
   the World receiving its battle request and the World receiving its schedule. End/restore use those actual bindings.
7. **Stale restoration during overlapping scene unload.** If A published then B replaced it, removing A first left B's
   saved baseline pointing to A's soon-unloaded settings. The departing bootstrap forwards its baseline to successors.
   In the opposite order, restoring A through Publish increments Revision; A now receives that restored revision too,
   so it can later release its own table. External newer publishers remain protected by the revision guard.
8. **Missed late initialization.** An enable before World/session creation previously dropped its one Restart request;
   a missing action table was remembered as a completed publish attempt forever. Initial setup is now retained until it
   can enter the same request buffer once. A late CombatActionSystem triggers a retry through an allocation-free existing
   system-handle check; no manifest/curve rebuild occurs while waiting. Disable clears a pending request. Explicit restart
   also publishes a changed config before queueing the battle start.
9. **Shader setting not applied after first draw.** `EnsureGpu` validated/assigned the shader only when creating its
   material. Reconfiguring an existing renderer therefore retained the old shader, even when the new field was null.
   Validation now runs before submission and a changed supported shader rebinds the same shared material. Error logging
   rearms after recovery; no per-fighter or per-frame material creation is introduced.
10. **Empty clip list crashes preview.** Manifest v1 accepts `clips: {}`, but the preview clamped the selected index to
    -1 and indexed it. It now reports that no frame is available and returns before indexing; the rest of the Inspector
    still lists missing required combat clips. This also addresses the preview item already recorded in concurrent #52.

Integration with #52: PR #51 contains its referenced scene fix and now the empty-preview guard. Its separate new key
layout/command-intent work is not implemented by this audit; downstream work should use the updated #51 rather than
duplicate these fixes. Input sampling/action rules are reviewed here without changing the action asset or bit flags.

The handoff search for other bootstraps runs only on table release (disable or World rebind), using existing scene
components. There is no per-frame search, global registry, extra config/timing singleton or second ECS state writer.
DefaultWorld replacement republishes timing; a new battle in that World still uses its explicit lifecycle request.

## Checks actually performed

- Parsed **all 83 C# source files** with Tree-sitter, no syntax errors. This does not check Unity types, source generation,
  Burst or shaders and is **not compilation**.
- Independent publisher/restore model: **307 scenarios** passed — one to five publishers, every removal permutation,
  intervening external writer and equal revision values in different Worlds. Model results do not execute Entities C#.
- Independent piecewise anchor-tangent finite differences: **72 combinations** passed (above/below support, both facings,
  pitches 5°/40°/85°, yaws 0°/35°, three directions).
- Static ownership/order/input checks: position/motion — Movement; facing — Facing; attack state — Action; hit resolution
  and HP — Hit; spawn initialization — Lifecycle. Input TakePressed remains in the existing producer on every active tick.
- Scene config references and CharacterIds agree; the config GUID resolves; all **50** 192×192 cells are valid/disjoint.
  Import metadata remains non-readable, point/clamp, mipmaps off, uncompressed RGBA; the PNG itself is absent.
- Owner animation asset, original JSON/pivot, PNG/art sources unchanged by this review. No new per-fighter objects,
  textures/materials, systems, meshes or render buffers. Existing steady-state shared timing/atlas cache paths retained.
- `git diff --check`; original mathematical trim/UV/flip/culling checks and current curve integration reviewed alongside
  the new transition corrections. The one-tick-landing counterexample now has idle epoch 100 rather than 105.
- Existing expectations corrected to the current serialized tuning: punches 14/6/9, kicks 20/10/30, spin 53/14/53;
  jump landing 27 ticks. The intentional punch_right/manifest marker mismatch is still reported, not silently normalized.

**NOT RUN:** Unity compile/import/source generation/Burst, EditMode/PlayMode, graphics/shader execution, keyboard play,
Frame Debugger, GC/performance measurements, Domain Reload disabled and player build. No Unity Editor/dependencies or
external art PNG are available here. Historical PASS results do not certify this revision.

## Owner checks remaining

Use the external PNG with its existing .meta. Verify extreme poses and anchors in both facings; change ImageOffsetPixels,
switch renderer source away/back and confirm restoration; replace catalog content without changing storage. Hold direction
through all attack phases and through landing; press Attack/Kick/Jump during recovery and confirm no deferred start.
Check frame 4 then 5, pause/restart in the recovery window and idle/run on the first free tick. In Inspector, clear or
mismatch bootstrap config/CharacterId and confirm diagnostics. Inspect 2/100/400 batching/culling/resource/GC results.
Scene/World lifetime fixes require their own Unity exercise; source/model checks are not a substitute for it.

## Separate pre-existing camera limitation

The camera Inspector allows pitch up to 90°, but `SpriteCardBasis` clamps the cosine divisor to 0.1. Above about 84.26°
the upright card's projected height shrinks, reaching zero at 90°; exact square source-pixel projection is therefore not
supported across the entire advertised range. This is not the reported stale-trim clipping and is not changed here:
changing camera limits or the established upright/ground depth model needs a separate presentation/camera decision.
The current scene uses 40°. Frustum envelopes remain conservative at extreme angles; the visual projection itself is the
limitation. [Follow-up #53](https://github.com/HolyPenguin21/mlf-legends/issues/53) records this instead of silently
changing the owner's camera controls in PR #51.
