#!/usr/bin/env python3
"""Reproducible SOURCE / PYTHON MODEL checks, never a Unity/C# execution claim."""
import argparse
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
CORRECTION_BASE = '180b4121792e68931f27c109b78cd0d24c21a4d4'
TUNING_COMMIT = '68c43cbb51ff863ec5b7a5bac82e5f39b60c4db1'
# Exact git blob IDs, not a directory exemption. These are the ONLY included art/config changes.
TUNING_BLOBS = {
    'Assets/MLFLegends/Art/Characters/MageFemale01/Atlas/mage_female_01_00.png.meta':
        '2a531fc30a6cf9f402001841981b6915e96dc0b5',
    'Assets/MLFLegends/Combat/PresentationData/CharacterAnimationConfig.asset':
        'c22eaf52b5213ecbc249aa878b700aa445b6b7e1',
}

def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT, text=True).strip()

args = argparse.ArgumentParser(description=__doc__)
args.add_argument('--output', type=Path, default=ROOT / 'Docs/Evidence/58/static-checks.json',
                  help='Use an outside-repository path to verify a final head without changing its worktree.')
options = args.parse_args()
assert not git('status', '--porcelain', '--untracked-files=all'), 'Verify a clean committed head, not an uncommitted approximation.'
TESTED_SHA = git('rev-parse', 'HEAD')
TESTED_TREE = git('rev-parse', 'HEAD^{tree}')
for ancestor in (BASE, CORRECTION_BASE, TUNING_COMMIT):
    subprocess.run(['git', 'merge-base', '--is-ancestor', ancestor, TESTED_SHA], cwd=ROOT, check=True)

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
queries_checked = pairs_checked = batch_ranges_checked = 0
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
        # Independently check flat-buffer range semantics against reference results, including an empty range.
        flat, ranges = [], []
        for query in queries:
            row = model_query(grid, query)
            ranges.append((len(flat), len(row)))
            flat.extend(row)
        for (offset, length), want in zip(ranges, expected):
            assert flat[offset:offset+length] == want
            batch_ranges_checked += 1
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

# Focused negative controls: these mutate the Python model, NOT the C# implementation.
# They establish that the reference checks can reject lost boundary cells, duplicates, bad ordering and stamp reuse.
control_actors = [(2, (0., 0., 0.), (1., 2., 1.)), (1, (0., 0., 0.), (1., 2., 1.))]
control_grid = model_grid(control_actors)
control_query = ((-1., 0., -1.), (1., 2., 1.))
want = reference(control_actors, control_query)
raw = [ident for rows in control_grid.values() for ident, _, _ in rows]
assert raw != want  # Missing deduplication.
assert list(dict.fromkeys(raw)) != want  # Missing stable Id ordering.
missing_upper = {key: rows for key, rows in control_grid.items() if key[0] < 1 and key[1] < 1}
tangent = ((1., 2., 1.), (1., 2., 1.))
assert model_query(missing_upper, tangent) != reference(control_actors, tangent)
old_seen = {1: 1, 2: 1}
assert [ident for ident in want if old_seen[ident] != 1] != want
old_seen.clear()
assert [ident for ident in want if old_seen.get(ident, 0) != 1] == want
negative_controls = 4

# Counterexamples for the original membership and lifecycle predicates; no Unity API execution is implied.
# An incomplete newly-enabled actor changes expected membership but not complete-body membership or structural order.
for missing in ('body', 'position', 'team'):
    published_ids = {1}
    complete_live_ids = {1}
    all_enabled_ids = {1, 2}
    old_current = len(complete_live_ids) == len(published_ids) and published_ids <= all_enabled_ids
    new_current = old_current and len(all_enabled_ids) == len(published_ids)
    assert old_current and not new_current
# IgnoreComponentEnabledState alone still excludes the whole-entity Disabled tag.
old_cleanup = [entity for entity, disabled in ((1, False), (2, True)) if not disabled]
new_cleanup = [1, 2]
assert 2 not in old_cleanup and 2 in new_cleanup

def range_cells(lo, hi):
    keys = (math.floor(lo[0]), math.floor(lo[1]), math.floor(hi[0]), math.floor(hi[1]))
    if min(keys) < -(2**31) or max(keys) > 2**31 - 1:
        return None
    width, depth = keys[2] - keys[0] + 1, keys[3] - keys[1] + 1
    return None if width > (2**63 - 1) // depth else width * depth
