using System;
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
    /// <summary>Deterministic test-only impulse source keyed by battle-relative tick and role (Collection phase, before Movement).</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(CombatCommandCollectionSystemGroup))]
    public partial class ScriptedImpulseProducer : SystemBase
    {
        public ReplayWorld Driver;
        public readonly List<(uint RelativeTick, ReplayRole Role, float3 Impulse)> Script = new List<(uint, ReplayRole, float3)>();

        protected override void OnUpdate()
        {
            var session = SystemAPI.GetSingleton<BattleSession>();
            if (!session.IsActive) return;
            Driver.Track(session);
            var relative = session.RelativeTick(SystemAPI.GetSingleton<SimulationClock>().Tick);
            var buffer = SystemAPI.GetSingletonBuffer<CombatImpulse>();
            foreach (var (tick, role, impulse) in Script)
                if (tick == relative)
                    buffer.Add(new CombatImpulse { Target = Driver.Resolve(role), Impulse = impulse, BattleNumber = session.BattleNumber });
        }
    }

    /// <summary>A future Hit-phase producer stand-in: submits after Hit of tick T, so Movement T+1 applies it.</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(CombatStateUpdateSystemGroup))]
    [UpdateAfter(typeof(CombatHitSystem))]
    public partial class AfterHitImpulseProducer : SystemBase
    {
        public uint OnTick;
        public CombatantId Target;
        public float3 Impulse;

        protected override void OnUpdate()
        {
            if (SystemAPI.GetSingleton<SimulationClock>().Tick != OnTick) return;
            SystemAPI.GetSingletonBuffer<CombatImpulse>().Add(new CombatImpulse
            {
                Target = Target, Impulse = Impulse, BattleNumber = SystemAPI.GetSingleton<BattleSession>().BattleNumber,
            });
        }
    }

    public class CombatImpulseTests
    {
        const float Dt = SimulationTiming.TickDuration;
        static CombatBody Body(float radius = .3f, float mass = 1) => new CombatBody { Radius = radius, Height = 2, Mass = mass };
        static CombatArenaShape Floor => CombatArenaTests.Box(0, -.5f, 0, 20, 0, 20);

        static Entity[] Actors(ReplayWorld w)
        {
            using var q = w.Manager.CreateEntityQuery(typeof(CombatantId));
            using var e = q.ToEntityArray(Allocator.Temp);
            return e.ToArray().OrderBy(x => w.Manager.GetComponentData<CombatantId>(x).Value).ToArray();
        }

        static Entity[] Start(ReplayWorld w, CombatArenaShape[] arena = null, float3? ai = null, ushort allies = 0)
        {
            if (arena != null)
            {
                var owner = w.World.GetExistingSystemManaged<CombatArenaSystem>();
                Assert.That(owner.Stage(owner.Acquire(), arena), Is.True);
            }
            var setup = BattleSetup.Default; setup.HumanAllies = setup.AiAllies = allies;
            setup.Human.Position = float3.zero; setup.Ai.Position = ai ?? new float3(10, 0, 0);
            w.Request(BattleRequest.Start(setup));
            w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            return Actors(w);
        }

        static float3 P(ReplayWorld w, Entity e) => w.Manager.GetComponentData<CombatPosition>(e).Value;
        static CombatMotion M(ReplayWorld w, Entity e) => w.Manager.GetComponentData<CombatMotion>(e);
        static CombatantId Id(ReplayWorld w, Entity e) => w.Manager.GetComponentData<CombatantId>(e);
        static void Push(ReplayWorld w, Entity e, float3 j) => CombatImpulse.Submit(w.Manager, Id(w, e), j);
        static CombatContactDiagnostics Contacts(ReplayWorld w) => w.World.GetExistingSystemManaged<CombatSpatialIndexSystem>().LastContacts;
        static int Pending(ReplayWorld w)
        {
            using var q = w.Manager.CreateEntityQuery(typeof(CombatImpulseQueue));
            return w.Manager.GetBuffer<CombatImpulse>(q.GetSingletonEntity()).Length;
        }
        static void Command(ReplayWorld w, Entity e, sbyte x, CommandButtons pressed = CommandButtons.None, CommandSource source = CommandSource.Player) =>
            w.SubmitBetweenFrames(new CombatCommand { Combatant = Id(w, e), Tick = w.Tick + 1, MoveX = x, Source = source, Pressed = pressed });
        static void Separated(ReplayWorld w, Entity a, Entity b)
        {
            var pa = P(w, a); var pb = P(w, b);
            var ab = w.Manager.GetComponentData<CombatBody>(a); var bb = w.Manager.GetComponentData<CombatBody>(b);
            Assert.That(math.all(math.isfinite(pa)) && math.all(math.isfinite(pb)), Is.True);
            if (pa.y < pb.y + bb.Height && pb.y < pa.y + ab.Height)
                Assert.That(math.distance(pa.xz, pb.xz), Is.GreaterThanOrEqualTo(ab.Radius + bb.Radius - .00021f));
        }

        // Independent expectation of the documented integrator: displacement v·dt, then Coulomb deceleration and stop speed.
        static (float distance, int ticks) Coast(float v0, float deceleration)
        {
            float v = v0, x = 0; var ticks = 0;
            while (v > 0) { x += v * Dt; ticks++; var next = v - deceleration * Dt; v = next <= CombatImpulseTuning.StopSpeed ? 0 : next; }
            return (x, ticks);
        }

        [TestCase(1f)] [TestCase(4f)]
        public void ImpulsesGiveDeltaVOverMassOnceSumInOrderAndDecayToRest(float mass)
        {
            using var w = new ReplayWorld("impulse mass"); var e = Start(w);
            w.Manager.SetComponentData(e[0], Body(mass: mass));
            Push(w, e[0], new float3(4, 0, 0)); Push(w, e[0], new float3(0, 0, -2 * mass)); Push(w, e[0], new float3(2, 0, 0));
            w.Step();
            var v0 = new float2(6 / mass, -2);
            Assert.That(P(w, e[0]).x, Is.EqualTo(v0.x * Dt).Within(1e-5)); Assert.That(P(w, e[0]).z, Is.EqualTo(v0.y * Dt).Within(1e-5));
            var speed = math.length(v0) - CombatImpulseTuning.GroundDeceleration * Dt;
            Assert.That(math.length(M(w, e[0]).ExternalVelocity), Is.EqualTo(speed).Within(1e-4));
            Assert.That(Contacts(w).ImpulsesApplied, Is.EqualTo(3)); Assert.That(Pending(w), Is.Zero, "Consumed exactly once.");
            var (distance, ticks) = Coast(math.length(v0), CombatImpulseTuning.GroundDeceleration);
            w.Step(ticks + 2);
            Assert.That(math.length(P(w, e[0]).xz), Is.EqualTo(distance).Within(2e-4));
            Assert.That(M(w, e[0]).ExternalVelocity, Is.EqualTo(float2.zero));
            Assert.That(M(w, e[0]).MoveStreakDistance, Is.Zero, "Passive displacement is not running.");
            Assert.That(M(w, e[0]).LastMoveTick, Is.Zero);
            Assert.That(w.Manager.GetComponentData<CombatFacing>(e[0]).Value, Is.EqualTo(FacingDirection.Right));
        }

        [Test]
        public void ZeroImpulseKeepsTheStage043RecordExactly()
        {
            using var a = new ReplayWorld("no impulse"); using var b = new ReplayWorld("zero impulse");
            var ea = Start(a, new[] { Floor }, new float3(.6f, 0, 0)); var eb = Start(b, new[] { Floor }, new float3(.6f, 0, 0));
            for (var t = 0; t < 30; t++)
            {
                Command(a, ea[0], 1); Command(b, eb[0], 1);
                Push(b, eb[0], float3.zero); Push(b, eb[1], float3.zero);
                a.Step(); b.Step();
            }
            Assert.That(b.RawLog, Is.EqualTo(a.RawLog));
            Assert.That(Contacts(b).PhysicalBodies, Is.Zero);
        }

        [Test]
        public void ControlledAndAiRolesAndSourcesGetIdenticalPhysics()
        {
            using var w = new ReplayWorld("role parity"); var e = Start(w, new[] { Floor }, new float3(0, 0, 5));
            Push(w, e[0], new float3(3, 0, 0)); Push(w, e[1], new float3(3, 0, 0));
            Command(w, e[0], 0); Command(w, e[1], 0, source: CommandSource.AI);
            for (var t = 0; t < 20; t++) w.Step();
            Assert.That(P(w, e[1]).x - P(w, e[0]).x, Is.EqualTo(0).Within(1e-6));
            Assert.That(M(w, e[1]).ExternalVelocity, Is.EqualTo(M(w, e[0]).ExternalVelocity));
        }

        [Test]
        public void StrikePhasesAndLandingLockScaleOnlyControlNeverTheImpulse()
        {
            using var w = new ReplayWorld("impulse during strike"); var e = Start(w, new[] { Floor });
            var before = w.Manager.GetComponentData<CombatFacing>(e[0]);
            Command(w, e[0], -1, CommandButtons.HandSeries); Push(w, e[0], new float3(6, 0, 0));
            w.Step();
            var strike = w.Manager.GetComponentData<CombatAttackState>(e[0]);
            Assert.That(strike.Kind, Is.EqualTo(CombatAttackKind.Hand1));
            Assert.That(P(w, e[0]).x, Is.EqualTo(6 * Dt - CombatMovementTuning.MoveSpeed * Dt * .5f).Within(.00021),
                "Full external step plus 50% Windup control in the opposite direction.");
            Assert.That(w.Manager.GetComponentData<CombatFacing>(e[0]).Value, Is.EqualTo(FacingDirection.Right), "Retreat control and impulse do not turn away from the nearest enemy.");
            Assert.That(before.Value, Is.EqualTo(FacingDirection.Right));
            Assert.That(w.Manager.GetComponentData<CombatAttackState>(e[0]).StartTick, Is.EqualTo(strike.StartTick));

            // Landing recovery blocks control, not physics.
            var m = M(w, e[1]); m.LandTick = w.Tick; m.LandingRecoveryTicks = 12; w.Manager.SetComponentData(e[1], m);
            var x = P(w, e[1]).x;
            Command(w, e[1], -1); Push(w, e[1], new float3(6, 0, 0)); w.Step();
            Assert.That(P(w, e[1]).x - x, Is.EqualTo(6 * Dt).Within(.00021));
            Assert.That(M(w, e[1]).LandTick, Is.EqualTo(m.LandTick), "Physical displacement creates no landing.");
            Assert.That(M(w, e[1]).MoveStreakDistance, Is.Zero);
        }

        [Test]
        public void PositiveLiftLeavesSupportWithoutJumpLandsOnceAndLowCeilingClips()
        {
            using var w = new ReplayWorld("impulse lift"); var e = Start(w, new[] { Floor, CombatArenaTests.Box(10, 2.3f, 0, 1, 3, 1) });
            Push(w, e[0], new float3(0, 6, 0)); Push(w, e[1], new float3(0, 6, 0));
            w.Step();
            Assert.That(M(w, e[0]).Grounded, Is.False);
            Assert.That(M(w, e[0]).VerticalVelocity, Is.EqualTo(6 - CombatMovementTuning.Gravity * Dt).Within(1e-5));
            Assert.That(P(w, e[1]).y + 2, Is.LessThanOrEqualTo(2.30021f), "The ceiling above the second fighter stops the head.");
            var landings = 0; var last = 0u;
            for (var t = 0; t < 120; t++)
            {
                w.Step();
                Assert.That(P(w, e[1]).y + 2, Is.LessThanOrEqualTo(2.30021f));
                var land = M(w, e[0]).LandTick;
                if (land != last) { landings++; last = land; }
            }
            Assert.That(landings, Is.EqualTo(1)); Assert.That(M(w, e[0]).Grounded, Is.True); Assert.That(P(w, e[0]).y, Is.EqualTo(0).Within(1e-5));
            // A downward impulse on support is absorbed: no flight, no landing.
            Push(w, e[0], new float3(0, -6, 0)); w.Step();
            Assert.That(M(w, e[0]).Grounded, Is.True); Assert.That(M(w, e[0]).LandTick, Is.EqualTo(last));
            Assert.That(M(w, e[0]).VerticalVelocity, Is.Zero);
        }

        [TestCase(1f, 1f)] [TestCase(1f, 3f)] [TestCase(3f, 1f)]
        public void EnemyBlocksWalkingButReceivesMassWeightedInelasticTransfer(float ma, float mb)
        {
            using var w = new ReplayWorld("enemy transfer"); var e = Start(w, new[] { Floor }, new float3(.6f, 0, 0));
            w.Manager.SetComponentData(e[0], Body(mass: ma)); w.Manager.SetComponentData(e[1], Body(mass: mb));
            for (var t = 0; t < 10; t++) { Command(w, e[0], 1); w.Step(); Separated(w, e[0], e[1]); }
            Assert.That(P(w, e[1]).x, Is.EqualTo(.6f), "Ordinary control never bulldozes a passive enemy.");
            var v0 = 6f;
            Push(w, e[0], new float3(v0 * ma, 0, 0)); w.Step(); Separated(w, e[0], e[1]);
            var va = M(w, e[0]).ExternalVelocity.x; var vb = M(w, e[1]).ExternalVelocity.x;
            Assert.That(vb, Is.GreaterThan(0), "The enemy is physically pushed.");
            var common = ma * v0 / (ma + mb) - CombatImpulseTuning.GroundDeceleration * Dt;
            Assert.That(va, Is.EqualTo(common).Within(.02f)); Assert.That(vb, Is.EqualTo(common).Within(.02f));
            Assert.That(ma * va + mb * vb, Is.LessThanOrEqualTo(ma * v0 + 1e-3f), "Momentum is not created.");
            Assert.That(ma * va * va + mb * vb * vb, Is.LessThan(ma * v0 * v0), "Inelastic: kinetic energy decreases.");
            Assert.That(M(w, e[1]).MoveStreakDistance, Is.Zero); Assert.That(M(w, e[1]).LastMoveTick, Is.Zero);
        }

        [Test]
        public void ImpulseTravelsAlongAMixedChainWithoutCreatingEnergyOrOverlap()
        {
            using var w = new ReplayWorld("impulse chain"); var e = Start(w, new[] { Floor }, allies: 1);
            float[] mass = { 2, 1, 3, 1 }; byte[] team = { 1, 2, 1, 2 };
            for (var i = 0; i < 4; i++)
            {
                w.Manager.SetComponentData(e[i], new CombatPosition { Value = new float3(i * .6f, 0, 0) });
                w.Manager.SetComponentData(e[i], Body(mass: mass[i])); w.Manager.SetComponentData(e[i], new CombatTeam { Value = team[i] });
            }
            var v0 = 8f; Push(w, e[0], new float3(v0 * mass[0], 0, 0));
            var energy = mass[0] * v0 * v0;
            for (var t = 0; t < 40; t++)
            {
                w.Step();
                var now = e.Select((x, i) => mass[i] * math.lengthsq(M(w, x).ExternalVelocity)).Sum();
                Assert.That(now, Is.LessThanOrEqualTo(energy + 1e-3f), "Contacts never create kinetic energy.");
                energy = now;
                for (var i = 0; i < 4; i++) for (var j = i + 1; j < 4; j++) Separated(w, e[i], e[j]);
            }
            Assert.That(P(w, e[3]).x, Is.GreaterThan(1.8f + .05f), "The last body of the chain moved.");
            Assert.That(e.All(x => M(w, x).ExternalVelocity.Equals(float2.zero)), Is.True, "Friction stops the group.");
            Assert.That(Contacts(w).FrozenBodies, Is.Zero);
        }

        [Test]
        public void WallRemovesNormalVelocityKeepsTangentialAndStoresNothingForLater()
        {
            using var w = new ReplayWorld("impulse wall");
            var e = Start(w, new[] { Floor, CombatArenaTests.Box(.5f, 0, 0, .1f, 4, 10) }, new float3(-5, 0, 0));
            Push(w, e[0], new float3(12, 0, 12));
            for (var t = 0; t < 4; t++) w.Step();
            Assert.That(P(w, e[0]).x, Is.LessThanOrEqualTo(.1f + .00021f), "No body passes the wall.");
            var v = M(w, e[0]).ExternalVelocity;
            Assert.That(v.x, Is.EqualTo(0).Within(1e-3), "Normal velocity removed against the wall.");
            Assert.That(v.y, Is.GreaterThan(9), "Tangential motion keeps sliding (frictionless wall, ground friction only).");
            // Straight pressure: velocity cannot hide in the wall and shoot out later.
            using var w2 = new ReplayWorld("impulse wall pressure");
            var f = Start(w2, new[] { Floor, CombatArenaTests.Box(.5f, 0, 0, .1f, 4, 10) }, new float3(-5, 0, 0));
            for (var t = 0; t < 10; t++) { Push(w2, f[0], new float3(5, 0, 0)); w2.Step(); }
            w2.Step(2);
            Assert.That(M(w2, f[0]).ExternalVelocity, Is.EqualTo(float2.zero));
            var x = P(w2, f[0]).x; w2.Step(10);
            Assert.That(P(w2, f[0]).x, Is.EqualTo(x), "Nothing is released after the pressure stops.");
        }

        // #69 counterexample: the old velocity = achieved displacement / dt kept 6 - .4 = 5.6 u/s INTO the wall after a hit
        // after half a tick of travel. The final velocity of an inelastic impact on a static wall has no normal part.
        [Test]
        public void WallHitAfterPartOfTheTickLeavesNoNormalVelocityAndKeepsTheTangent()
        {
            using var w = new ReplayWorld("toi wall");
            var owner = w.World.GetExistingSystemManaged<CombatArenaSystem>(); var lease = owner.Acquire();
            Assert.That(owner.Stage(lease, new[] { Floor, CombatArenaTests.Box(.45f, 0, 0, .05f, 4, 10) }), Is.True);
            var e = Start(w, null, new float3(-5, 0, 0));
            Push(w, e[0], new float3(12, 0, 6)); w.Step();
            Assert.That(P(w, e[0]).x, Is.EqualTo(.1f).Within(.0005f), "Only the free gap .1 of the requested .2 is travelled.");
            var v = M(w, e[0]).ExternalVelocity;
            Assert.That(v.x, Is.EqualTo(0).Within(1e-5), "No hidden normal velocity into the wall.");
            Assert.That(v.y, Is.EqualTo(6 - CombatImpulseTuning.GroundDeceleration * Dt).Within(1e-4), "Frictionless wall: tangent kept.");
            Push(w, e[0], new float3(-6, 0, 0)); // a later push away from the wall is not cancelled by any stored velocity
            w.Step();
            Assert.That(M(w, e[0]).ExternalVelocity.x, Is.LessThan(-5.5f));

            // Pressure with control + impulse, then the wall disappears: nothing stored is released.
            using var p = new ReplayWorld("toi wall removed");
            var po = p.World.GetExistingSystemManaged<CombatArenaSystem>(); var pl = po.Acquire();
            Assert.That(po.Stage(pl, new[] { Floor, CombatArenaTests.Box(.45f, 0, 0, .05f, 4, 10) }), Is.True);
            var f = Start(p, null, new float3(-5, 0, 0));
            for (var t = 0; t < 6; t++) { Command(p, f[0], 1); Push(p, f[0], new float3(4, 0, 0)); p.Step(); }
            Assert.That(M(p, f[0]).ExternalVelocity, Is.EqualTo(float2.zero), "Combined control + impulse: no normal velocity.");
            Assert.That(po.Stage(pl, new[] { Floor }), Is.True);
            var x = P(p, f[0]).x; p.Step(10);
            Assert.That(P(p, f[0]).x, Is.EqualTo(x), "The released contact frees no old impulse.");
        }

        // #70: rotated acute (45°) and obtuse (135°) corners. Two sequential clip passes pushed velocity back into the first
        // wall of an acute corner; every published velocity must now leave each touched wall alone, in either shape order,
        // and the walls hold nothing for the next tick.
        [TestCase(false)] [TestCase(true)]
        public void RotatedCornersPublishNoVelocityIntoAnyTouchedWallInEitherShapeOrder(bool obtuse)
        {
            var s = math.sqrt(.5f);
            var wallA = CombatArenaTests.Box(-1.1f, 0, 1, .1f, 4, 3); // face x = -1, outward (1,0)
            var normalB = obtuse ? new float2(s, s) : new float2(-s, s);
            var faceB = obtuse ? new float2(0, -2) : float2.zero; // both faces meet wall A at (-1,-1)
            var centerB = faceB - normalB * .1f;
            var wallB = CombatArenaTests.Box(centerB.x, 0, centerB.y, 3, 4, .1f, obtuse ? -math.PI / 4 : math.PI / 4);
            var start = obtuse ? new float3(.5f, 0, .5f) : new float3(-.4f, 0, 1);
            var push = obtuse ? new float3(-12, 0, -12) : new float3(-5, 0, -12);
            var walls = new[] { (new float2(-1, 0), new float2(1, 0)), (faceB, normalB) };
            var trajectories = new float3[2][];
            for (var order = 0; order < 2; order++)
            {
                using var w = new ReplayWorld("rotated corner");
                var e = Start(w, order == 0 ? new[] { Floor, wallA, wallB } : new[] { Floor, wallB, wallA }, new float3(10, 0, 0));
                w.Manager.SetComponentData(e[0], new CombatPosition { Value = start });
                var radius = w.Manager.GetComponentData<CombatBody>(e[0]).Radius;
                Push(w, e[0], push);
                trajectories[order] = new float3[40];
                int both = 0, touched = 0;
                for (var t = 0; t < 40; t++)
                {
                    w.Step();
                    var p = P(w, e[0]).xz; var v = M(w, e[0]).ExternalVelocity;
                    var touching = 0;
                    foreach (var (face, n) in walls)
                    {
                        var gap = math.dot(p - face, n) - radius;
                        Assert.That(gap, Is.GreaterThanOrEqualTo(-.00021f), $"tick {t}: no body inside a wall");
                        if (gap > 1e-3f) continue;
                        touching++;
                        Assert.That(math.dot(v, n), Is.GreaterThanOrEqualTo(-1e-4f), $"tick {t}: hidden velocity {v} into wall {n}");
                    }
                    if (touching == 2) both++;
                    if (touching > 0) touched++;
                    Assert.That(Contacts(w).WallOverflowBodies, Is.Zero);
                    trajectories[order][t] = P(w, e[0]);
                }
                // Acute: the apex holds the body against both walls. Obtuse: it slides off along the second wall instead.
                if (obtuse) Assert.That(touched, Is.GreaterThan(0), "The body reached the walls.");
                else Assert.That(both, Is.GreaterThan(0), "The body reached the corner.");
                Assert.That(M(w, e[0]).ExternalVelocity, Is.EqualTo(float2.zero));
                // Release: a push out of the corner is not held back by anything stored from the blocked ticks.
                var away = obtuse ? new float3(6, 0, 6) : new float3(5, 0, 12);
                Push(w, e[0], away); w.Step();
                var released = M(w, e[0]).ExternalVelocity;
                Assert.That(math.length(released), Is.EqualTo(math.length(away.xz) - CombatImpulseTuning.GroundDeceleration * Dt).Within(1e-3));
                Assert.That(math.dot(math.normalize(released), math.normalize(away.xz)), Is.GreaterThan(.9999f));
            }
            for (var t = 0; t < 40; t++)
                Assert.That(math.distance(trajectories[0][t], trajectories[1][t]), Is.LessThan(1e-4f), $"tick {t}: shape order");
        }

        // #70: a body resting on wall A slides parallel to it into an acute wall B. Projecting only onto B would return (-6,-6),
        // straight into A: A must stay active (B's depenetration re-touches it; the final touching-wall query also keeps it).
        [Test]
        public void SlidingAlongAWallIntoAnAcuteCornerKeepsTheRestingWallActive()
        {
            var s = math.sqrt(.5f);
            var wallB = CombatArenaTests.Box(.1f * s, 0, -.1f * s, 3, 4, .1f, math.PI / 4); // face z = x, outward (-s, s)
            using var w = new ReplayWorld("resting wall");
            var e = Start(w, new[] { Floor, CombatArenaTests.Box(-1.1f, 0, 1, .1f, 4, 3), wallB }, new float3(10, 0, 0));
            var radius = w.Manager.GetComponentData<CombatBody>(e[0]).Radius;
            w.Manager.SetComponentData(e[0], new CombatPosition { Value = new float3(-1 + radius, 0, .2f) });
            Push(w, e[0], new float3(0, 0, -12));
            var reached = false;
            for (var t = 0; t < 10; t++)
            {
                w.Step();
                var v = M(w, e[0]).ExternalVelocity;
                Assert.That(P(w, e[0]).x, Is.EqualTo(-1 + radius).Within(.00021f), $"tick {t}: still resting on wall A");
                Assert.That(v.x, Is.GreaterThanOrEqualTo(-1e-4f), $"tick {t}: hidden velocity {v} into the resting wall");
                reached |= math.dot(P(w, e[0]).xz, new float2(-s, s)) - radius < 1e-3f;
            }
            Assert.That(reached, Is.True, "The body reached wall B.");
            Assert.That(M(w, e[0]).ExternalVelocity, Is.EqualTo(float2.zero));
        }

        // #70: running out of the shared pair-visit budget INSIDE the final-velocity pass is not a partial success: positions
        // solved before the pass are published, contact velocities are zeroed and counted, no impulse is retried.
        [Test]
        public void VelocityPassWorkLimitKeepsSolvedPositionsAndPublishesNoPartialVelocity()
        {
            float3 a, b; long visits;
            using (var w = new ReplayWorld("velocity budget reference"))
            {
                var e = Start(w, new[] { Floor }, new float3(.7f, 0, 0));
                Push(w, e[0], new float3(12, 0, 0)); w.Step();
                var d = Contacts(w);
                Assert.That(d.VelocityContacts, Is.EqualTo(1)); Assert.That(d.VelocityComplete, Is.True); Assert.That(d.WorkLimit, Is.False);
                visits = d.PairVisits; a = P(w, e[0]); b = P(w, e[1]);
            }
            using var g = new ReplayWorld("velocity budget guard");
            var f = Start(g, new[] { Floor }, new float3(.7f, 0, 0));
            var owner = g.World.GetExistingSystemManaged<CombatSpatialIndexSystem>();
            owner.PairVisitBudgetOverride = visits - 1; // the velocity pass is the last consumer: its final visit is refused
            Push(g, f[0], new float3(12, 0, 0)); g.Step();
            var guard = Contacts(g);
            Assert.That((guard.WorkLimit, guard.ResidualComplete, guard.VelocityComplete), Is.EqualTo((true, true, false)));
            Assert.That(guard.VelocityDiscarded, Is.GreaterThan(0)); Assert.That(guard.FrozenBodies, Is.Zero);
            Assert.That(P(g, f[0]), Is.EqualTo(a)); Assert.That(P(g, f[1]), Is.EqualTo(b), "Solved positions are published unchanged.");
            Assert.That(M(g, f[0]).ExternalVelocity, Is.EqualTo(float2.zero)); Assert.That(M(g, f[1]).ExternalVelocity, Is.EqualTo(float2.zero));
            Assert.That(Pending(g), Is.Zero, "The consumed impulse is not re-queued.");
            owner.PairVisitBudgetOverride = 0; g.Step(3);
            Assert.That(Contacts(g).VelocityComplete, Is.True);
            Assert.That(P(g, f[0]), Is.EqualTo(a)); Assert.That(P(g, f[1]), Is.EqualTo(b), "Nothing discarded is released later.");
        }

        // #69 counterexample: equal bodies .1 apart, 12/0 u/s: the old rule published 8.6/2.6 (average displacements); the
        // declared perfectly inelastic normal response gives the common velocity 6/6, i.e. 5.6/5.6 after ground friction.
        [TestCase(1f, 1f)] [TestCase(1f, 3f)] [TestCase(3f, 1f)]
        public void BodyHitAfterPartOfTheTickSharesTheInelasticCommonVelocity(float ma, float mb)
        {
            using var w = new ReplayWorld("toi body"); var e = Start(w, new[] { Floor }, new float3(.7f, 0, 0));
            w.Manager.SetComponentData(e[0], Body(mass: ma)); w.Manager.SetComponentData(e[1], Body(mass: mb));
            const float v0 = 12;
            Push(w, e[0], new float3(v0 * ma, 0, 0)); w.Step(); Separated(w, e[0], e[1]);
            var va = M(w, e[0]).ExternalVelocity; var vb = M(w, e[1]).ExternalVelocity;
            var common = ma * v0 / (ma + mb) - CombatImpulseTuning.GroundDeceleration * Dt;
            Assert.That(va.x, Is.EqualTo(common).Within(1e-3)); Assert.That(vb.x, Is.EqualTo(common).Within(1e-3));
            Assert.That(va.y, Is.Zero); Assert.That(vb.y, Is.Zero);
            Assert.That(ma * va.x * va.x + mb * vb.x * vb.x, Is.LessThan(ma * v0 * v0), "Inelastic: energy decreases.");
            Assert.That(Contacts(w).VelocityContacts, Is.EqualTo(1)); Assert.That(Contacts(w).FrozenBodies, Is.Zero);
            w.Step(40);
            Assert.That(M(w, e[0]).ExternalVelocity, Is.EqualTo(float2.zero)); Assert.That(M(w, e[1]).ExternalVelocity, Is.EqualTo(float2.zero));
            Separated(w, e[0], e[1]);
            Assert.That(M(w, e[1]).MoveStreakDistance, Is.Zero, "Passive transfer is not running.");
        }

        // #69 baseline for the #66 limits: a FREE (no wall, no jam) touching chain of 6 or 20 bodies of both teams transfers
        // an impulse end to end with no hold, discard or rollback, no overlap and no energy gain.
        [TestCase(2)] [TestCase(9)]
        public void FreeSmallAndMediumChainsTransferWithoutHoldsOrDiscards(int allies)
        {
            using var w = new ReplayWorld("free chain"); var e = Start(w, new[] { Floor }, allies: (ushort)allies);
            for (var i = 0; i < e.Length; i++)
                w.Manager.SetComponentData(e[i], new CombatPosition { Value = new float3(i * .6f, 0, 0) });
            var last = e.Length - 1;
            const float v0 = 12;
            Push(w, e[0], new float3(v0, 0, 0));
            var energy = v0 * v0;
            var maxSlop = 0f;
            for (var t = 0; t < 60; t++)
            {
                w.Step();
                var d = Contacts(w);
                Assert.That((d.LocalHolds, d.DiscardedVelocities, d.RolledBackBodies, d.FrozenBodies, d.WorkLimit), Is.EqualTo((0, 0, 0, 0, false)), $"tick {t}");
                var now = e.Sum(x => math.lengthsq(M(w, x).ExternalVelocity));
                Assert.That(now, Is.LessThanOrEqualTo(energy + 1e-3f), "Contacts never create kinetic energy.");
                energy = now;
                // A long pressing chain may publish the documented, diagnosed slop (<= 5 % of the radius, resolved next tick).
                var slop = CombatContactTuning.SlopRadiusFraction * .3f;
                Assert.That(d.SlopAcceptedDepth, Is.LessThanOrEqualTo(slop + 1e-6f));
                for (var i = 0; i + 1 < e.Length; i++)
                    Assert.That(math.distance(P(w, e[i]).xz, P(w, e[i + 1]).xz), Is.GreaterThanOrEqualTo(.6f - slop - .00021f), $"tick {t} pair {i}");
                maxSlop = math.max(maxSlop, d.SlopAcceptedDepth);
            }
            TestContext.WriteLine($"chain {e.Length}: max accepted slop {maxSlop}");
            if (e.Length <= 6) Assert.That(maxSlop, Is.Zero, "A small free chain needs no slop.");
            Assert.That(P(w, e[last]).x, Is.GreaterThan(last * .6f + .01f), "The far end of the chain moved.");
            Assert.That(e.All(x => M(w, x).ExternalVelocity.Equals(float2.zero)), Is.True, "Friction stops the chain.");
        }

        // #69: whole-entity Disabled (Unity tag) hides the fighter from every default query; it must not keep and later
        // release its external velocity, exactly like a disabled InCombat bit.
        [Test]
        public void WholeEntityDisabledFighterLosesItsExternalVelocity()
        {
            using var w = new ReplayWorld("impulse disabled entity"); var e = Start(w, new[] { Floor });
            Push(w, e[1], new float3(-6, 0, 0)); w.Step();
            Assert.That(M(w, e[1]).ExternalVelocity.x, Is.LessThan(0));
            w.Manager.SetEnabled(e[1], false); w.Step();
            Assert.That(M(w, e[1]).ExternalVelocity, Is.EqualTo(float2.zero), "Cleared while disabled.");
            var parked = P(w, e[1]);
            w.Manager.SetEnabled(e[1], true); w.Step(3);
            Assert.That(P(w, e[1]), Is.EqualTo(parked), "Re-enabling releases no old impulse.");
            Assert.That(M(w, e[1]).VerticalVelocity, Is.Zero, "Vertical semantics unchanged (grounded).");
        }

        [Test]
        public void FiniteFloorEdgeCornerAndRampUseTheSameArena()
        {
            using var w = new ReplayWorld("impulse edge");
            var ramp = new CombatArenaShape { Kind = CombatArenaKind.Ramp, Center = new float2(0, 4), HalfSize = new float2(2, 1),
                Bottom = 0, Top = 1, AxisX = new float2(1, 0) };
            var e = Start(w, new[] { CombatArenaTests.Box(0, -.5f, 0, 1.5f, 0, 1.5f), CombatArenaTests.Box(0, -.5f, 4, 4, 0, 1.5f), ramp,
                CombatArenaTests.Box(-1.6f, 0, 0, .1f, 4, 1.5f), CombatArenaTests.Box(0, 0, -1.6f, 1.5f, 4, .1f) }, new float3(-1.5f, .2f, 4));
            Push(w, e[0], new float3(12, 0, 0)); Push(w, e[1], new float3(6, 0, 0));
            var fell = false;
            for (var t = 0; t < 30; t++)
            {
                w.Step(); fell |= !M(w, e[0]).Grounded;
                var r = P(w, e[1]);
                Assert.That(r.y, Is.EqualTo(math.clamp((r.x + .3f + 2) * .25f, 0, 1)).Within(.00021), "The ramp supports the pushed body.");
            }
            Assert.That(fell, Is.True); Assert.That(P(w, e[0]).y, Is.LessThan(0), "Off the finite floor edge.");
            using var corner = new ReplayWorld("impulse corner");
            var c = Start(corner, new[] { CombatArenaTests.Box(0, -.5f, 0, 1.5f, 0, 1.5f), CombatArenaTests.Box(-1.6f, 0, 0, .1f, 4, 1.5f),
                CombatArenaTests.Box(0, 0, -1.6f, 1.5f, 4, .1f) }, new float3(1, 0, 1));
            Push(corner, c[0], new float3(-12, 0, -12));
            for (var t = 0; t < 30; t++)
            {
                corner.Step();
                Assert.That(P(corner, c[0]).x, Is.GreaterThanOrEqualTo(-1.2f - .00021f)); Assert.That(P(corner, c[0]).z, Is.GreaterThanOrEqualTo(-1.2f - .00021f));
            }
            Assert.That(M(corner, c[0]).ExternalVelocity, Is.EqualTo(float2.zero));
        }

        [Test]
        public void FastOpposingBodiesCannotExchangeAndFindNewNeighbours()
        {
            using var w = new ReplayWorld("impulse fast"); var e = Start(w, new[] { Floor }, new float3(.7f, 0, 0), allies: 1);
            w.Manager.SetComponentData(e[2], new CombatPosition { Value = new float3(-.62f, 0, 0) });
            w.Manager.SetComponentData(e[3], new CombatPosition { Value = new float3(1.32f, 0, 0) });
            Push(w, e[0], new float3(24, 0, 0)); Push(w, e[1], new float3(-24, 0, 0));
            w.Step();
            for (var i = 0; i < 4; i++) for (var j = i + 1; j < 4; j++) Separated(w, e[i], e[j]);
            Assert.That(P(w, e[0]).x, Is.LessThan(P(w, e[1]).x));
            Assert.That(Contacts(w).FrozenBodies, Is.Zero); Assert.That(Contacts(w).WorkLimit, Is.False);
            Assert.That(Contacts(w).ResidualComplete, Is.True);
        }

        [Test]
        public void SpeedLimitRejectsWithoutClippingAndRollbackDiscardsOnceWithoutNaN()
        {
            using var w = new ReplayWorld("impulse limits"); var e = Start(w);
            Push(w, e[0], new float3(CombatImpulseTuning.MaxExternalSpeed + 1, 0, 0));
            Push(w, e[0], new float3(float.NaN, 0, 0));
            Push(w, e[1], new float3(CombatImpulseTuning.MaxExternalSpeed, 0, 0));
            w.Step();
            var d = Contacts(w);
            Assert.That(d.ImpulsesRejected, Is.EqualTo(2)); Assert.That(d.ImpulsesApplied, Is.EqualTo(1));
            Assert.That(P(w, e[0]).x, Is.Zero, "A rejected impulse is not clipped into a smaller one.");
            Assert.That(P(w, e[1]).x, Is.EqualTo(10 + CombatImpulseTuning.MaxExternalSpeed * Dt).Within(1e-4));

            using var r = new ReplayWorld("impulse rollback"); var f = Start(r, allies: 1);
            for (var i = 0; i < 3; i++) { r.Manager.SetComponentData(f[i], Body(2)); r.Manager.SetComponentData(f[i], new CombatPosition()); }
            r.Manager.SetComponentData(f[3], new CombatPosition { Value = new float3(20, 0, 0) });
            Push(r, f[0], new float3(6, 0, 0)); Push(r, f[3], new float3(6, 0, 0));
            r.Step();
            var c = Contacts(r);
            Assert.That(c.FrozenBodies, Is.EqualTo(3)); Assert.That(c.DiscardedVelocities, Is.EqualTo(1));
            Assert.That(P(r, f[0]), Is.EqualTo(float3.zero)); Assert.That(M(r, f[0]).ExternalVelocity, Is.EqualTo(float2.zero));
            Assert.That(P(r, f[3]).x, Is.GreaterThan(20.09f), "A distant island keeps its physics.");
            r.Step();
            Assert.That(Contacts(r).ImpulsesApplied, Is.Zero, "The consumed J is not applied again.");
            Assert.That(P(r, f[0]), Is.EqualTo(float3.zero));
            Assert.That(r.Records.SelectMany(x => x.Combatants).All(x => math.all(math.isfinite(x.Position)) && math.all(math.isfinite(x.Motion.ExternalVelocity))), Is.True);
        }

        [Test]
        public void PauseKeepsInputAndRestartDisableDespawnAndNewWorldDropIt()
        {
            using var w = new ReplayWorld("impulse lifetime"); var e = Start(w); w.Step();
            Push(w, e[0], new float3(6, 0, 0));
            w.World.SetTime(new TimeData(w.World.Time.ElapsedTime, 0));
            w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update(); w.FixedStep.Update();
            Assert.That(Pending(w), Is.EqualTo(1), "Pause consumes no physics tick."); Assert.That(P(w, e[0]).x, Is.Zero);
            w.Request(BattleRequest.Restart(BattleSetup.Default)); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            w.Step();
            Assert.That(Contacts(w).ImpulsesDropped, Is.EqualTo(1), "The old battle's impulse cannot reach the new battle.");
            Assert.That(Pending(w), Is.Zero);
            Assert.That(w.Manager.Exists(e[0]), Is.False);

            var g = Actors(w);
            Push(w, g[1], new float3(6, 0, 0)); w.Step();
            Assert.That(M(w, g[1]).ExternalVelocity.x, Is.GreaterThan(0));
            w.Manager.SetComponentEnabled<InCombat>(g[1], false);
            Push(w, g[1], new float3(6, 0, 0)); w.Step();
            Assert.That(Contacts(w).ImpulsesDropped, Is.EqualTo(1)); Assert.That(M(w, g[1]).ExternalVelocity, Is.EqualTo(float2.zero));
            var parked = P(w, g[1]);
            w.Manager.SetComponentEnabled<InCombat>(g[1], true); w.Step();
            Assert.That(P(w, g[1]), Is.EqualTo(parked), "Re-enabling releases no old impulse.");

            var id = Id(w, g[1]);
            w.Request(BattleRequest.Despawn(id)); w.Step();
            CombatImpulse.Submit(w.Manager, id, new float3(6, 0, 0)); w.Step();
            Assert.That(Contacts(w).ImpulsesDropped, Is.EqualTo(1));
            w.Request(BattleRequest.End()); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();

            using var fresh = new ReplayWorld("impulse new world"); Start(fresh);
            Assert.That(Pending(fresh), Is.Zero, "A new World starts with its own empty input.");
        }

        [Test]
        public void HitPhaseProducerAppliesOnTheNextMovementAndKeepsThatTicksSnapshot()
        {
            using var w = new ReplayWorld("after hit impulse"); var e = Start(w); w.Step(2);
            var producer = w.AddToPhase<AfterHitImpulseProducer>(typeof(CombatStateUpdateSystemGroup));
            producer.OnTick = w.Tick + 1; producer.Target = Id(w, e[1]); producer.Impulse = new float3(-6, 0, 0);
            w.Step();
            var record = w.Records.Last();
            Assert.That(record.Impulses.Length, Is.EqualTo(1), "Pending after Hit T, recorded with its epoch.");
            Assert.That(record.Impulses[0].BattleNumber, Is.EqualTo(w.Session.BattleNumber));
            Assert.That(P(w, e[1]).x, Is.EqualTo(10), "Hit T does not move the target.");
            Assert.That(record.Spatial.Single(b => b.Id.Equals(Id(w, e[1]))).Position.x, Is.EqualTo(10), "Snapshot T unchanged.");
            Assert.That(record.Raw.Any(l => l.StartsWith("impulse #")), Is.True);
            w.Step();
            Assert.That(P(w, e[1]).x, Is.EqualTo(10 - 6 * Dt).Within(1e-5), "Applied in Movement T+1.");
            Assert.That(w.Records.Last().Impulses, Is.Empty);
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(10)]
        public void ChunkOrderSourcePacingAndReplayKeepImpulsePhysicsIdentical(int pacing)
        {
            using var a = new ReplayWorld("impulse canonical"); using var b = new ReplayWorld("impulse reordered");
            foreach (var w in new[] { a, b })
            {
                var producer = w.AddToPhase<ScriptedImpulseProducer>(typeof(CombatCommandCollectionSystemGroup)); producer.Driver = w;
                producer.Script.Add((3, ReplayRole.Human, new float3(8, 0, 1))); producer.Script.Add((3, ReplayRole.Human, new float3(-2, 3, 0)));
                producer.Script.Add((17, ReplayRole.Ai, new float3(-9, 0, 0))); producer.Script.Add((31, ReplayRole.Human, new float3(0, 0, -5)));
                for (uint t = 1; t <= 60; t++)
                {
                    w.Script.Player(t, t, ReplayRole.Human, moveX: (sbyte)(t % 3 - 1), pressed: t % 13 == 0 ? CommandButtons.HandSeries : CommandButtons.None);
                    w.Script.Ai(t, t, ReplayRole.Ai, moveX: -1, pressed: t % 11 == 0 ? CommandButtons.FootSeries : CommandButtons.None);
                }
            }
            Start(a, new[] { Floor }, new float3(1.2f, 0, 0)); var eb = Start(b, new[] { Floor }, new float3(1.2f, 0, 0));
            b.Manager.AddComponentData(eb[1], new ContactChunkMarker());
            for (var i = 0; i < 60 / pacing; i++) b.Frame(pacing);
            a.Step(60);
            Assert.That(b.RawLog, Is.EqualTo(a.RawLog), "Position, Motion incl. external velocity, HP, strikes and pending input.");
            Assert.That(b.SpatialLog, Is.EqualTo(a.SpatialLog));
            Assert.That(a.Records.Any(r => r.Combatants.Any(c => math.any(c.Motion.ExternalVelocity != 0))), Is.True);
        }
    }
}
