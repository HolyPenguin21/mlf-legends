using System.Collections.Generic;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;

namespace MLFLegends.Tests.EditMode
{
    // Stage 05.1: the five strikes through the real runtime chain (queue → processor → CombatActionSystem → facing →
    // movement → CombatHitSystem) in an isolated replay World. Expectations are written from CombatAttackTuning's numbers
    // and the documented rules (COMBAT_SPEC.md), never from presentation.
    public class CombatAttackTests
    {
        const CommandButtons A = CommandButtons.Attack;
        const CommandButtons K = CommandButtons.Kick;

        static BattleSetup Duel(float3 ai, FacingDirection human = FacingDirection.Right, FacingDirection aiFacing = FacingDirection.Left) => new BattleSetup
        {
            Human = new CombatantSetup { Team = 1, Position = float3.zero, Facing = human },
            Ai = new CombatantSetup { Team = 2, Position = ai, Facing = aiFacing },
        };

        static CombatAttackDefinition Def(CombatAttackKind kind) => CombatAttackTuning.Get(kind);

        static uint Rel(TickRecord r, uint absolute) => absolute - r.BattleStartTick;

        static CombatantRecord H(TickRecord r) => r.Combatant(r.HumanId);

        static CombatantRecord Ai(TickRecord r) => r.Combatant(r.AiId);

        static TickRecord[] Play(BattleSetup setup, ReplayScript script, int ticks)
        {
            using var w = new ReplayWorld("strikes");
            w.Script = script;
            w.Request(BattleRequest.Start(setup));
            w.Step(ticks);
            return w.BattleRecords(1);
        }

        /// <summary>Strikes the human performs, as (relative start tick, kind), read from the recorded state.</summary>
        static List<(uint start, CombatAttackKind kind)> Strikes(TickRecord[] records, System.Func<TickRecord, CombatantRecord> who)
        {
            var result = new List<(uint, CombatAttackKind)>();
            foreach (var r in records)
            {
                var a = who(r).Attack;
                if (a.IsAttacking && Rel(r, a.StartTick) == r.Tick - r.BattleStartTick)
                    result.Add((Rel(r, a.StartTick), a.Kind));
            }

            return result;
        }

        /// <summary>
        /// Presses a series: the first button on r1, every next one on the first Active tick of the strike before it, so it is
        /// buffered and starts exactly at that strike's chain point. Returns the expected (start, kind) list.
        /// </summary>
        static List<(uint start, CombatAttackKind kind)> Series(ReplayScript script, string buttons, CommandSource source = CommandSource.Player,
            ReplayRole role = ReplayRole.Human)
        {
            var expected = new List<(uint, CombatAttackKind)>();
            uint start = 1;
            var previous = CombatAttackKind.None;
            for (var i = 0; i < buttons.Length; i++)
            {
                var button = buttons[i] == 'A' ? A : K;
                var kind = CombatAttackTuning.Select(button, previous);
                if (i > 0)
                {
                    var before = Def(previous);
                    var press = start + before.WindupTicks;
                    start += (uint)(before.WindupTicks + before.ActiveTicks + before.ChainDelayTicks);
                    Press(script, press, button, source, role: role);
                }
                else
                    Press(script, start, button, source, role: role);

                expected.Add((start, kind));
                previous = kind;
            }

            return expected;
        }

        static void Press(ReplayScript script, uint tick, CommandButtons button, CommandSource source = CommandSource.Player,
            sbyte moveX = 0, ReplayRole role = ReplayRole.Human)
        {
            if (source == CommandSource.Player)
                script.Player(tick, tick, role, moveX: moveX, pressed: button);
            else
                script.Ai(tick, tick, role, moveX: moveX, pressed: button);
        }

