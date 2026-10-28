using System;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace MLFLegends.Tests.EditMode
{
    public class CombatArenaTests
    {
        static CombatBody Body => CombatBodyTuning.Default;
        internal static CombatArenaShape Box(float x, float bottom, float z, float hx, float top, float hz, float yaw = 0) =>
            new CombatArenaShape { Center = new float2(x, z), HalfSize = new float2(hx, hz), Bottom = bottom, Top = top,
                AxisX = new float2(math.cos(yaw), math.sin(yaw)) };
        static CombatArenaShape Floor => Box(0, -.5f, 0, 30, 0, 30);
        static CombatArenaMove Move(CombatArenaShape[] rows, float3 start, float2 delta, bool grounded = true, float vy = 0, bool jump = false, uint landing = 12)
        {
            using var shapes = new NativeArray<CombatArenaShape>(rows, Allocator.Temp);
            return CombatArenaGeometry.Move(shapes, Body, start, delta, vy, grounded, jump, landing);
        }
        static void Good(CombatArenaMove r)
        {
            Assert.That(r.Failure, Is.EqualTo(CombatArenaFailure.None));
            Assert.That(math.all(math.isfinite(r.Position)), Is.True);
        }

        [TestCase(1)] [TestCase(-1)]
        public void HeadOnStopsAtRadiusButParallelAndAwayRemainFree(int side)
        {
            var rows = new[] { Floor, Box(0, 0, 0, .05f, 4, 5) };
            var r = Move(rows, new float3(-side, 0, 0), new float2(side * 1.5f, 1)); Good(r);
            Assert.That(r.Position.x, Is.EqualTo(-side * .35f).Within(.001));
            Assert.That(r.Position.z, Is.EqualTo(1).Within(.001));
            var parallel = Move(rows, r.Position, new float2(0, -1)); Good(parallel);
            Assert.That(parallel.Position.x, Is.EqualTo(r.Position.x).Within(.001));
            var away = Move(rows, parallel.Position, new float2(-side * .2f, 0)); Good(away);
            Assert.That(away.Position.x, Is.EqualTo(r.Position.x - side * .2f).Within(.001));
            for (var i = 0; i < 100; i++) { r = Move(rows, r.Position, new float2(side * .1f, 0)); Good(r); }
            Assert.That(r.Position.x, Is.EqualTo(-side * .35f).Within(.001));
        }
        [Test]
        public void ZOnlyAndRotatedWallHaveExactFacesAndRoundedCorners()
        {
            var z = Move(new[] { Floor, Box(0, 0, 0, 5, 4, .1f) }, new float3(0, 0, -1), new float2(0, 1.4f)); Good(z);
            Assert.That(z.Position.z, Is.EqualTo(-.4f).Within(.001));
            var angle = math.PI / 4;
            var wall = Box(0, 0, 0, .1f, 3, 2, angle);
            var start = wall.WorldVector(new float2(-1, 0));
            var move = wall.WorldVector(new float2(1.2f, .5f));
            var rotated = Move(new[] { Floor, wall }, new float3(start.x, 0, start.y), move); Good(rotated);
            var local = wall.Local(new float2(rotated.Position.x, rotated.Position.z));
            Assert.That(local.x, Is.EqualTo(-.4f).Within(.001)); Assert.That(local.y, Is.EqualTo(.5f).Within(.001));
            // This position is inside the rotated box AABB but outside its actual footprint.
            var clear = Move(new[] { Floor, wall }, new float3(1.2f, 0, 0), float2.zero); Good(clear);
            Assert.That(clear.Position.x, Is.EqualTo(1.2f).Within(.001));
            Assert.That(CombatArenaGeometry.DiskRectangle(new float2(1.2f, 1.2f), .3f, new float2(-1), new float2(1), out var correction), Is.True);
            Assert.That(math.length(correction), Is.EqualTo(.3 - Math.Sqrt(.08)).Within(1e-5));
        }
        [Test]
        public void ThinWallCannotTunnelAndExcessWorkFreezesWithoutClipping()
        {
            var wall = Box(0, 0, 0, .01f, 5, 3);
            var valid = Move(new[] { Floor, wall }, new float3(-.5f, 0, 0), new float2(.8f, 0)); Good(valid);
            Assert.That(valid.Position.x, Is.EqualTo(-.31f).Within(.001));
            var excessive = Move(new[] { Floor, wall }, new float3(-1, 0, 0), new float2(100, 0));
            Assert.That(excessive.Failure, Is.EqualTo(CombatArenaFailure.WorkLimit));
            Assert.That(excessive.Position, Is.EqualTo(new float3(-1, 0, 0)));
        }
        [Test]
        public void InnerCornerAndInitialOverlapAreBoundedAndDeterministic()
        {
            var rows = new[] { Floor, Box(0, 0, 0, .1f, 3, 4), Box(0, 0, 0, 4, 3, .1f) };
            var a = Move(rows, new float3(-1, 0, -1), new float2(1.2f)); Good(a);
            Assert.That(a.Position.x, Is.EqualTo(-.4f).Within(.001)); Assert.That(a.Position.z, Is.EqualTo(-.4f).Within(.001));
            var slight = Move(new[] { Floor, rows[1] }, new float3(-.35f, 0, 0), float2.zero); Good(slight);
            Assert.That(slight.Position.x, Is.EqualTo(-.4f).Within(.001)); Assert.That(slight.ControlledDistance, Is.Zero);
            var deep = Move(new[] { Floor, Box(0, -2, 0, 3, 5, 3) }, float3.zero, float2.zero);
            Assert.That(deep.Failure, Is.EqualTo(CombatArenaFailure.InitialOverlap)); Assert.That(deep.Position, Is.EqualTo(float3.zero));
        }
        [Test]
        public void LowCeilingStopsHeadAndEdgeFallsOntoActualLowerFloor()
        {
            var rows = new[] { Floor, Box(0, 2.2f, 0, 5, 2.4f, 5) };
            var p = float3.zero; var vy = 0f; var grounded = true; var landed = 0;
            var peak = 0f;
            for (var i = 0; i < 80; i++)
            {
                var r = Move(rows, p, float2.zero, grounded, vy, i == 0); Good(r);
                peak = math.max(peak, r.Position.y); p = r.Position; vy = r.Velocity; grounded = r.Grounded;
                if (r.Landed) landed++;
            }
            Assert.That(peak, Is.EqualTo(.2f).Within(.001)); Assert.That(landed, Is.EqualTo(1));
            var platform = Box(0, 1.5f, 0, 1, 2, 1);
            p = new float3(0, 2, 0); grounded = true; vy = 0; var fell = false; landed = 0;
            for (var i = 0; i < 120; i++)
            {
                var r = Move(new[] { Floor, platform }, p, new float2(.05f, 0), grounded, vy); Good(r);
                fell |= !r.Grounded; if (r.Landed) landed++;
                p = r.Position; grounded = r.Grounded; vy = r.Velocity;
            }
            Assert.That(fell, Is.True); Assert.That(landed, Is.EqualTo(1)); Assert.That(p.y, Is.EqualTo(0).Within(.001));
        }
        [Test]
        public void RampUsesDiskSupportAndContinuousSlopeWithoutRepeatedLanding()
        {
            var ramp = Box(0, 0, 0, 2, 2, 1); ramp.Kind = CombatArenaKind.Ramp;
            Assert.That(CombatArenaGeometry.TopAt(ramp, float2.zero, .3f, out var height), Is.True);
            Assert.That(height, Is.EqualTo(1.15f).Within(.0001)); // max x of disk .3, slope .5
            var p = new float3(-2.5f, 0, 0); var grounded = true; var vy = 0f; var landings = 0;
            for (var i = 0; i < 60; i++)
            {
                var r = Move(new[] { Floor, ramp }, p, new float2(.05f, 0), grounded, vy); Good(r);
                if (r.Landed) landings++;
                p = r.Position; grounded = r.Grounded; vy = r.Velocity;
            }
            Assert.That(p.x, Is.EqualTo(.5f).Within(.002)); Assert.That(p.y, Is.EqualTo(1.4f).Within(.002));
            Assert.That(landings, Is.Zero);
            ramp.Top = 4; Assert.That(ramp.Slope, Is.EqualTo(CombatArenaTuning.MaxSlope));
            ramp.Top = 4.1f;
            using var shapes = new NativeArray<CombatArenaShape>(new[] { ramp }, Allocator.Temp);
            Assert.That(CombatArenaGeometry.Support(shapes, new float3(0, 4, 0), .3f, 4, 0, true, out _), Is.False);
        }

        static CombatArenaSystem Arena(ReplayWorld world) => world.World.GetExistingSystemManaged<CombatArenaSystem>();
        static ulong Install(ReplayWorld w, params CombatArenaShape[] shapes)
        {
            var lease = CombatArenaSystem.Acquire(w.Manager);
            Assert.That(CombatArenaSystem.Stage(w.Manager, lease, shapes), Is.True);
            return lease;
        }
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(10)]
        public void ActualEcsMotionAndFinalIndexAgreeAtAnyCatchUpPacing(int pacing)
        {
            using var w = new ReplayWorld("arena replay");
            using var reference = new ReplayWorld("one-tick arena replay");
            Install(w, Floor, Box(0, 0, 0, .05f, 4, 5));
            Install(reference, Floor, Box(0, 0, 0, .05f, 4, 5));
            var setup = BattleSetup.Default; setup.Human.Position = new float3(-1, 0, 0); setup.Ai.Position = new float3(1, 0, 0);
            var script = new ReplayScript();
            for (uint i = 1; i <= 60; i++) { script.Player(i, i, ReplayRole.Human, moveX: 1); script.Ai(i, i, ReplayRole.Ai, moveX: -1); }
            w.Script = script; w.Request(BattleRequest.Start(setup));
            reference.Script = script; reference.Request(BattleRequest.Start(setup)); reference.Step(60);
            for (var i = 0; i < 60; i += pacing) w.Frame(Math.Min(pacing, 60 - i));
            Assert.That(w.RawLog, Is.EqualTo(reference.RawLog), "Every recorded combat field/tick matches one-tick pacing.");
            var last = w.BattleRecords(1).Last();
            var human = last.Combatant(last.HumanId); var ai = last.Combatant(last.AiId);
            Assert.That(human.Position.x, Is.EqualTo(-.35f).Within(.001)); Assert.That(ai.Position.x, Is.EqualTo(.35f).Within(.001));
            Assert.That(human.Motion.MoveStreakDistance, Is.EqualTo(.65f).Within(.002));
            Assert.That(human.Motion.LastMoveTick, Is.LessThan(last.Tick - 6));
            using var hits = new NativeList<CombatSpatialBody>(Allocator.Temp);
            var index = w.World.GetExistingSystemManaged<CombatSpatialIndexSystem>();
            Assert.That(index.TryQueryAabb(new CombatSpatialBounds { Min = new float3(-2, -1, -1), Max = new float3(2, 5, 1) }, hits), Is.True);
            Assert.That(hits.Length, Is.EqualTo(2));
            // Compare final ECS positions, not predicted solver outputs as reference.
            Assert.That(hits[0].Position, Is.EqualTo(human.Position)); Assert.That(hits[1].Position, Is.EqualTo(ai.Position));
        }
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(10)]
        public void PlayerAndAiShareIdenticalCanonicalMovementJumpAndStrikeRules(int pacing)
        {
            using var w = new ReplayWorld("arena player AI parity"); Install(w, Floor, Box(0, 0, 0, .05f, 4, 5));
            var table = CombatStrikeSchedule.Default; table.LandingRecoveryTicks = 12; CombatStrikeSchedule.Publish(w.Manager, table);
            var setup = BattleSetup.Default; setup.Human.Position = new float3(-1, 0, -2); setup.Ai.Position = new float3(-1, 0, 2);
            var script = new ReplayScript();
            for (uint i = 1; i <= 90; i++)
            {
                var pressed = i == 1 ? CommandButtons.Jump : i == 65 ? CommandButtons.PunchLeft : CommandButtons.None;
                script.Player(i, i, ReplayRole.Human, moveX: 1, pressed: pressed);
                script.Ai(i, i, ReplayRole.Ai, moveX: 1, pressed: pressed);
            }
            w.Script = script; w.Request(BattleRequest.Start(setup));
            for (var i = 0; i < 90; i += pacing) w.Frame(Math.Min(pacing, 90 - i));
            foreach (var row in w.BattleRecords(1))
            {
                var human = row.Combatant(row.HumanId); var ai = row.Combatant(row.AiId);
                Assert.That(ai.Position.x, Is.EqualTo(human.Position.x)); Assert.That(ai.Position.y, Is.EqualTo(human.Position.y));
                Assert.That(ai.Motion, Is.EqualTo(human.Motion)); Assert.That(ai.Attack, Is.EqualTo(human.Attack));
                Assert.That(ai.Facing, Is.EqualTo(human.Facing)); Assert.That(ai.Health.Current, Is.EqualTo(human.Health.Current));
            }
        }
        [TestCase(2)] [TestCase(100)] [TestCase(400)]
        public void RevisionLeasesRestartAndDisposalBelongToOneWorld(int count)
        {
            using var w = new ReplayWorld("arena lifecycle");
            using var other = new ReplayWorld("another arena");
            var old = Install(w, Floor);
            var current = Install(w, Floor, Box(0, 0, 0, .1f, 3, 3));
            Assert.That(CombatArenaSystem.Release(w.Manager, old), Is.False);
            using var stateQuery = w.Manager.CreateEntityQuery(typeof(CombatArenaState));
            Assert.That(stateQuery.GetSingleton<CombatArenaState>().Revision, Is.Zero, "Staging never changes active geometry.");
            Arena(w).Update();
            Assert.That(stateQuery.GetSingleton<CombatArenaState>().Revision, Is.EqualTo(1));
            var setup = BattleSetup.Default; setup.HumanAllies = setup.AiAllies = (ushort)(count / 2 - 1);
            w.Request(BattleRequest.Start(setup)); w.Step(2);
            w.Request(BattleRequest.Restart(setup)); w.Step(2);
            Assert.That(w.BattleRecords(2).Last().Combatants.Length, Is.EqualTo(count));
            Assert.That(CombatArenaSystem.Stage(w.Manager, current, new[] { Box(0, 0, 0, -1, 2, 1) }), Is.False);
            Arena(w).Update(); Assert.That(stateQuery.GetSingleton<CombatArenaState>().Valid, Is.False);
            using var otherQuery = other.Manager.CreateEntityQuery(typeof(CombatArenaState));
            Assert.That(otherQuery.GetSingleton<CombatArenaState>().Required, Is.False);
            Assert.That(CombatArenaSystem.Release(w.Manager, current), Is.True);
        }
        [Test]
        public void LandingCopiesTwelveTicksOnceDropsPressesAndResumesWithoutRunGraceDelay()
        {
            using var w = new ReplayWorld("arena landing input");
            Install(w, Floor);
            var table = CombatStrikeSchedule.Default; table.LandingRecoveryTicks = 12;
            CombatStrikeSchedule.Publish(w.Manager, table);
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: CommandButtons.Jump);
            w.Request(BattleRequest.Start(BattleSetup.Default)); w.Step();
            CombatantRecord Human() => w.BattleRecords(1).Last().Combatant(w.Session.HumanCombatant.Value);
            for (var guard = 0; !Human().Motion.Grounded && guard < 100; guard++) w.Step();
            var contact = Human(); Assert.That(contact.Motion.Grounded, Is.True);
            var landTick = contact.Motion.LandTick;
            Assert.That(contact.Motion.LandingRecoveryTicks, Is.EqualTo(12));
            for (var i = 1; i < 12; i++)
            {
                w.SubmitBetweenFrames(new CombatCommand { Combatant = w.Session.HumanCombatant, Tick = w.Tick + 1,
                    MoveX = 1, Pressed = CommandButtons.Attack | CommandButtons.Jump, Source = CommandSource.Player });
                w.Step(); var actor = Human();
                Assert.That(actor.Position, Is.EqualTo(contact.Position)); Assert.That(actor.Motion.LandTick, Is.EqualTo(landTick));
                Assert.That(actor.Attack.IsAttacking, Is.False); Assert.That(actor.Attack.Buffered, Is.EqualTo(CommandButtons.None));
            }
            w.SubmitBetweenFrames(new CombatCommand { Combatant = w.Session.HumanCombatant, Tick = w.Tick + 1,
                MoveX = 1, Source = CommandSource.Player });
            w.Step();
            Assert.That(Human().Position.x, Is.GreaterThan(contact.Position.x));
            Assert.That(Human().Motion.MoveStreakStartTick, Is.EqualTo(w.Tick));
            Assert.That(Human().Attack.IsAttacking, Is.False); Assert.That(Human().Motion.Grounded, Is.True);
            Assert.That(Human().Motion.LandTick, Is.EqualTo(landTick));
        }
        [Test]
        public void WallRetainsStrikeTimingPhaseSpeedAndOneHitLedger()
        {
            using var w = new ReplayWorld("arena attack");
            Install(w, Floor, Box(0, 0, 0, .05f, 4, 5));
            var table = CombatStrikeSchedule.Default;
            table.PunchLeft = CombatStrikeTiming.FromTicks(6, 4, 8, 4);
            CombatStrikeSchedule.Publish(w.Manager, table);
            var setup = BattleSetup.Default; setup.Human.Position = new float3(-.5f, 0, 0); setup.Ai.Position = new float3(.5f, 0, 0);
            var script = new ReplayScript();
            for (uint i = 1; i <= 18; i++) script.Player(i, i, ReplayRole.Human, moveX: 1,
                pressed: i == 1 ? CommandButtons.Attack : CommandButtons.None);
            w.Script = script; w.Request(BattleRequest.Start(setup)); w.Step(18);
            var records = w.BattleRecords(1);
            Assert.That(records[0].Combatant(records[0].HumanId).Position.x, Is.EqualTo(-.5f + 4f / 60 * .5f).Within(.0001));
            for (var i = 0; i < 18; i++)
            {
                var actor = records[i].Combatant(records[i].HumanId);
                Assert.That(actor.Attack.Kind, Is.EqualTo(CombatAttackKind.PunchLeft));
                Assert.That(actor.Attack.Timing, Is.EqualTo(table.PunchLeft));
                Assert.That(actor.Position.x, Is.LessThanOrEqualTo(-.35f + .001f));
                Assert.That(actor.Hits.Length, Is.EqualTo(i < 6 ? 0 : 1));
                var hp = records[i].Combatant(records[i].AiId).Health.Current;
                Assert.That(hp, Is.EqualTo(100 - (i < 6 ? 0 : CombatAttackTuning.Get(CombatAttackKind.PunchLeft).Damage)));
            }
        }
        [Test]
        public void PausedEndRestartAndDisposedOldWorldCannotAffectAnotherArena()
        {
            using var w = new ReplayWorld("paused arena"); using var other = new ReplayWorld("surviving arena");
            Install(w, Floor); Install(other, Floor);
            w.Request(BattleRequest.Start(BattleSetup.Default)); w.Step(2);
            var tick = w.Tick;
            w.World.SetTime(new Unity.Core.TimeData(w.World.Time.ElapsedTime, 0));
            w.Request(BattleRequest.End()); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            Assert.That(w.Session.IsActive, Is.False); Assert.That(w.Tick, Is.EqualTo(tick));
            w.Request(BattleRequest.Restart(BattleSetup.Default)); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            Assert.That(w.Session.IsActive, Is.True); Assert.That(w.Tick, Is.EqualTo(tick));
            w.Dispose();
            Assert.That(w.World.IsCreated, Is.False);
            other.Request(BattleRequest.Start(BattleSetup.Default)); other.Step(2);
            using var query = other.Manager.CreateEntityQuery(typeof(CombatArenaState));
            Assert.That(query.GetSingleton<CombatArenaState>().Valid, Is.True);
        }
        [Test]
        public void ElevatedSpawnCannotAttackOrInventItsOwnFloorOnFirstTick()
        {
            using var w = new ReplayWorld("elevated arena spawn"); Install(w, Floor);
            var setup = BattleSetup.Default; setup.Human.Position = new float3(-2, 3, 0);
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: CommandButtons.Attack);
            w.Request(BattleRequest.Start(setup)); w.Step();
            var first = w.BattleRecords(1).Single().Combatant(w.Session.HumanCombatant.Value);
            Assert.That(first.Motion.Grounded, Is.False); Assert.That(first.Attack.IsAttacking, Is.False);
            w.Step(120);
            var last = w.BattleRecords(1).Last().Combatant(w.Session.HumanCombatant.Value);
            Assert.That(last.Position.y, Is.EqualTo(0).Within(.001)); Assert.That(last.Motion.GroundHeight, Is.EqualTo(0).Within(.001));
        }
        [Test]
        public void ReleasedSceneFreezesItsBattleButCannotPoisonNextBareFixtureOrReviveItsLease()
        {
            using var w = new ReplayWorld("released arena scope");
            var lease = Install(w, Floor);
            w.Request(BattleRequest.Start(BattleSetup.Default)); w.Step();
            Assert.That(CombatArenaSystem.Release(w.Manager, lease), Is.True);
            Assert.That(CombatArenaSystem.Stage(w.Manager, lease, new[] { Floor }), Is.False, "Released token is retired.");
            w.Step();
            using var query = w.Manager.CreateEntityQuery(typeof(CombatArenaState));
            Assert.That(query.GetSingleton<CombatArenaState>().Required, Is.True);
            Assert.That(query.GetSingleton<CombatArenaState>().Valid, Is.False, "Same production battle stays fail-closed.");
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: CommandButtons.Attack);
            w.Request(BattleRequest.Restart(BattleSetup.Default)); w.Step();
            var actor = w.BattleRecords(2).Single().Combatant(w.Session.HumanCombatant.Value);
            Assert.That(actor.Attack.IsAttacking, Is.True, "Next bare fixture can start on its original flat support.");
            w.Step(); Assert.That(query.GetSingleton<CombatArenaState>().Required, Is.False);
            var successor = Install(w, Floor);
            Assert.That(successor, Is.GreaterThan(lease));
            Assert.That(CombatArenaSystem.Stage(w.Manager, lease, new[] { Floor }), Is.False);
            Assert.That(CombatArenaSystem.Release(w.Manager, lease), Is.False);
        }
        [Test]
        public void PendingRowsAreCopiedAndInvalidProductionSourceNeverCreatesAFlatFloor()
        {
            using var w = new ReplayWorld("arena numeric copy");
            var rows = new[] { Floor }; var lease = Install(w, rows); rows[0].Top = 99;
            Arena(w).Update();
            using var query = w.Manager.CreateEntityQuery(typeof(CombatArenaState));
            Assert.That(w.Manager.GetBuffer<CombatArenaShape>(query.GetSingletonEntity())[0].Top, Is.Zero);
            Assert.That(CombatArenaSystem.Stage(w.Manager, lease, null), Is.False);
            var setup = BattleSetup.Default; setup.Human.Position = new float3(-2, 3, 0);
            w.Request(BattleRequest.Start(setup)); w.Step(4);
            var actor = w.BattleRecords(1).Last().Combatant(w.Session.HumanCombatant.Value);
            Assert.That(actor.Position, Is.EqualTo(setup.Human.Position));
            using var diagnostics = w.Manager.CreateEntityQuery(typeof(CombatArenaDiagnostics));
            Assert.That(diagnostics.GetSingleton<CombatArenaDiagnostics>().LastFailure, Is.EqualTo(CombatArenaFailure.ArenaUnavailable));
            Assert.That(actor.Motion.Grounded, Is.False);
        }
        [Test]
        public void AuthoringRejectsPitchShearMissingAndMeshesInsteadOfApproximatingThem()
        {
            var go = new GameObject("arena authoring fixture");
            try
            {
                var source = new CombatArenaSource { Transform = go.transform };
                go.transform.localScale = new Vector3(2, 3, 4); go.transform.rotation = Quaternion.Euler(0, 33.32f, 0);
                Assert.That(CombatArenaAuthoring.TryBuild(source, out var box, out _), Is.True);
                Assert.That(box.HalfSize, Is.EqualTo(new float2(1, 2)));
                go.transform.rotation = Quaternion.Euler(10, 0, 0);
                Assert.That(CombatArenaAuthoring.TryBuild(source, out _, out _), Is.False);
                var parent = new GameObject("sheared parent");
                try
                {
                    parent.transform.localScale = new Vector3(2, 1, 1);
                    go.transform.SetParent(parent.transform, false); go.transform.localScale = Vector3.one;
                    go.transform.localRotation = Quaternion.Euler(0, 45, 0);
                    Assert.That(CombatArenaAuthoring.TryBuild(source, out _, out _), Is.False);
                }
                finally { go.transform.SetParent(null, false); UnityEngine.Object.DestroyImmediate(parent); }
                go.transform.rotation = Quaternion.identity; go.AddComponent<MeshCollider>();
                Assert.That(CombatArenaAuthoring.TryBuild(source, out _, out _), Is.False);
                Assert.That(CombatArenaAuthoring.TryBuild(null, out _, out _), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
