#!/usr/bin/env python3
"""Stage 04.2 SOURCE / INDEPENDENT PYTHON MODEL checks. Never C#/Unity execution."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import random
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
BASE = 'e26e668435b9cb55bcf5bced163710b6c2b61230'
MAIN_AT_DISPATCH = '180b4121792e68931f27c109b78cd0d24c21a4d4'
def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT, text=True).strip()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, default=ROOT / 'Docs/Evidence/61/source-checks.json')
options = parser.parse_args()
assert not git('status', '--porcelain', '--untracked-files=all'), 'Require clean committed head'
head = git('rev-parse', 'HEAD')
subprocess.run(['git', 'merge-base', '--is-ancestor', BASE, head], cwd=ROOT, check=True)
changed = git('diff', '--name-only', BASE, head).splitlines()
protected = []
for name in git('ls-tree', '-r', '--name-only', BASE).splitlines():
    if name.endswith('.md'): continue
    if (name.startswith(('Assets/MLFLegends/Art/', 'Assets/MLFLegends/Combat/PresentationData/',
                         'Assets/MLFLegends/Combat/Runtime/Presentation/', 'Assets/MLFLegends/Combat/Editor/',
                         'Assets/MLFLegends/Combat/Runtime/Commands/', 'Assets/MLFLegends/Combat/Runtime/Input/',
                         'Packages/', 'ProjectSettings/', 'Assets/InputSystem_'))
        or name.endswith(('CombatActionSystem.cs', 'CombatFacingSystem.cs', 'CombatHitSystem.cs',
                          'CombatBody.cs', 'CombatBodyTuning.cs', 'CombatSpatialIndexSystem.cs', 'CombatSpatialTuning.cs',
                          'CombatAttackTuning.cs', 'CombatStrikeSchedule.cs', 'CombatMovementTuning.cs',
                          'CombatSpatialIndexTests.cs', 'CombatSpatialLifecycleTests.cs', 'CombatSpatialBenchmarkTests.cs'))):
        assert name not in changed, 'Protected source changed: ' + name
        protected.append(name)
# Motion data layout is unchanged; only its obsolete spawn-floor documentation is corrected.
motion = 'Assets/MLFLegends/Combat/Runtime/Core/CombatMotion.cs'
old = subprocess.check_output(['git', 'show', BASE + ':' + motion], cwd=ROOT, text=True)
strip = lambda s: re.sub(r'///[^\n]*', '', s)
assert strip(old) == strip((ROOT / motion).read_text())
from tree_sitter import Language, Parser
import tree_sitter_c_sharp
syntax = Parser(Language(tree_sitter_c_sharp.language()))
cs = sorted((ROOT / 'Assets').rglob('*.cs'))
assert not [str(p) for p in cs if syntax.parse(p.read_bytes()).root_node.has_error]
assert all(Path(str(p) + '.meta').is_file() for p in cs)
guids = [re.search(r'^guid: (\w+)$', p.read_text(), re.M).group(1)
         for p in (ROOT / 'Assets').rglob('*.meta') if re.search(r'^guid: (\w+)$', p.read_text(), re.M)]
assert len(guids) == len(set(guids))
scene = (ROOT / 'Assets/Scenes/CombatSmoke.unity').read_text()
ids = re.findall(r'^--- !u!\d+ &(\d+)$', scene, re.M)
assert len(ids) == len(set(ids))
for match in re.finditer(r'\{fileID: (\d+)\}', scene):
    assert match[1] == '0' or match[1] in ids, 'Dangling local scene reference ' + match[1]
assert all(name in scene for name in ('Arena Floor', 'Arena Platform', 'Arena Ramp', 'm_Name: Cube', 'm_RequireArena: 1'))
assert 'y: 0.28669173' in scene, 'Original cube yaw retained'
base_runtime = ROOT / 'Assets/MLFLegends/Combat/Runtime'
geometry = (base_runtime / 'Simulation/CombatArenaGeometry.cs').read_text()
movement = (base_runtime / 'Simulation/CombatMovementSystem.cs').read_text()
owner = (base_runtime / 'Simulation/CombatArenaSystem.cs').read_text()
lifecycle = (base_runtime / 'Bootstrap/BattleLifecycleSystem.cs').read_text()
assert 'RefRW<' not in geometry and 'EntityManager' not in geometry and 'UnityEngine' not in geometry
assert 'Physics.' not in geometry and 'new Native' not in geometry
assert movement.count('position.ValueRW.Value = next') == movement.count('motion.ValueRW = kinetic') == 1
assert 'result.ControlledDistance' in movement and 'CombatArenaGeometry.Move' in movement
assert 'new CombatMotion' in lifecycle and 'CombatArenaGeometry.Support' in lifecycle
assert '[UpdateBefore(typeof(BattleLifecycleSystem))]' in owner
assert '[UpdateBefore(typeof(BattleLifecycleInitializationProxy))]' in owner
replay = (ROOT / 'Assets/Tests/EditMode/Combat/CombatReplayDriver.cs').read_text()
assert replay.count('typeof(CombatArenaSystem),') == replay.count('typeof(CombatArenaInitializationProxy),') == 1
assert 'MaxSubsteps = 256' in geometry and 'MaxIterations = 12' in geometry
assert 'result.Failure = CombatArenaFailure.WorkLimit; return result;' in geometry
subprocess.run(['git', 'diff', '--check', BASE, head], cwd=ROOT, check=True)

# Two independent MODEL formulations only: analytic disk rectangle vs boundary-segment distance;
# ramp support formula vs candidate extrema from circle/rectangle intersections. No solver port or C# execution.
rng = random.Random(61)
disk_checks = ramp_checks = 0
for _ in range(10000):
    x, z = rng.uniform(-4, 4), rng.uniform(-4, 4)
    hx, hz, radius = rng.uniform(.1, 3), rng.uniform(.1, 3), rng.uniform(.01, 2)
    dx, dz = max(abs(x) - hx, 0), max(abs(z) - hz, 0)
    analytic = dx*dx + dz*dz < radius*radius
    vertices = [(-hx,-hz), (hx,-hz), (hx,hz), (-hx,hz)]
    distances = []
    for a, b in zip(vertices, vertices[1:] + vertices[:1]):
        vx, vz = b[0]-a[0], b[1]-a[1]
        t = max(0, min(1, ((x-a[0])*vx+(z-a[1])*vz)/(vx*vx+vz*vz)))
        distances.append((x-a[0]-t*vx)**2 + (z-a[1]-t*vz)**2)
    reference = (-hx <= x <= hx and -hz <= z <= hz) or min(distances) < radius*radius
    assert analytic == reference
    disk_checks += 1
    if not analytic: continue
    candidates = [(x+radius,z), (x-radius,z), (x,z+radius), (x,z-radius)] + vertices
    for edge_z in (-hz,hz):
        if abs(edge_z-z) <= radius:
            d = math.sqrt(max(0, radius*radius-(edge_z-z)**2))
            candidates.extend([(x+d,edge_z),(x-d,edge_z)])
    for edge_x in (-hx,hx):
        if abs(edge_x-x) <= radius:
            d = math.sqrt(max(0, radius*radius-(edge_x-x)**2))
            candidates.extend([(edge_x,z+d),(edge_x,z-d)])
    valid = [a for a,b in candidates if -hx-1e-9 <= a <= hx+1e-9 and -hz-1e-9 <= b <= hz+1e-9
             and (a-x)**2+(b-z)**2 <= radius*radius+1e-9]
    assert valid
    expected = max(valid)
    actual = min(hx, x + math.sqrt(max(0, radius*radius-dz*dz)))
    assert abs(expected-actual) < 1e-8
    ramp_checks += 1
# Independent exact scenario numbers used by authored tests.
assert abs(.3 - math.sqrt(.08) - .017157287525381) < 1e-12
assert (2.3 / 4) * 2 == 1.15
# Substep corner sagitta is less than Skin across the supported size domain.
skin = float(re.search(r'const float Skin = ([.0-9]+)f', geometry)[1])
for radius in (.01,.3,1,10,32):
    length = min(radius/4, math.sqrt(4*radius*skin))
    assert radius - math.sqrt(radius*radius-length*length/4) < skin

report = {'scope':'SOURCE / INDEPENDENT PYTHON MODEL ONLY; never C#/Unity execution',
    'base_corrected_issue60_head':BASE, 'main_at_dispatch':MAIN_AT_DISPATCH,
    'tested_sha':head, 'tested_tree':git('rev-parse','HEAD^{tree}'), 'clean_worktree_at_start':True,
    'changed_paths':changed, 'protected_files_byte_unchanged':len(protected),
    'motion_layout_unchanged':'PASS (source text excluding XML comments)',
    'csharp_syntax_only_files':len(cs), 'unique_guids':len(guids), 'scene_local_references':'PASS',
    'model_disk_rectangle_vs_segment_checks':disk_checks, 'model_ramp_extrema_checks':ramp_checks,
    'source_hashes_sha256':{str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in cs
        if 'Arena' in p.name or p.name in {'CombatMovementSystem.cs','BattleLifecycleSystem.cs','CombatReplayDriver.cs','CombatAnimationSetTests.cs'}},
    'results':{'source':'PASS','independent_python_models':'PASS','observed_failures':[],
               'csharp_geometry_integration_lifecycle_suites':'NOT RUN', 'unity_import_compile_entities_burst':'NOT RUN',
               'unity_scene_gpu_domain_reload_build':'NOT RUN','runtime_cpu_gc_native_benchmarks':'NOT RUN'}}
out = options.output.resolve(); out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps({k:v for k,v in report.items() if k not in ('source_hashes_sha256','changed_paths')},indent=2))
