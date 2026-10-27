import json
from pathlib import Path
from PIL import Image
p=Path(__file__).resolve().parent.parent
m=json.loads((p/'Metadata/mage_female_01.atlas.json').read_text())
a=Image.open(p/'Atlas/mage_female_01_00.png')
assert a.mode=='RGBA' and a.size==(1152,192)
assert set(a.getchannel('A').getdata())=={0,255}
assert len(m['frames'])==6 and set(m['clips'])=={'idle_combat'}
assert m['source_size']==[192,192] and m['pivot']==[96,160] and m['nominal_height_px']==128
checks=[]; boxes=[]
for index,(id,f) in enumerate(m['frames'].items()):
 x,y,w,h=f['rect']; tx,ty=f['trim_offset']
 assert f['page']==0 and x>=2 and y>=2 and x+w+2<=1152 and y+h+2<=192
 assert tx>=0 and ty>=0 and tx+w<=192 and ty+h<=192
 assert x//192==index and (x+w+2)<=(index+1)*192
 box=(x-2,y-2,x+w+2,y+h+2)
 for q in boxes: assert box[2]<=q[0] or q[2]<=box[0] or box[3]<=q[1] or q[3]<=box[1]
 boxes.append(box)
 source=Image.open(p/'frames'/('mage_female_01_'+id+'.png')).convert('RGBA')
 rebuilt=Image.new('RGBA',(192,192)); rebuilt.paste(a.crop((x,y,x+w,y+h)),(tx,ty))
 assert source.tobytes()==rebuilt.tobytes()
 bb=source.getchannel('A').getbbox(); assert bb and bb[2]-bb[0]<=128 and bb[3]-bb[1]<=128
 # Full atlas cell must exactly equal source: no extrusion blocks outside the useful rect.
 assert source.tobytes()==a.crop((index*192,0,(index+1)*192,192)).tobytes()
 # 4px guard within useful rect; its extrusion is consequently fully transparent.
 assert bb[0]>=tx+4 and bb[1]>=ty+4 and bb[2]<=tx+w-4 and bb[3]<=ty+h-4
 # No disconnected one-pixel blocks in the exported body.
 mask=source.getchannel('A'); coords={(xx,yy) for yy in range(192) for xx in range(192) if mask.getpixel((xx,yy))}
 components=[]
 while coords:
  q=[coords.pop()];size=0
  while q:
   xx,yy=q.pop();size+=1
   for ox,oy in ((-1,-1),(0,-1),(1,-1),(-1,0),(1,0),(-1,1),(0,1),(1,1)):
    n=(xx+ox,yy+oy)
    if n in coords:coords.remove(n);q.append(n)
  components.append(size)
 assert min(components)>1,(id,components)
 checks.append({'id':id,'body_bbox':bb,'source_equals_atlas_cell':'PASS','source_equals_json_reconstruction':'PASS','transparent_guards_and_extrusion':'PASS','connected_components':components})
clip=m['clips']['idle_combat']; assert clip['loop'] and not clip['hold_last'] and len(clip['frames'])==6
assert sum(f['duration_ms'] for f in clip['frames'])==1200
for f in clip['frames']: assert f['id'] in m['frames'] and f['duration_ms']>0
for f in m['frames'].values():
 assert set(f['anchors'])=={'hand_left','hand_right','cast_origin'}
 for name in ('hand_left','hand_right'):
  an=f['anchors'][name]; assert any(an['direction']) and an['layer'] in ('front','back') and type(an['visible']) is bool
v={'technical_validation':'PASS','binary_alpha':'PASS','rgba8':'PASS','page_size':[1152,192],'cell_size':[192,192],'body_limit':[128,128],'rgba8_bytes':1152*192*4,'frame_checks':checks,'unity_smoke_import':'NOT RUN','art_review':'PENDING'}
(p/'validation.json').write_text(json.dumps(v,indent=2)+'\n')
print(json.dumps(v))
