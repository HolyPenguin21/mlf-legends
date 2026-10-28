using System;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;

namespace MLFLegends.Tests.EditMode
{
    // Stage 03 tuning: strike windows as a published, per-strike frozen schedule (CombatStrikeSchedule → CombatAttackState.Timing)
    // through the real runtime chain in an isolated replay World. Expectations come from the published windows only.
    public class CombatStrikeScheduleTests
    {
        const CommandButtons A = CommandButtons.Attack;

        static BattleSetup Duel(float aiX) => new BattleSetup
        {
            Human = new CombatantSetup { Team = 1, Position = float3.zero, Facing = FacingDirection.Right },
            Ai = new CombatantSetup { Team = 2, Position = new float3(aiX, 0f, 0f), Facing = FacingDirection.Left },
        };

        static CombatantRecord H(TickRecord r) => r.Combatant(r.HumanId);

        static CombatantRecord Ai(TickRecord r) => r.Combatant(r.AiId);

        /// <summary>Default table with punch_left replaced by the given tick windows (its chain delay kept).</summary>
        static CombatStrikeSchedule WithPunch(int windup, int active, int recovery)
        {
            var schedule = CombatStrikeSchedule.Default;
            schedule.PunchLeft = CombatStrikeTiming.FromTicks(windup, active, recovery, CombatAttackTuning.Get(CombatAttackKind.PunchLeft).ChainDelayTicks);
            return schedule;
        }

        static CombatStrikeSchedule Table(ReplayWorld w)
        {
            using var query = w.Manager.CreateEntityQuery(typeof(CombatStrikeSchedule));
            return query.GetSingleton<CombatStrikeSchedule>();
        }

        [Test]
        public void TimingQuantizationNeverSkipsActiveAndKeepsExactTickMultiples()
        {
            const double dt = 1.0 / SimulationTiming.TickRateHz;
            Assert.That(CombatStrikeTiming.TryFromSeconds(13 * dt, 18 * dt, 29 * dt, 4, out var exact, out var error), Is.True, error);
            Assert.That((exact.WindupTicks, exact.ActiveTicks, exact.RecoveryTicks, (int)exact.ChainPoint), Is.EqualTo((13, 5, 11, 22)),
                "Boundaries on exact tick multiples land on those ticks despite double rounding.");
            Assert.That(exact.Equals(CombatAttackTuning.Get(CombatAttackKind.PunchLeft).DefaultTiming), Is.True);

            // Active [1.2, 1.26) ticks lies inside tick 1: that tick is Active, Preparation keeps tick 0.
            Assert.That(CombatStrikeTiming.TryFromSeconds(1.2 * dt, 1.26 * dt, 3 * dt, 4, out var tiny, out error), Is.True, error);
            Assert.That(((int)tiny.ActiveStart, (int)tiny.RecoveryStart, (int)tiny.End), Is.EqualTo((1, 2, 3)));
            Assert.That(tiny.PhaseAt(0), Is.EqualTo(CombatAttackPhase.Windup));
            Assert.That(tiny.PhaseAt(1), Is.EqualTo(CombatAttackPhase.Active));
            Assert.That(tiny.PhaseAt(2), Is.EqualTo(CombatAttackPhase.Recovery));
            Assert.That(tiny.PhaseAt(3), Is.EqualTo(CombatAttackPhase.None));

            // All three boundaries crossed inside tick 0: the one tick is Active, the strike lasts that tick.
            Assert.That(CombatStrikeTiming.TryFromSeconds(0.1 * dt, 0.2 * dt, 0.3 * dt, 4, out var crossed, out error), Is.True, error);
            Assert.That(((int)crossed.ActiveStart, (int)crossed.RecoveryStart, (int)crossed.End), Is.EqualTo((0, 1, 1)));
            Assert.That(crossed.IsValid, Is.True);

            // A chain delay longer than Recovery chains on the first tick after it, so a short Recovery keeps the series.
            var shortRecovery = CombatStrikeTiming.FromTicks(3, 2, 1, 4);
            Assert.That((int)shortRecovery.ChainPoint, Is.EqualTo(shortRecovery.End));
            Assert.That(CombatStrikeTiming.FromTicks(3, 2, 1, CombatAttackTuning.NoChain).CanChain, Is.False);

            foreach (var (active, recovery, duration) in new[] { (-0.1, 0.2, 0.3), (0.2, 0.2, 0.3), (0.1, 0.3, 0.3), (double.NaN, 0.2, 0.3), (0.1, 0.2, 2000.0) })
                Assert.That(CombatStrikeTiming.TryFromSeconds(active, recovery, duration, 4, out _, out error), Is.False, $"{active}/{recovery}/{duration}");
        }

