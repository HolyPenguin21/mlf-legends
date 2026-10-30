#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using static MLFLegends.Tests.PlayMode.SpritePlayModeSupport;

namespace MLFLegends.Tests.PlayMode
{
    [Category("Graphics")]
    public class CombatGuardGraphicsTests
    {
        [UnityTest]
        public IEnumerator BlockAndBreakUseSharedSpriteFeedbackAndNoBlockedImpact()
        {
            RequireGraphicsDevice();
            using var w=StrikeEffectsPlayModeSupport.Create(2,out var actors,out var table);
            var em=w.EntityManager;
            var defender=actors.Single(e=>em.GetComponentData<CombatTeam>(e).Value==2);
            var attacker=actors.Single(e=>em.GetComponentData<CombatTeam>(e).Value==1);
            var target=CreateTarget(); var camera=CreateCamera("Guard feedback",new Vector3(.5f,.8f,0),2,target);
            var renderer=CreateRenderer(camera); renderer.BindWorld(w); renderer.DrawEffects=false;
            var readback=new Texture2D(256,256,TextureFormat.RGBA32,false);
            try
            {
                using var q=em.CreateEntityQuery(typeof(CombatCommandQueue));
                var queue=q.GetSingletonEntity();
                void Commands(uint tick)
                {
                    var b=em.GetBuffer<CombatCommandSubmission>(queue);
                    b.Add(new CombatCommandSubmission(new CombatCommand {Combatant=em.GetComponentData<CombatantId>(defender),
                        Tick=tick,Held=CommandButtons.Guard,Source=CommandSource.AI}));
                    b.Add(new CombatCommandSubmission(new CombatCommand {Combatant=em.GetComponentData<CombatantId>(attacker),
                        Tick=tick,Pressed=CommandButtons.HandSeries,Source=CommandSource.Player}));
                }
                Commands(1); StrikeEffectsPlayModeSupport.Step(w,1);
                var blocked=RenderAndRead(renderer,camera,readback);
                var defenderId=em.GetComponentData<CombatantId>(defender).Value;
                var index=Enumerable.Range(0,renderer.Extraction.Count).Single(i=>renderer.Extraction.InstanceIds[i]==defenderId);
                Assert.That(renderer.Extraction.Instances[index].GuardFeedback,Is.GreaterThan(0));
                Assert.That(renderer.Extraction.Instances[index].DamageFactor,Is.Zero);
                using var streams=em.CreateEntityQuery(typeof(CombatEffectStream));
                var stream=em.GetComponentData<CombatEffectStream>(streams.GetSingletonEntity());
                var facts=em.GetBuffer<CombatEffectFact>(streams.GetSingletonEntity());
                Assert.That(Enumerable.Range(0,stream.Count).Any(i=>facts[(stream.First+i)%CombatEffectStream.Capacity].Kind==CombatEffectKind.Impact),Is.False);
                // Force the next genuine confirmation to break through pressure, without touching HP or Action state.
                table.Hand1Strike.GuardDamage=200; CombatStrikeSchedule.Publish(em,table);
                for(uint tick=2;tick<=5;tick++) { Commands(tick); StrikeEffectsPlayModeSupport.Step(w,tick); }
                var broken=RenderAndRead(renderer,camera,readback);
                Assert.That(em.GetComponentData<CombatStability>(defender).BreakSequence,Is.GreaterThan(0));
                Assert.That(em.GetComponentData<CombatHealth>(defender).Current,Is.LessThan(100));
                Assert.That(broken.Zip(blocked,(a,b)=>!a.Equals(b)).Count(x=>x),Is.GreaterThan(5));
                SaveEvidence(readback,"stage06-1-guard-break");
                yield return null;
            }
            finally { DestroyAll(renderer,camera,target,readback); }
        }
    }
}
#endif