assert range_cells((-2_000_000_000., -2_000_000_000.), (2_000_000_000., 2_000_000_000.)) is None
assert range_cells((-(2**31), -(2**31)), (-(2**31), -(2**31))) == 1
assert range_cells((2147483520., 2147483520.), (2147483520., 2147483520.)) == 1

cs = sorted((ROOT / 'Assets').rglob('*.cs'))
from tree_sitter import Language, Parser
import tree_sitter_c_sharp
parser = Parser(Language(tree_sitter_c_sharp.language()))
assert not [str(p) for p in cs if parser.parse(p.read_bytes()).root_node.has_error]
# C# using locals are readonly structs. An indexer assignment directly on a using NativeArray is CS1654,
# even though syntax parsing accepts it. Writable aliases keep the using storage as the only disposal owner.
for p in cs:
    text = p.read_text()
    for name in re.findall(r'using var (\w+) = new NativeArray<', text):
        assert not re.search(r'\b' + re.escape(name) + r'\s*\[[^\n]+?\]\s*=(?!=)', text), (p, name)
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
assert 'm_ExpectedFighters.CalculateEntityCount() != m_Bodies.Length' in index
assert 'TryQueryAabbBatch' in index and 'GetSubArray(offset, results.Length - offset).Sort' in index
batch_method = index.split('public bool TryQueryAabbBatch', 1)[1].split('void ResetQueryDiagnostics', 1)[0]
assert batch_method.count('IsCurrent()') == 1
assert batch_method.index('IsCurrent()') < batch_method.index('AppendQuery(')
replay = (ROOT / 'Assets/Tests/EditMode/Combat/CombatReplayDriver.cs').read_text()
assert replay.count('typeof(CombatSpatialIndexSystem),') == 1
lifecycle = (runtime / 'Bootstrap/BattleLifecycleSystem.cs').read_text()
assert lifecycle.count('typeof(CombatBody)') == lifecycle.count('CombatBodyTuning.Default') == 1
assert 'EntityQueryOptions.IgnoreComponentEnabledState | EntityQueryOptions.IncludeDisabledEntities' in lifecycle
baseline_path = 'Assets/Tests/EditMode/Performance/CombatBaselineTests.cs'
baseline = (ROOT / baseline_path).read_text()
def archetype_types(text):
    return set(re.findall(r'typeof\((\w+)\)', text.split('CreateArchetype(', 1)[1].split(');', 1)[0]))
def allowed_types(text):
    return set(re.findall(r'typeof\((\w+)\)', text.split('s_CombatantComponents =', 1)[1].split('};', 1)[0]))
assert allowed_types(baseline) == archetype_types(lifecycle) | {'AppliedCombatCommand'}
reviewed_baseline = subprocess.check_output(['git', 'show', 'c73897d:' + baseline_path], cwd=ROOT, text=True)
old_missing_types = sorted(archetype_types(lifecycle) - allowed_types(reviewed_baseline))
assert old_missing_types == ['CombatBody'], 'Reproduce the reviewed-head baseline contract omission as SOURCE evidence.'
changed = git('diff', '--name-only', BASE, TESTED_SHA).splitlines()
assert set(git('diff-tree', '--no-commit-id', '--name-only', '-r', TUNING_COMMIT).splitlines()) == set(TUNING_BLOBS)
included_tuning = {}
for name, expected in TUNING_BLOBS.items():
    assert git('rev-parse', TUNING_COMMIT + ':' + name) == expected, name
    assert git('hash-object', '--no-filters', name) == expected, 'Tuning blob changed: ' + name
    assert git('rev-parse', TESTED_SHA + ':' + name) == expected, name
    assert name in changed, 'Included tuning must remain visible against the original base: ' + name
    included_tuning[name] = {'commit': TUNING_COMMIT, 'expected_git_blob': expected,
                             'tested_git_blob': expected, 'original_base_git_blob': git('rev-parse', BASE + ':' + name)}

protected = []
production_prefixes = ('Packages/', 'ProjectSettings/', 'Assets/Scenes/', 'Assets/MLFLegends/Art/',
                       'Assets/MLFLegends/Combat/PresentationData/', 'Assets/MLFLegends/Combat/Editor/',
                       'Assets/MLFLegends/Combat/Runtime/Presentation/')