        [TestCase("A", CombatAttackKind.PunchLeft)]
        [TestCase("AA", CombatAttackKind.PunchRight)]
        [TestCase("K", CombatAttackKind.KickLeft)]
        [TestCase("KK", CombatAttackKind.KickRight)]
        [TestCase("KKK", CombatAttackKind.KickSpin)]
        public void EveryStrikeRunsWindupActiveRecoveryAndDamagesItsTargetOncePerStrike(string buttons, CombatAttackKind last)
        {
            var script = new ReplayScript();
            var expected = Series(script, buttons);
            Assert.That(expected.Last().kind, Is.EqualTo(last));
            var end = expected.Last().start + (uint)Def(last).TotalTicks;
            var records = Play(Duel(new float3(0.9f, 0f, 0f)), script, (int)end + 5);

            Assert.That(Strikes(records, H), Is.EqualTo(expected), "Each strike starts on its tick with its kind (sides alternate, Kick after kick_right spins).");
            var hp = CombatAttackTuning.MaxHealth;
            for (var j = 0; j < expected.Count; j++)
            {
                var (start, kind) = expected[j];
                var strike = Def(kind);
                var stop = j + 1 < expected.Count ? expected[j + 1].start : end;
                for (var t = start; t < stop; t++)
                {
                    var r = records[t - 1];
                    var phase = CombatAttackTuning.PhaseAt(strike, t - start);
                    Assert.That(H(r).Attack.Kind, Is.EqualTo(kind), $"r{t}");
                    Assert.That(phase, Is.Not.EqualTo(CombatAttackPhase.None), $"r{t}: a chained strike never outlives its own recovery");
                    if (t == start + strike.WindupTicks)
                        hp -= strike.Damage;
                    Assert.That(Ai(r).Health.Current, Is.EqualTo(hp), $"r{t} {phase}: damage only on the first Active tick, never again in this strike");
                    Assert.That(H(r).Hits, Is.EqualTo(phase == CombatAttackPhase.Windup ? new uint[0] : new[] { r.AiId }), $"r{t} ledger");
                }
            }

            var free = records[end - 1];
            Assert.That(H(free).Attack.Kind, Is.EqualTo(CombatAttackKind.None), "Recovery ran out: free on this tick.");
            Assert.That(Rel(free, H(free).Attack.EndTick), Is.EqualTo(end));
            Assert.That(H(free).Attack.SeriesLength, Is.Zero);
            Assert.That(Ai(records.Last()).Health.Current, Is.EqualTo(CombatAttackTuning.MaxHealth - expected.Sum(e => Def(e.kind).Damage)));
            Assert.That(H(records.Last()).Health.Current, Is.EqualTo(CombatAttackTuning.MaxHealth), "Nobody hit the attacker.");
        }

        [TestCase(1.0f, 0f, 0f, true, TestName = "Reach max inclusive")]
        [TestCase(1.01f, 0f, 0f, false, TestName = "Beyond reach")]
        [TestCase(0f, 0f, 0f, true, TestName = "Overlapping position")]
        [TestCase(-0.05f, 0f, 0f, false, TestName = "Behind the attacker")]
        [TestCase(0.5f, 0.4f, 0f, true, TestName = "Depth limit inclusive")]
        [TestCase(0.5f, -0.41f, 0f, false, TestName = "Beyond depth")]
        [TestCase(0.5f, 0f, 0.9f, true, TestName = "Height limit inclusive")]
        [TestCase(0.5f, 0f, 0.95f, false, TestName = "Above height tolerance")]
        public void PunchBoxUsesFacingReachDepthAndHeight(float x, float z, float y, bool hit)
        {
            var script = new ReplayScript();
            Press(script, 1, A);
            var records = Play(Duel(new float3(x, y, z)), script, 40);
            Assert.That(Ai(records.Last()).Health.Current, Is.EqualTo(hit ? CombatAttackTuning.MaxHealth - Def(CombatAttackKind.PunchLeft).Damage : CombatAttackTuning.MaxHealth));
            Assert.That(records.Any(r => H(r).Hits.Length > 0), Is.EqualTo(hit));
        }

