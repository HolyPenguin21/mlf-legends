"""Install an external sprite-atlas package (Sprite Atlas Contract v1) into the project's tracked metadata (#70).

Reads the package JSON and the PNG page that is installed in the project (the PNG itself stays outside Git), checks the page
against the manifest, then writes, reproducibly:
  * the project manifest (package JSON; only pages[].file is set to the installed page basename, recorded in meta),
  * the page importer .meta sprite sheet: one Sprite per frame_id on its FULL source canvas, pivot = reference support
    source_size x [0.5, 0.85] (no pose alignment, no carried-over pivots); every other importer setting is kept,
  * the catalog's frame_id -> Sprite bindings (stable ids derived from the frame id).
--check only verifies and prints the report. Nothing is resized, regenerated or repainted.

  * --pivot-x-offsets: optional JSON {frame_id: dx} of per-frame horizontal image pivot shifts in source pixels (the
    owner's custom positions, #70); the pivot stays on the feet line, the Sprite rect stays on the frame's canvas.

Usage: python Tools/Install-AtlasPackage.py <package.json> <installed page.png> [--check] [--report out.json]
       [--pivot-x-offsets offsets.json]
Requires Pillow and numpy (verification only).
"""
import argparse
import hashlib
import json
import os
import re
import sys

import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CHARACTER = 'Assets/MLFLegends/Art/Characters/MageFemale01'
MANIFEST = CHARACTER + '/Metadata/mage_female_01.atlas.json'
CATALOG = CHARACTER + '/Metadata/MageFemale01SpriteAtlasCatalog.asset'
EXTRUSION = 2
SUPPORT = (0.5, 0.85)  # reference world support, fraction of source_size from the top-left (SpriteAtlasDefinition)


def stable_id(name):
    value = int.from_bytes(hashlib.md5(('mlf-sprite:' + name).encode()).digest()[:4], 'little', signed=True)
    return value if value not in (0, 21300000) else value + 1


def sprite_guid(name):
    return hashlib.md5(('mlf-sprite-guid:' + name).encode()).hexdigest()


