const sharp=require('sharp'),fs=require('fs'),path=require('path');
const root=path.resolve(__dirname,'..');
(async()=>{
 const input=path.join(__dirname,'idle_combat_generated_source.png');
 const page=Buffer.alloc(1152*192*4),frames={},ordered=[],checks=[];
 const top=136,base=585,sourceH=base-top,scaledW=103,scaledH=128,left=44,destTop=32;
 const hands=[[[153,293],[307,360]],[[151,280],[305,349]],[[151,282],[305,349]],[[151,296],[305,364]],[[151,308],[305,376]],[[151,296],[305,364]]];
 for(let k=0;k<6;k++){
  // Area-aware reduction of premultiplied RGBA; no point-sampling during reduction.
  const {data}=await sharp(input).extract({left:k*362,top,width:362,height:sourceH}).resize(scaledW,scaledH,{kernel:'lanczos3'}).ensureAlpha().raw().toBuffer({resolveWithObject:true});
  const frame=Buffer.alloc(192*192*4);let bounds=[192,192,-1,-1],alphaCount=0;
  for(let y=0;y<scaledH;y++)for(let x=0;x<scaledW;x++){
   let si=(y*scaledW+x)*4,dx=left+x,dy=destTop+y,di=(dy*192+dx)*4;
   if(data[si+3]<128)continue;
   for(let c=0;c<3;c++)frame[di+c]=data[si+c];frame[di+3]=255;alphaCount++;
   bounds=[Math.min(bounds[0],dx),Math.min(bounds[1],dy),Math.max(bounds[2],dx),Math.max(bounds[3],dy)];
  }
  const id=`idle_combat_${String(k).padStart(3,'0')}`;
  // Fixed 136px useful rect includes transparent guards. Its 2px extrusion is entirely
  // transparent, avoiding visible blocks on the full atlas while satisfying v1 packing.
  for(let y=26;y<166;y++)for(let x=26;x<166;x++){
   let sx=Math.max(28,Math.min(163,x)),sy=Math.max(28,Math.min(163,y));
   frame.copy(page,(y*1152+k*192+x)*4,(sy*192+sx)*4,(sy*192+sx)*4+4);
  }
  const anchor=(p,layer,direction)=>({position:[+(left+p[0]*scaledW/362).toFixed(2),+(destTop+(p[1]-top)*scaledH/sourceH).toFixed(2)],direction,layer,visible:true});
  frames[id]={page:0,rect:[k*192+28,28,136,136],trim_offset:[28,28],anchors:{hand_left:anchor(hands[k][0],'front',[.3,-1]),hand_right:anchor(hands[k][1],'back',[1,.2]),cast_origin:null}};
  ordered.push({id,duration_ms:200});checks.push({id,bounds,opaque_pixels:alphaCount});
  await sharp(frame,{raw:{width:192,height:192,channels:4}}).png().toFile(path.join(root,'frames',`mage_female_01_${id}.png`));
  await sharp(frame,{raw:{width:192,height:192,channels:4}}).resize(768,768,{kernel:'nearest'}).flatten({background:'#e4e1dd'}).png().toFile(path.join(root,'previews',id+'_4x.png'));
 }
 const atlas=path.join(root,'Atlas','mage_female_01_00.png');
 await sharp(page,{raw:{width:1152,height:192,channels:4}}).png().toFile(atlas);
 await sharp(atlas).resize(2304,384,{kernel:'nearest'}).flatten({background:'#e4e1dd'}).png().toFile(path.join(root,'previews','atlas_2x.png'));
 const m={schema_version:1,character_id:'mage_female_01',source_size:[192,192],nominal_height_px:128,pivot:[96,160],authored_facing:'right',pages:[{file:'mage_female_01_00.png',width:1152,height:192}],frames,clips:{idle_combat:{frames:ordered,loop:true,hold_last:false,markers:{}}},compatible_transitions:[],meta:{status:'intermediate_idle_combat_only',revision:2,cell_size:[192,192],body_limit:[128,128],layout:'one animation per row; row 0 idle_combat',duration_ms:1200,anchors_status:'estimated by hand, pending anatomical review',art_review:'pending',extension:'Preserve IDs and cell positions; append animation rows, then additional pages. Rects changed in v2 repair; replace PNG and JSON together.'}};
 fs.writeFileSync(path.join(root,'Metadata','mage_female_01.atlas.json'),JSON.stringify(m,null,2)+'\n');
 fs.writeFileSync(path.join(root,'validation.json'),JSON.stringify({revision:2,checks,unity_smoke_import:'NOT RUN',art_review:'PENDING'},null,2));
 console.log(JSON.stringify(checks));
})();
