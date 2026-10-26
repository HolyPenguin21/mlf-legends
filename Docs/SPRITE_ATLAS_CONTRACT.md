# Sprite atlas contract v1 (Stage 03, Issue #38)

**Audience:** the graphic agent exporting character atlases and the code that loads them. This file is the input contract
between the two. It does not depend on the character's clothes or hair design. Machine-readable example:
[`SpriteAtlas/mage_female_01.example.json`](SpriteAtlas/mage_female_01.example.json) (validated by the EditMode suite).

The loader is `SpriteAtlasManifest.Parse` (`Assets/MLFLegends/Combat/Runtime/Presentation/Sprites/`). It rejects a
manifest that breaks any rule below with a message naming the field path and id, e.g.
`frames.idle_relaxed_00.rect: [130,2,60,112] exceeds page 0 (256x128)`. A rejected manifest produces no catalog and no
GPU data; the battle keeps simulating and nothing is drawn.

## 1. Files

- One UTF-8 JSON manifest per character (a byte order mark is tolerated; comments and trailing content are not JSON
  and are rejected) plus its PNG pages (RGBA8 with alpha, each side ≤ 2048 px). A page imported without an alpha
  channel is rejected: every rect would draw as an opaque box.
- **Body alpha is binary:** the body is alpha-clipped at 50 %. Pixels with alpha < 128 are not drawn and do not occlude;
  pixels ≥ 128 are drawn fully opaque. Do not rely on soft/anti-aliased edges or translucent hair/cloth in body frames;
  translucent effects belong to a future VFX layer.
- Pages may have different sizes. Not every clip needs its own page; a page may hold frames of several clips.
- PNGs and large art sources are delivered outside Git. Suggested import folder:
  `Assets/MLFLegends/Art/Characters/MageFemale01/` (content only, no code).
- Unity import settings for pages: Texture Type Default, sRGB on, Alpha Is Transparency off (not needed), Filter **Point**,
  Wrap **Clamp**, Generate Mipmaps **off**, Compression **None**, Max Size ≥ the page size (no resize). The loader rejects
  a page whose size differs from the manifest or whose format is GPU-compressed, and warns about mipmaps/linear data.
  The shader samples with its own point/clamp sampler at mip 0, so a wrong filter/wrap import cannot blur or wrap pixels.

## 2. Coordinates

- **Source canvas:** every frame is cut from one virtual canvas of `source_size` pixels shared by all frames
  (start: 192 × 192). This is the drawing's frame, not the body size.
- Origin is the **top-left** corner of the canvas (and of each page), **X right, Y down**, in pixels.
- Coordinates are continuous: pixel `(i, j)` covers `[i, i+1) × [j, j+1)`. Pixel edges are integers, centres are `+0.5`.
- **pivot:** the virtual support point on the source canvas (start `[96, 160]`). It is placed exactly at the fighter's
  authoritative `CombatPosition`. It may be fractional.
- **nominal_height_px:** height of the calm standing figure (start 96), not of every bent pose. The runtime scale maps it
  to a configurable world height (start 2 world units); no other size is derived from it.
- **authored_facing:** `"right"` (the only accepted v1 value). Facing left mirrors the whole drawing and its anchors
  around the canvas: `x' = source_width − x`, `y' = y`; direction `dx' = −dx`. The pivot mirrors too, so a pose stays
  on its support point. Anatomical anchor names are **not** swapped (`hand_left` stays `hand_left`).

## 3. Top-level fields

| Field | Type | Rule |
|---|---|---|
| `schema_version` | integer | `1`. Any other value is rejected with "unsupported schema_version". |
| `character_id` | string | Stable, non-empty. First art package: `mage_female_01`. |
| `source_size` | `[w, h]` integers | 1…4096 each. |
| `nominal_height_px` | number | Finite, > 0, ≤ source height. |
| `pivot` | `[x, y]` numbers | Finite, inside `[0, w] × [0, h]`. |
| `authored_facing` | string | `"right"`. |
| `pages` | array of `{file, width, height}` | ≥ 1 entry; `file` non-empty and unique; `width`/`height` integers 1…2048. Frames reference pages by **zero-based index** in this array. |
| `frames` | object `frame_id → frame` | ≥ 1 entry; ids non-empty and unique. |
| `clips` | object `clip_id → clip` | May be empty; ids non-empty and unique. |
| `compatible_transitions` | array | May be empty. |
| `meta` | any | Optional, ignored (exporter name, notes). |

Any other key at any level is rejected (catches typos such as `trim_ofset`). New keys need a new `schema_version`.
**Duplicate keys** in any object (e.g. the same frame id twice) are rejected; the loader sees them before a dictionary
could silently keep only one.

## 4. Frame

```json
"idle_relaxed_00": {
  "page": 0,
  "rect": [2, 2, 60, 112],
  "trim_offset": [64, 50],
  "anchors": { "hand_left": {…}, "hand_right": {…}, "cast_origin": null }
}
```

