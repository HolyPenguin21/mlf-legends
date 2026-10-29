using System;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace MLFLegends.Tests.EditMode
{
    // Stage 04.5: Hit reads the final spatial snapshot. Every check compares the real owner with the independent brute-force
    // oracle (CombatHitOracle.cs) on the same final positions, before HP/ledger mutation; the Stage 05.1 suites stay as they are.
    public class CombatIndexedHitTests
    {
        static Entity[] Actors(ReplayWorld w)
        {
            using var q = w.Manager.CreateEntityQuery(typeof(CombatantId));
            using var e = q.ToEntityArray(Allocator.Temp);
            return e.ToArray().OrderBy(x => w.Manager.GetComponentData<CombatantId>(x).Value).ToArray();
        }

        static Entity[] Start(ReplayWorld w, int count)
        {
            var setup = BattleSetup.Default; setup.HumanAllies = setup.AiAllies = (ushort)(count / 2 - 1);
            w.Request(BattleRequest.Start(setup));
            w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            return Actors(w);
        }

        // Flat legacy support at the fighter's own height keeps the requested Y on the final position.
        static void Place(ReplayWorld w, Entity e, float3 p, float radius = .01f, float height = 2, FacingDirection? facing = null)
        {
            w.Manager.SetComponentData(e, new CombatPosition { Value = p });
            w.Manager.SetComponentData(e, new CombatMotion { Grounded = true, GroundHeight = p.y });
            w.Manager.SetComponentData(e, new CombatBody { Radius = radius, Height = height, Mass = 1 });
            if (facing.HasValue) w.Manager.SetComponentData(e, new CombatFacing { Value = facing.Value });
        }

        static void Command(ReplayWorld w, Entity e, sbyte x = 0, CommandButtons pressed = CommandButtons.None) =>
            w.SubmitBetweenFrames(new CombatCommand { Combatant = w.Manager.GetComponentData<CombatantId>(e), Tick = w.Tick + 1, MoveX = x,
                Pressed = pressed, Source = CommandSource.AI });

        static CombatHitDiagnostics LastHits(ReplayWorld w) =>
            w.World.Unmanaged.GetUnsafeSystemRef<CombatHitSystem>(w.World.GetExistingSystem<CombatHitSystem>()).LastHits;

        [TestCase(2, 1.5f, 11)] [TestCase(100, 8f, 12)] [TestCase(100, 2f, 13)] [TestCase(400, 12f, 14)] [TestCase(400, 4f, 15)]
        public void RandomSparseAndDenseLayoutsMatchTheBruteForceOracleBeforeMutation(int count, float spread, int seed)
        {
            using var w = new ReplayWorld("indexed hit oracle"); var e = Start(w, count);
            var oracle = HitOracle.Attach(w);
            var rng = new System.Random(seed);
            float Next() => (float)(rng.NextDouble() * 2 - 1);
            float[] heights = { 0, .5f, .9f, 1.3f };
            // Centred across negative and positive cells, with exact cell boundaries and several height layers.
            foreach (var actor in e)
                Place(w, actor, new float3(-.5f + Next() * spread, heights[rng.Next(heights.Length)], Next() * spread * .5f),
                    facing: rng.Next(2) == 0 ? FacingDirection.Left : FacingDirection.Right);
            var buttons = new[] { CommandButtons.PunchLeft, CommandButtons.PunchRight, CommandButtons.KickLeft, CommandButtons.KickRight,
                CommandButtons.KickSpin, CommandButtons.Attack, CommandButtons.Kick };
            for (var t = 0; t < 90; t++)
            {
                foreach (var actor in e)
                    Command(w, actor, (sbyte)(rng.Next(5) == 0 ? rng.Next(3) - 1 : 0),
                        rng.Next(6) == 0 ? buttons[rng.Next(buttons.Length)] : CommandButtons.None);
                w.Step();
            }
            Assert.That(oracle.Mismatches, Is.Empty);
            Assert.That(oracle.CheckedTicks, Is.EqualTo(90)); Assert.That(oracle.ActiveTicks, Is.GreaterThan(10));
            if (count > 2) Assert.That(oracle.CheckedHits, Is.GreaterThan(0), "The layout produced real accepted hits.");
            var d = LastHits(w);
            Assert.That(d.Queries, Is.EqualTo(d.ActiveAttackers)); Assert.That(d.UsedFullScan, Is.False);
            Assert.That(d.Candidates, Is.LessThanOrEqualTo(d.ActiveAttackers * count));
        }

        [TestCase(FacingDirection.Right, 1f, 0f, true)] [TestCase(FacingDirection.Right, 1.0001f, 0f, false)]
        [TestCase(FacingDirection.Left, -1f, 0f, true)] [TestCase(FacingDirection.Left, -1.0001f, 0f, false)]
        [TestCase(FacingDirection.Left, 0f, .4f, true)] [TestCase(FacingDirection.Left, .05f, 0f, false)]
        [TestCase(FacingDirection.Right, .5f, -.4f, true)] [TestCase(FacingDirection.Right, .5f, -.4001f, false)]
        public void ExactClosedBoundsInNegativeCellsForBothFacings(FacingDirection facing, float dx, float dz, bool hit)
        {
            using var w = new ReplayWorld("indexed hit bounds"); var e = Start(w, 2);
            var origin = new float3(-6f, 0, 0); // dx = ±1 lands exactly on the cell boundaries x=-7 / x=-5; dz=-.4 in cell z=-1.
            Place(w, e[0], origin, facing: facing); Place(w, e[1], origin + new float3(dx, 0, dz));
            var oracle = HitOracle.Attach(w);
            Command(w, e[0], pressed: CommandButtons.PunchLeft);
            w.Step(CombatAttackTuning.Get(CombatAttackKind.PunchLeft).TotalTicks);
            Assert.That(w.Manager.GetComponentData<CombatHealth>(e[1]).Current, Is.EqualTo(hit ? 96 : 100));
            Assert.That(w.Manager.GetBuffer<CombatHitRecord>(e[0]).Length, Is.EqualTo(hit ? 1 : 0));
            Assert.That(oracle.Mismatches, Is.Empty);
        }

        [Test]
        public void LargeBodyIsACandidateButOnlyItsPointTargetDecides()
        {
            using var w = new ReplayWorld("indexed hit radius"); var e = Start(w, 2);
            // Disjoint height intervals (no body contact), |ΔY| .6 within the tolerance, point 1.5 beyond reach 1.0.
            Place(w, e[0], float3.zero, radius: .01f, height: .5f, facing: FacingDirection.Right);
            Place(w, e[1], new float3(1.5f, .6f, 0), radius: 3f);
            Command(w, e[0], pressed: CommandButtons.PunchLeft);
            var timing = CombatAttackTuning.Get(CombatAttackKind.PunchLeft);
            w.Step(timing.WindupTicks + 1);
            var d = LastHits(w);
            Assert.That(d.ActiveAttackers, Is.EqualTo(1)); Assert.That(d.Candidates, Is.EqualTo(2), "Attacker and the big body.");
            Assert.That(d.InsideTests, Is.EqualTo(1)); Assert.That(d.AcceptedHits, Is.Zero);
            Assert.That(w.Manager.GetComponentData<CombatHealth>(e[1]).Current, Is.EqualTo(100));
            Assert.That(w.Manager.GetComponentData<CombatPosition>(e[1]).Value, Is.EqualTo(new float3(1.5f, .6f, 0)));
        }

        [Test]
        public void SeveralTargetsActiveTicksAndANewStrikeKeepTheLedgerRules()
        {
            using var w = new ReplayWorld("indexed hit ledger"); var e = Start(w, 6);
            var oracle = HitOracle.Attach(w);
            Place(w, e[0], float3.zero, facing: FacingDirection.Right);
            var enemies = e.Where(x => w.Manager.GetComponentData<CombatTeam>(x).Value == 2).ToArray();
            var allies = e.Where(x => x != e[0] && w.Manager.GetComponentData<CombatTeam>(x).Value == 1).ToArray();
            for (var i = 0; i < enemies.Length; i++) Place(w, enemies[i], new float3(.3f + i * .3f, 0, i * .1f - .1f));
            for (var i = 0; i < allies.Length; i++) Place(w, allies[i], new float3(.45f + i * .3f, 0, .2f));
            var punch = CombatAttackTuning.Get(CombatAttackKind.PunchLeft);
            Command(w, e[0], pressed: CommandButtons.PunchLeft); w.Step(punch.TotalTicks);
            Assert.That(enemies.All(x => w.Manager.GetComponentData<CombatHealth>(x).Current == 100 - punch.Damage), Is.True,
                "Each enemy once despite several Active ticks.");
            Assert.That(allies.All(x => w.Manager.GetComponentData<CombatHealth>(x).Current == 100), Is.True);
            Assert.That(w.Manager.GetBuffer<CombatHitRecord>(e[0]).AsNativeArray().ToArray().Select(h => h.Target.Value),
                Is.EqualTo(enemies.Select(x => w.Manager.GetComponentData<CombatantId>(x).Value).OrderBy(v => v)), "Id order.");
            Command(w, e[0], pressed: CommandButtons.PunchLeft); w.Step(punch.TotalTicks);
            Assert.That(enemies.All(x => w.Manager.GetComponentData<CombatHealth>(x).Current == 100 - 2 * punch.Damage), Is.True,
                "A new strike starts with an empty ledger.");
            Assert.That(oracle.Mismatches, Is.Empty); Assert.That(oracle.CheckedHits, Is.EqualTo(2 * enemies.Length));
        }

        [Test]
        public void StaleSnapshotWithActiveAttackersFailsBeforeAnyHpOrLedgerWrite()
        {
            using var w = new ReplayWorld("indexed hit stale"); var e = Start(w, 4);
            Place(w, e[0], float3.zero, facing: FacingDirection.Right); Place(w, e[1], new float3(.5f, 0, 0));
            Place(w, e[2], new float3(-5, 0, 0)); Place(w, e[3], new float3(5, 0, 0));
            var punch = CombatAttackTuning.Get(CombatAttackKind.PunchLeft);
            Command(w, e[0], pressed: CommandButtons.PunchLeft); w.Step(punch.WindupTicks + 1);
            Assert.That(w.Manager.GetComponentData<CombatHealth>(e[1]).Current, Is.EqualTo(96));
            var hit = w.World.GetExistingSystem<CombatHitSystem>();
            void Reset() { w.Manager.GetBuffer<CombatHitRecord>(e[0]).Clear(); w.Manager.SetComponentData(e[1], new CombatHealth { Current = 100, Max = 100 }); }

            Reset(); hit.Update(w.World.Unmanaged); // Control: the same tick's snapshot is still current and would write.
            Assert.That(w.Manager.GetComponentData<CombatHealth>(e[1]).Current, Is.EqualTo(96));

            Reset();
            var ledgerTarget = w.Manager.GetComponentData<CombatantId>(e[3]).Value;
            w.Manager.SetComponentEnabled<InCombat>(e[3], false); // Membership changed after publication: snapshot is stale.
            var rejected = false; string unexpected = null;
            void OnLog(string message, string stack, LogType type)
            {
                if (type == LogType.Exception && message.Contains("final spatial snapshot")) rejected = true;
                else if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert) unexpected = message;
            }
            var oldIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            Application.logMessageReceived += OnLog;
            try
            {
                try { hit.Update(w.World.Unmanaged); }
                catch (InvalidOperationException ex) { StringAssert.Contains("final spatial snapshot", ex.Message); rejected = true; }
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                LogAssert.ignoreFailingMessages = oldIgnore;
            }
            Assert.That(rejected, Is.True, $"stale snapshot must not be an empty successful result (bystander {ledgerTarget})");
            Assert.That(unexpected, Is.Null);
            Assert.That(w.Manager.GetComponentData<CombatHealth>(e[1]).Current, Is.EqualTo(100));
            Assert.That(w.Manager.GetBuffer<CombatHitRecord>(e[0]).Length, Is.Zero);
        }
    }
}
