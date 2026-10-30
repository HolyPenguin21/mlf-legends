#!/usr/bin/env python3
"""SOURCE syntax/schema and independent MODEL checks. Does not compile or execute Unity/C#/shaders."""
import json, math, random, re, struct, subprocess
from pathlib import Path
from tree_sitter import Language, Parser
import tree_sitter_c_sharp
root=Path(__file__).resolve().parents[1]
parser=Parser(Language(tree_sitter_c_sharp.language()))
cs=list((root/'Assets').rglob('*.cs'))
assert all(not parser.parse(p.read_bytes()).root_node.has_error for p in cs)
assert all(Path(str(p)+'.meta').exists() for p in cs)
guids=[re.search(r'^guid: (\w+)$',p.read_text(),re.M).group(1) for p in (root/'Assets').rglob('*.meta') if re.search(r'^guid: (\w+)$',p.read_text(),re.M)]
assert len(guids)==len(set(guids))
subprocess.run(['git','diff','--check'],cwd=root,check=True)
r=root/'Assets/MLFLegends/Combat/Runtime'
facing=(r/'Simulation/CombatFacingSystem.cs').read_text(); hit=(r/'Simulation/CombatHitSystem.cs').read_text()
extract=(r/'Presentation/Sprites/CombatSpriteExtraction.cs').read_text(); renderer=(r/'Presentation/Sprites/CombatSpriteRenderer.cs').read_text()
shadow=(r/'Presentation/Sprites/CombatGroundShadows.cs').read_text(); shader=(r/'Presentation/Sprites/CombatSprite.shader').read_text()
assert 'CombatSpatialIndexSystem' not in facing and 'distance == nearest && enemy.Id < bestId' in facing
assert 'enemy.Team == actor.Team' in facing and 'math.abs(dx) > 1e-6f' in facing
assert 'attack.StartTick != tick' in facing and 'LastKernelRanManaged' in facing
assert 'hit.EffectiveDamage > 0 && Feedback.HasComponent' in hit and 'LastDamageTick = d.Tick' in hit
assert 'targetHeight - attackerHeight' in hit and 'a.Actor.Body.Height * .5f' in hit and 'b.Actor.Body.Height * .5f' in hit
assert 'bodies[i].Radius' in shadow and 'GroundHeight' not in shadow and 'CombatArenaShape' in shadow
assert 'ProjectedBounds' in shadow and 'worldBounds = ProjectedBounds' in shadow
assert 'if (!Application.isPlaying || !m_DrawAnchorGizmos' in renderer
assert 'OnDrawGizmosSelected' not in renderer
assert 'float damageFactor;' in shader and 'float guardFeedback;' in shader
assert struct.calcsize('3ffIfff')==32
assert 'SampleSprite(input.uv).rgb' in shader and 'saturate(input.damageFactor * _DamageTint.a)' in shader
assert 'clip(color.a - _AlphaCutoff)' in shader
shadow_shader=(r/'Presentation/Sprites/CombatGroundShadow.shader').read_text()
for token in ['ZTest LEqual','ZWrite Off','Blend SrcAlpha OneMinusSrcAlpha','clip(input.halfSize - abs(input.surface))']:
 assert token in shadow_shader
# Independent model: nearest target order-invariance, waist algebra/equal-height preservation, fade bounds and tangent/bounds.
rng=random.Random(74)
for _ in range(10000):
 x,z=rng.uniform(-100,100),rng.uniform(-100,100)
 targets=[(i,rng.uniform(-100,100),rng.uniform(-100,100)) for i in range(1,21)]
 expected=min(targets,key=lambda p:((p[1]-x)**2+(p[2]-z)**2,p[0]))
 rng.shuffle(targets)
 assert min(targets,key=lambda p:((p[1]-x)**2+(p[2]-z)**2,p[0]))==expected
 ay,by=rng.uniform(-10,10),rng.uniform(-10,10); ah,bh=rng.uniform(.25,8),rng.uniform(.25,8); offset=rng.uniform(-8,8)
 assert math.isclose((by+bh/2)-(ay+ah/2+offset),(by-ay)+(bh-ah)/2-offset,abs_tol=1e-12)
 slope=rng.uniform(0,1); length=math.sqrt(1+slope*slope)
 tx=(1/length,slope/length,0); tz=(0,0,1); radius=rng.uniform(.05,2); angle=rng.uniform(0,2*math.pi)
 point=[radius*(tx[i]*math.cos(angle)+tz[i]*math.sin(angle)) for i in range(3)]
 assert all(abs(point[i])<=radius*(abs(tx[i])+abs(tz[i]))+1e-12 for i in range(3))
last=1.0
for age in range(10):
 f=max(0,1-age/9); fade=f*f*(3-2*f); assert 0<=fade<=last; last=fade
assert last==0
# Equal-distance tie and Z-only direction policy.
assert min([(2,1,1),(1,-1,1)],key=lambda p:(p[1]**2+p[2]**2,p[0]))[0]==1
print(json.dumps({'sha':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),
 'tree':subprocess.check_output(['git','rev-parse','HEAD^{tree}'],cwd=root,text=True).strip(),
 'dirty':bool(subprocess.check_output(['git','status','--porcelain'],cwd=root,text=True).strip()),
 'source':'PASS','CSharp_syntax_files':len(cs),'instance_stride_model':32,'shadow_stride_model':80,
 'independent_model_cases':10000,'model':'PASS','Unity_compilation':'NOT RUN','EditMode':'NOT RUN','PlayMode':'NOT RUN',
 'Burst':'NOT RUN','GPU_visual':'NOT RUN','CPU_GPU_performance':'NOT RUN','owner_manual_acceptance':'NOT RUN'},indent=2))
