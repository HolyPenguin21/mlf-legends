using System;
using System.Collections.Generic;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace MLFLegends.Tests.PlayMode
{
    /// <summary>Isolated World using only production owners. No recorder/alternate clock in the measured runtime region.</summary>
    static class StrikeEffectsPlayModeSupport
    {
        public static World Create(int count, out Entity[] actors, out CombatStrikeSchedule table)
        {
            var w = new World("strike effects fixture");
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(w, new[]
            {
                typeof(BeginSimulationEntityCommandBufferSystem), typeof(FixedStepSimulationSystemGroup),
                typeof(BeginFixedStepSimulationEntityCommandBufferSystem), typeof(EndFixedStepSimulationEntityCommandBufferSystem),
                typeof(BattleLifecycleSystem), typeof(BattleLifecycleInitializationProxy), typeof(CombatClockSystem),
                typeof(CombatArenaSystem), typeof(CombatArenaInitializationProxy), typeof(CombatCommandCollectionSystemGroup),
                typeof(CombatCommandProcessorSystem), typeof(CombatStateUpdateSystemGroup), typeof(CombatActionSystem),
                typeof(CombatFacingSystem), typeof(CombatMovementSystem), typeof(CombatSpatialIndexSystem), typeof(CombatHitSystem),
                typeof(CombatPublicationSystemGroup),
            });
            var em = w.EntityManager;
            var errors = new List<string>(); var definition = SpriteAtlasManifest.Parse(TechnicalAtlasFixture.CombatManifestJson(), errors);
            var set = CombatAnimationSet.Create(definition, id =>
            {
                definition.TryGetClipIndex(id, out var i);
                return new CharacterAnimationSettings { ClipId = id, EndFrame = definition.Clips[i].Frames.Count - 1,
                    TotalDurationSeconds = .6, PreparationEndFrame = 0, ActiveEndFrame = 1 };
            }, "VFX fixture", errors);
            Assert.That(set, Is.Not.Null, string.Join("; ", errors));
            var combat = new CharacterCombatSettings();
            for (var k = CombatAttackKind.Hand1; k <= CombatAttackKind.PhysicalAbility; k++)
            {
                combat.Strike(k).Swing = new CharacterEffectSettings { Source = CombatEffectSource.Technical, ClipId = "swing", Offset = new Vector3(.65f, .9f, 0) };
                combat.Strike(k).Impact = new CharacterEffectSettings { Source = CombatEffectSource.Technical, ClipId = "impact", DurationSeconds = .2f };
            }
            table = CombatStrikeSchedule.Default; table.Body.Radius = .05f;
            table.Hand1 = table.Hand2 = CombatStrikeTiming.FromTicks(0, 2, 2, 0);
            table.Hand1Strike.DepthHalfWidth = table.Hand2Strike.DepthHalfWidth = 4;
            using (var q = em.CreateEntityQuery(typeof(CombatEffectResources)))
                em.GetComponentObject<CombatEffectResources>(q.GetSingletonEntity()).Prepare(ref table, combat, set, null, Vector2.zero);
            CombatStrikeSchedule.Publish(em, table);
            var setup = SpritePlayModeSupport.Duel(float3.zero, FacingDirection.Right, new float3(.8f, 0, 0), FacingDirection.Left, count / 2 - 1);
            BattleRequest.Submit(em, BattleRequest.Start(setup)); w.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            using (var q = em.CreateEntityQuery(typeof(CombatantId), typeof(CombatTeam)))
            using (var e = q.ToEntityArray(Allocator.Temp)) actors = e.ToArray();
            var indices = new int[3];
            foreach (var e in actors)
            {
                var team = em.GetComponentData<CombatTeam>(e).Value;
                em.SetComponentData(e, new CombatPosition { Value = new float3(team == 1 ? 0 : .8f, 0, indices[team]++ * .15f) });
            }
            return w;
        }
        public static void Submit(World w, Entity[] actors, uint tick, bool mass)
        {
            if (tick % 4 != 1) return;
            using var query = w.EntityManager.CreateEntityQuery(typeof(CombatCommandQueue));
            var submissions = w.EntityManager.GetBuffer<CombatCommandSubmission>(query.GetSingletonEntity());
            foreach (var e in actors)
            {
                if (!mass && e != actors[0]) continue;
                submissions.Add(new CombatCommandSubmission(new CombatCommand { Combatant = w.EntityManager.GetComponentData<CombatantId>(e),
                    Tick = tick, Pressed = CommandButtons.HandSeries, Source = CommandSource.AI }));
            }
        }
        public static void Step(World w, uint tick)
        {
            w.SetTime(new TimeData((tick - .5) * SimulationTiming.TickDuration, SimulationTiming.TickDuration));
            w.GetExistingSystemManaged<FixedStepSimulationSystemGroup>().Update();
        }
    }
}
