using System.Collections.Generic;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace MLFLegends.Tests.EditMode
{
    public class CombatFeedbackTests
    {
        static Entity Actor(ReplayWorld w, CombatantId id)
        {
            using var q = w.Manager.CreateEntityQuery(typeof(CombatantId)); using var e = q.ToEntityArray(Allocator.Temp);
            return e.ToArray().Single(x => w.Manager.GetComponentData<CombatantId>(x).Equals(id));
        }
        [TestCase(0, 100)] [TestCase(4, 100)] [TestCase(4, 0)]
        public void OnlyPositiveConfirmedDamageFlashesIncludingHpZeroAndLedgerDoesNotRefresh(int damage, int hp)
        {
            using var w = new ReplayWorld("feedback hit owner");
            var table = CombatStrikeSchedule.Default; var strike = table.Hand1Strike; strike.Damage = damage;
            table.Hand1Strike = strike; table.Hand1 = CombatStrikeTiming.FromTicks(0, 3, 1, 0);
            CombatStrikeSchedule.Publish(w.Manager, table);
            var setup = BattleSetup.Default; setup.Human.Position = float3.zero; setup.Ai.Position = new float3(.8f, 0, 0);
            w.Request(BattleRequest.Start(setup)); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            var h = Actor(w, w.Session.HumanCombatant); var target = Actor(w, w.Session.AiCombatant);
            w.Manager.SetComponentData(target, new CombatHealth { Current = hp, Max = 100 });
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: CommandButtons.HandSeries);
            w.Frame(3);
            ref var hit = ref w.World.Unmanaged.GetUnsafeSystemRef<CombatHitSystem>(w.World.GetExistingSystem<CombatHitSystem>());
            Assert.That(hit.LastHits.TraceSegments, Is.Zero, "Ordinary Hit schedules with a valid empty motion view.");
            var last = w.Manager.GetComponentData<CombatDamageFeedback>(target).LastDamageTick;
            Assert.That(last, Is.EqualTo(damage > 0 ? 1u : 0u));
            Assert.That(w.Manager.GetComponentData<CombatDamageFeedback>(h).LastDamageTick, Is.Zero);
            Assert.That(w.Manager.GetBuffer<CombatHitRecord>(h).Length, Is.EqualTo(1));
            Assert.That(w.Manager.GetComponentData<CombatHealth>(target).Current, Is.EqualTo(math.max(0, hp - damage)));
            var factor = CombatSpriteExtraction.DamageFactor(w.Tick, last, 9);
            w.World.SetTime(new Unity.Core.TimeData(w.World.Time.ElapsedTime, 0)); w.FixedStep.Update();
            Assert.That(CombatSpriteExtraction.DamageFactor(w.Tick, last, 9), Is.EqualTo(factor));
            w.Request(BattleRequest.Restart(setup)); w.Script = new ReplayScript(); w.Step();
            Assert.That(w.Manager.GetComponentData<CombatDamageFeedback>(Actor(w, w.Session.AiCombatant)).LastDamageTick, Is.Zero);
        }

        [Test]
        public void SimultaneousAttackersConfirmMultipleTargetsAndRefreshOnlyVictims()
        {
            using var w = new ReplayWorld("multi-target feedback"); var table = CombatStrikeSchedule.Default;
            table.Hand1 = CombatStrikeTiming.FromTicks(0, 3, 1, 0); table.Body.Radius = .05f;
            CombatStrikeSchedule.Publish(w.Manager, table);
            var setup = BattleSetup.Default; setup.HumanAllies = setup.AiAllies = 1;
            w.Request(BattleRequest.Start(setup)); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            using var q = w.Manager.CreateEntityQuery(typeof(CombatantId), typeof(CombatTeam));
            using var entities = q.ToEntityArray(Allocator.Temp); var counters = new int[3];
            foreach (var e in entities)
            {
                var team = w.Manager.GetComponentData<CombatTeam>(e).Value; var i = counters[team]++;
                w.Manager.SetComponentData(e, new CombatPosition { Value = new float3(team == 1 ? 0 : .8f, 0, i == 0 ? -.15f : .15f) });
                if (team == 1) w.SubmitBetweenFrames(new CombatCommand { Combatant = w.Manager.GetComponentData<CombatantId>(e),
                    Tick = 1, Pressed = CommandButtons.HandSeries, Source = CommandSource.AI });
            }
            w.Frame(3);
            foreach (var e in entities)
            {
                var team = w.Manager.GetComponentData<CombatTeam>(e).Value;
                Assert.That(w.Manager.GetComponentData<CombatDamageFeedback>(e).LastDamageTick, Is.EqualTo(team == 1 ? 0u : 1u));
                Assert.That(w.Manager.GetComponentData<CombatHealth>(e).Current, Is.EqualTo(team == 1 ? 100 : 100 - table.Hand1Strike.Damage * 2));
            }
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(10)]
        public void RepeatedConfirmedHitsRefreshTheWindowUnderCatchUp(int pacing)
        {
            using var w = new ReplayWorld("feedback refresh"); var table = CombatStrikeSchedule.Default;
            table.Hand1 = CombatStrikeTiming.FromTicks(0, 1, 1, 0); CombatStrikeSchedule.Publish(w.Manager, table);
            var setup = BattleSetup.Default; setup.Human.Position = float3.zero; setup.Ai.Position = new float3(.8f, 0, 0);
            for (uint t = 1; t <= 10; t++) w.Script.Player(t, t, ReplayRole.Human, pressed: CommandButtons.HandSeries);
            w.Request(BattleRequest.Start(setup));
            for (var t = 0; t < 10; t += pacing) w.Frame(System.Math.Min(pacing, 10 - t));
            var last = w.Manager.GetComponentData<CombatDamageFeedback>(Actor(w, w.Session.AiCombatant)).LastDamageTick;
            Assert.That(last, Is.EqualTo(10)); Assert.That(CombatSpriteExtraction.DamageFactor(w.Tick, last, 9), Is.EqualTo(1));
        }

        [TestCase(2f, 2f, .9f, 0f, true)]
        [TestCase(2f, 2f, .9001f, 0f, false)]
        [TestCase(2f, 1f, 0f, 0f, true)]
        [TestCase(4f, 1f, 0f, 0f, false)]
        [TestCase(4f, 1f, 0f, -1.5f, true)]
        [TestCase(2f, 2f, 1.4f, .5f, true)]
        public void WaistHeightUsesAcceptedBodiesAndIndependentClosedFormula(float ah, float bh, float y, float offset, bool expected)
        {
            var strike = new CombatStrikeParameters { ReachMin = 0, ReachMax = 1, DepthHalfWidth = .2f, HeightTolerance = .9f, HitOffsetY = offset };
            foreach (var facing in new[] { -1f, 1f })
                Assert.That(CombatHitSystem.Inside(strike, float3.zero, facing, new float3(.5f * facing, y, 0), ah, bh), Is.EqualTo(expected));
            var query = CombatHitSystem.StrikeQuery(strike, float3.zero, 1, ah);
            Assert.That((query.Min.y + query.Max.y) / 2, Is.EqualTo(ah / 2 + offset).Within(1e-5));
        }

        [Test]
        public void FadeIsBoundedSimulationTimeOnlyAndEndsAtNineTicks()
        {
            Assert.That(CombatSpriteExtraction.DamageFactor(1, 0, 9), Is.Zero);
            Assert.That(CombatSpriteExtraction.DamageFactor(5, 6, 9), Is.Zero);
            var previous = 1f;
            for (uint age = 0; age <= 9; age++)
            {
                var f = CombatSpriteExtraction.DamageFactor(100 + age, 100, 9);
                Assert.That(f, Is.InRange(0, previous)); previous = f;
            }
            Assert.That(previous, Is.Zero);
        }

        [TestCase(false, 0f, false)] [TestCase(false, 1.5f, true)]
        [TestCase(true, 0f, false)] [TestCase(true, 1.5f, true)]
        public void CompactWaistFlowsThroughMovementBroadphaseAndPointOrSweptHit(bool swept, float offset, bool expectedHit)
        {
            using var w = new ReplayWorld("accepted compact waist integration");
            var table = CombatStrikeSchedule.Default; table.Body.Height = 4;
            table.PhysicalAbility = CombatStrikeTiming.FromTicks(0, 2, 1, CombatAttackTuning.NoChain);
            var strike = table.PhysicalAbilityStrike;
            strike.CompactHeight = 1; strike.CompactEnd = 2; strike.HeightTolerance = .05f;
            strike.HitOffsetY = offset; strike.SelfImpulse = swept ? 1 : 0; strike.LaunchTick = 0;
            table.PhysicalAbilityStrike = strike; CombatStrikeSchedule.Publish(w.Manager, table);
            var setup = BattleSetup.Default; setup.Human.Position = float3.zero; setup.Ai.Position = new float3(.8f, 0, 0);
            w.Request(BattleRequest.Start(setup)); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: CommandButtons.PhysicalAbility);
            w.Step();
            var attacker = Actor(w, w.Session.HumanCombatant); var victim = Actor(w, w.Session.AiCombatant);
            Assert.That(w.Manager.GetComponentData<CombatBody>(attacker).Height, Is.EqualTo(1));
            Assert.That(w.Manager.GetComponentData<CombatBody>(victim).Height, Is.EqualTo(4));
            // Independent expectation: feet share Y=0, waists are .5 and 2; offset 1.5 aligns them.
            Assert.That(w.Manager.GetComponentData<CombatHealth>(victim).Current, Is.EqualTo(expectedHit ? 100 - strike.Damage : 100));
            Assert.That(w.Manager.GetBuffer<CombatHitRecord>(attacker).Length, Is.EqualTo(expectedHit ? 1 : 0));
            Assert.That(w.Manager.GetComponentData<CombatDamageFeedback>(victim).LastDamageTick, Is.EqualTo(expectedHit ? 1u : 0u));
        }

        [Test]
        public void ExtractionKeepsFeedbackWithItsEntityAcrossSortPagesAndWorldReplacement()
        {
            using var w = new ReplayWorld("feedback extraction"); w.Request(BattleRequest.Start(BattleSetup.Default)); w.Step();
            var pages = TechnicalAtlasFixture.CreatePages(); var errors = new List<string>();
            using var atlas = SpriteAtlas.Load(TechnicalAtlasFixture.ManifestJson, TechnicalAtlasFixture.Bind(pages), errors, null);
            using var extraction = new CombatSpriteExtraction();
            try
            {
                var victim = Actor(w, w.Session.AiCombatant);
                w.Manager.SetComponentData(victim, new CombatDamageFeedback { LastDamageTick = w.Tick });
                extraction.Select(w.Session.AiCombatant, 2);
                var basis = SpriteCardBasis.FromCamera(new float3(0, -.5f, 1), new float3(0, 1, .5f));
                extraction.Run(w.World, atlas, basis, .002f, 0);
                for (var i = 0; i < extraction.Count; i++)
                    Assert.That(extraction.Instances[i].DamageFactor, Is.EqualTo(extraction.InstanceIds[i] == w.Session.AiCombatant.Value ? 1 : 0));
                using var other = new ReplayWorld("replacement same ids"); other.Request(BattleRequest.Start(BattleSetup.Default)); other.Step();
                extraction.Run(other.World, atlas, basis, .002f, 0);
                Assert.That(extraction.Instances.ToArray().All(x => x.DamageFactor == 0), Is.True);
            }
            finally { foreach (var page in pages) Object.DestroyImmediate(page); }
        }

        [Test]
        public void ShadowProjectsOntoFloorRampAndPlatformAndHidesWithoutSurface()
        {
            var shapes = new NativeArray<CombatArenaShape>(2, Allocator.Temp);
            try
            {
                var floor = new CombatArenaShape { Kind = CombatArenaKind.Box, AxisX = new float2(1, 0), Center = float2.zero,
                    HalfSize = new float2(10), Bottom = -1, Top = 0 };
                shapes[0] = floor;
                shapes[1] = new CombatArenaShape { Kind = CombatArenaKind.Ramp, AxisX = new float2(1, 0), Center = float2.zero,
                    HalfSize = new float2(1), Bottom = 0, Top = 1 };
                Assert.That(CombatGroundShadows.TryProject(shapes, new float3(0, 4, 0), .4f, 0, out var ramp), Is.True);
                Assert.That(ramp.PositionRadius.y, Is.EqualTo(.5f));
                Assert.That(ramp.TangentX.y / ramp.TangentX.x, Is.EqualTo(.5f).Within(1e-6));
                Assert.That(CombatGroundShadows.TryProject(shapes, new float3(3, 4, 0), .4f, 0, out var grounded), Is.True);
                Assert.That(grounded.PositionRadius.y, Is.Zero, "Airborne Y must not lift the projection.");
                var platform = floor; platform.HalfSize = new float2(1); platform.Bottom = 1.5f; platform.Top = 2; shapes[1] = platform;
                Assert.That(CombatGroundShadows.TryProject(shapes, new float3(0, 4, 0), .4f, 0, out var projected), Is.True);
                Assert.That(projected.PositionRadius.y, Is.EqualTo(2));
                Assert.That(CombatGroundShadows.TryProject(shapes, new float3(11, 4, 0), .4f, 0, out _), Is.False);
            }
            finally { shapes.Dispose(); }
        }
    }
}
