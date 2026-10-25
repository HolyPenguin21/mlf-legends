using System.Collections.Generic;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Core;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace MLFLegends.Tests.EditMode
{
    // Isolated test worlds only: they never touch the default world or its clock.
    public partial class CombatClockTests
    {
        World m_World;
        FixedStepSimulationSystemGroup m_FixedStep;
        SystemHandle m_Clock;
        uint m_Frame;

        [SetUp]
        public void SetUp()
        {
            m_World = CreateClockWorld("MLF Legends clock test", out m_FixedStep, out m_Clock);
            m_Frame = 0;
        }

        [TearDown]
        public void TearDown()
        {
            if (m_World != null && m_World.IsCreated)
                m_World.Dispose();
        }

        static World CreateClockWorld(string name, out FixedStepSimulationSystemGroup fixedStep, out SystemHandle clock)
        {
            var world = new World(name);
            fixedStep = world.CreateSystemManaged<FixedStepSimulationSystemGroup>();
            // Same group members the clock is ordered against in the default world.
            fixedStep.AddSystemToUpdateList(world.CreateSystemManaged<BeginFixedStepSimulationEntityCommandBufferSystem>());
            clock = world.CreateSystem<CombatClockSystem>();
            fixedStep.AddSystemToUpdateList(clock);
            fixedStep.SortSystems();
            return world;
        }

        static EntityQuery ClockQuery(World world) =>
            world.EntityManager.CreateEntityQuery(typeof(SimulationClock));

        static uint Tick(World world) => ClockQuery(world).GetSingleton<SimulationClock>().Tick;

        // Renders a frame half a tick after the n-th tick time: the catch-up rate manager then runs
        // exactly one fixed step per frame, with no dependency on floating-point rounding at the edge.
        static void RenderOneTickPerFrame(World world, FixedStepSimulationSystemGroup fixedStep, ref uint frame)
        {
            var dt = (double)SimulationTiming.TickDuration;
            world.SetTime(new TimeData((frame + 0.5) * dt, (float)dt));
            fixedStep.Update();
            frame++;
        }

        [Test]
        public void ClockStartsAtNoTickWithTheConfiguredTimestep()
        {
            Assert.That(ClockQuery(m_World).CalculateEntityCount(), Is.EqualTo(1));
            var clock = ClockQuery(m_World).GetSingleton<SimulationClock>();
            Assert.That(clock.Tick, Is.EqualTo(SimulationClock.NoTick));
            Assert.That(clock.HasStarted, Is.False);
            Assert.That(m_FixedStep.Timestep, Is.EqualTo(SimulationTiming.TickDuration));
            Assert.That(SimulationTiming.TickDuration, Is.EqualTo(1f / SimulationTiming.TickRateHz));
        }

        [Test]
        public void NFixedStepsProduceNTicksStartingAtOne()
        {
            RenderOneTickPerFrame(m_World, m_FixedStep, ref m_Frame);
            Assert.That(Tick(m_World), Is.EqualTo(1u), "The first simulated tick is 1.");

            const uint steps = 500;
            for (uint i = 1; i < steps; i++)
            {
                RenderOneTickPerFrame(m_World, m_FixedStep, ref m_Frame);
                Assert.That(Tick(m_World), Is.EqualTo(i + 1));
            }

            Assert.That(ClockQuery(m_World).CalculateEntityCount(), Is.EqualTo(1));
        }

        [TestCase(10)]
        [TestCase(30)]
        [TestCase(60)]
        [TestCase(144)]
        [TestCase(1000)]
        public void RenderFrameRateDoesNotChangeTheTickCount(int framesPerSecond)
        {
            const int seconds = 2;
            var dt = (double)SimulationTiming.TickDuration;
            for (var frame = 0; frame <= framesPerSecond * seconds; frame++)
            {
                m_World.SetTime(new TimeData((double)frame / framesPerSecond, 1f / framesPerSecond));
                m_FixedStep.Update();
            }

            // Common final frame half a tick past the end, so every frame rate stops at the same step.
            m_World.SetTime(new TimeData(seconds + 0.5 * dt, (float)(0.5 * dt)));
            m_FixedStep.Update();

            // The catch-up rate manager runs its first step at world time 0, then one per elapsed tick.
            Assert.That(Tick(m_World), Is.EqualTo(1u + seconds * SimulationTiming.TickRateHz));
        }

        [Test]
        public void SecondClockSystemInstanceIsRejectedAndTheClockKeepsOneWriter()
        {
            Assert.That(() => m_World.CreateSystem<CombatClockSystem>(), Throws.Exception);
            Assert.That(ClockQuery(m_World).CalculateEntityCount(), Is.EqualTo(1));

            RenderOneTickPerFrame(m_World, m_FixedStep, ref m_Frame);
            RenderOneTickPerFrame(m_World, m_FixedStep, ref m_Frame);
            Assert.That(Tick(m_World), Is.EqualTo(2u));
        }

        [Test]
        public void RecreatedWorldStartsANewClockAndWorldsDoNotShareTicks()
        {
            for (var i = 0; i < 5; i++)
                RenderOneTickPerFrame(m_World, m_FixedStep, ref m_Frame);
            Assert.That(Tick(m_World), Is.EqualTo(5u));

            // Same lifecycle as leaving and re-entering Play Mode: the world and its clock are disposed.
            m_World.Dispose();
            m_World = CreateClockWorld("MLF Legends clock test (recreated)", out m_FixedStep, out m_Clock);
            m_Frame = 0;
            Assert.That(Tick(m_World), Is.EqualTo(SimulationClock.NoTick));

            using (var other = CreateClockWorld("MLF Legends clock test (other)", out var otherFixedStep, out _))
            {
                uint otherFrame = 0;
                RenderOneTickPerFrame(m_World, m_FixedStep, ref m_Frame);
                for (var i = 0; i < 3; i++)
                    RenderOneTickPerFrame(other, otherFixedStep, ref otherFrame);

                Assert.That(Tick(m_World), Is.EqualTo(1u));
                Assert.That(Tick(other), Is.EqualTo(3u));
            }
        }

        [Test]
        public void DestroyingTheClockSystemRemovesTheClock()
        {
            m_World.DestroySystem(m_Clock);
            Assert.That(ClockQuery(m_World).CalculateEntityCount(), Is.Zero);
        }

        [Test]
        public void TickNeverWrapsToNoTick()
        {
            Assert.That(SimulationClock.TryGetNextTick(SimulationClock.NoTick, out var first), Is.True);
            Assert.That(first, Is.EqualTo(1u));

            Assert.That(SimulationClock.TryGetNextTick(SimulationClock.LastTick - 1, out var last), Is.True);
            Assert.That(last, Is.EqualTo(SimulationClock.LastTick));

            Assert.That(SimulationClock.TryGetNextTick(SimulationClock.LastTick, out var saturated), Is.False);
            Assert.That(saturated, Is.EqualTo(SimulationClock.LastTick));
        }

        [Test]
        public void ClockUpdateDoesNotAllocateManagedMemory()
        {
            m_Clock.Update(m_World.Unmanaged);
            Assert.That(() => m_Clock.Update(m_World.Unmanaged), Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void ClockOwnsTheFixedStepTimestep()
        {
            using (var world = new World("MLF Legends clock timestep test"))
            {
                var fixedStep = world.CreateSystemManaged<FixedStepSimulationSystemGroup>();
                fixedStep.Timestep = 0.5f; // anything but the Unity default, which also happens to be 1/60
                world.CreateSystem<CombatClockSystem>();
                Assert.That(fixedStep.Timestep, Is.EqualTo(SimulationTiming.TickDuration));
            }
        }

        [Test]
        public void ClockCreationFailsWithoutAFixedStepGroup()
        {
            using (var world = new World("MLF Legends clock without fixed step"))
            {
                Assert.That(() => world.CreateSystem<CombatClockSystem>(), Throws.Exception);
                Assert.That(ClockQuery(world).CalculateEntityCount(), Is.Zero);
            }
        }

        [Test]
        public void TickIsAdvancedBeforeEveryCombatPhaseInPhaseOrder()
        {
            var log = AddPhasesWithProbes(m_World, m_FixedStep);

            RenderOneTickPerFrame(m_World, m_FixedStep, ref m_Frame);
            RenderOneTickPerFrame(m_World, m_FixedStep, ref m_Frame);

            Assert.That(log, Is.EqualTo(new[]
            {
                "collection:1", "state:1", "publication:1",
                "collection:2", "state:2", "publication:2",
            }));
        }

        [Test]
        public void CombatPhasesDoNotRunWithoutAClock()
        {
            using (var world = new World("MLF Legends phases without clock"))
            {
                var fixedStep = world.CreateSystemManaged<FixedStepSimulationSystemGroup>();
                var log = AddPhasesWithProbes(world, fixedStep);

                uint frame = 0;
                for (var i = 0; i < 3; i++)
                    RenderOneTickPerFrame(world, fixedStep, ref frame);

                Assert.That(log, Is.Empty);
            }
        }

        [Test]
        public void LastTickStopsTheClockAndEveryPhaseInsteadOfRepeatingATick()
        {
            var log = AddPhasesWithProbes(m_World, m_FixedStep);
            var manager = m_World.EntityManager;
            var clockEntity = ClockQuery(m_World).GetSingletonEntity();
            manager.SetComponentData(clockEntity, SimulationClock.AtTick(SimulationClock.LastTick - 1));

            RenderOneTickPerFrame(m_World, m_FixedStep, ref m_Frame);
            var last = SimulationClock.LastTick;
            Assert.That(Tick(m_World), Is.EqualTo(last));
            Assert.That(log, Is.EqualTo(new[] { $"collection:{last}", $"state:{last}", $"publication:{last}" }));

            log.Clear();
            LogAssert.Expect(LogType.Error, "SimulationClock reached its last tick; the clock and combat phases stopped.");
            RenderOneTickPerFrame(m_World, m_FixedStep, ref m_Frame);
            RenderOneTickPerFrame(m_World, m_FixedStep, ref m_Frame);

            Assert.That(Tick(m_World), Is.EqualTo(last), "The tick never wraps to NoTick.");
            Assert.That(log, Is.Empty, "No phase may run again with an already simulated tick.");
            Assert.That(manager.HasComponent<SimulationClockRunning>(clockEntity), Is.False);
            Assert.That(ClockQuery(m_World).CalculateEntityCount(), Is.EqualTo(1));
        }

        static List<string> AddPhasesWithProbes(World world, FixedStepSimulationSystemGroup fixedStep)
        {
            var log = new List<string>();
            var phases = new ComponentSystemGroup[]
            {
                world.CreateSystemManaged<CombatCommandCollectionSystemGroup>(),
                world.CreateSystemManaged<CombatStateUpdateSystemGroup>(),
                world.CreateSystemManaged<CombatPublicationSystemGroup>(),
            };
            var probes = new PhaseProbe[]
            {
                world.CreateSystemManaged<CommandCollectionProbe>(),
                world.CreateSystemManaged<StateUpdateProbe>(),
                world.CreateSystemManaged<PublicationProbe>(),
            };

            // Add in reverse so the order comes from the attributes, not from insertion order.
            for (var i = phases.Length - 1; i >= 0; i--)
            {
                probes[i].Log = log;
                phases[i].AddSystemToUpdateList(probes[i]);
                fixedStep.AddSystemToUpdateList(phases[i]);
            }
            fixedStep.SortSystems();
            return log;
        }

        abstract partial class PhaseProbe : SystemBase
        {
            public List<string> Log;
            EntityQuery m_ClockQuery;

            protected abstract string Phase { get; }

            protected override void OnCreate() => m_ClockQuery = GetEntityQuery(typeof(SimulationClock));

            protected override void OnUpdate() =>
                Log.Add($"{Phase}:{m_ClockQuery.GetSingleton<SimulationClock>().Tick}");
        }

        [DisableAutoCreation]
        partial class CommandCollectionProbe : PhaseProbe
        {
            protected override string Phase => "collection";
        }

        [DisableAutoCreation]
        partial class StateUpdateProbe : PhaseProbe
        {
            protected override string Phase => "state";
        }

        [DisableAutoCreation]
        partial class PublicationProbe : PhaseProbe
        {
            protected override string Phase => "publication";
        }
    }
}