def verify(manifest, png):
    image = Image.open(png)
    report = {'png': os.path.basename(png), 'png_sha256': hashlib.sha256(open(png, 'rb').read()).hexdigest(),
              'png_mode': image.mode, 'png_size': list(image.size), 'errors': [], 'warnings': []}
    errors, warnings = report['errors'], report['warnings']
    pixels = np.array(image.convert('RGBA')).astype(int)
    width, height = manifest['source_size']
    page = manifest['pages'][0]
    if len(manifest['pages']) != 1:
        errors.append('expected exactly one page')
    if image.mode != 'RGBA':
        errors.append(f'page mode {image.mode}, expected RGBA')
    if list(image.size) != [page['width'], page['height']]:
        errors.append(f'page is {image.size}, manifest says {page["width"]}x{page["height"]}')
    if max(image.size) > 2048:
        errors.append('page side exceeds 2048')
    report['clips'] = {cid: len(c['frames']) for cid, c in manifest['clips'].items()}
    report['clip_count'] = len(manifest['clips'])
    report['frame_count'] = len(manifest['frames'])
    report['source_size'] = manifest['source_size']
    report['nominal_height_px'] = manifest['nominal_height_px']
    report['pivot'] = manifest['pivot']
    report['reference_support'] = [width * SUPPORT[0], height * SUPPORT[1]]
    alpha = pixels[..., 3]
    semi = int(((alpha > 0) & (alpha < 255)).sum())
    report['semi_transparent_pixels'] = semi
    if semi:
        warnings.append(f'{semi} semi-transparent pixels; the body shader clips at alpha 128')
    covered = np.zeros(alpha.shape, bool)
    grown = []
    frames = {}
    for fid, f in manifest['frames'].items():
        x, y, w, h = f['rect']
        tx, ty = f['trim_offset']
        if f['page'] != 0:
            errors.append(f'{fid}: page {f["page"]}')
        if tx + w > width or ty + h > height:
            errors.append(f'{fid}: rect exceeds the source canvas')
        if x - EXTRUSION < 0 or y - EXTRUSION < 0 or x + w + EXTRUSION > page['width'] or y + h + EXTRUSION > page['height']:
            errors.append(f'{fid}: extruded rect leaves the page')
        g = (x - EXTRUSION, y - EXTRUSION, x + w + EXTRUSION, y + h + EXTRUSION)
        for other, og in grown:
            if g[0] < og[2] and og[0] < g[2] and g[1] < og[3] and og[1] < g[3]:
                errors.append(f'{fid}: extruded rect overlaps {other}')
        grown.append((fid, g))
        covered[g[1]:g[3], g[0]:g[2]] = True
        cell = pixels[y:y + h, x:x + w]
        if not (np.array_equal(pixels[y - 2:y, x:x + w], np.repeat(cell[:1], 2, 0))
                and np.array_equal(pixels[y + h:y + h + 2, x:x + w], np.repeat(cell[-1:], 2, 0))
                and np.array_equal(pixels[y:y + h, x - 2:x], np.repeat(cell[:, :1], 2, 1))
                and np.array_equal(pixels[y:y + h, x + w:x + w + 2], np.repeat(cell[:, -1:], 2, 1))):
            warnings.append(f'{fid}: 2 px extrusion does not repeat the edge pixels')
        body = np.argwhere(cell[..., 3] >= 128)
        if len(body) == 0:
            errors.append(f'{fid}: no body pixel')
            continue
        (top, left), (bottom, right) = body.min(0), body.max(0)
        useful = [int(left + tx), int(top + ty), int(right + tx + 1), int(bottom + ty + 1)]  # canvas, [x0,y0,x1,y1)
        frames[fid] = useful
        if useful[0] == 0 or useful[1] == 0 or useful[2] == width or useful[3] == height:
            warnings.append(f'{fid}: body touches the source canvas edge {useful}')
    outside = int(((alpha > 0) & ~covered).sum())
    if outside:
        errors.append(f'{outside} non-transparent pixels outside the extruded frame rects')
    report['useful_bounds'] = frames
    if frames:
        b = np.array(list(frames.values()))
        report['useful_union'] = [int(b[:, 0].min()), int(b[:, 1].min()), int(b[:, 2].max()), int(b[:, 3].max())]
    idle = [frames[i['id']] for i in manifest['clips'].get('idle_combat', {'frames': []})['frames'] if i['id'] in frames]
    if idle:
        tops = [u[1] for u in idle]; feet = [u[3] for u in idle]
        report['idle_standing_height_px'] = [int(min(feet) - max(tops)), int(max(feet) - min(tops))]
        report['idle_feet_y'] = [int(min(feet)), int(max(feet))]
    for cid, clip in manifest['clips'].items():
        n = len(clip['frames'])
        for marker, values in clip['markers'].items():
            if any(v < 0 or v >= n for v in values):
                errors.append(f'{cid}.markers.{marker}: outside 0..{n - 1}')
    return report


def sprite_entry(name, rect, pivot):
    x, y, w, h = rect
    return f"""    - serializedVersion: 2
      name: {name}
      rect:
        serializedVersion: 2
        x: {x}
        y: {y}
        width: {w}
        height: {h}
      alignment: 9
      pivot: {{x: {pivot[0]}, y: {pivot[1]}}}
      border: {{x: 0, y: 0, z: 0, w: 0}}
      customData:
      outline: []
      physicsShape: []
      tessellationDetail: 0
      bones: []
      spriteID: {sprite_guid(name)}
      internalID: {stable_id(name)}
      vertices: []
      indices:
      edges: []
      weights: []
"""


