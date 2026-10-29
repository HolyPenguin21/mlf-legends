using System;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace MLFLegends.Tests.EditMode
{
    public class CombatPhysicalAbilityTests
    {
        static BattleSetup Duel(float x = 20) => new BattleSetup
        {
            Human = new CombatantSetup { Team = 1, Facing = FacingDirection.Right, Position = float3.zero },
            Ai = new CombatantSetup { Team = 2, Facing = FacingDirection.Left, Position = new float3(x, 0, .2f) },
        };
        static Entity Actor(ReplayWorld w, CombatantId id)
        {
            using var q = w.Manager.CreateEntityQuery(typeof(CombatantId));
            using var rows = q.ToEntityArray(Allocator.Temp);
            foreach (var e in rows) if (w.Manager.GetComponentData<CombatantId>(e).Equals(id)) return e;
            throw new InvalidOperationException();
        }
        static CombatStrikeSchedule Schedule(float impulse = 12)
        {
            var table = CombatStrikeSchedule.Default;
            table.PhysicalAbility = CombatStrikeTiming.FromTicks(0, 4, 2, CombatAttackTuning.NoChain);
            var strike = table.PhysicalAbilityStrike;
            strike.SelfImpulse = impulse; strike.ElevationDegrees = 35; strike.LaunchTick = 0;
            table.PhysicalAbilityStrike = strike; return table;
        }
        static ReplayWorld Start(CombatStrikeSchedule table, ReplayScript script, BattleSetup? setup = null)
        {
            var w = new ReplayWorld("physical ability");
            CombatStrikeSchedule.Publish(w.Manager, table); w.Script = script;
            w.Request(BattleRequest.Start(setup ?? Duel()));
            w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update(); return w;
        }

        [TestCase(-1)] [TestCase(1)]
        public void LaunchAtTickZeroUsesFinalFacingAndJOverMassExactlyOnce(int sign)
        {
            var table = Schedule(); table.Body.Mass = 2;
            using var w = Start(table, new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: (sbyte)-sign, pressed: CommandButtons.PhysicalAbility), Duel(20 * sign));
            w.Step();
            var entity = Actor(w, w.Session.HumanCombatant);
            var motion = w.Manager.GetComponentData<CombatMotion>(entity);
            var expected = 6 * math.cos(math.radians(35));
            Assert.That(motion.ExternalVelocity.x, Is.EqualTo(sign * (expected - 2f / 60)).Within(.001));
            Assert.That(motion.VerticalVelocity, Is.EqualTo(6 * math.sin(math.radians(35)) - CombatMovementTuning.Gravity / 60).Within(.001));
            Assert.That(motion.Grounded, Is.False);
            w.Step();
            Assert.That(math.abs(w.Manager.GetComponentData<CombatMotion>(entity).ExternalVelocity.x), Is.LessThan(expected));
        }

        [Test]
        public void JumpPriorityDoesNotCreatePhantomLaunch()
        {
            using var w = Start(Schedule(), new ReplayScript().Player(1, 1, ReplayRole.Human,
                pressed: CommandButtons.Jump | CommandButtons.PhysicalAbility));
            w.Step(); var e = Actor(w, w.Session.HumanCombatant);
            Assert.That(w.Manager.GetComponentData<CombatAttackState>(e).IsAttacking, Is.False);
            Assert.That(w.Manager.GetComponentData<CombatMotion>(e).ExternalVelocity, Is.EqualTo(float2.zero));
        }

        [Test]
        public void MovingTargetCrossingBetweenEndpointsIsHitAndImpactWaitsForNextMovement()
        {
            var table = Schedule(24); table.Body.Radius = .05f; var strike = table.PhysicalAbilityStrike;
            strike.ElevationDegrees = 0; strike.ReachMin = 0; strike.ReachMax = .02f;
            strike.OnHitImpulse = new float3(0, 1, 2); table.PhysicalAbilityStrike = strike;
            using var w = Start(table, new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: CommandButtons.PhysicalAbility), Duel(.2f));
            CombatImpulse.Submit(w.Manager, w.Session.AiCombatant, new float3(-24, 0, 0));
            w.Step();
            var target = Actor(w, w.Session.AiCombatant);
            Assert.That(w.Manager.GetComponentData<CombatHealth>(target).Current, Is.EqualTo(100 - strike.Damage));
            Assert.That(w.Manager.GetComponentData<CombatDamageFeedback>(target).LastDamageTick, Is.EqualTo(1));
            Assert.That(w.Manager.GetComponentData<CombatMotion>(target).ExternalVelocity.y, Is.Zero);
            using (var q = w.Manager.CreateEntityQuery(typeof(CombatImpulseQueue)))
                Assert.That(w.Manager.GetBuffer<CombatImpulse>(q.GetSingletonEntity()).Length, Is.EqualTo(1));
            w.Step();
            Assert.That(w.Manager.GetComponentData<CombatMotion>(target).ExternalVelocity.y, Is.GreaterThan(0));
            Assert.That(w.Manager.GetComponentData<CombatHealth>(target).Current, Is.EqualTo(100 - strike.Damage));
        }

        [Test]
        public void CompactBodyRestoresAfterExecutionAndRestartUsesSpawnHeight()
        {
            var table = Schedule(0); var strike = table.PhysicalAbilityStrike;
            strike.CompactHeight = 1; strike.CompactEnd = 3; table.PhysicalAbilityStrike = strike;
            using var w = Start(table, new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: CommandButtons.PhysicalAbility));
            w.Step(); var e = Actor(w, w.Session.HumanCombatant);
            Assert.That(w.Manager.GetComponentData<CombatBody>(e).Height, Is.EqualTo(1));
            table.Body.Height = 3; CombatStrikeSchedule.Publish(w.Manager, table);
            w.Step(4);
            Assert.That(w.Manager.GetComponentData<CombatBody>(e).Height, Is.EqualTo(2), "Live base height is frozen at spawn.");
            w.Script = new ReplayScript(); w.Request(BattleRequest.Restart(Duel())); w.Step();
            Assert.That(w.Manager.GetComponentData<CombatBody>(Actor(w, w.Session.HumanCombatant)).Height, Is.EqualTo(3));
        }

        [Test]
        public void BlockedExpansionRetriesWithoutLockingAction()
        {
            var table = Schedule(0); var strike = table.PhysicalAbilityStrike;
            strike.CompactHeight = 1; strike.CompactEnd = 2; table.PhysicalAbilityStrike = strike;
            using var w = Start(table, new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: CommandButtons.PhysicalAbility));
            w.Step(); var e = Actor(w, w.Session.HumanCombatant);
            var arena = w.World.GetExistingSystemManaged<CombatArenaSystem>(); var lease = arena.Acquire();
            var floor = new CombatArenaShape { Kind = CombatArenaKind.Box, Center = float2.zero, HalfSize = new float2(100), Bottom = -1, Top = 0, AxisX = new float2(1, 0) };
            var ceiling = new CombatArenaShape { Kind = CombatArenaKind.Box, Center = float2.zero, HalfSize = new float2(2), Bottom = 1.2f, Top = 2, AxisX = new float2(1, 0) };
            arena.Stage(lease, new[] { floor, ceiling }); w.Step(8);
            Assert.That(w.Manager.GetComponentData<CombatBody>(e).Height, Is.EqualTo(1));
            Assert.That(w.Manager.GetComponentData<CombatBodyProfile>(e).RestorePending, Is.True);
            Assert.That(w.Manager.GetComponentData<CombatAttackState>(e).IsAttacking, Is.False);
            arena.Stage(lease, new[] { floor }); w.Step();
            Assert.That(w.Manager.GetComponentData<CombatBody>(e).Height, Is.EqualTo(2));
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(10)]
        public void PhysicalExecutionReplayMatchesRenderPacing(int pacing)
        {
            string[] Run(int step)
            {
                using var w = Start(Schedule(), new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: CommandButtons.PhysicalAbility));
                for (var i = 0; i < 60; i += step) w.Frame(Math.Min(step, 60 - i));
                return w.Records.SelectMany(r => r.Normalized).ToArray();
            }
            CollectionAssert.AreEqual(Run(1), Run(pacing));
        }

        [TestCase(1f)] [TestCase(-1f)]
        public void IndependentSweepOracleAndActiveClipping(float facing)
        {
            var strike = new CombatStrikeParameters { ReachMin = .5f, ReachMax = 1, DepthHalfWidth = .2f, HeightTolerance = .1f };
            var a = float3.zero; var b0 = new float3(2 * facing, 0, 0); var b1 = new float3(-facing, 0, 0);
            Assert.That(CombatMotionGeometry.Sweep(strike, facing, a, a, b0, b1, 0, 1, out var contact), Is.True);
            Assert.That(contact, Is.EqualTo(1f / 3).Within(1e-6)); // 2 - 3t = 1, independent equation
            Assert.That(CombatMotionGeometry.Sweep(strike, facing, a, a, b0, b1, .6f, 1, out _), Is.False);
            strike.HitOffsetY = 1;
            Assert.That(CombatMotionGeometry.Sweep(strike, facing, a, a, b0, b1, 0, 1, out _), Is.False);
        }

        [Test]
        public void OcclusionAndCommittedChordRejectSolidButAllowFloorTangency()
        {
            var shapes = new NativeArray<CombatArenaShape>(1, Allocator.Temp);
            try
            {
                shapes[0] = new CombatArenaShape { Kind = CombatArenaKind.Box, Center = float2.zero, HalfSize = new float2(.1f, 10), Bottom = -1, Top = 2, AxisX = new float2(1, 0) };
                Assert.That(CombatMotionGeometry.SegmentClear(shapes, new float3(-1, .5f, 0), new float3(1, .5f, 0)), Is.False);
                shapes[0] = new CombatArenaShape { Kind = CombatArenaKind.Box, Center = float2.zero, HalfSize = new float2(10), Bottom = -1, Top = 0, AxisX = new float2(1, 0) };
                Assert.That(CombatMotionGeometry.SegmentClear(shapes, float3.zero, new float3(1, 0, 0), .3f, 2), Is.True);
            }
            finally { shapes.Dispose(); }
        }
    }
}