        [Test]
        public void PublishValidatesTheTableAndOnlyStrikesStartedAfterwardsUseIt()
        {
            using var w = new ReplayWorld("publish");
            Assert.That(Table(w).Revision, Is.Zero);
            Assert.That(Table(w).IsValid, Is.True);
            Assert.That(Table(w).PunchLeft.Equals(CombatAttackTuning.Get(CombatAttackKind.PunchLeft).DefaultTiming), Is.True);

            var broken = CombatStrikeSchedule.Default;
            broken.KickSpin = CombatStrikeTiming.FromTicks(5, 0, 5, CombatAttackTuning.NoChain);
            Assert.Throws<ArgumentException>(() => CombatStrikeSchedule.Publish(w.Manager, broken), "Empty Active is rejected.");
            broken = CombatStrikeSchedule.Default;
            broken.KickSpin = CombatStrikeTiming.FromTicks(5, 1, 5, 2);
            Assert.Throws<ArgumentException>(() => CombatStrikeSchedule.Publish(w.Manager, broken), "The finisher never chains.");
            Assert.That(Table(w).Revision, Is.Zero, "A rejected publish changes nothing.");

            var first = CombatAttackTuning.Get(CombatAttackKind.PunchLeft).DefaultTiming;
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: A).Player(40, 40, ReplayRole.Human, pressed: A);
            w.Request(BattleRequest.Start(Duel(5f)));
            w.Step(5);
            Assert.That(CombatStrikeSchedule.Publish(w.Manager, WithPunch(3, 2, 4)), Is.EqualTo(1u));
            w.Step(45);
            var records = w.BattleRecords(1);
            Assert.That(records.Take(first.End).All(r => H(r).Attack.Timing.Equals(first)), Is.True,
                "The strike started before the publish keeps its windows to the end, however the table changes mid-strike.");
            Assert.That(H(records[first.End]).Attack.IsAttacking, Is.False, "…and ends exactly when its own timing ends.");
            var second = H(records[39]).Attack;
            Assert.That(second.Kind, Is.EqualTo(CombatAttackKind.PunchLeft));
            Assert.That(second.Timing.Equals(WithPunch(3, 2, 4).PunchLeft), Is.True, "The next strike copies the new table.");
            Assert.That(H(records[39 + 9]).Attack.IsAttacking, Is.False, "3 + 2 + 4 ticks.");
        }

        [TestCase(0, 1, 1)]
        [TestCase(4, 1, 6)]
        [TestCase(2, 8, 3)]
        [TestCase(10, 2, 1)]
        public void DamageResolvesOnlyInActiveAndOncePerTargetForAnyPublishedWindows(int windup, int active, int recovery)
        {
            using var w = new ReplayWorld("windows");
            CombatStrikeSchedule.Publish(w.Manager, WithPunch(windup, active, recovery));
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: A);
            w.Request(BattleRequest.Start(Duel(0.5f)));
            w.Step(windup + active + recovery + 5);
            var records = w.BattleRecords(1);
            var timing = WithPunch(windup, active, recovery).PunchLeft;
            var damage = CombatAttackTuning.Get(CombatAttackKind.PunchLeft).Damage;
            for (var e = 0; e < records.Length; e++)
            {
                var phase = timing.PhaseAt((uint)e);
                var hp = Ai(records[e]).Health.Current;
                var expected = phase == CombatAttackPhase.Windup ? CombatAttackTuning.MaxHealth : CombatAttackTuning.MaxHealth - damage;
                Assert.That(hp, Is.EqualTo(expected), $"strike tick {e} ({phase}): no damage before Active, exactly one hit from its first tick on");
                Assert.That(H(records[e]).Hits.Length, Is.EqualTo(phase == CombatAttackPhase.Windup ? 0 : 1), $"tick {e}: ledger");
            }

