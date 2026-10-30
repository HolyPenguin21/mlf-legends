using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace MLFLegends.Tests.EditMode
{
    [DisableAutoCreation]
    public partial class GuardLoadProducer : SystemBase
    {
        public string Scenario;
        protected override void OnUpdate()
        {
            var tick=SystemAPI.GetSingleton<SimulationClock>().Tick;
            var phase=(tick-1)%120;
            var buffer=SystemAPI.GetSingletonBuffer<CombatCommandSubmission>();
            foreach(var (id,team) in SystemAPI.Query<RefRO<CombatantId>,RefRO<CombatTeam>>().WithAll<InCombat>())
            {
                var defend=Scenario=="guarding" || team.ValueRO.Value==2 && Scenario!="free" && phase<40;
                var pressure=team.ValueRO.Value==1 && (Scenario=="block" || Scenario=="break") && phase<40 && phase%6==0;
                buffer.Add(new CombatCommandSubmission(new CombatCommand {Combatant=id.ValueRO,Tick=tick,
                    Held=defend?CommandButtons.Guard:0,
                    Pressed=pressure?CommandButtons.HandSeries:Scenario=="break" && defend && phase==0?CommandButtons.Guard:0,
                    Source=CommandSource.AI}));
            }
        }
    }

    [Category("Performance")]
    public class CombatGuardBenchmarkTests
    {
        [Test]
        public void GuardFullTickCostsFor2_100_400WithBurstOnOnly()
        {
            if(!Unity.Burst.BurstCompiler.IsEnabled) Assert.Ignore("Owner excluded Burst OFF.");
            var rows=new List<string> {
                "# Unity "+Application.unityVersion+"; CPU "+SystemInfo.processorType+"; OS "+SystemInfo.operatingSystem,
                "# Action/Hit/effect extraction/draw/GPU timings require the Unity Profiler capture; this headless fixture does not invent them.",
                "count\tscenario\tticks\tfull_mean_us\tfull_p95_us\tfacing_mean_us\tmovement_mean_us\tindex_mean_us\tgc_bytes\tmotion_capacity_bytes\tmanaged_kernel\tblocks\tbreaks"
            };
            foreach(var count in new[]{2,100,400})
            foreach(var scenario in new[]{"free","guarding","block","break"})
            {
                using var world=new World("guard benchmark");
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world,ReplayWorld.RuntimeSystems);
                var em=world.EntityManager; var t=CombatStrikeSchedule.Default;
                t.Body.Radius=.05f; t.Hand1=CombatStrikeTiming.FromTicks(0,1,1,0);
                t.Guard.MaxStability=scenario=="break"?20:100000;
                CombatStrikeSchedule.Publish(em,t);
                BattleRequest.Submit(em,BattleRequest.Start(new BattleSetup {
                    Human=new CombatantSetup {Team=1,Position=float3.zero,Facing=FacingDirection.Right},
                    Ai=new CombatantSetup {Team=2,Position=new float3(.8f,0,0),Facing=FacingDirection.Left},
                    HumanAllies=(ushort)(count/2-1),AiAllies=(ushort)(count/2-1) }));
                world.GetExistingSystemManaged<InitializationSystemGroup>().Update();
                using(var q=em.CreateEntityQuery(typeof(CombatantId),typeof(CombatTeam)))
                using(var es=q.ToEntityArray(Allocator.Temp))
                {
                    var n=new int[3];
                    foreach(var e in es) {
                        var team=em.GetComponentData<CombatTeam>(e).Value;
                        em.SetComponentData(e,new CombatPosition {Value=new float3(team==1?0:.8f,0,n[team]++*.15f)});
                    }
                }
                var producer=world.CreateSystemManaged<GuardLoadProducer>(); producer.Scenario=scenario;
                var collection=world.GetExistingSystemManaged<CombatCommandCollectionSystemGroup>();
                collection.AddSystemToUpdateList(producer); collection.SortSystems();
                var group=world.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
                var spatial=world.GetExistingSystemManaged<CombatSpatialIndexSystem>();
                uint tick=0;
                void Step() { tick++;world.SetTime(new TimeData((tick-.5)*SimulationTiming.TickDuration,SimulationTiming.TickDuration));group.Update(); }
                for(var i=0;i<120;i++) Step();
                var samples=new List<double>(600); double facing=0,movement=0,index=0; var managed=false;
                var gc=GC.GetAllocatedBytesForCurrentThread();
                for(var i=0;i<600;i++)
                {
                    var begin=Stopwatch.GetTimestamp(); Step(); samples.Add((Stopwatch.GetTimestamp()-begin)*1e6/Stopwatch.Frequency);
                    ref var f=ref world.Unmanaged.GetUnsafeSystemRef<CombatFacingSystem>(world.GetExistingSystem<CombatFacingSystem>());
                    ref var hit=ref world.Unmanaged.GetUnsafeSystemRef<CombatHitSystem>(world.GetExistingSystem<CombatHitSystem>());
                    facing+=f.LastMicroseconds; movement+=spatial.LastMovementKernelMicroseconds; index+=spatial.LastBuildMicroseconds;
                    managed|=f.LastKernelRanManaged || spatial.LastContacts.KernelRanManaged || hit.LastHits.Tick==tick && hit.LastHits.KernelRanManaged;
                }
                gc=GC.GetAllocatedBytesForCurrentThread()-gc; samples.Sort();
                using var stability=em.CreateEntityQuery(typeof(CombatStability));
                using var values=stability.ToComponentDataArray<CombatStability>(Allocator.Temp);
                var breaks=values.ToArray().Sum(x=>(long)x.BreakSequence);
                using var streams=em.CreateEntityQuery(typeof(CombatEffectStream));
                var se=streams.GetSingletonEntity(); var stream=em.GetComponentData<CombatEffectStream>(se);
                var facts=em.GetBuffer<CombatEffectFact>(se,true);
                var blocks=Enumerable.Range(0,stream.Count).Count(i=>facts[(stream.First+i)%CombatEffectStream.Capacity].Kind==CombatEffectKind.Block);
                if(scenario=="break") Assert.That(breaks,Is.GreaterThan(0));
                if(scenario=="block") Assert.That(blocks,Is.GreaterThan(0));
                Assert.That(managed,Is.False,"Burst ON runtime evidence must confirm actual kernels, not only the setting.");
                rows.Add(FormattableString.Invariant($"{count}\t{scenario}\t600\t{samples.Average():F3}\t{samples[569]:F3}\t{facing/600:F3}\t{movement/600:F3}\t{index/600:F3}\t{gc}\t{spatial.MotionCapacityBytes}\t{managed}\t{blocks}\t{breaks}"));
            }
            var path=Path.Combine(Application.temporaryCachePath,"stage06-guard.tsv");
            File.WriteAllLines(path,rows); TestContext.WriteLine(path);
        }
    }
}
