#!/usr/bin/env python3
"""Reproducible SOURCE / PYTHON MODEL checks, never a Unity/C# execution claim."""
import hashlib
import json
import math
from pathlib import Path
import random
import re
import struct
import subprocess

ROOT = Path(__file__).resolve().parents[1]
BASE = 'a6e96295c0a1e008327e28e8ff89205caf0a4f77'

def f32(value):
    try:
        return struct.unpack('f', struct.pack('f', value))[0]
    except OverflowError:
        return math.copysign(math.inf, value)

def bounds(p, b):
    r, h, _ = b
    return (f32(p[0] - r), p[1], f32(p[2] - r)), (f32(p[0] + r), f32(p[1] + h), f32(p[2] + r))

def model_grid(actors):
    grid = {}
    for ident, p, body in actors:
        lo, hi = bounds(p, body)
        for x in range(math.floor(lo[0]), math.floor(hi[0]) + 1):
            for z in range(math.floor(lo[2]), math.floor(hi[2]) + 1):
                grid.setdefault((x, z), []).append((ident, lo, hi))
    return grid

def model_query(grid, query):
    lo, hi = query
    seen = set()
    for x in range(math.floor(lo[0]), math.floor(hi[0]) + 1):
        for z in range(math.floor(lo[2]), math.floor(hi[2]) + 1):
            for ident, a, b in grid.get((x, z), ()):
                if all(a[k] <= hi[k] and b[k] >= lo[k] for k in range(3)):
                    seen.add(ident)
    return sorted(seen)

def reference(actors, query):
    # Independent full scan of six inequalities. No grid, production code or model overlap helper.
    lo, hi = query
    out = []
    for ident, (x, y, z), (r, h, _) in actors:
        if (f32(x-r) <= hi[0] and f32(x+r) >= lo[0]
                and y <= hi[1] and f32(y+h) >= lo[1]
                and f32(z-r) <= hi[2] and f32(z+r) >= lo[2]):
            out.append(ident)
    return sorted(out)

rng = random.Random(58)
queries_checked = pairs_checked = 0
for scene in range(36):
    count = (2, 100, 400)[scene % 3]
    actors = []
    for i in range(count):
        p = (0., 0., 0.) if i < 3 else tuple(f32(rng.uniform(-12, 12)) for _ in range(3))
        b = (f32((0.0001, 0.3, 1., 4.25)[i % 4]), f32(rng.uniform(0.01, 8)), f32(rng.uniform(0.01, 100)))
        actors.append((i + 1, p, b))
    queries = [((float(x), float(y), float(z)), (float(x), float(y), float(z)))
               for x, y, z in ((-1, 0, -1), (0, 0, 0), (1, 2, 1), (-12, -12, -12))]
    for _ in range(60):
        lo = tuple(f32(rng.uniform(-18, 18)) for _ in range(3))
        hi = tuple(f32(lo[k] + rng.uniform(0, 8)) for k in range(3))
        queries.append((lo, hi))
    expected = [reference(actors, q) for q in queries]
    for insertion in (actors, list(reversed(actors))):
        grid = model_grid(insertion)
        for query, want in zip(queries, expected):
            got = model_query(grid, query)
            assert got == want, (scene, query, got, want)
            queries_checked += 1
        pairs = set()
        for ident, p, body in actors:
            q = bounds(p, body)
            got = model_query(grid, q)
            for target in got:
                if target > ident:
                    assert (ident, target) not in pairs
                    pairs.add((ident, target))
            assert [x for x in got if x > ident] == [x for x in reference(actors, q) if x > ident]
            pairs_checked += 1
assert math.floor(-0.01) == -1
assert f32(3.4028234663852886e38 + 3.4028234663852886e38) == math.inf

cs = sorted((ROOT / 'Assets').rglob('*.cs'))
from tree_sitter import Language, Parser
import tree_sitter_c_sharp
parser = Parser(Language(tree_sitter_c_sharp.language()))
assert not [str(p) for p in cs if parser.parse(p.read_bytes()).root_node.has_error]
guids = []
for p in (ROOT / 'Assets').rglob('*.meta'):
    match = re.search(r'^guid: (\w+)$', p.read_text(), re.M)
    if match:
        guids.append(match[1])
assert len(guids) == len(set(guids)), 'Duplicate Unity GUID'
assert all(p.with_suffix(p.suffix + '.meta').is_file() for p in cs)

runtime = ROOT / 'Assets/MLFLegends/Combat/Runtime'
index = (runtime / 'Simulation/CombatSpatialIndexSystem.cs').read_text()
assert 'RefRW<' not in index and 'SetComponentData' not in index
assert 'static Native' not in index
assert 'Math.Floor((double)bounds.Min.x' in index
assert 'EntityManager.IsComponentEnabled<InCombat>' in index
assert 'EntityOrderVersion != m_EntityOrderVersion' in index
replay = (ROOT / 'Assets/Tests/EditMode/Combat/CombatReplayDriver.cs').read_text()
assert replay.count('typeof(CombatSpatialIndexSystem),') == 1
lifecycle = (runtime / 'Bootstrap/BattleLifecycleSystem.cs').read_text()
assert lifecycle.count('typeof(CombatBody)') == lifecycle.count('CombatBodyTuning.Default') == 1
changed = subprocess.check_output(['git', 'diff', '--name-only', BASE], cwd=ROOT, text=True).splitlines()
protected = []
for name in subprocess.check_output(['git', 'ls-tree', '-r', '--name-only', BASE], cwd=ROOT, text=True).splitlines():
    if (name.startswith(('Packages/', 'ProjectSettings/', 'Assets/Scenes/', 'Assets/MLFLegends/Art/'))
        or '/Presentation/' in name or '/Editor/' in name
        or name.startswith('Assets/MLFLegends/Combat/Runtime/Simulation/') and name.endswith('.cs')
        or name.endswith('/BattleSession.cs')):
        # New files are not in base; existing Movement/Hit/Action/tunings and all presentation remain byte-identical.
        assert name not in changed, name
        protected.append(name)
subprocess.run(['git', 'diff', '--check'], cwd=ROOT, check=True)
source_hashes = {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
                 for p in cs if 'Spatial' in p.name or 'CombatBody' in p.name}
report = {
    'base': BASE,
    'scope': 'SOURCE / PYTHON MODEL ONLY; not C#, Unity, Entities codegen, Burst or performance execution',
    'csharp_syntax_files': len(cs), 'guid_count_unique': len(guids),
    'model_queries_against_independent_scan': queries_checked,
    'model_pair_source_queries': pairs_checked,
    'insertion_orders_per_scene': 2,
    'protected_base_files_byte_unchanged': len(protected),
    'source_hashes_sha256': source_hashes,
    'status': 'PASS',
    'unity_compile_editmode_playmode_burst_gpu_leaks_performance': 'NOT RUN',
}
out = ROOT / 'Docs/Evidence/58/static-checks.json'
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps({k: v for k, v in report.items() if k != 'source_hashes_sha256'}, indent=2))