            Assert.That(Ai(records.Last()).Health.Current, Is.EqualTo(CombatAttackTuning.MaxHealth - damage),
                "Hit exactly once, however many Active ticks overlapped it.");
        }

        [Test]
        public void PreparationMovementFollowsTheNormalRulesAndTheStrikeKeepsDrawingInsteadOfRun()
        {
            using var w = new ReplayWorld("preparation move");
            CombatStrikeSchedule.Publish(w.Manager, WithPunch(10, 3, 5));
            var script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: 1, moveZ: 1, pressed: A);
            for (uint r = 2; r <= 25; r++)
                script.Player(r, r, ReplayRole.Human, moveX: -1, moveZ: 1, pressed: r == 4 ? CommandButtons.Jump : CommandButtons.None);
            w.Script = script;
            w.Request(BattleRequest.Start(Duel(5f)));
            w.Step(25);
            var records = w.BattleRecords(1);
            var step = CombatMovementTuning.MoveSpeed * SimulationTiming.TickDuration * 0.7071067811865475f; // diagonal, as when free
            var set = CombatAnimationFixture.Set();
            for (var e = 0; e < 18; e++)
            {
                var c = H(records[e]);
                var moved = Math.Min(e + 1, 10);
                Assert.That(c.Position.z, Is.EqualTo(moved * step).Within(1e-4f), $"tick {e}: Z moves only in Preparation");
                Assert.That(c.Attack.Kind, Is.EqualTo(CombatAttackKind.PunchLeft), $"tick {e}: moving never cancels the strike");
                Assert.That(c.Motion.Grounded, Is.True, "No takeoff during the strike.");
                Assert.That(c.Facing, Is.EqualTo(FacingDirection.Right), "Facing stays locked after the first tick (backpedal, no turn).");
                var frame = set.FrameFor(records[e].Tick, records[e].BattleStartTick, c.Motion, c.Attack);
                var phase = c.Attack.Timing.PhaseAt((uint)e);
                var expected = phase == CombatAttackPhase.Windup ? CombatAnimationFixture.TechA
                    : phase == CombatAttackPhase.Active ? CombatAnimationFixture.TechC : CombatAnimationFixture.TechB;
                Assert.That(frame, Is.EqualTo(expected), $"tick {e} ({phase}): the strike clip plays, not run");
            }

            Assert.That(H(records[0]).Position.x, Is.EqualTo(step).Within(1e-4f), "The first tick moves with its own command.");
            Assert.That(H(records[9]).Position.x, Is.EqualTo(-8 * step).Within(1e-4f), "Backpedalling in Preparation.");
        }

        [Test]
        public void AShortRecoveryStillChainsAtItsEndAndTheChainPointIsNeverLost()
        {
            using var w = new ReplayWorld("short recovery chain");
            CombatStrikeSchedule.Publish(w.Manager, WithPunch(3, 2, 1)); // ChainDelay 4 > Recovery 1
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: A).Player(4, 4, ReplayRole.Human, pressed: A);
            w.Request(BattleRequest.Start(Duel(5f)));
            w.Step(12);
            var records = w.BattleRecords(1);
            Assert.That(H(records[6]).Attack.Kind, Is.EqualTo(CombatAttackKind.PunchRight), "Follow-up on the strike's End tick (r7).");
            Assert.That(H(records[6]).Attack.StartTick - records[6].BattleStartTick, Is.EqualTo(7u));
            Assert.That(H(records[6]).Attack.SeriesLength, Is.EqualTo(2));
        }

        [TestCase(CombatAttackPhase.Windup)]
        [TestCase(CombatAttackPhase.Active)]
        [TestCase(CombatAttackPhase.Recovery)]
        public void PauseResumeRestartAndDespawnInEveryPhase(CombatAttackPhase phase)
        {
            var schedule = WithPunch(4, 4, 4);
            var timing = schedule.PunchLeft;
            var steps = phase == CombatAttackPhase.Windup ? 2 : phase == CombatAttackPhase.Active ? 6 : 10; // strike ticks 1 / 5 / 9
            var damage = CombatAttackTuning.Get(CombatAttackKind.PunchLeft).Damage;
            var hitBefore = phase != CombatAttackPhase.Windup;

            using (var w = new ReplayWorld("pause " + phase))
            {
                CombatStrikeSchedule.Publish(w.Manager, schedule);
                w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: A);
                w.Request(BattleRequest.Start(Duel(0.5f)));
                w.Step(steps);
                var paused = H(w.Records.Last()).Attack;
                Assert.That(timing.PhaseAt(w.Tick - paused.StartTick), Is.EqualTo(phase));
                for (var i = 0; i < 5; i++)
                {
                    w.World.SetTime(new TimeData(w.World.Time.ElapsedTime, 0f));
                    w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
                    w.FixedStep.Update();
                }
                Assert.That(w.BattleRecords(1).Length, Is.EqualTo(steps), "No tick while paused: the phase does not advance.");
                CombatStrikeSchedule.Publish(w.Manager, WithPunch(1, 1, 1)); // an edit while paused
                w.Step(timing.End);
                var records = w.BattleRecords(1);
                Assert.That(records.Skip(steps).TakeWhile(r => H(r).Attack.IsAttacking).All(r => H(r).Attack.Timing.Equals(timing)), Is.True,
                    "Resume continues the same strike with its own windows.");
                Assert.That(records.Count(r => H(r).Attack.IsAttacking), Is.EqualTo(timing.End), "Exactly its length across the pause.");
                Assert.That(Ai(records.Last()).Health.Current, Is.EqualTo(CombatAttackTuning.MaxHealth - damage), "One hit across the pause.");
            }

            using (var w = new ReplayWorld("restart " + phase))
            {
                CombatStrikeSchedule.Publish(w.Manager, schedule);
                w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: A);
                w.Request(BattleRequest.Start(Duel(0.5f)));
                w.Step(steps);
                w.Request(BattleRequest.Restart(Duel(0.5f)));
                w.Script = new ReplayScript();
                w.Step(20);
                Assert.That(w.BattleRecords(2).All(r => r.Combatants.All(c => !c.Attack.IsAttacking && c.Hits.Length == 0
                    && c.Health.Current == CombatAttackTuning.MaxHealth)), Is.True, "Restart drops the strike in any phase.");
            }

            using (var w = new ReplayWorld("despawn attacker " + phase))
            {
                CombatStrikeSchedule.Publish(w.Manager, schedule);
                w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: A);
                w.Request(BattleRequest.Start(Duel(0.5f)));
                w.Step(steps);
                w.Request(BattleRequest.Despawn(w.Session.HumanCombatant));
                w.Step(20);
                Assert.That(Ai(w.Records.Last()).Health.Current, Is.EqualTo(CombatAttackTuning.MaxHealth - (hitBefore ? damage : 0)),
                    "A despawned attacker lands nothing more.");
            }

            using (var w = new ReplayWorld("despawn target " + phase))
            {
                CombatStrikeSchedule.Publish(w.Manager, schedule);
                w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: A);
                w.Request(BattleRequest.Start(Duel(0.5f)));
                w.Step(steps);
                w.Request(BattleRequest.Despawn(w.Session.AiCombatant));
                w.Step(20);
                Assert.That(w.Records.Last().Combatants.Length, Is.EqualTo(1));
                Assert.That(H(w.Records.Last()).Attack.IsAttacking, Is.False, "The attacker finishes its strike normally.");
            }
        }

        [TestCase(2)]
        [TestCase(3)]
        [TestCase(7)]
        public void PublishedWindowsGiveTheSameBattleAtAnyRenderPacing(int ticksPerFrame)
        {
            using var single = new ReplayWorld("paced single");
            using var grouped = new ReplayWorld("paced grouped");
            var script = new ReplayScript();
            for (uint r = 1; r <= 84; r++)
                script.Player(r, r, ReplayRole.Human, moveX: (sbyte)(r % 30 < 15 ? 1 : -1), pressed: r % 9 == 1 ? A : CommandButtons.None);
            foreach (var w in new[] { single, grouped })
            {
                CombatStrikeSchedule.Publish(w.Manager, WithPunch(6, 1, 2));
                w.Script = script;
                w.Request(BattleRequest.Start(Duel(0.7f)));
            }

            single.Step(84);
            for (var i = 0; i < 84 / ticksPerFrame; i++)
                grouped.Frame(ticksPerFrame);
            Assert.That(grouped.Tick, Is.EqualTo(single.Tick), "84 ticks at every tested pacing.");
            Assert.That(grouped.RawLog, Is.EqualTo(single.RawLog), "Render pacing never changes a strike, a hit or HP.");
            Assert.That(single.Records.Any(r => Ai(r).Health.Current < CombatAttackTuning.MaxHealth), Is.True, "The script lands hits.");
        }
    }
}