        [Test]
        public void TheBoxFollowsFacingAndTurningIsAllowedOnlyOnAStrikesFirstTick()
        {
            var script = new ReplayScript();
            // r1: Attack while holding left: turn, then strike to the left. r2..: holding right never turns mid-strike.
            Press(script, 1, A, moveX: -1);
            for (uint r = 2; r <= 40; r++)
                script.Player(r, r, ReplayRole.Human, moveX: 1);
            var records = Play(Duel(new float3(-0.8f, 0f, 0f)), script, 40);
            var end = 1 + Def(CombatAttackKind.PunchLeft).TotalTicks;
            for (var t = 1; t < end; t++)
                Assert.That(H(records[t - 1]).Facing, Is.EqualTo(FacingDirection.Left), $"r{t}: locked");
            Assert.That(H(records[end - 1]).Facing, Is.EqualTo(FacingDirection.Right), "Free again: MoveX turns.");
            Assert.That(Ai(records.Last()).Health.Current, Is.LessThan(CombatAttackTuning.MaxHealth), "Target behind the spawn facing was hit after the turn.");
        }

        [Test]
        public void StrikesBlockMovementAndJumpWhileAirbornePressesStartNothing()
        {
            var script = new ReplayScript();
            Press(script, 1, A, moveX: 1);
            for (uint r = 2; r <= 50; r++)
                script.Player(r, r, ReplayRole.Human, moveX: 1, pressed: r == 5 ? CommandButtons.Jump : CommandButtons.None);
            var records = Play(Duel(new float3(5f, 0f, 0f)), script, 50);
            var end = 1 + Def(CombatAttackKind.PunchLeft).TotalTicks;
            for (var t = 1; t < end; t++)
            {
                Assert.That(H(records[t - 1]).Position, Is.EqualTo(float3.zero), $"r{t}: no planar move, no takeoff during the strike");
                Assert.That(H(records[t - 1]).Motion.Grounded, Is.True);
            }
            Assert.That(H(records[end - 1]).Position.x, Is.GreaterThan(0f), "Movement resumes on the first free tick.");

            script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: CommandButtons.Jump).Player(5, 5, ReplayRole.Human, pressed: A);
            records = Play(Duel(new float3(0.5f, 0f, 0f)), script, 80);
            Assert.That(records.All(r => !H(r).Attack.IsAttacking), Is.True, "An airborne press is dropped, not buffered until landing.");
            Assert.That(records.All(r => Ai(r).Health.Current == CombatAttackTuning.MaxHealth), Is.True);

