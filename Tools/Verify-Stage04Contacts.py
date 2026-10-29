#!/usr/bin/env python3
"""Clean-commit SOURCE / independent PYTHON MODEL checks. Never execution of C#/Unity/Burst."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import random
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
BASE = 'adf98cd1c9ca4df2b2d2e96aa65d3a7e17eb3a5a'

def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT, text=True).strip()

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, required=True, help='Use a path outside the repo to keep the tested tree clean.')
args = parser.parse_args()
assert not git('status', '--porcelain', '--untracked-files=all'), 'Require clean committed head'
sha, tree = git('rev-parse', 'HEAD'), git('rev-parse', 'HEAD^{tree}')
subprocess.run(['git', 'merge-base', '--is-ancestor', BASE, sha], cwd=ROOT, check=True)
changed = git('diff', '--name-only', BASE, sha).splitlines()
protected = []
for name in git('ls-tree', '-r', '--name-only', BASE).splitlines():
    if name.endswith('.md'): continue
    if name.startswith(('Assets/MLFLegends/Art/', 'Assets/MLFLegends/Combat/PresentationData/',
        'Assets/MLFLegends/Combat/Runtime/Bootstrap/', 'Assets/MLFLegends/Combat/Runtime/Core/',
        'Assets/MLFLegends/Combat/Runtime/Commands/', 'Assets/MLFLegends/Combat/Runtime/Input/',
        'Assets/MLFLegends/Combat/Runtime/Presentation/', 'Assets/MLFLegends/Combat/Editor/',
        'Assets/Scenes/', 'Packages/', 'ProjectSettings/')) or name.endswith((
            'CombatActionSystem.cs', 'CombatFacingSystem.cs', 'CombatHitSystem.cs', 'CombatAttackTuning.cs',
            'CombatStrikeSchedule.cs', 'CombatMovementTuning.cs', 'CombatBodyTuning.cs', 'CombatSpatialTuning.cs',
            'CombatArenaSystem.cs', 'CombatArenaTests.cs', 'CombatSpatialIndexTests.cs',
            'CombatSpatialLifecycleTests.cs', 'CombatSpatialBenchmarkTests.cs')):
        assert name not in changed, 'Protected production/reference source changed: ' + name
        protected.append(name)
subprocess.run(['git', 'diff', '--check', BASE, sha], cwd=ROOT, check=True)
from tree_sitter import Language, Parser
import tree_sitter_c_sharp
syntax = Parser(Language(tree_sitter_c_sharp.language()))
cs = sorted((ROOT / 'Assets').rglob('*.cs'))
assert not [str(p) for p in cs if syntax.parse(p.read_bytes()).root_node.has_error]
assert all(Path(str(p) + '.meta').is_file() for p in cs)
guids = [m[1] for p in (ROOT / 'Assets').rglob('*.meta') if (m := re.search(r'^guid: (\w+)$', p.read_text(), re.M))]
assert len(guids) == len(set(guids))
sim = ROOT / 'Assets/MLFLegends/Combat/Runtime/Simulation'
move = (sim / 'CombatMovementSystem.cs').read_text()
owner = (sim / 'CombatSpatialIndexSystem.cs').read_text()
job = (sim / 'CombatBodyContacts.cs').read_text()
geometry = (sim / 'CombatArenaGeometry.cs').read_text()
assert '[BurstCompile' not in move
assert move.count('positions[row.Entity] =') == move.count('motions[row.Entity] =') == 1
assert move.index('owner.CalculateMovement(') < move.index('positions[row.Entity] =')
assert 'finally { owner.EndMovement(); }' in move and 'session.HumanCombatant.Value' in move
assert 'CommandSource.' not in move + job
assert owner.count('new NativeParallelMultiHashMap<') == 1
assert '.Schedule().Complete()' in owner and '[BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]' in job
assert 'EntityManager' not in job and 'SystemAPI.' not in job and 'CombatSpatialIndexSystem.' not in job
assert 'CombatSpatialBounds.TryCells' in job and 'm_Published = false;' in owner
assert owner.index('m_Published = false;', owner.index('BeginMovement()')) < owner.index('m_Movement.Clear();')
assert 'tick != m_Info.Tick' in owner and 'battle != m_Info.BattleNumber' in owner and 'EntityOrderVersion !=' in owner
for name in ('m_Movement', 'm_ContactPairs', 'm_ContactDiagnostics', 'm_EmptyShapes', 'm_Bodies', 'm_Cells', 'm_Seen'):
    assert f'if ({name}.IsCreated) {name}.Dispose();' in owner
assert 'Pairs.Sort(new ByPair())' in job and 'j <= i || Seen[j]' in job
assert 'integrateTime: false' in job and 'bool integrateTime = true' in geometry
assert 'KernelRanManaged = true' in job and '[BurstDiscard]' in job
assert 'FreezeIsland' in job and 'd.WorkLimit = true' in job and 'ResidualConstraint' in job
assert 'expected != m_Movement.Length' in owner and 'expected != m_Fighters.CalculateEntityCount()' in owner
assert 'a.CanRestoreSupport' in job and 'a.Result.Landed &= a.Result.Grounded;' in job
assert 'd.ResidualComplete = !d.WorkLimit' in job and 'd.UnresolvedPenetration' in job
assert 'ControlledMobility = .2f' in job and 'MaxIterations = 48' in job
assert '[UpdateAfter(typeof(CombatMovementSystem))]' in owner and '[UpdateBefore(typeof(CombatHitSystem))]' in owner

# Independent MODEL formulations of continuous finite-cylinder overlap:
# quadratic root interval versus minimum of a quadratic on the vertical-overlap time interval.
# These are Python mathematics checks; neither executes the production C# test or solver.
rng = random.Random(64)
sweeps = 0

def slab(qy, vy, ah, bh):
    if abs(vy) < 1e-12:
        return (0., 1.) if -bh < qy < ah else None
    a, b = sorted(((-bh-qy)/vy, (ah-qy)/vy))
    lo, hi = max(0., a), min(1., b)
    return (lo, hi) if lo < hi else None

for _ in range(18000):
    a = [rng.uniform(-4, 4) for _ in range(3)]
    b = [rng.uniform(-4, 4) for _ in range(3)]
    da = [rng.uniform(-6, 6) for _ in range(3)]
    db = [rng.uniform(-6, 6) for _ in range(3)]
    ra, rb, ah, bh = [rng.uniform(.01, 3) for _ in range(4)]
    q = [b[i]-a[i] for i in range(3)]; v = [db[i]-da[i] for i in range(3)]
    interval = slab(q[1], v[1], ah, bh)
    if interval is None: continue
    lo, hi = interval
    aa = v[0]**2 + v[2]**2; bb = q[0]*v[0] + q[2]*v[2]
    cc = q[0]**2 + q[2]**2 - (ra+rb)**2
    at = max(lo, min(hi, -bb/aa)) if aa else lo
    minimum = (q[0]+v[0]*at)**2 + (q[2]+v[2]*at)**2 - (ra+rb)**2
    if abs(minimum) < 1e-8: continue
    expected = minimum < 0
    discr = bb*bb-aa*cc
    roots = False
    if aa == 0: roots = cc < 0
    elif discr >= 0:
        root = math.sqrt(discr)
        roots = max(lo, (-bb-root)/aa) < min(hi, (-bb+root)/aa)
    assert roots == expected
    sweeps += 1

# Independent grid-vs-all-pairs envelope check, including arbitrary insertion order, negative coordinates,
# large bodies, crossed paths, and corrections that add neighbours. Full X/Y/Z AABBs refine candidates.
def envelope(p, d, r, h):
    return ([min(p[k],p[k]+d[k])-(r if k != 1 else 0) for k in range(3)],
            [max(p[k],p[k]+d[k])+(r if k != 1 else h) for k in range(3)])

def overlap(a, b):
    return all(a[0][k] <= b[1][k] and b[0][k] <= a[1][k] for k in range(3))

pairs_checked = 0
rollback_cross_checks = 0
for case in range(60):
    count = (2, 100, 400)[case % 3]
    actors = [([rng.uniform(-12,12) for _ in range(3)], [rng.uniform(-3,3) for _ in range(3)],
               rng.choice((.01,.3,2.)), rng.uniform(.01,4)) for _ in range(count)]
    bounds = [envelope(*a) for a in actors]
    expected = {(i,j) for i in range(count) for j in range(i+1,count) if overlap(bounds[i],bounds[j])}
    order = list(range(count)); rng.shuffle(order)
    grid = {}
    for i in order:
        lo, hi = bounds[i]
        for x in range(math.floor(lo[0]),math.floor(hi[0])+1):
            for z in range(math.floor(lo[2]),math.floor(hi[2])+1): grid.setdefault((x,z),[]).append(i)
    actual = set()
    for bucket in grid.values():
        for a in bucket:
            for b in bucket:
                if a < b and overlap(bounds[a],bounds[b]): actual.add((a,b))
    assert sorted(actual) == sorted(expected)
    pairs_checked += count*(count-1)//2
    # Independent graph proof/control: any rollback endpoint is in its working envelope.
    # Rolling an island back cannot newly overlap a surviving island.
    parent = list(range(count))
    def root(i):
        while parent[i] != i: i = parent[i]
        return i
    for i,j in expected:
        a,b = root(i),root(j)
        parent[max(a,b)] = min(a,b)
    frozen = root(case % count)
    final = [envelope(p if root(i) == frozen else [p[k]+d[k] for k in range(3)], [0,0,0], r, h)
             for i,(p,d,r,h) in enumerate(actors)]
    for i in range(count):
        for j in range(i+1,count):
            if (root(i) == frozen) == (root(j) == frozen): continue
            assert not overlap(final[i],final[j])
            rollback_cross_checks += 1
assert rollback_cross_checks > 100

# Independent arithmetic contact examples, not a port of the iterative solver.
assert math.hypot(.5,.5) > .6  # AABB diagonal is not a disk contact.
assert slab(2, 0, 2, 2) is None  # Vertical tangency does not block.
assert slab(3, -6, 2, 2) == (1/6, 5/6)  # Mid-sweep vertical overlap.
mobility = .2
assert abs(1/(1+mobility) - 5/6) < 1e-12
for mass in (1,4,10):
    light_displacement = .2*mass/(mass+1)
    heavy_displacement = .2/(mass+1)
    assert abs(heavy_displacement*mass-light_displacement) < 1e-12

# Independent 1-D edge-pressure arithmetic. This is not an executed C# solver.
# Three allies (first with mobility .2), passive enemy on the right, floor edge x=1, radius=.3.
x = [.0995 + 1/60, .6995, 1.2995, 1.8995]
used = [0.]*4
old_grounded = True
for iteration in range(48):
    violation = False
    for a,b in ((0,1),(1,2),(2,3)):
        depth = .6-(x[b]-x[a])
        if depth <= .0002: continue
        violation = True
        share = .2/1.2 if a == 0 else .5 if a == 1 else 1.
        ca = min(depth*share, .3+(1/60 if a == 0 else 0)-used[a])
        cb = min(depth*(1-share), .3-used[b])
        x[a] -= ca; x[b] += cb; used[a] += ca; used[b] += cb
        if x[2] >= 1.3: old_grounded = False
    if not violation: break
final_supported = x[2]-1 < .3
assert not violation and not old_grounded and final_supported and max(used) < .3
edge_control = {'scope':'1-D independent arithmetic, not C# execution', 'iterations':iteration+1,
                'old_sticky_grounded':old_grounded, 'final_disk_supported':final_supported,
                'final_edge_actor_x':x[2], 'max_correction_used':max(used)}

report = {
    'scope': 'SOURCE / INDEPENDENT PYTHON MODEL ONLY; not execution of C#/Unity/Burst or solver',
    'base_integrated_main': BASE, 'tested_sha': sha, 'tested_tree': tree,
    'clean_worktree_at_start': True, 'changed_paths': changed,
    'protected_files_byte_unchanged': len(protected), 'csharp_syntax_only_files': len(cs), 'unique_guids': len(guids),
    'source_owner_working_final_boundary': 'PASS (source assertions only)',
    'independent_continuous_geometry_model_cases': sweeps,
    'independent_grid_pair_reference_comparisons': pairs_checked,
    'independent_rollback_cross_island_checks':rollback_cross_checks,
    'edge_support_counterexample_model':edge_control,
    'source_hashes': {str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest()
                      for p in sorted(sim.glob('*.cs'))},
    'results': {'source':'PASS', 'independent_python_models':'PASS', 'observed_source_model_failures':[],
                'csharp_authored_suites':'NOT RUN', 'unity_compile_import_entities_burst':'NOT RUN',
                'unity_scene_lifecycle_domain_reload_build':'NOT RUN', 'cpu_gc_native_memory_benchmarks':'NOT RUN'},
}
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(report, indent=2)+'\n')
print(json.dumps({k:v for k,v in report.items() if k not in ('source_hashes','changed_paths')}, indent=2))
