#!/usr/bin/env python3
"""SOURCE/data checks + independent arithmetic MODEL. Does NOT compile/run C#, Unity, jobs or shaders."""
import hashlib
import json
import math
import random
import re
import struct
import subprocess
from collections import deque
from fractions import Fraction
from pathlib import Path
import yaml
from tree_sitter import Language, Parser
import tree_sitter_c_sharp

root = Path(__file__).resolve().parents[1]
base = 'd5e97dea7326fa6bc6ccd28ebb44f823c601a92d'
runtime = root / 'Assets/MLFLegends/Combat/Runtime'
parser = Parser(Language(tree_sitter_c_sharp.language()))
cs = sorted((root/'Assets').rglob('*.cs'))
assert not [str(p.relative_to(root)) for p in cs if parser.parse(p.read_bytes()).root_node.has_error]
assert all(Path(str(p)+'.meta').exists() for p in cs)
subprocess.run(['git', 'diff', '--check'], cwd=root, check=True)
subprocess.run(['git', 'diff', '--cached', '--check'], cwd=root, check=True)
subprocess.run(['git', 'diff', base, '--check'], cwd=root, check=True)
def old(path):
    return subprocess.check_output(['git','show',base+':'+path],cwd=root)
def text(path):
    return (root/path).read_text()
def unity_yaml(data):
    return yaml.safe_load(re.sub(r'^--- !u!.*$', '---', data, flags=re.M))
# Every previous owner-authored setting remains exactly equal, including curves/body/phases/launch/pivots.
asset = 'Assets/MLFLegends/Combat/PresentationData/CharacterAnimationConfig.asset'
before, after = unity_yaml(old(asset).decode()), unity_yaml(text(asset))
added = {'OverrideDamageDuration', 'DamageDurationSeconds', 'Swing', 'Impact'}
def previous_shape(node):
    if isinstance(node, dict): return {k: previous_shape(v) for k,v in node.items() if k not in added}
    if isinstance(node, list): return [previous_shape(v) for v in node]
    return node
assert previous_shape(after) == before
characters = after['MonoBehaviour']['m_Characters']
for c in characters:
    for k in ('Hand1','Hand2','Foot1','Foot2','PhysicalAbility'):
        slot=c['Combat'][k]
        assert slot['OverrideDamageDuration']==0
        assert slot['Swing']['Source']==slot['Impact']['Source']==2
        assert slot['Swing']['ClipId']=='swing' and slot['Impact']['ClipId']=='impact'
scene = 'Assets/Scenes/CombatSmoke.unity'
without_vfx = re.sub(r'^  m_EffectShader:.*\n|^  m_DrawEffects:.*\n', '', text(scene), flags=re.M)
assert without_vfx.encode()==old(scene), 'Existing owner scene fields changed'
# Retired bits are never reused, F does not conflict with existing/future keys, old bindings remain identical.
actions = 'Assets/InputSystem_Actions.inputactions'
old_input, new_input = json.loads(old(actions)), json.loads(text(actions))
for old_map, new_map in zip(old_input['maps'], new_input['maps']):
    if old_map['name']!='Player': assert old_map==new_map; continue
    assert [a for a in new_map['actions'] if a['name']!='FocusToggle']==old_map['actions']
    assert [b for b in new_map['bindings'] if b['action']!='FocusToggle']==old_map['bindings']
    f=[b for b in new_map['bindings'] if b['action']=='FocusToggle']
    assert len(f)==1 and f[0]['path']=='<Keyboard>/f'
buttons=text('Assets/MLFLegends/Combat/Runtime/Core/CombatCommand.cs')
assert 'FocusToggle = 1 << 10' in buttons
assert not re.search(r'= 1 << [4-8]\b', buttons)
# Existing owners/physics/body/shadow shader/art have not been refactored or redrawn.
unchanged = [
    'Assets/MLFLegends/Combat/Runtime/Simulation/CombatActionSystem.cs',
    'Assets/MLFLegends/Combat/Runtime/Simulation/CombatMovementSystem.cs',
    'Assets/MLFLegends/Combat/Runtime/Simulation/CombatBodyContacts.cs',
    'Assets/MLFLegends/Combat/Runtime/Presentation/Sprites/CombatSprite.shader',
    'Assets/MLFLegends/Combat/Runtime/Presentation/Sprites/CombatGroundShadow.shader',
    'Assets/MLFLegends/Art/Characters/MageFemale01/Metadata/mage_female_01.atlas.json',
]
for path in unchanged: assert old(path)==(root/path).read_bytes(), path
hit=(runtime/'Simulation/CombatHitSystem.cs').read_text()
facing=(runtime/'Simulation/CombatFacingSystem.cs').read_text()
consumer=(runtime/'Presentation/Sprites/CombatEffectsPresentation.cs').read_text()
assert 'Timing.DealsDamageOn' in hit and 'Timing.DamageWindow' in hit
assert 'Timing.PhaseAt' not in hit and 'index.EmptyMotionSegments' in hit
assert 'time >= attacker.DamageHi' in hit
assert 'position = victim' in hit and 'Time = tick - 1d + hit.Time' in hit
assert hit.index('state.Dependency.Complete();\n                LastHits') < hit.index('Kind = CombatEffectKind.Impact')
assert 'stream.LastSwingTick != tick' in hit and 'm_Swings.Sort(new ById())' in hit
assert 'mode.LastToggleTick != tick' in facing and 'command.Tick == tick' in facing
assert 'actor.HasIdentity && actor.AutoFocus' in facing and 'attack.StartTick != tick' in facing
assert not any(t in consumer for t in ('SetComponentData','SetComponent(','AddComponent','Time.time','Coroutine','new GameObject','ParticleSystem'))
assert 'fact.Time - (fact.StartTick - 1d)' in consumer and 'now >= fact.End' in consumer
shader=(runtime/'Presentation/Sprites/CombatEffect.shader').read_text()
for token in ('Blend SrcAlpha OneMinusSrcAlpha','ZTest LEqual','ZWrite Off','_EffectInstances','instance.rotation'):
    assert token in shader
