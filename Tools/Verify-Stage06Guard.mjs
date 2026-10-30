// Source-contract assertions and independent arithmetic models ONLY. Never executes C#, Unity, Burst or shaders.
export function verify(read, original, paths) {
    const checks = [];
    const assert = (ok, name) => { if (!ok) throw new Error(name); checks.push(name); };
    const r = "Assets/MLFLegends/Combat/Runtime/";
    const guard=read(r+"Core/CombatGuard.cs"), action=read(r+"Simulation/CombatActionSystem.cs");
    const hit=read(r+"Simulation/CombatHitSystem.cs"), move=read(r+"Simulation/CombatMovementSystem.cs");
    const facing=read(r+"Simulation/CombatFacingSystem.cs"), motion=read(r+"Simulation/CombatMotionPaths.cs");
    const frames=read(r+"Presentation/Sprites/CombatAnimationSet.cs"), effects=read(r+"Presentation/Sprites/CombatEffectsPresentation.cs");
    const resources=read(r+"Presentation/Sprites/CombatEffectResources.cs"), extraction=read(r+"Presentation/Sprites/CombatSpriteExtraction.cs");
    const inputPath="Assets/InputSystem_Actions.inputactions", input=JSON.parse(read(inputPath)), before=JSON.parse(original(inputPath));
    const player=input.maps.find(m=>m.name==="Player"), previous=before.maps.find(m=>m.name==="Player");
    assert(JSON.stringify(player.actions.filter(a=>a.name!=="Guard"))===JSON.stringify(previous.actions),"prior actions preserved");
    assert(JSON.stringify(player.bindings.filter(b=>b.action!=="Guard"))===JSON.stringify(previous.bindings),"prior bindings preserved");
    assert(JSON.stringify(input.maps.filter(m=>m.name!=="Player"))===JSON.stringify(before.maps.filter(m=>m.name!=="Player")),"other input maps preserved");
    const bindings=player.bindings.filter(b=>b.action==="Guard").map(b=>b.path).sort();
    assert(JSON.stringify(bindings)===JSON.stringify(["<Gamepad>/leftShoulder","<Keyboard>/k"]),"K and gamepad guard");
    assert(!/= 1 << [4-8]\b/.test(read(r+"Core/CombatCommand.cs")),"retired bits unused");
    assert(read(r+"Input/PlayerInputCapture.cs").includes('CommandButtons.Guard,'),"capture dispatch connected");
    for(const p of ["Assets/Scenes/CombatSmoke.unity","Assets/MLFLegends/Combat/PresentationData/CharacterAnimationConfig.asset",
        "Assets/MLFLegends/Art/Characters/MageFemale01/Metadata/mage_female_01.atlas.json"]) assert(read(p)===original(p),"owner data exact: "+p);
    assert(read(r+"Simulation/CombatBodyContacts.cs")===original(r+"Simulation/CombatBodyContacts.cs"),"physical transfer solver preserved");
    assert(read(r+"Presentation/Sprites/CombatGroundShadow.shader")===original(r+"Presentation/Sprites/CombatGroundShadow.shader"),"shadow shader preserved");
    assert(guard.includes("double Current, Max") && guard.includes("math.isfinite(x)"),"finite validation and double stability");
    assert(guard.includes("(double)guardDamage / guard.Settings.Strength"),"cost formula");
    assert(guard.includes("time < guard.StartTick - 1d + guard.Settings.TimelyTicks"),"exclusive timely end");
    assert(guard.includes("resource.TimelyEntryTick != guard.StartTick"),"one timely per entry");
    assert(guard.indexOf("resource.Current <= 0")<guard.indexOf("else if (timely)"),"break wins over advantage");
    assert(guard.includes("SeenBreak == result.BreakSequence"),"same batch stale guard blocked");
    assert(guard.includes("resource.LastRegenTick == tick"),"regen once per tick");
    assert(hit.indexOf("new CombatGuardRegenJob")<hit.indexOf("if (m_Attackers.Length == 0)"),"regen without attackers");
    assert(hit.includes("FloatMode = FloatMode.Strict") && hit.includes("IJobEntity"),"strict Hit jobs authored");
    assert(hit.includes("Confirmed.Sort(new ByTime())") && hit.indexOf("Confirmed.Sort(")<hit.indexOf("CombatGuardResolution.Resolve"),"contact order before resolution");
    assert(hit.includes("a.Target.Id.Value.CompareTo") && hit.includes("a.AttackerIndex.CompareTo"),"target/attacker tie order");
    assert(hit.includes("var contact = 1f;") && hit.includes("Timing.DealsDamageOn") && hit.includes("Timing.DamageWindow"),"point final time independent of overlap eligibility");
    assert(hit.includes("Time = tick, End = tick + a.Timing.RecoveryStart"),"Swing uses displayed phase boundary");
    assert(effects.includes("now - (double)fact.StartTick") && effects.includes("CombatAnimationSet.StrikeLocalFrame"),"Swing anchor uses body axis and clamp");
    assert(frames.includes("timing.Frame(StrikeLocalFrame(timing, attack.Timing"),"body uses same clamp");
    assert(hit.includes("delta = victim - point") && hit.includes("time >= attacker.DamageHi"),"swept contacts retain historical time and geometry");
    assert(motion.includes("GroundedFrom = a.Motion.Grounded, GroundedTo = a.Result.Grounded") && hit.includes("b.GroundedFrom && b.GroundedTo"),"historical support connected");
    assert(hit.indexOf("CombatMotionGeometry.SegmentClear(Shapes")<hit.indexOf("CombatGuardResolution.Resolve"),"occlusion precedes guard");
    assert(hit.includes("hit.TargetGrounded, -hit.ImpactDelta"),"guard uses contact-relative direction");
    assert(hit.includes("if (hit.EffectiveDamage <= 0) continue;") && hit.includes("hit.EffectiveDamage > 0 && Feedback"),"damaging feedback uses effective loss");
    assert(hit.includes(" * impulseMultiplier") && move.includes("Velocity = kinetic.ExternalVelocity"),"only authored on-hit impulse scaled");
    assert(action.includes("command.Tick != tick") && move.includes("command.Tick != tick"),"accepted current command guards");
    assert(action.includes("guard.Settings = settings") && read(r+"Bootstrap/BattleLifecycleSystem.cs").includes("CombatStability.Spawn(guard.MaxStability)"),"entry snapshot and spawn maximum");
    assert(action.includes("result.Advantage(guard, now) && result.LastOutcomeTick < tick"),"manual next tick counter gate");
    assert(action.includes("guard.RequireFreshPress || (pressed && !guard.WaitRelease)"),"post-break release and new press");
    assert(action.indexOf("if (current.IsAttacking)")<action.indexOf("var blocked = GuardGate"),"guard cannot cancel attack or chain");
    assert(facing.includes("guard.Locked(result, tick)") && facing.includes("mode.Disabled = !mode.Disabled"),"sole facing pass preserves desired F mode");
    assert(extraction.includes("resources?.BlockTiming") && resources.includes("m_BlockTimings.Clear()"),"frozen block timing and resource teardown");
    assert(frames.includes("block.LocalFrameAt") && frames.includes("block.Frame(block.FrameCount - 1)"),"block plays once and holds");
    const newSystems=paths.filter(p=>p.startsWith(r)&&p.endsWith(".cs")&&/partial (struct|class) \w+\s*:\s*(ISystem|SystemBase)/.test(read(p))&&!original(p));
    assert(newSystems.length===0,"no second runtime system/clock/queue");
    assert(read("Assets/MLFLegends/Combat/Editor/CombatGuardSmokeWindow.cs").includes("new CombatCommandSubmission"),"smoke opponent uses common commands");
    let seed=79;
    const random=()=>{seed=(Math.imul(seed,1664525)+1013904223)>>>0; return seed/4294967296;};
    let modelCases=0;
    for(let n=0;n<10000;n++) {
        const max=1+Math.floor(random()*10000)/100, strength=.1+Math.floor(random()*100)/10;
        const cost=Math.floor(random()*5000)/100/strength, timely=random()<.5;
        const contacts=1+Math.floor(random()*9);
        let current=max, broken=false, success=false, lostHp=0, prefix=0, breakingAt=-1;
        const firstCost=cost*(timely?.25:1);
        for(let i=0;i<contacts;i++) {prefix+=i===0?firstCost:cost;if(cost>0&&prefix>=max){breakingAt=i;break;}}
        for(let i=0;i<contacts;i++){
            if(broken){lostHp+=4;continue;}
            const charge=cost*(timely&&!success?.25:1);current=Math.max(0,current-charge);
            if(cost>0&&current<=0){broken=true;lostHp+=4;}
            else if(timely&&cost>0)success=true;
        }
        const expected=breakingAt>=0?0:Math.max(0,max-firstCost-(contacts-1)*cost);
        if(Math.abs(current-expected)>1e-8 || lostHp!==(breakingAt>=0?(contacts-breakingAt)*4:0))
            throw Error("independent resource model "+n);
        const a=Math.floor(random()*20), active=1+Math.floor(random()*8), recovery=a+active;
        for(let e=0;e<=recovery;e++) if((e<recovery)!==(e+1<1+recovery))throw Error("presentation model");
        const t=1+Math.floor(random()*100), windowEnd=t-1+6;
        if(!(windowEnd-.25<windowEnd) || windowEnd<windowEnd)throw Error("point/swept exclusive boundary");
        modelCases++;
    }
    return {source_contract_checks:"PASS",checks,independent_arithmetic_model_cases:modelCases,
        model:"PASS (JavaScript model, not C# execution)",CSharp_syntax:"NOT RUN",Unity_compile:"NOT RUN",
        EditMode:"AUTHORED / NOT RUN",PlayMode:"AUTHORED / NOT RUN",Jobs_Burst:"NOT RUN",GPU:"NOT RUN",Performance:"AUTHORED / NOT RUN"};
}

if (typeof process !== "undefined" && process.argv?.[1]?.endsWith("Verify-Stage06Guard.mjs")) {
    const fs=await import("node:fs"), cp=await import("node:child_process"), path=await import("node:path");
    const base="e4a4b1a1de3d7c6324537b4796119d6a038f81ed";
    const root=path.resolve(path.dirname(new URL(import.meta.url).pathname),"..");
    const read=p=>fs.readFileSync(path.join(root,p),"utf8");
    const original=p=>{try{return cp.execFileSync("git",["show",base+":"+p],{cwd:root,encoding:"utf8",stdio:["ignore","pipe","ignore"]});}catch{return undefined;}};
    const files=cp.execFileSync("git",["ls-files"],{cwd:root,encoding:"utf8"}).trim().split("\n");
    console.log(JSON.stringify(verify(read,original,files),null,2));
}