gameplay_prefixes = tuple('Assets/MLFLegends/Combat/Runtime/' + layer + '/' for layer in
                          ('Core', 'Simulation', 'Input', 'Commands'))

def protected_path(name):
    return not name.endswith('.md') and (name.startswith(production_prefixes)
            or name.startswith(gameplay_prefixes) and name.endswith(('.cs', '.meta'))
            or name.endswith('/BattleSession.cs') or name == 'Assets/InputSystem_Actions.inputactions')

for name in git('ls-tree', '-r', '--name-only', BASE).splitlines():
    if protected_path(name) and name not in TUNING_BLOBS:
        assert name not in changed, 'Protected production file changed: ' + name
        protected.append(name)
# Newly added production art/config cannot escape the base-file loop either.
for name in changed:
    if name.startswith(production_prefixes) and not name.endswith('.md'):
        assert name in TUNING_BLOBS, 'Unlisted production asset/presentation change: ' + name
# #60 changes tests/verifier/docs only: also pin ALL production files to the merged 04.1 head,
# including its new Body/index files and lifecycle changes that intentionally differ from the original BASE.
correction_changes = git('diff', '--name-only', CORRECTION_BASE, TESTED_SHA).splitlines()
for name in correction_changes:
    assert (name.endswith('.md') or name.startswith('Assets/Tests/')
            or not name.startswith(('Assets/', 'Packages/', 'ProjectSettings/'))), 'Production changed from #60 base: ' + name
# A clean tree has no unstaged diff. Check the committed delivery, not that empty diff.
subprocess.run(['git', 'diff', '--check', BASE, TESTED_SHA], cwd=ROOT, check=True)
source_hashes = {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
                 for p in cs if 'Spatial' in p.name or 'CombatBody' in p.name or p.name in {'BattleLifecycleSystem.cs', 'CombatReplayDriver.cs', 'CombatBaselineTests.cs', 'CombatBenchmarkHarness.cs', 'CombatStrikeBenchmarkTests.cs', 'CombatAnimationSetTests.cs', 'BootstrapRosterAndStrikeWindowTests.cs', 'CombatSpriteRendererPlayModeTests.cs'}}
report = {
    'base': BASE,
    'correction_base': CORRECTION_BASE,
    'tested_sha': TESTED_SHA, 'tested_tree': TESTED_TREE, 'worktree_clean_at_start': True,
    'included_tuning_assets': included_tuning,
    'changed_paths_from_original_base': changed,
    'correction_changed_paths': correction_changes,
    'scope': 'SOURCE / PYTHON MODEL ONLY; not C#, Unity, Entities codegen, Burst or performance execution',
    'csharp_syntax_files': len(cs), 'guid_count_unique': len(guids),
    'csharp_readonly_using_nativearray_assignment_scan': 'PASS (source pattern, not compilation)',
    'model_queries_against_independent_scan': queries_checked,
    'model_pair_source_queries': pairs_checked,
    'model_batch_ranges_against_independent_scan': batch_ranges_checked,
    'python_model_negative_controls_detected': negative_controls,
    'source_membership_counterexamples': 3,
    'source_disabled_cleanup_counterexamples': 1,
    'source_lifecycle_baseline_archetype_contract': 'PASS',
    'reviewed_head_baseline_missing_components_source_only': old_missing_types,
    'insertion_orders_per_scene': 2,
    'protected_base_files_byte_unchanged': len(protected),
    'protected_base_files_with_pinned_tuning': len(TUNING_BLOBS),
    'all_production_non_document_files_byte_unchanged_from_correction_base': 'PASS',
    'committed_diff_whitespace_from_original_base': 'PASS',
    'source_hashes_sha256': source_hashes,
    'status': 'PASS',
    'results': {'source': 'PASS', 'python_model': 'PASS', 'observed_failures': [],
                'csharp_suites': 'NOT RUN', 'unity_compile_import_entities_burst': 'NOT RUN',
                'unity_gpu_allocations_leaks_performance': 'NOT RUN'},
    'unity_compile_editmode_playmode_burst_gpu_leaks_performance': 'NOT RUN',
}
out = options.output.resolve()
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps({k: v for k, v in report.items() if k != 'source_hashes_sha256'}, indent=2))