| Field | Rule |
|---|---|
| `page` | Integer index into `pages`. |
| `rect` | `[x, y, w, h]` integers on the page: the **useful** area only, without padding/extrusion. `x, y ≥ 0`, `w, h > 0`, inside the page. Never rotated (packing rotation is not supported). |
| `trim_offset` | `[x, y]` integers ≥ 0: where the rect's top-left corner lies on the full source canvas. `trim_offset + [w, h]` ≤ `source_size`. Transparent trim is allowed and expected. |
| `anchors` | Object with exactly `hand_left`, `hand_right` (records) and `cast_origin` (record or `null` outside casting). |

Packing rules checked by the loader:

- Each rect gets **2 px extrusion** (edge pixels repeated) supplied by the artist, then transparent separation. The
  rect grown by 2 px on every side must stay inside its page and must not overlap any other frame's grown rect on the
  same page. The renderer never includes extrusion in the frame geometry.
- Rect sizes may all differ. Several clips may reference the same `frame_id` (shared frames are allowed).

Anchor record:

```json
{ "position": [117, 119.5], "direction": [0.25, 1], "layer": "front", "visible": true }
```

- `position`: finite `[x, y]` on the **untrimmed** source canvas (before trim), continuous coordinates. It may lie
  outside the useful rect (e.g. a hidden hand) and is not range-checked.
- `direction`: finite, non-zero `[dx, dy]` in canvas axes (Y down); not required to be normalised.
- `layer`: `"front"` or `"back"` relative to the body, for future weapon/VFX layers.
- `visible`: boolean. A hidden hand keeps its virtual position with `visible: false`.

## 5. Clip

```json
"cast_release": {
  "frames": [ { "id": "idle_relaxed_01", "duration_ms": 62.5 }, { "id": "cast_release_00", "duration_ms": 187.5 } ],
  "loop": false,
  "hold_last": true,
  "markers": { "entry": [0], "release_pose": [1] }
}
```

- `frames`: ordered, ≥ 1 entry; `id` must exist in `frames`; `duration_ms` a finite number > 0, fractions allowed.
- `loop`, `hold_last`: booleans, **not both true**. Both false = play once.
- `markers`: object `marker_name → array of local frame indices` (zero-based inside this clip). A single marker is still
  an array of one element; an empty object is valid; arrays are non-empty, without repeats.
- Known marker names: `entry`, `strike_pose`, `chain_exit`, `return`, `release_pose`; other names are accepted.

## 6. Compatible transitions

```json
{ "from_clip": "idle_relaxed", "exit_frame": 1, "to_clip": "cast_release", "entry_frame": 0 }
```

Clip ids must exist; `exit_frame`/`entry_frame` are local zero-based indices within their clip. Exact duplicates are
rejected.

**Markers and transitions describe graphics only.** They never cause damage, projectiles, gameplay cancel windows or
cooldowns; gameplay timing stays in the ECS simulation.

## 7. Art clip ids (compatibility list)

`idle_relaxed, idle_combat, relaxed_to_combat, combat_to_relaxed, walk, run, punch_left, punch_right, kick_left,
kick_right, kick_spin, jump_start, jump_rise, jump_apex, jump_fall, jump_land, dash_start, dash_loop, dash_end,
cast_windup, cast_charge_loop, cast_release, cast_recover`.

The artist's plan is 23 clips / 124 frames, but the runtime hard-codes neither the number nor the presence of any clip.
Adding a weapon clip or page later keeps all earlier ids and needs no renderer change. Clip availability does not
authorise gameplay for attacks, dash or magic.

## 8. Canvas size check before mass export

The artist checks the 192 × 192 canvas against extreme poses and long hair before mass production. If it is objectively
too small, one shared `source_size`/`pivot` change is agreed before export. Code reads both from the manifest (no 192 in
the shader); never cut hair/limbs or shrink single frames to fit the old canvas.

## 9. How the runtime places a frame (for reference)

With `s` = world height / `nominal_height_px` world units per pixel (start 2/96) and facing `f` = +1 right / −1 left,
a canvas point `(x, y)` lands at, relative to the pivot:

- horizontal screen offset `f · (x − pivot.x) · s` (along the camera's right axis);
- vertical screen offset `(pivot.y − y) · s` (up).

The trimmed rect covers `x ∈ [trim.x, trim.x + w]`, `y ∈ [trim.y, trim.y + h]`, so changing frames or rect sizes never
moves the support point. Anchors use the same function. Depth/geometry details: `Presentation/Sprites/SPRITES.md`.

## 10. Handshake

1. Code agent delivers this file and the example (done in #38).
2. Artist exports the first real frame + JSON; both run it through the loader (EditMode test or the smoke scene catalog)
   before mass export. Until then the art smoke is **NOT RUN**; the technical atlas is generated by code.
