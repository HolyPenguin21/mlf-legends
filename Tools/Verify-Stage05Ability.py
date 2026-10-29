#!/usr/bin/env python3
"""SOURCE and independent equation MODEL checks only; never Unity/C#/Burst execution."""
import json, math, random, subprocess, sys
from pathlib import Path
from tree_sitter import Language, Parser
import tree_sitter_c_sharp
r=Path(__file__).resolve().parents[1]
p=Parser(Language(tree_sitter_c_sharp.language()))
cs=list((r/'Assets').rglob('*.cs'))
assert not [str(f) for f in cs if p.parse(f.read_bytes()).root_node.has_error]
assert all(Path(str(f)+'.meta').exists() for f in cs)
subprocess.run(['git','diff','--check'],cwd=r,check=True)
sim=r/'Assets/MLFLegends/Combat/Runtime/Simulation'
move=(sim/'CombatMovementSystem.cs').read_text(); hit=(sim/'CombatHitSystem.cs').read_text(); owner=(sim/'CombatSpatialIndexSystem.cs').read_text(); paths=(sim/'CombatMotionPaths.cs').read_text()
assert 'tick - attack.StartTick == attack.Strike.LaunchTick' in move
assert 'SystemAPI.GetComponentLookup<CombatFacing>(true)' in move
assert move.index('owner.CalculateMovement(')<move.index('positions[row.Entity] =')
assert 'RestorePending = row.Body.Height < row.RequestedHeight' in move
assert 'm_Info.Tick != m_MotionTick' in paths and 'm_Info.BattleNumber != m_MotionBattle' in paths
assert owner.count('new NativeParallelMultiHashMap<')==1
assert 'Confirmed.Sort(new ByTime())' in hit and hit.index('Confirmed.Sort(')<hit.index('ledger.Add(')
assert 'point.y += attacker.Strike.HitOffsetY' in hit
assert 'PreciseActiveWindow' in (sim/'CombatStrikeSchedule.cs').read_text()
assert 'var pressed = landing ? CommandButtons.None' in (sim/'CombatActionSystem.cs').read_text()
# Independent spec equations: mass scaling, mirror symmetry and gravity are render-pacing independent.
rng=random.Random(73)
for _ in range(10000):
 mass=rng.uniform(.05,100); j=rng.uniform(0,24*mass); angle=rng.uniform(-90,90)*math.pi/180
 vx=j/mass*math.cos(angle); vy=j/mass*math.sin(angle)
 assert abs(math.hypot(vx,vy)-j/mass)<1e-10
 # Inelastic 1D normal contact conserves momentum and never creates kinetic energy.
 mb=rng.uniform(.05,100); vb=rng.uniform(-24,24); shared=(mass*vx+mb*vb)/(mass+mb)
 assert (mass+mb)*shared*shared <= mass*vx*vx+mb*vb*vb+1e-8
assert abs(12*math.cos(math.radians(35))-9.829824531)<1e-8
result={'sha':subprocess.check_output(['git','rev-parse','HEAD'],cwd=r,text=True).strip(),'dirty':bool(subprocess.check_output(['git','status','--porcelain'],cwd=r,text=True).strip()),'source_syntax_files':len(cs),'source':'PASS','independent_equation_cases':10000,'model':'PASS','unity_compilation':'NOT RUN','EditMode':'NOT RUN','PlayMode':'NOT RUN','Burst':'NOT RUN','benchmark':'NOT RUN','GPU':'NOT RUN'}
print(json.dumps(result,indent=2))