assert struct.calcsize('3ffIfff')==32
# Fixture schema and separate, non-looping, non-character clips/pages are parsed as JSON, not inferred from pixels.
fixture=(runtime/'Presentation/Sprites/TechnicalEffectFixture.cs').read_text()
manifest=json.loads(re.search(r'ManifestJson = @"(.*?)";',fixture,re.S).group(1).replace('""','"'))
assert manifest['source_size']==[64,64] and len(manifest['pages'])==2
assert set(manifest['clips'])=={'swing','impact'} and all(not c['loop'] for c in manifest['clips'].values())
assert all(f['page'] in (0,1) and len(f['trim_offset'])==2 for f in manifest['frames'].values())
# Independent enforcement of the existing parser's 2px extrusion/trim/overlap contract.
for name, f in manifest['frames'].items():
    x,y,w,h=f['rect']; page=manifest['pages'][f['page']]
    assert x>=2 and y>=2 and x+w+2<=page['width'] and y+h+2<=page['height'], name
    tx,ty=f['trim_offset']; sw,sh=manifest['source_size']
    assert 0<=tx and 0<=ty and tx+w<=sw and ty+h<=sh
    for other, g in manifest['frames'].items():
        if name>=other or f['page']!=g['page']: continue
        a,b,c,d=g['rect']
        assert not (x-2<a+c+2 and a-2<x+w+2 and y-2<b+d+2 and b-2<y+h+2)
# Independent exact-rational interval/trajectory model. Compare analytic admitted time to dense interval intersections;
# negative/late/long windows and moving target relative velocity all participate. This is NOT execution of the C# solver.
rng=random.Random(77)
for _ in range(10000):
    first=Fraction(rng.randrange(0,1000),100)
    duration=Fraction(rng.randrange(1,1000),100)
    end=first+duration
    execution=math.ceil(end)+rng.randrange(0,5)
    admitted=[]
    for tick in range(execution+2):
        overlap=max(Fraction(tick),first)<min(Fraction(tick+1),end)
        expected=any(first<=p<end for p in (max(first,Fraction(tick)), min(end,Fraction(tick+1))-Fraction(1,10000))) and tick<end and tick+1>first
        assert overlap==expected
        if overlap: admitted.append(tick)
    assert admitted==list(range(math.floor(first), math.ceil(end)))
    # Relative linear contact with a finite forward slab; positive relative speed chosen to cross it.
    x=Fraction(rng.randrange(100,1000),100); velocity=Fraction(rng.randrange(100,1000),100)
    reach=Fraction(rng.randrange(1,99),100)
    enter=(x-reach)/velocity; leave=x/velocity
    lo=max(first%1,enter,Fraction(0)); hi=min(end%1+1,leave,Fraction(1))
    if lo<=hi and lo<1:
        relative=x-velocity*lo
        assert 0<=relative<=reach
    # Timing shares normalize to one one-shot interval independent of body canvas or Impact lifetime.
    shares=[rng.randrange(1,100) for _ in range(6)]; total=sum(shares)
    ends=[Fraction(sum(shares[:i+1]),total) for i in range(6)]
    assert ends[-1]==1 and all(a<b for a,b in zip(ends,ends[1:]))
# Bounded sequence/overwrite oracle.
ring=[None]*17; first=count=0; sequence=0; oracle=deque(maxlen=17)
for _ in range(10000):
    sequence+=1
    if count==17: first=(first+1)%17; count-=1
    ring[(first+count)%17]=sequence; count+=1; oracle.append(sequence)
    assert [ring[(first+i)%17] for i in range(count)]==list(oracle)
# Deterministic source fingerprint, including untracked additions before a commit. Evidence-only files are excluded.
files=sorted(p for directory in ('Assets','Tools') for p in (root/directory).rglob('*') if p.is_file() and '.git' not in p.parts)
fingerprints={str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
combined=hashlib.sha256(json.dumps(fingerprints,sort_keys=True).encode()).hexdigest()
print(json.dumps({
    'tested_source_sha':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),
    'base_sha':base,
    'source_dirty':bool(subprocess.check_output(['git','status','--porcelain','--','Assets','Tools'],cwd=root,text=True).strip()),
    'source':'PASS','CSharp_syntax_files':len(cs),'data':'PASS','owner_settings_unchanged':True,
    'independent_model_cases':10000,'ring_model_operations':10000,'model':'PASS','effect_instance_stride_model':32,
    'source_fingerprint_sha256':combined,'files_sha256':fingerprints,
    'Unity_compilation':'NOT RUN','EditMode':'AUTHORED / NOT RUN','PlayMode':'AUTHORED / NOT RUN',
    'Graphics':'AUTHORED / NOT RUN','Performance':'AUTHORED / NOT RUN','actual_Burst':'NOT RUN',
    'GPU_ms':'NOT MEASURED','owner_manual_acceptance':'NOT RUN',
},indent=2))
