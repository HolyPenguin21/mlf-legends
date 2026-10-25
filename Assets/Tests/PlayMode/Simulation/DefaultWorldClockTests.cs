using System.Collections;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;

namespace MLFLegends.Tests.PlayMode
{
    // Read-only checks of the auto-created default world; does not add systems or clocks to it.
    public class DefaultWorldClockTests
    {
        [UnityTest]
        public IEnumerator DefaultWorldHasOneClockAheadOfTheCombatPhases()
        {
            yield return null;

            var world = World.DefaultGameObjectInjectionWorld;
            Assert.That(world, Is.Not.Null, "Entities default world bootstrap is expected in Play Mode.");

            var fixedStep = world.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
            Assert.That(fixedStep.Timestep, Is.EqualTo(SimulationTiming.TickDuration));

            using (var order = fixedStep.GetAllSystems(Allocator.Temp))
            {
                var clock = order.IndexOf(world.GetExistingSystem<CombatClockSystem>());
                var collection = order.IndexOf(world.GetExistingSystem<CombatCommandCollectionSystemGroup>());
                var state = order.IndexOf(world.GetExistingSystem<CombatStateUpdateSystemGroup>());
                var publication = order.IndexOf(world.GetExistingSystem<CombatPublicationSystemGroup>());

                Assert.That(clock, Is.GreaterThanOrEqualTo(0));
                Assert.That(collection, Is.GreaterThan(clock));
                Assert.That(state, Is.GreaterThan(collection));
                Assert.That(publication, Is.GreaterThan(state));
            }

            var clockQuery = world.EntityManager.CreateEntityQuery(typeof(SimulationClock));
            Assert.That(clockQuery.CalculateEntityCount(), Is.EqualTo(1));
            var before = clockQuery.GetSingleton<SimulationClock>().Tick;

            // Wait in time, not frames: fast frames alone must not be what advances the tick.
            yield return new WaitForSeconds(0.25f);

            Assert.That(clockQuery.CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(clockQuery.GetSingleton<SimulationClock>().Tick, Is.GreaterThan(before));
        }
    }
}
