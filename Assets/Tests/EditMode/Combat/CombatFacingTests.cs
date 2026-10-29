using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace MLFLegends.Tests.EditMode
{
    // #74: common player/AI orientation from a start-of-tick snapshot, independent of world-space movement input.
    public class CombatFacingTests
    {
        const FacingDirection R = FacingDirection.Right, L = FacingDirection.Left;
        static BattleSetup Setup(float enemyX = 3, bool sameTeam = false) => new BattleSetup
        {
            Human = new CombatantSetup { Team = 1, Position = float3.zero, Facing = R },
            Ai = new CombatantSetup { Team = sameTeam ? (byte)1 : (byte)2, Position = new float3(enemyX, 0, 0), Facing = L },
        };
        static Entity Actor(ReplayWorld w, CombatantId id)
        {
            using var q = w.Manager.CreateEntityQuery(typeof(CombatantId)); using var rows = q.ToEntityArray(Allocator.Temp);
            return rows.ToArray().Single(e => w.Manager.GetComponentData<CombatantId>(e).Equals(id));
        }
        static void Initialize(ReplayWorld w, BattleSetup setup)
        {
            w.Request(BattleRequest.Start(setup)); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
        }
        static FacingDirection Facing(ReplayWorld w, Entity e) => w.Manager.GetComponentData<CombatFacing>(e).Value;
        static void Place(ReplayWorld w, Entity e, float x, float z = 0) => w.Manager.SetComponentData(e, new CombatPosition { Value = new float3(x, 0, z) });

        [TestCase(-1)] [TestCase(1)]
        public void RetreatFacesEnemyAndPreservesWorldMovementForBothSources(int side)
        {
            using var w = new ReplayWorld("nearest facing");
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: (sbyte)-side)
                .Ai(1, 1, ReplayRole.Ai, moveX: (sbyte)side);
            Initialize(w, Setup(side * 3)); w.Step();
            var h = Actor(w, w.Session.HumanCombatant); var ai = Actor(w, w.Session.AiCombatant);
            Assert.That(Facing(w, h), Is.EqualTo(side > 0 ? R : L));
            Assert.That(Facing(w, ai), Is.EqualTo(side > 0 ? L : R));
            Assert.That(w.Manager.GetComponentData<CombatPosition>(h).Value.x * side, Is.LessThan(0));
            Assert.That((w.Manager.GetComponentData<CombatPosition>(ai).Value.x - 3 * side) * side, Is.GreaterThan(0));
        }

        [Test]
        public void EqualDistancesUseStableIdThenNearestSwitchesInFreeState()
        {
            using var w = new ReplayWorld("nearest tie"); var setup = Setup(); setup.AiAllies = 1; Initialize(w, setup);
            var h = Actor(w, w.Session.HumanCombatant); var a = Actor(w, w.Session.AiCombatant);
            using var q = w.Manager.CreateEntityQuery(typeof(CombatantId)); using var entities = q.ToEntityArray(Allocator.Temp);
            var b = entities.ToArray().Single(e => e != h && e != a);
            Place(w, a, 2, 1); Place(w, b, -2, 1); w.Manager.SetComponentData(h, new CombatFacing { Value = L });
            w.Step(); Assert.That(Facing(w, h), Is.EqualTo(R), "Leader opponent has the lower stable id.");
            Place(w, b, -1); w.Step(); Assert.That(Facing(w, h), Is.EqualTo(L));
        }

        [TestCase(0f)] [TestCase(.0000001f)]
        public void ZOnlyOrEpsilonDeltaKeepsPreviousSide(float dx)
        {
            using var w = new ReplayWorld("z-only facing"); Initialize(w, Setup());
            var h = Actor(w, w.Session.HumanCombatant); var ai = Actor(w, w.Session.AiCombatant);
            Place(w, ai, dx, 3); w.Manager.SetComponentData(h, new CombatFacing { Value = L });
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: 1); w.Step();
            Assert.That(Facing(w, h), Is.EqualTo(L), "Found Z-only enemy suppresses MoveX fallback.");
        }

        [Test]
        public void OpponentSearchExcludesEnabledMembershipAndWholeEntityDisabledButIncludesHpZero()
        {
            using var w = new ReplayWorld("facing membership"); var setup = Setup(); setup.AiAllies = 1; Initialize(w, setup);
            var h = Actor(w, w.Session.HumanCombatant); var a = Actor(w, w.Session.AiCombatant);
            using var q = w.Manager.CreateEntityQuery(typeof(CombatantId)); using var entities = q.ToEntityArray(Allocator.Temp);
            var b = entities.ToArray().Single(e => e != h && e != a);
            Place(w, a, 1); Place(w, b, -2);
            w.Manager.SetComponentData(a, new CombatHealth { Current = 0, Max = 100 });
            w.Step(); Assert.That(Facing(w, h), Is.EqualTo(R));
            w.Manager.SetComponentEnabled<InCombat>(a, false); w.Step(); Assert.That(Facing(w, h), Is.EqualTo(L));
            w.Manager.SetComponentEnabled<InCombat>(a, true); w.Manager.AddComponent<Disabled>(a);
            w.Step(); Assert.That(Facing(w, h), Is.EqualTo(L));
            w.Manager.RemoveComponent<Disabled>(a); w.Step(); Assert.That(Facing(w, h), Is.EqualTo(R));
            w.Request(BattleRequest.Despawn(w.Session.AiCombatant)); w.Step(); Assert.That(Facing(w, h), Is.EqualTo(L));
        }

        [Test]
        public void StrikeDirectionLocksUntilEndAndCanReselectOnFirstTickOfChainedStrike()
        {
            using var w = new ReplayWorld("strike facing lock"); var table = CombatStrikeSchedule.Default;
            var t = table.Hand1; w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: CommandButtons.HandSeries)
                .Player((uint)(1 + t.ActiveStart), (uint)(1 + t.ActiveStart), ReplayRole.Human, pressed: CommandButtons.HandSeries);
            Initialize(w, Setup()); w.Step();
            var h = Actor(w, w.Session.HumanCombatant); var ai = Actor(w, w.Session.AiCombatant);
            Place(w, ai, -3);
            for (var i = 1; i < t.ChainPoint; i++) { w.Step(); Assert.That(Facing(w, h), Is.EqualTo(R)); }
            w.Step(); Assert.That(Facing(w, h), Is.EqualTo(L), "Next strike's first tick may reselect.");
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(10)]
        public void PacingDoesNotChangeFacingMovementOrHits(int pacing)
        {
            string[] Run(int k)
            {
                using var w = new ReplayWorld("facing replay");
                for (uint i = 1; i <= 60; i++)
                    w.Script.Player(i, i, ReplayRole.Human, moveX: -1, moveZ: (sbyte)(i % 2),
                        pressed: i == 1 ? CommandButtons.HandSeries : CommandButtons.None);
                w.Request(BattleRequest.Start(Setup()));
                for (var i = 0; i < 60; i += k) w.Frame(System.Math.Min(k, 60 - i));
                return w.Records.SelectMany(r => r.Normalized).ToArray();
            }
            CollectionAssert.AreEqual(Run(1), Run(pacing));
        }

        [TestCase(2)] [TestCase(100)] [TestCase(400)]
        public void EveryRosterActorUsesSameRuleAndFallbackStillUsesAcceptedMoveX(int count)
        {
            using var w = new ReplayWorld("facing bounded roster"); var setup = Setup(sameTeam: true);
            setup.HumanAllies = setup.AiAllies = (ushort)(count / 2 - 1); Initialize(w, setup);
            using var q = w.Manager.CreateEntityQuery(typeof(CombatantId)); using var entities = q.ToEntityArray(Allocator.Temp);
            foreach (var e in entities)
                w.SubmitBetweenFrames(new CombatCommand { Combatant = w.Manager.GetComponentData<CombatantId>(e), Tick = 1, MoveX = -1, Source = CommandSource.AI });
            w.Step(); foreach (var e in entities) Assert.That(Facing(w, e), Is.EqualTo(L));
            ref var system = ref w.World.Unmanaged.GetUnsafeSystemRef<CombatFacingSystem>(w.World.GetExistingSystem<CombatFacingSystem>());
            Assert.That(system.LastParticipants, Is.EqualTo(count)); Assert.That(system.LastOpponents, Is.EqualTo(count));
        }

        [Test]
        public void RejectedAndDeferredCommandsAffectOnlyTheFallbackAtTheirAcceptedTick()
        {
            using var w = new ReplayWorld("fallback command acceptance"); Initialize(w, Setup(sameTeam: true));
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: -1)
                .Player(2, 2, ReplayRole.Human, moveX: 2).Player(2, 4, ReplayRole.Human, moveX: 1);
            var h = Actor(w, w.Session.HumanCombatant); w.Step(3); Assert.That(Facing(w, h), Is.EqualTo(L));
            w.Step(); Assert.That(Facing(w, h), Is.EqualTo(R));
        }

        [Test]
        public void PauseRestartAndOtherWorldDoNotLeakOpponentSnapshots()
        {
            using var w = new ReplayWorld("facing lifecycle"); using var other = new ReplayWorld("foreign opponents");
            Initialize(w, Setup(-3)); Initialize(other, Setup(3)); w.Step(); other.Step();
            var old = Actor(w, w.Session.HumanCombatant); var hOther = Actor(other, other.Session.HumanCombatant);
            Assert.That(Facing(w, old), Is.EqualTo(L)); Assert.That(Facing(other, hOther), Is.EqualTo(R));
            w.World.SetTime(new TimeData(w.World.Time.ElapsedTime, 0)); w.FixedStep.Update(); Assert.That(Facing(w, old), Is.EqualTo(L));
            w.Request(BattleRequest.Restart(Setup(3))); w.Script = new ReplayScript(); w.Step();
            Assert.That(w.Manager.Exists(old), Is.False); Assert.That(Facing(w, Actor(w, w.Session.HumanCombatant)), Is.EqualTo(R));
            w.Request(BattleRequest.End()); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            Assert.That(w.CountWith<CombatFacing>(), Is.Zero);
        }

        [Test]
        public void ClockGuardAndPartialFixturesRemainSafe()
        {
            using var world = new World("partial facing"); var system = world.CreateSystem<CombatFacingSystem>();
            var entity = world.EntityManager.CreateEntity(typeof(CombatFacing), typeof(InCombat), typeof(AppliedCombatCommand));
            world.EntityManager.SetComponentData(entity, new CombatFacing { Value = L });
            world.EntityManager.SetComponentData(entity, new AppliedCombatCommand(new CombatCommand { MoveX = 1 }));
            system.Update(world.Unmanaged); Assert.That(world.EntityManager.GetComponentData<CombatFacing>(entity).Value, Is.EqualTo(L));
            world.EntityManager.CreateEntity(typeof(SimulationClockRunning)); system.Update(world.Unmanaged);
            Assert.That(world.EntityManager.GetComponentData<CombatFacing>(entity).Value, Is.EqualTo(R), "No team/id fixture takes MoveX fallback.");
        }
    }
}