            script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: A | CommandButtons.Jump);
            records = Play(Duel(new float3(0.5f, 0f, 0f)), script, 10);
            Assert.That(H(records[0]).Motion.Grounded, Is.False, "Jump + strike on one grounded tick: the jump wins.");
            Assert.That(records.All(r => !H(r).Attack.IsAttacking), Is.True, "…and the strike press is dropped.");
        }

        [TestCase("A", new[] { CombatAttackKind.PunchLeft, CombatAttackKind.PunchRight, CombatAttackKind.PunchLeft, CombatAttackKind.PunchRight })]
        [TestCase("K", new[] { CombatAttackKind.KickLeft, CombatAttackKind.KickRight, CombatAttackKind.KickSpin })]
        public void MashingChainsAtTheChainPointsUpToTheSeriesEndThenStartsAFreshSeries(string button, CombatAttackKind[] series)
        {
            var b = button == "A" ? A : K;
            var script = new ReplayScript();
            for (uint r = 1; r <= 200; r++)
                Press(script, r, b);
            var records = Play(Duel(new float3(0.9f, 0f, 0f)), script, 200);
            var strikes = Strikes(records, H);
            uint start = 1;
            for (var i = 0; i < series.Length; i++)
            {
                Assert.That(strikes[i], Is.EqualTo((start, series[i])), $"strike {i}: windup presses ignored, buffered press waits for the chain point");
                var d = Def(series[i]);
                start += i + 1 < series.Length ? (uint)(d.WindupTicks + d.ActiveTicks + d.ChainDelayTicks) : (uint)d.TotalTicks;
            }

            Assert.That(strikes[series.Length], Is.EqualTo((start, series[0])),
                "The series end plays the full recovery; a press on the first free tick starts a new series on the left.");
            Assert.That(records.Max(r => H(r).Attack.SeriesLength), Is.EqualTo(series.Length));
        }

        [Test]
        public void MixedSeriesAlternateSidesAndKickAfterTheRightKickSpins()
        {
            var script = new ReplayScript();
            var expected = Series(script, "AKK");
            var records = Play(Duel(new float3(0.9f, 0f, 0f)), script, 120);
            Assert.That(expected.Select(e => e.kind), Is.EqualTo(new[] { CombatAttackKind.PunchLeft, CombatAttackKind.KickRight, CombatAttackKind.KickSpin }));
            Assert.That(Strikes(records, H), Is.EqualTo(expected));

            script = new ReplayScript();
            expected = Series(script, "KAA");
            records = Play(Duel(new float3(0.9f, 0f, 0f)), script, 120);
            Assert.That(expected.Select(e => e.kind), Is.EqualTo(new[] { CombatAttackKind.KickLeft, CombatAttackKind.PunchRight, CombatAttackKind.PunchLeft }));
            Assert.That(Strikes(records, H), Is.EqualTo(expected));
        }

        [Test]
        public void ForbiddenTransitionsNeverSkipRecovery()
        {
            // A windup press is dropped; a press after the finisher's / full series' chain point is dropped at its end.
            var punch = Def(CombatAttackKind.PunchLeft);
            var script = new ReplayScript();
            Press(script, 1, A);
            Press(script, 5, A);
            var records = Play(Duel(new float3(0.9f, 0f, 0f)), script, 80);
            Assert.That(Strikes(records, H), Is.EqualTo(new[] { (1u, CombatAttackKind.PunchLeft) }));
            Assert.That(Rel(records.Last(), H(records.Last()).Attack.EndTick), Is.EqualTo(1 + punch.TotalTicks));

            script = new ReplayScript();
            var expected = Series(script, "KKK");
            var spin = expected.Last();
            Press(script, spin.start + Def(CombatAttackKind.KickSpin).WindupTicks + 1, K);
            Press(script, spin.start + (uint)Def(CombatAttackKind.KickSpin).TotalTicks - 1, A);
            records = Play(Duel(new float3(0.9f, 0f, 0f)), script, 160);
            Assert.That(Strikes(records, H), Is.EqualTo(expected), "The spin finisher plays its whole recovery and drops its buffer.");

            script = new ReplayScript();
            expected = Series(script, "AAAA");
            var fourth = expected.Last();
            Press(script, fourth.start + Def(fourth.kind).WindupTicks + 2, A);
            records = Play(Duel(new float3(0.9f, 0f, 0f)), script, 160);
            Assert.That(Strikes(records, H), Is.EqualTo(expected), "The fourth strike ends the series.");
            Assert.That(records.Count(r => H(r).Attack.IsAttacking), Is.EqualTo(fourth.start - 1 + Def(fourth.kind).TotalTicks));
        }

        [Test]
        public void ABufferedPressLateInRecoveryChainsOnItsOwnTickAndHoldNeverRepeats()
        {
            var punch = Def(CombatAttackKind.PunchLeft);
            var late = 1u + punch.WindupTicks + punch.ActiveTicks + punch.ChainDelayTicks + 3;
            var script = new ReplayScript();
            Press(script, 1, A);
            Press(script, late, A);
            Assert.That(Strikes(Play(Duel(new float3(0.9f, 0f, 0f)), script, 80), H),
                Is.EqualTo(new[] { (1u, CombatAttackKind.PunchLeft), (late, CombatAttackKind.PunchRight) }));

            script = new ReplayScript().Player(1, 1, ReplayRole.Human, held: A, pressed: A);
            for (uint r = 2; r <= 120; r++)
                script.Player(r, r, ReplayRole.Human, held: A);
            var records = Play(Duel(new float3(0.9f, 0f, 0f)), script, 120);
            Assert.That(Strikes(records, H), Is.EqualTo(new[] { (1u, CombatAttackKind.PunchLeft) }), "Holding the button is one strike, no queue.");
            Assert.That(Ai(records.Last()).Health.Current, Is.EqualTo(CombatAttackTuning.MaxHealth - punch.Damage));
        }

        [Test]
        public void AlliesAndSelfAreNeverHitAndEveryValidEnemyInTheBoxIsHitOnce()
        {
            using var w = new ReplayWorld("strike targets");
            var setup = Duel(new float3(0.8f, 0f, 0f));
            setup.HumanAllies = 1;
            setup.AiAllies = 2;
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: A);
            w.Request(BattleRequest.Start(setup));
            w.World.GetExistingSystemManaged<Unity.Entities.InitializationSystemGroup>().Update();
            var session = w.Session;
            var first = session.HumanCombatant.Value;
            // IDs: human, AI, human ally, AI allies. Same spawn support (y 0), positions moved before the first tick.
            var places = new Dictionary<uint, float3>
            {
                [first + 2] = new float3(0.5f, 0f, 0f), // ally inside the box
                [first + 3] = new float3(0.6f, 0f, 0.3f), // enemy inside the box
                [first + 4] = new float3(3f, 0f, 0f), // enemy outside
            };
            using (var query = w.Manager.CreateEntityQuery(typeof(CombatantId), typeof(CombatPosition)))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities)
                    if (places.TryGetValue(w.Manager.GetComponentData<CombatantId>(entity).Value, out var place))
                        w.Manager.SetComponentData(entity, new CombatPosition { Value = place });
            w.Step(40);
            var last = w.Records.Last();
            var damaged = CombatAttackTuning.MaxHealth - Def(CombatAttackKind.PunchLeft).Damage;
            Assert.That(last.Combatants.Select(c => (c.Id, c.Health.Current)), Is.EquivalentTo(new[]
            {
                (first, CombatAttackTuning.MaxHealth), (first + 1, damaged), (first + 2, CombatAttackTuning.MaxHealth),
                (first + 3, damaged), (first + 4, CombatAttackTuning.MaxHealth),
            }));
            Assert.That(w.Records.Select(r => r.Combatant(first).Hits).Where(h => h.Length > 0).Distinct(new SequenceComparer()).Single(),
                Is.EqualTo(new[] { first + 1, first + 3 }), "Ledger in CombatantId order, each target once.");
        }

        sealed class SequenceComparer : IEqualityComparer<uint[]>
        {
            public bool Equals(uint[] a, uint[] b) => a.SequenceEqual(b);
            public int GetHashCode(uint[] a) => a.Length;
        }

        [Test]
        public void PlayerAndAiStrikesFollowIdenticalRules()
        {
            var script = new ReplayScript();
            var human = Series(script, "AKK");
            var ai = Series(script, "AKK", CommandSource.AI, ReplayRole.Ai);
            var records = Play(Duel(new float3(0.9f, 0f, 0f)), script, 140);
            Assert.That(Strikes(records, H), Is.EqualTo(human));
            Assert.That(Strikes(records, Ai), Is.EqualTo(ai), "Same series, same ticks, other source.");
            Assert.That(Ai(records.Last()).Health.Current, Is.EqualTo(H(records.Last()).Health.Current), "Symmetric damage.");
            Assert.That(H(records.Last()).Health.Current, Is.LessThan(CombatAttackTuning.MaxHealth));
        }

        static ReplayScript BusyScript()
        {
            var script = new ReplayScript();
            for (uint r = 1; r <= 150; r++)
            {
                script.Player(r, r, ReplayRole.Human, moveX: (sbyte)(r % 40 < 20 ? 1 : -1),
                    pressed: r % 13 == 0 ? A : r % 17 == 0 ? K : r % 47 == 0 ? CommandButtons.Jump : CommandButtons.None);
                script.Ai(r, r, ReplayRole.Ai, moveX: (sbyte)(r % 30 < 15 ? -1 : 1), moveZ: (sbyte)(r % 7 - 3 > 0 ? 1 : 0),
                    pressed: r % 11 == 0 ? K : r % 19 == 0 ? A : CommandButtons.None);
            }

            return script;
        }

        [TestCase(2)] [TestCase(3)] [TestCase(10)]
        public void CatchUpFramesGiveTheSameStrikesHitsAndHealthAsSingleTickFrames(int k)
        {
            using var single = new ReplayWorld("strikes single");
            using var grouped = new ReplayWorld("strikes catch-up");
            single.Script = grouped.Script = BusyScript();
            var setup = Duel(new float3(0.7f, 0f, 0f));
            single.Request(BattleRequest.Start(setup));
            grouped.Request(BattleRequest.Start(setup));
            single.Step(150);
            for (var i = 0; i < 150 / k; i++)
                grouped.Frame(k);
            Assert.That(grouped.Tick, Is.EqualTo(single.Tick));
            Assert.That(grouped.RawLog, Is.EqualTo(single.RawLog), "Render frame pacing never changes a strike, hit or HP.");
            Assert.That(single.Records.Any(r => r.Combatants.Any(c => c.Health.Current < c.Health.Max)), Is.True, "The script lands hits.");
        }

        [Test]
        public void PausedFramesFreezeStrikesAndRestartDropsThemWithTheirBuffer()
        {
            using var w = new ReplayWorld("strike lifecycle");
            var punch = Def(CombatAttackKind.PunchLeft);
            // r1 strike; a buffered press at the first Active tick would chain at r(1+W+A+chain).
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: A).Player(1u + punch.WindupTicks, 1u + punch.WindupTicks, ReplayRole.Human, pressed: A);
            w.Request(BattleRequest.Start(Duel(new float3(0.9f, 0f, 0f))));
            w.Step(punch.WindupTicks + 1);
            var before = w.Records.Last();
            Assert.That(H(before).Attack.Buffered, Is.EqualTo(A));
            for (var i = 0; i < 5; i++)
            {
                w.World.SetTime(new TimeData(w.World.Time.ElapsedTime, 0f));
                w.World.GetExistingSystemManaged<Unity.Entities.InitializationSystemGroup>().Update();
                w.FixedStep.Update();
            }
            Assert.That(w.Records.Count, Is.EqualTo(punch.WindupTicks + 1), "No tick while paused.");

            w.Request(BattleRequest.Restart(Duel(new float3(0.9f, 0f, 0f))));
            w.Script = new ReplayScript();
            w.Step(60);
            var fresh = w.BattleRecords(2);
            Assert.That(fresh.All(r => r.Combatants.All(c => !c.Attack.IsAttacking && c.Attack.Buffered == CommandButtons.None
                && c.Health.Current == CombatAttackTuning.MaxHealth && c.Hits.Length == 0)), Is.True,
                "A restarted battle has no strike, buffer, ledger or damage from the previous one.");
        }

        [Test]
        public void DespawnedTargetsAndAttackersLeaveNoPendingHits()
        {
            var punch = Def(CombatAttackKind.PunchLeft);
            using (var w = new ReplayWorld("despawn target"))
            {
                w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: A);
                w.Request(BattleRequest.Start(Duel(new float3(0.9f, 0f, 0f))));
                w.Step(punch.WindupTicks - 1);
                w.Request(BattleRequest.Despawn(w.Session.AiCombatant));
                w.Step(punch.TotalTicks);
                Assert.That(w.Records.Last().Combatants.Length, Is.EqualTo(1));
                Assert.That(w.Records.All(r => H(r).Hits.Length == 0), Is.True, "A target gone before the Active window is never hit.");
                Assert.That(H(w.Records.Last()).Attack.IsAttacking, Is.False, "The attacker finished normally.");
            }

            using (var w = new ReplayWorld("despawn attacker"))
            {
                w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: A);
                w.Request(BattleRequest.Start(Duel(new float3(0.9f, 0f, 0f))));
                w.Step(punch.WindupTicks - 1);
                w.Request(BattleRequest.Despawn(w.Session.HumanCombatant));
                w.Step(punch.TotalTicks);
                Assert.That(Ai(w.Records.Last()).Health.Current, Is.EqualTo(CombatAttackTuning.MaxHealth), "No strike of a despawned attacker lands.");
            }
        }

        [Test]
        public void AnOutOfCombatTargetIsNeverHitAndAnOutOfCombatAttackerNeverStrikes()
        {
            var punch = Def(CombatAttackKind.PunchLeft);
            using (var w = new ReplayWorld("disabled target"))
            {
                w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: A);
                w.Request(BattleRequest.Start(Duel(new float3(0.9f, 0f, 0f))));
                w.Step(punch.WindupTicks - 1);
                SetInCombat(w, w.Session.AiCombatant, false);
                w.Step(punch.TotalTicks);
                SetInCombat(w, w.Session.AiCombatant, true);
                w.Step();
                Assert.That(Ai(w.Records.Last()).Health.Current, Is.EqualTo(CombatAttackTuning.MaxHealth), "Disabled InCombat is not a valid target.");
                Assert.That(w.Records.All(r => H(r).Hits.Length == 0), Is.True);
            }

            using (var w = new ReplayWorld("disabled attacker"))
            {
                w.Script = new ReplayScript().Player(2, 2, ReplayRole.Human, pressed: A);
                w.Request(BattleRequest.Start(Duel(new float3(0.9f, 0f, 0f))));
                w.Step();
                SetInCombat(w, w.Session.HumanCombatant, false);
                w.Step(punch.TotalTicks);
                Assert.That(Ai(w.Records.Last()).Health.Current, Is.EqualTo(CombatAttackTuning.MaxHealth), "An out-of-combat fighter starts and lands nothing.");
                Assert.That(H(w.Records.Last()).Attack.IsAttacking, Is.False);
            }
        }

        static void SetInCombat(ReplayWorld w, CombatantId id, bool value)
        {
            using var query = w.Manager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { typeof(CombatantId), typeof(InCombat) }, Options = EntityQueryOptions.IgnoreComponentEnabledState,
            });
            using var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
                if (w.Manager.GetComponentData<CombatantId>(entity).Value == id.Value)
                    w.Manager.SetComponentEnabled<InCombat>(entity, value);
        }

        [TestCase(2)] [TestCase(100)] [TestCase(400)]
        public void EveryRosterActorUsesTheSameStrikeAndHitPath(int count)
        {
            using var w = new ReplayWorld("strike roster");
            var setup = Duel(new float3(0.6f, 0f, 0f));
            setup.HumanAllies = setup.AiAllies = (ushort)(count / 2 - 1);
            w.Request(BattleRequest.Start(setup));
            w.World.GetExistingSystemManaged<Unity.Entities.InitializationSystemGroup>().Update();
            var first = w.Session.HumanCombatant.Value;
            var ids = new List<CombatantId>();
            using (var query = w.Manager.CreateEntityQuery(typeof(CombatantId), typeof(CombatTeam)))
            using (var entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (var entity in entities)
                {
                    var id = w.Manager.GetComponentData<CombatantId>(entity);
                    var team = w.Manager.GetComponentData<CombatTeam>(entity).Value;
                    // Index inside the team (leader 0, then its allies): enemies with the same index face each other
                    // 0.6 apart, rows 0.3 apart in depth, so every fighter has enemies within reach and depth.
                    var index = id.Value - first < 2 ? 0 : team == 1 ? id.Value - first - 1 : id.Value - first - 1 - setup.HumanAllies;
                    w.Manager.SetComponentData(entity, new CombatPosition { Value = new float3(team == 1 ? 0f : 0.6f, 0f, index * 0.3f) });
                    ids.Add(id);
                }
            }

            for (uint r = 1; r <= 60; r++)
            {
                foreach (var id in ids)
                    w.SubmitBetweenFrames(new CombatCommand
                    {
                        Combatant = id, Tick = w.Tick + 1, Pressed = r % 5 == 1 ? (id.Value % 2 == 0 ? A : K) : CommandButtons.None,
                        Source = id.Value % 3 == 0 ? CommandSource.Player : CommandSource.AI,
                    });
                w.Step();
            }
            foreach (var r in w.Records)
            foreach (var c in r.Combatants)
            {
                Assert.That(c.Health.Current, Is.InRange(0, CombatAttackTuning.MaxHealth));
                Assert.That(c.Hits.Distinct().Count(), Is.EqualTo(c.Hits.Length), "A ledger never holds a target twice.");
                Assert.That(c.Hits.All(id => r.Combatant(id).Team != c.Team), Is.True, "Only enemies are hit.");
            }
            Assert.That(w.Records.Last().Combatants.Count(c => c.Health.Current < c.Health.Max), Is.EqualTo(count), "Every fighter took hits.");
        }
    }
}