def write_meta(path, manifest, offsets):
    text = open(path, encoding='utf-8').read().replace('\r\n', '\n')
    width, height = manifest['source_size']
    page_height = manifest['pages'][0]['height']
    entries, table = [], []
    for fid, f in manifest['frames'].items():
        x, y, w, h = f['rect']
        tx, ty = f['trim_offset']
        cx, cy = x - tx, y - ty  # full source canvas origin on the page (SourceCanvasCells storage)
        # Unity pivot: normalized, Y up from the rect bottom.
        pivot = (round(SUPPORT[0] + offsets.get(fid, 0) / width, 6), round(1 - SUPPORT[1], 6))
        entries.append(sprite_entry(fid, (cx, page_height - cy - height, width, height), pivot))
        table.append(f'      {fid}: {stable_id(fid)}\n')
    start = text.index('  spriteSheet:\n    serializedVersion: 2\n    sprites:\n') + len('  spriteSheet:\n    serializedVersion: 2\n    sprites:\n')
    # The sheet's own trailing key (4 spaces), never a sprite's 'outline' (6 spaces): every old sprite is replaced.
    end = re.compile(r'^    outline: \[\]$', re.M).search(text, start).start()
    text = text[:start] + ''.join(entries) + text[end:]
    start = text.index('    nameFileIdTable:\n') + len('    nameFileIdTable:\n')
    end = text.index('  mipmapLimitGroupName:', start)
    text = text[:start] + ''.join(sorted(table)) + text[end:]
    for key, value in (('spriteMode', '2'), ('maxTextureSize', '2048'), ('nPOTScale', '0'), ('enableMipMap', '0'),
                       ('filterMode', '0'), ('wrapU', '1'), ('wrapV', '1')):
        if not re.search(rf'^\s+{key}: {value}$', text, re.M):
            raise SystemExit(f'importer setting {key} is not {value}; fix the import settings first')
    if re.search(r'textureCompression: [^0]', text):
        raise SystemExit('a platform uses texture compression; import with Compression None')
    open(path, 'w', encoding='utf-8', newline='\n').write(text)


def write_catalog(path, manifest, texture_guid, page_file):
    text = open(path, encoding='utf-8').read().replace('\r\n', '\n')
    pages = f'  m_Pages:\n  - File: {page_file}\n    Texture: {{fileID: 2800000, guid: {texture_guid}, type: 3}}\n'
    pivots = ''.join(f'  - FrameId: {fid}\n    Sprite: {{fileID: {stable_id(fid)}, guid: {texture_guid}, type: 3}}\n'
                     for fid in manifest['frames'])
    text = re.sub(r'  m_Pages:\n(?:  - .*\n(?:    .*\n)*)', lambda _: pages, text)
    text = re.sub(r'  m_ImagePivots:\n(?:  - .*\n(?:    .*\n)*)*', lambda _: '  m_ImagePivots:\n' + pivots, text)
    open(path, 'w', encoding='utf-8', newline='\n').write(text)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('package_json')
    parser.add_argument('page_png')
    parser.add_argument('--check', action='store_true')
    parser.add_argument('--report')
    parser.add_argument('--pivot-x-offsets')
    args = parser.parse_args()
    manifest = json.load(open(args.package_json, encoding='utf-8-sig'))
    report = verify(manifest, args.page_png)
    report['package_json_sha256'] = hashlib.sha256(open(args.package_json, 'rb').read()).hexdigest()
    report['package_page_file'] = manifest['pages'][0]['file']
    if args.report:
        json.dump(report, open(args.report, 'w', encoding='utf-8'), indent=1)
    summary = {k: v for k, v in report.items() if k not in ('useful_bounds',)}
    print(json.dumps(summary, indent=1))
    if report['errors']:
        return 1
    if args.check:
        return 0
    page_file = os.path.basename(args.page_png)
    meta_path = os.path.join(ROOT, CHARACTER, 'Atlas', page_file + '.meta')
    texture_guid = re.search(r'^guid: (\w+)', open(meta_path, encoding='utf-8').read(), re.M).group(1)
    manifest['pages'][0]['file'] = page_file
    manifest.setdefault('meta', {})['project_install'] = (
        f"page installed as {page_file} (package file {report['package_page_file']}); pixels and all other fields unchanged")
    with open(os.path.join(ROOT, MANIFEST), 'w', encoding='utf-8', newline='\n') as out:
        json.dump(manifest, out, indent=2, ensure_ascii=False)
        out.write('\n')
    offsets = json.load(open(args.pivot_x_offsets, encoding='utf-8')) if args.pivot_x_offsets else {}
    unknown = set(offsets) - set(manifest['frames'])
    if unknown:
        raise SystemExit(f'pivot offsets for unknown frames: {sorted(unknown)}')
    write_meta(meta_path, manifest, offsets)
    write_catalog(os.path.join(ROOT, CATALOG), manifest, texture_guid, page_file)
    print(f'installed {len(manifest["frames"])} frames: {MANIFEST}, {page_file}.meta, catalog bindings')
    return 0


if __name__ == '__main__':
    sys.exit(main())
