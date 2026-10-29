using System;
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
    public struct ContactChunkMarker : IComponentData { public int Value; }

    public class CombatBodyContactTests
    {
        static CombatBody Body(float radius = .3f, float mass = 1) => new CombatBody { Radius = radius, Height = 2, Mass = mass };
        static Entity[] Actors(ReplayWorld w)
        {
            using var q = w.Manager.CreateEntityQuery(typeof(CombatantId));
            using var e = q.ToEntityArray(Allocator.Temp);
            return e.ToArray().OrderBy(x => w.Manager.GetComponentData<CombatantId>(x).Value).ToArray();
        }
        static void Start(ReplayWorld w, bool allies = false, CombatArenaShape[] arena = null)
        {
            if (arena != null)
            {
                var owner = w.World.GetExistingSystemManaged<CombatArenaSystem>();
                Assert.That(owner.Stage(owner.Acquire(), arena), Is.True);
            }
            var setup = BattleSetup.Default;
            setup.Human.Position = float3.zero; setup.Ai.Position = new float3(.6f, 0, 0);
            w.Request(BattleRequest.Start(setup));
            w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            if (allies) foreach (var e in Actors(w)) w.Manager.SetComponentData(e, new CombatTeam { Value = 1 });
        }
        static float3 Position(ReplayWorld w, Entity e) => w.Manager.GetComponentData<CombatPosition>(e).Value;
        static void Command(ReplayWorld w, Entity e, sbyte x, CommandSource source = CommandSource.Player, sbyte z = 0,
            CommandButtons pressed = CommandButtons.None) => w.SubmitBetweenFrames(new CombatCommand
            { Combatant = w.Manager.GetComponentData<CombatantId>(e), Tick = w.Tick + 1, MoveX = x, MoveZ = z, Source = source, Pressed = pressed });
        static CombatArenaShape Floor => CombatArenaTests.Box(0, -.5f, 0, 20, 0, 20);
        static void Clear(ReplayWorld w, Entity a, Entity b)
        {
            var pa = Position(w, a); var pb = Position(w, b);
            // Independent cylinder expectation, not a call back into the production contact solver.
            var ab = w.Manager.GetComponentData<CombatBody>(a); var bb = w.Manager.GetComponentData<CombatBody>(b);
            if (pa.y < pb.y + bb.Height && pb.y < pa.y + ab.Height)
                Assert.That(math.distance(pa.xz, pb.xz), Is.GreaterThanOrEqualTo(ab.Radius + bb.Radius - .00021f));
            Assert.That(math.all(math.isfinite(pa)) && math.all(math.isfinite(pb)), Is.True);
        }

        static void SnapshotMatchesFinalState(TickRecord tick)
        {
            Assert.That(tick.SpatialValid, Is.True); Assert.That(tick.SpatialTick, Is.EqualTo(tick.Tick));
            var expected = tick.Combatants.Where(c => c.InCombat).OrderBy(c => c.Id).ToArray();
            Assert.That(tick.Spatial.Length, Is.EqualTo(expected.Length));
            for (var i = 0; i < expected.Length; i++)
            {
                var actor = expected[i]; var row = tick.Spatial[i];
                Assert.That(row.Id.Value, Is.EqualTo(actor.Id)); Assert.That(row.Team.Value, Is.EqualTo(actor.Team));
                Assert.That(row.Position, Is.EqualTo(actor.Position)); Assert.That(row.Body, Is.EqualTo(actor.Body));
                Assert.That(row.Bounds.Min, Is.EqualTo(new float3(actor.Position.x - actor.Body.Radius, actor.Position.y, actor.Position.z - actor.Body.Radius)));
                Assert.That(row.Bounds.Max, Is.EqualTo(new float3(actor.Position.x + actor.Body.Radius, actor.Position.y + actor.Body.Height, actor.Position.z + actor.Body.Radius)));
            }
        }

        [Test]
        public void IndependentCylinderExamplesRejectDiagonalAabbAndVerticalTangencies()
        {
            Assert.That(CombatBodyContacts.Penetration(float3.zero, Body(), new float3(.5f, 0, .5f), Body()), Is.Zero);
            Assert.That(CombatBodyContacts.Penetration(float3.zero, Body(), new float3(.4f, 0, 0), Body()), Is.EqualTo(.2f).Within(1e-6));
            Assert.That(CombatBodyContacts.Penetration(float3.zero, Body(), new float3(0, 2, 0), Body()), Is.Zero);
            Assert.That(CombatBodyContacts.Penetration(float3.zero, Body(), new float3(0, 1.99f, 0), Body()), Is.EqualTo(.6f).Within(1e-6));
        }

        [Test]
        public void ContinuousPairTestFindsFastExchangesAndBothMovingCandidateEnvelope()
        {
            // Final points do not overlap, and neither final body covers the other's initial point.
            Assert.That(CombatBodyContacts.Sweep(new float3(-2, 0, 0), new float3(1, 0, 0), Body(),
                new float3(2, 0, 0), new float3(-1, 0, 0), Body(), 1, 2, out var normal, out var depth), Is.True);
            Assert.That(normal.x, Is.EqualTo(1).Within(1e-6)); Assert.That(depth, Is.EqualTo(2.6f).Within(1e-5));
            Assert.That(CombatBodyContacts.Sweep(new float3(-2, 3, 0), new float3(1, 3, 0), Body(),
                new float3(2, 0, 0), new float3(-1, 0, 0), Body(), 1, 2, out _, out _), Is.False);
            // Vertical intervals overlap only during the middle of the sweep.
            Assert.That(CombatBodyContacts.Sweep(new float3(-1, 3, 0), new float3(1, -3, 0), Body(),
                float3.zero, float3.zero, Body(), 1, 2, out _, out _), Is.True);
        }

        [Test]
        public void SweptDisksAgreeWithIndependentClosestTimeForRandomNonGrazingPaths()
        {
            var rng = new System.Random(6403); var checkedCases = 0;
            float Next(float size) => (float)(rng.NextDouble() * 2 - 1) * size;
            for (var i = 0; i < 4000; i++)
            {
                var start = new float2(Next(4), Next(4)); var da = new float2(Next(3), Next(3)); var db = new float2(Next(3), Next(3));
                var relative = db - da;
                if (math.length(start) < .602f || math.lengthsq(relative) < .0001f) continue;
                var closestTime = math.clamp(-math.dot(start, relative) / math.lengthsq(relative), 0, 1);
                var distance = math.length(start + relative * closestTime);
                if (math.abs(distance - .6f) < .002f) continue;
                var actual = CombatBodyContacts.Sweep(float3.zero, new float3(da.x, 0, da.y), Body(),
                    new float3(start.x, 0, start.y), new float3(start.x + db.x, 0, start.y + db.y), Body(), 1, 2, out _, out _);
                Assert.That(actual, Is.EqualTo(distance < .6f), "Independent closest-time circle expectation, no quadratic roots.");
                checkedCases++;
            }
            Assert.That(checkedCases, Is.GreaterThan(3000));
        }

        [TestCase(false)] [TestCase(true)]
        public void PassiveEnemyNeverBulldozesAndPressureDoesNotAccumulateRunning(bool reverseChunks)
        {
            using var w = new ReplayWorld("enemy pressure"); Start(w);
            var e = Actors(w);
            if (reverseChunks) w.Manager.AddComponentData(e[0], new ContactChunkMarker());
            for (var t = 0; t < 90; t++) { Command(w, e[0], 1); w.Step(); Clear(w, e[0], e[1]); }
            Assert.That(Position(w, e[0]).x, Is.EqualTo(0).Within(.00021));
            Assert.That(Position(w, e[1]).x, Is.EqualTo(.6f));
            Assert.That(w.Manager.GetComponentData<CombatMotion>(e[0]).MoveStreakDistance, Is.LessThan(.00021));
            Assert.That(w.Manager.GetComponentData<CombatMotion>(e[0]).LastMoveTick, Is.Zero);
            Command(w, e[0], -1); w.Step(); Assert.That(Position(w, e[0]).x, Is.LessThan(-.06f));
        }

        [TestCase(false)] [TestCase(true)]
        public void AlliesYieldButWallTrappedAllyStopsControlledFighter(bool wall)
        {
            using var w = new ReplayWorld("ally pressure");
            var rows = wall ? new[] { Floor, CombatArenaTests.Box(1, 0, 0, .1f, 4, 4),
                CombatArenaTests.Box(0, 0, .4f, 20, 4, .1f), CombatArenaTests.Box(0, 0, -.4f, 20, 4, .1f) } : new[] { Floor };
            Start(w, true, rows); var e = Actors(w);
            for (var t = 0; t < 30; t++) { Command(w, e[0], 1); w.Step(); Clear(w, e[0], e[1]); }
            if (wall)
            {
                Assert.That(Position(w, e[1]).x, Is.LessThanOrEqualTo(.60021f));
                Assert.That(Position(w, e[0]).x, Is.LessThan(.00061f), "World and body skins can combine, but cannot accumulate unbounded progress.");
                Assert.That(w.Manager.GetComponentData<CombatMotion>(e[0]).MoveStreakDistance, Is.Zero,
                    "Joint contact tolerance is not controlled running progress.");
            }
            else
            {
                Assert.That(Position(w, e[0]).x, Is.GreaterThan(1.4f).And.LessThan(2));
                Assert.That(Position(w, e[0]).x, Is.GreaterThan(Position(w, e[1]).x), "The controller passes the ally instead of pushing a queue forever.");
                Assert.That(math.abs(Position(w, e[1]).z), Is.GreaterThan(.1f));
                Assert.That(w.Manager.GetComponentData<CombatMotion>(e[1]).MoveStreakDistance, Is.Zero,
                    "Passive ally displacement is not running.");
            }
        }

        [Test]
        public void AllyAtBackWallCanYieldSidewaysAndBlockedPreferredSideUsesFreeSide()
        {
            using var w = new ReplayWorld("ally free side");
            Start(w, true, new[] { Floor, CombatArenaTests.Box(1, 0, 0, .1f, 4, 4),
                CombatArenaTests.Box(0, 0, -.4f, 20, 4, .1f) });
            var e = Actors(w); var attempts = 0;
            for (var t = 0; t < 30; t++)
            {
                Command(w, e[0], 1); w.Step(); Clear(w, e[0], e[1]);
                attempts += w.World.GetExistingSystemManaged<CombatSpatialIndexSystem>().LastContacts.YieldAttempts;
                foreach (var actor in e)
                {
                    Assert.That(Position(w, actor).x, Is.LessThanOrEqualTo(.60021f));
                    Assert.That(Position(w, actor).z, Is.GreaterThanOrEqualTo(-.00021f));
                }
            }
            Assert.That(Position(w, e[0]).x, Is.GreaterThan(.5f), "A back wall alone does not trap a body with free lateral room.");
            Assert.That(Position(w, e[1]).z, Is.GreaterThan(.4f), "The deterministic preferred negative-Z side is blocked; positive-Z is free.");
            Assert.That(attempts, Is.GreaterThan(0));
        }

        [TestCase(1f)] [TestCase(4f)]
        public void InitialOverlapSeparatesByMassWithoutRoleBiasAndCoincidenceIsFinite(float mass)
        {
            using var w = new ReplayWorld("mass overlap"); Start(w, true); var e = Actors(w);
            w.Manager.SetComponentData(e[0], new CombatBody { Radius = .3f, Height = 2, Mass = mass });
            w.Manager.SetComponentData(e[1], new CombatPosition { Value = new float3(.4f, 0, 0) });
            w.Step(); Clear(w, e[0], e[1]);
            var left = -Position(w, e[0]).x; var right = Position(w, e[1]).x - .4f;
            Assert.That(left * mass, Is.EqualTo(right).Within(.00021));
            // Equal-mass coincident centres need at most .3 displacement each, deterministic fallback axis.
            w.Manager.SetComponentData(e[0], Body());
            w.Manager.SetComponentData(e[0], new CombatPosition()); w.Manager.SetComponentData(e[1], new CombatPosition());
            w.Step(); Clear(w, e[0], e[1]);
            Assert.That(math.length(Position(w, e[0]).xz), Is.LessThanOrEqualTo(.30021f));
        }

        [TestCase(1f, true)] [TestCase(2.1f, false)]
        public void AirborneBodiesOnlyBlockWhenHeightIntervalsOverlap(float height, bool blocks)
        {
            using var w = new ReplayWorld("height contact"); Start(w); var e = Actors(w);
            w.Manager.SetComponentData(e[0], new CombatPosition { Value = new float3(0, height, 0) });
            w.Manager.SetComponentData(e[0], new CombatMotion { Grounded = false, GroundHeight = 0 });
            Command(w, e[0], 1); w.Step(); Clear(w, e[0], e[1]);
            Assert.That(Position(w, e[0]).x, blocks ? Is.LessThan(.00021f) : Is.GreaterThan(.06f));
            Assert.That(w.Manager.GetComponentData<CombatMotion>(e[0]).VerticalVelocity,
                Is.EqualTo(-CombatMovementTuning.Gravity / 60).Within(1e-6), "Corrections do not apply gravity again.");
        }

        [TestCase(.01f)] [TestCase(2f)] [TestCase(8f)]
        public void SupportedBodySizesStopAtTheirRealSumOfRadii(float radius)
        {
            using var w = new ReplayWorld("contact body size"); Start(w); var e = Actors(w);
            foreach (var actor in e) w.Manager.SetComponentData(actor, Body(radius));
            w.Manager.SetComponentData(e[1], new CombatPosition { Value = new float3(2 * radius, 0, 0) });
            Command(w, e[0], 1); w.Step(); Clear(w, e[0], e[1]);
            Assert.That(Position(w, e[0]).x, Is.EqualTo(0).Within(.00021));
        }

        [Test]
        public void LandingLockAllowsPassiveYieldWithoutRestartingLandingOrChangingFacing()
        {
            using var w = new ReplayWorld("landing ally"); Start(w, true); var e = Actors(w);
            w.Step();
            var m = w.Manager.GetComponentData<CombatMotion>(e[1]); m.LandTick = w.Tick; m.LandingRecoveryTicks = 12;
            w.Manager.SetComponentData(e[1], m);
            var facing = w.Manager.GetComponentData<CombatFacing>(e[1]);
            Command(w, e[0], 1); Command(w, e[1], -1, pressed: CommandButtons.Jump); w.Step();
            Clear(w, e[0], e[1]); Assert.That(Position(w, e[1]).x, Is.GreaterThan(.65f));
            var after = w.Manager.GetComponentData<CombatMotion>(e[1]);
            Assert.That(after.LandTick, Is.EqualTo(m.LandTick)); Assert.That(after.MoveStreakDistance, Is.Zero);
            Assert.That(w.Manager.GetComponentData<CombatFacing>(e[1]), Is.EqualTo(facing));
        }

        [TestCase(false)] [TestCase(true)]
        public void ContactKeepsAttackFacingAndLedgerAndAppliesPhaseSpeedOnlyOnce(bool allies)
        {
            using var w = new ReplayWorld("strike contacts"); Start(w, allies); var e = Actors(w);
            var timing = CombatAttackTuning.Get(CombatAttackKind.Hand1).DefaultTiming;
            var previous = 0f;
            for (var t = 0; t < timing.End; t++)
            {
                Command(w, e[0], 1, pressed: t == 0 ? CommandButtons.PunchLeft : CommandButtons.None); w.Step();
                var state = w.Manager.GetComponentData<CombatAttackState>(e[0]);
                Assert.That(state.Kind, Is.EqualTo(CombatAttackKind.Hand1));
                Assert.That(w.Manager.GetComponentData<CombatFacing>(e[0]).Value, Is.EqualTo(FacingDirection.Right));
                var advance = Position(w, e[0]).x - previous; previous = Position(w, e[0]).x;
                var multiplier = t < timing.ActiveStart ? .5f : .25f;
                if (allies) Assert.That(advance, Is.InRange((4f / 60) * multiplier * (5f / 6) - .00021f, (4f / 60) * multiplier + .00021f),
                    "The ally may leave the path; controlled speed is phase-scaled once, with bounded contact resistance.");
                else Assert.That(advance, Is.EqualTo(0).Within(.00021));
                Clear(w, e[0], e[1]);
            }
            Assert.That(w.Manager.GetComponentData<CombatHealth>(e[1]).Current,
                Is.EqualTo(allies ? 100 : 96), "Ordinary hit owner/one-hit ledger remains authoritative.");
            w.Step(); Assert.That(w.Manager.GetComponentData<CombatAttackState>(e[0]).IsAttacking, Is.False);
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(10)]
        public void ChunkOrderSourceAndPacingPreservePhysicalStateAndFinalSnapshots(int pacing)
        {
            using var a = new ReplayWorld("contacts canonical"); using var b = new ReplayWorld("contacts reordered");
            Start(a, true); Start(b, true); var ae = Actors(a); var be = Actors(b);
            wReorder(b, be);
            for (uint t = 1; t <= 60; t++)
            {
                foreach (var pair in new[] { (a, ae), (b, be) })
                    pair.Item1.SubmitBetweenFrames(new CombatCommand { Combatant = pair.Item1.Manager.GetComponentData<CombatantId>(pair.Item2[0]),
                        Tick = t, MoveX = 1, Source = pair.Item1 == a ? CommandSource.Player : CommandSource.AI });
                // Queue supports a finite lead, so advance instead of submitting 60 future commands at once.
                if (t % pacing == 0) b.Frame(pacing);
                a.Step();
            }
            for (var i = 0; i < 60; i++)
            {
                SnapshotMatchesFinalState(a.Records[i]); SnapshotMatchesFinalState(b.Records[i]);
                foreach (var id in new uint[] { 1, 2 })
                {
                    var ac = a.Records[i].Combatant(id); var bc = b.Records[i].Combatant(id);
                    Assert.That(bc.Position, Is.EqualTo(ac.Position)); Assert.That(bc.Motion, Is.EqualTo(ac.Motion));
                }
            }
            Assert.That(b.SpatialLog, Is.EqualTo(a.SpatialLog));
        }
        static void wReorder(ReplayWorld w, Entity[] e)
        {
            foreach (var actor in e.Reverse()) w.Manager.AddComponentData(actor, new ContactChunkMarker());
            w.Manager.RemoveComponent<ContactChunkMarker>(e[1]);
        }

        [Test]
        public void FastSmallBodiesCannotExchangeSidesInTheActualMovementPipeline()
        {
            using var w = new ReplayWorld("fast body exchange"); Start(w); var e = Actors(w);
            w.Manager.SetComponentData(e[0], Body(.01f)); w.Manager.SetComponentData(e[1], Body(.01f));
            w.Manager.SetComponentData(e[0], new CombatPosition { Value = new float3(-.025f, 0, 0) });
            w.Manager.SetComponentData(e[1], new CombatPosition { Value = new float3(.025f, 0, 0) });
            Command(w, e[0], 1); Command(w, e[1], -1); w.Step(); Clear(w, e[0], e[1]);
            Assert.That(Position(w, e[0]).x, Is.LessThan(0)); Assert.That(Position(w, e[1]).x, Is.GreaterThan(0));
            Assert.That(w.World.GetExistingSystemManaged<CombatSpatialIndexSystem>().LastContacts.PeakPairs, Is.EqualTo(1));
        }

        [Test]
        public void ACorrectionFindsTheNextNeighbourThatWasOutsideTheOriginalProposals()
        {
            using var w = new ReplayWorld("new neighbour"); var setup = BattleSetup.Default; setup.HumanAllies = 1;
            w.Request(BattleRequest.Start(setup)); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            var e = Actors(w);
            for (var i = 0; i < e.Length; i++)
            {
                w.Manager.SetComponentData(e[i], new CombatTeam { Value = 1 });
                w.Manager.SetComponentData(e[i], new CombatPosition { Value = new float3(i == 2 ? 1.23f : i * .6f, 0, 0) });
            }
            Command(w, e[0], 1); w.Step();
            Clear(w, e[0], e[1]); Clear(w, e[1], e[2]);
            Assert.That(Position(w, e[2]).x, Is.GreaterThan(1.23f));
            Assert.That(w.World.GetExistingSystemManaged<CombatSpatialIndexSystem>().LastContacts.Iterations, Is.GreaterThan(1));
        }

        [Test]
        public void OpposingGroupsInCorridorStaySeparatedAndDoNotPassWalls()
        {
            using var w = new ReplayWorld("opposing corridor");
            var arena = w.World.GetExistingSystemManaged<CombatArenaSystem>();
            Assert.That(arena.Stage(arena.Acquire(), new[] { Floor, CombatArenaTests.Box(0, 0, .5f, 20, 4, .1f),
                CombatArenaTests.Box(0, 0, -.5f, 20, 4, .1f) }), Is.True);
            var setup = BattleSetup.Default; setup.HumanAllies = setup.AiAllies = 1;
            w.Request(BattleRequest.Start(setup)); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            var e = Actors(w); var x = new[] { 0f, 1.8f, .6f, 1.2f };
            for (var i = 0; i < e.Length; i++) w.Manager.SetComponentData(e[i], new CombatPosition { Value = new float3(x[i], 0, 0) });
            for (var t = 0; t < 60; t++)
            {
                foreach (var actor in e) Command(w, actor, (sbyte)(w.Manager.GetComponentData<CombatTeam>(actor).Value == 1 ? 1 : -1));
                w.Step();
                for (var i = 0; i < e.Length; i++)
                {
                    Assert.That(math.abs(Position(w, e[i]).z), Is.LessThanOrEqualTo(.10021f));
                    for (var j = i + 1; j < e.Length; j++) Clear(w, e[i], e[j]);
                }
            }
            Assert.That(Position(w, e[2]).x, Is.LessThan(Position(w, e[3]).x));
        }

        [Test]
        public void AllyPressureOnRampUnderCeilingPreservesSupportAndHeadClearance()
        {
            using var w = new ReplayWorld("ramp ceiling group");
            var ramp = new CombatArenaShape { Kind = CombatArenaKind.Ramp, Center = float2.zero,
                HalfSize = new float2(2, 2), Bottom = 0, Top = 1, AxisX = new float2(1, 0) };
            Start(w, true, new[] { Floor, ramp, CombatArenaTests.Box(0, 2.6f, 0, 3, 3, 3) }); var e = Actors(w);
            w.Manager.SetComponentData(e[0], new CombatPosition { Value = new float3(-1, .325f, 0) });
            w.Manager.SetComponentData(e[1], new CombatPosition { Value = new float3(-.4f, .475f, 0) });
            foreach (var actor in e) w.Manager.SetComponentData(actor, new CombatMotion { Grounded = true, GroundHeight = Position(w, actor).y });
            for (var t = 0; t < 60; t++)
            {
                Command(w, e[0], 1); w.Step(); Clear(w, e[0], e[1]);
                foreach (var actor in e)
                {
                    var p = Position(w, actor);
                    Assert.That(p.y + 2, Is.LessThanOrEqualTo(2.60021f));
                    Assert.That(p.y, Is.EqualTo((p.x + .3f + 2) * .25f).Within(.00021));
                    Assert.That(w.Manager.GetComponentData<CombatMotion>(actor).LandTick, Is.Zero);
                }
            }
        }

        [Test]
        public void YieldingAcrossFiniteFloorEdgeLosesSupportWithoutStandingOnOtherBodies()
        {
            using var w = new ReplayWorld("group floor edge");
            Start(w, true, new[] { CombatArenaTests.Box(0, -.5f, 0, 1, 0, 3),
                CombatArenaTests.Box(0, 0, .4f, 20, 4, .1f), CombatArenaTests.Box(0, 0, -.4f, 20, 4, .1f) }); var e = Actors(w);
            var fell = false;
            for (var t = 0; t < 60; t++)
            {
                Command(w, e[0], 1); w.Step(); Clear(w, e[0], e[1]);
                fell |= !w.Manager.GetComponentData<CombatMotion>(e[1]).Grounded;
            }
            Assert.That(fell, Is.True); Assert.That(Position(w, e[1]).y, Is.LessThan(0));
        }

        [Test]
        public void IterativeExcursionOffAnEdgeAndBackDoesNotCreateAnotherLanding()
        {
            using var w = new ReplayWorld("transient support loss");
            var arena = w.World.GetExistingSystemManaged<CombatArenaSystem>();
            Assert.That(arena.Stage(arena.Acquire(), new[] { CombatArenaTests.Box(0, -.5f, 0, 1, 0, 5) }), Is.True);
            var setup = BattleSetup.Default; setup.HumanAllies = setup.AiAllies = 1;
            w.Request(BattleRequest.Start(setup)); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            var e = Actors(w);
            for (var i = 0; i < 4; i++)
            {
                w.Manager.SetComponentData(e[i], new CombatPosition { Value = new float3(.0995f + i * .6f, 0, 0) });
                w.Manager.SetComponentData(e[i], new CombatTeam { Value = i == 3 ? (byte)2 : (byte)1 });
                w.Manager.SetComponentData(e[i], new CombatMotion { Grounded = i < 3, GroundHeight = 0 });
            }
            var table = CombatStrikeSchedule.Default;
            table.Hand1 = CombatStrikeTiming.FromTicks(0, 60, 1, 4); // Explicit Active-speed fixture, not project tuning.
            table.LandingRecoveryTicks = 12;
            CombatStrikeSchedule.Publish(w.Manager, table);
            Command(w, e[0], 1, pressed: CommandButtons.PunchLeft); w.Step();
            var d = w.World.GetExistingSystemManaged<CombatSpatialIndexSystem>().LastContacts;
            Assert.That(d.FrozenBodies, Is.Zero, "This must exercise a solved island, not rollback to original grounded state.");
            Assert.That(d.Iterations, Is.GreaterThan(2));
            Assert.That(Position(w, e[2]).x, Is.LessThan(1.3f), "The final disk still overlaps the finite floor.");
            Assert.That(w.Manager.GetComponentData<CombatMotion>(e[2]).Grounded, Is.True);
            w.Step();
            Assert.That(w.Manager.GetComponentData<CombatMotion>(e[2]).LandTick, Is.Zero,
                "Iteration excursions are not physical flight/landing events or a fresh input lock.");
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void MissingPhysicalInputFailsBeforeAnyPositionWrites(int missing)
        {
            using var w = new ReplayWorld("incomplete contact inputs"); Start(w); w.Step(); var e = Actors(w);
            var before = e.Select(actor => Position(w, actor)).ToArray();
            if (missing == 0) w.Manager.RemoveComponent<CombatMotion>(e[1]);
            if (missing == 1) w.Manager.RemoveComponent<AppliedCombatCommand>(e[1]);
            if (missing == 2) w.Manager.RemoveComponent<CombatTeam>(e[1]);
            if (missing == 3) w.Manager.RemoveComponent<CombatBody>(e[1]);
            var movement = w.World.GetExistingSystem<CombatMovementSystem>();
            // Entities can call a managed ISystem via a Burst thunk which logs instead of rethrowing.
            // Assert the same rejection in both execution modes, without allowing unrelated errors through.
            var rejected = false; string unexpected = null;
            void OnLog(string message, string stack, LogType type)
            {
                if (type == LogType.Exception && message.Contains("Incomplete contact inputs:")) rejected = true;
                else if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert) unexpected = message;
            }
            var oldIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            Application.logMessageReceived += OnLog;
            try
            {
                try { movement.Update(w.World.Unmanaged); }
                catch (InvalidOperationException ex)
                {
                    StringAssert.Contains("Incomplete contact inputs:", ex.Message); rejected = true;
                }
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                LogAssert.ignoreFailingMessages = oldIgnore;
            }
            Assert.That(rejected, Is.True); Assert.That(unexpected, Is.Null);
            for (var i = 0; i < e.Length; i++) Assert.That(Position(w, e[i]), Is.EqualTo(before[i]));
            Assert.That(w.World.GetExistingSystemManaged<CombatSpatialIndexSystem>().TryGetSnapshotInfo(out _), Is.False);
        }

        [Test]
        public void DeepUnresolvableIslandIsDiagnosedWithoutFreezingDistantMotion()
        {
            using var w = new ReplayWorld("contact failure islands"); var setup = BattleSetup.Default;
            setup.HumanAllies = setup.AiAllies = 1;
            w.Request(BattleRequest.Start(setup)); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            var e = Actors(w);
            for (var i = 0; i < 3; i++)
            {
                w.Manager.SetComponentData(e[i], Body(2));
                w.Manager.SetComponentData(e[i], new CombatPosition());
            }
            w.Manager.SetComponentData(e[3], new CombatPosition { Value = new float3(20, 0, 0) });
            Command(w, e[3], 1); w.Step();
            for (var i = 0; i < 3; i++) Assert.That(Position(w, e[i]), Is.EqualTo(float3.zero));
            Assert.That(Position(w, e[3]).x, Is.GreaterThan(20.06f));
            var d = w.World.GetExistingSystemManaged<CombatSpatialIndexSystem>().LastContacts;
            Assert.That(d.FrozenBodies, Is.EqualTo(3)); Assert.That(d.ResidualComplete, Is.True);
            Assert.That(d.ResidualPenetration, Is.EqualTo(4).Within(.0001), "Actual final overlap after island rollback, not a pre-rollback lower bound.");
            Assert.That(d.PeakPairs, Is.EqualTo(3));
        }

        [TestCase(100, false)] [TestCase(100, true)] [TestCase(400, false)] [TestCase(400, true)]
        public void PressingFrontsKeepMovingWithoutRollbackOrBulldozing(int count, bool impulse)
        {
            // Two touching formations walk into each other (the benchmark's opposing front). Before #66 the iteration
            // budget left a tiny swept violation and the whole island rolled back every tick (400) or after one impulse (100).
            using var w = new ReplayWorld("pressing fronts");
            var arena = w.World.GetExistingSystemManaged<CombatArenaSystem>();
            Assert.That(arena.Stage(arena.Acquire(), new[] { CombatArenaTests.Box(0, -.5f, 0, 100, 0, 100) }), Is.True);
            var setup = BattleSetup.Default; setup.HumanAllies = setup.AiAllies = (ushort)(count / 2 - 1);
            w.Request(BattleRequest.Start(setup)); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            var e = Actors(w);
            for (var i = 0; i < e.Length; i++)
            {
                var team = i < count / 2 ? (byte)1 : (byte)2; var k = team == 1 ? i : i - count / 2;
                w.Manager.SetComponentData(e[i], new CombatTeam { Value = team });
                w.Manager.SetComponentData(e[i], new CombatPosition { Value = new float3(team == 1 ? -.3f - (k / 20) * .6f : .3f + (k / 20) * .6f, 0, (k % 20) * .6f) });
            }
            var owner = w.World.GetExistingSystemManaged<CombatSpatialIndexSystem>();
            var start = e.Select(x => Position(w, x)).ToArray();
            for (var t = 0; t < 60; t++)
            {
                foreach (var actor in e) Command(w, actor, (sbyte)(w.Manager.GetComponentData<CombatTeam>(actor).Value == 1 ? 1 : -1));
                if (impulse && t == 5) CombatImpulse.Submit(w.Manager, w.Manager.GetComponentData<CombatantId>(e[0]), new float3(8, 0, 2));
                w.Step();
                var c = owner.LastContacts;
                Assert.That(c.RolledBackBodies, Is.Zero, $"tick {t}: no island rollback (holds {c.LocalHolds}, iterations {c.Iterations}, constraint {c.ResidualConstraint}, unresolved {c.UnresolvedPenetration}, slop {c.SlopAcceptedPairs}, work limit {c.WorkLimit}, world rejections {c.WorldRejections}, physical {c.PhysicalBodies})");
                Assert.That(c.LocalHolds, Is.LessThanOrEqualTo(count / 5), $"tick {t}: only jammed bodies wait this tick");
            }
            var slop = CombatContactTuning.SlopRadiusFraction * .3f;
            for (var i = 0; i < e.Length; i++)
            {
                var p = Position(w, e[i]);
                Assert.That(math.all(math.isfinite(p)), Is.True);
                // Walking alone never shoves the other front; the single impulse may move a few bodies physically.
                if (!impulse) Assert.That(math.abs(p.x - start[i].x), Is.LessThan(.06f), "No bulldozing through accepted slop.");
            }
            using var rows = new NativeList<CombatSpatialBody>(Allocator.Temp);
            for (var i = 0; i < e.Length; i++)
            {
                Assert.That(owner.TryQueryAabb(new CombatSpatialBounds { Min = Position(w, e[i]) - .7f, Max = Position(w, e[i]) + .7f }, rows), Is.True);
                foreach (var other in rows) if (other.Id.Value > w.Manager.GetComponentData<CombatantId>(e[i]).Value)
                    Assert.That(math.distance(Position(w, e[i]).xz, other.Position.xz), Is.GreaterThanOrEqualTo(.6f - slop - .00021f));
            }
            // Held bodies wait only while jammed: the controlled fighter's front retreats when it stops pressing.
            var before = Position(w, e[0]).x;
            for (var t = 0; t < 20; t++)
            {
                foreach (var actor in e) Command(w, actor, (sbyte)(w.Manager.GetComponentData<CombatTeam>(actor).Value == 1 ? -1 : 0));
                w.Step();
                Assert.That(owner.LastContacts.RolledBackBodies, Is.Zero);
            }
            Assert.That(Position(w, e[0]).x, Is.LessThan(before - .3f), "The controlled fighter can retreat out of the press.");
        }

        [Test]
        public void PausedRestartKeepsOldContactDiagnosticsExplicitlyInTheirOldBattle()
        {
            using var w = new ReplayWorld("contact diagnostic epoch"); Start(w, true); var e = Actors(w);
            Command(w, e[0], 1); w.Step();
            var owner = w.World.GetExistingSystemManaged<CombatSpatialIndexSystem>();
            var old = owner.LastContacts;
            Assert.That(old.BattleNumber, Is.EqualTo(w.Session.BattleNumber));
            w.Request(BattleRequest.Restart(BattleSetup.Default));
            w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            Assert.That(w.Tick, Is.EqualTo(old.Tick), "Paused lifecycle does not invent a physics tick.");
            Assert.That(owner.LastContacts.BattleNumber, Is.Not.EqualTo(w.Session.BattleNumber),
                "Last-measured stats must not masquerade as a solve of the replacement battle at the same absolute tick.");
            Assert.That(owner.TryGetSnapshotInfo(out _), Is.False);
            w.Step();
            Assert.That(owner.LastContacts.BattleNumber, Is.EqualTo(w.Session.BattleNumber));
            Assert.That(owner.LastContacts.PeakPairs, Is.Zero);
        }

        [Test]
        public void PauseDisableDespawnRestartAndOwnerReplacementCannotReuseOldPairs()
        {
            using var w = new ReplayWorld("contact lifetime"); Start(w, true); var e = Actors(w);
            Command(w, e[0], 1); w.Step(); var before = Position(w, e[0]);
            w.World.SetTime(new TimeData(w.World.Time.ElapsedTime, 0));
            w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update(); w.FixedStep.Update();
            Assert.That(Position(w, e[0]), Is.EqualTo(before));
            var disabledPosition = Position(w, e[1]);
            w.Manager.SetComponentEnabled<InCombat>(e[1], false);
            Command(w, e[0], 1); w.Step(); Assert.That(Position(w, e[1]), Is.EqualTo(disabledPosition));
            w.Request(BattleRequest.Despawn(w.Manager.GetComponentData<CombatantId>(e[1]))); w.Step();
            Assert.That(w.Manager.Exists(e[1]), Is.False);
            w.Request(BattleRequest.Restart(BattleSetup.Default)); w.Step();
            Assert.That(w.World.GetExistingSystemManaged<CombatSpatialIndexSystem>().LastContacts.PeakPairs, Is.Zero);
            Assert.That(w.Records.Last().Spatial.Length, Is.EqualTo(2));
            var old = w.World.GetExistingSystemManaged<CombatSpatialIndexSystem>();
            var group = w.World.GetExistingSystemManaged<CombatStateUpdateSystemGroup>();
            group.RemoveSystemFromUpdateList(old); w.World.DestroySystemManaged(old);
            var replacement = w.World.CreateSystemManaged<CombatSpatialIndexSystem>();
            group.AddSystemToUpdateList(replacement); group.SortSystems();
            w.Step(); Assert.That(replacement.TryGetSnapshotInfo(out var info), Is.True);
            Assert.That(info.BodyCount, Is.EqualTo(2)); Assert.That(replacement.LastContacts.PeakPairs, Is.Zero);
            var fresh = Actors(w);
            w.Manager.AddComponent<Disabled>(fresh[1]);
            Command(w, fresh[0], 1); w.Step();
            Assert.That(w.Records.Last().Spatial.Length, Is.EqualTo(1));
            w.Request(BattleRequest.End()); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            Assert.That(replacement.TryGetSnapshotInfo(out _), Is.False);
        }
    }
}
