using System.Collections;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.TestTools;

namespace MLFLegends.Tests.PlayMode
{
    // Read-only checks of the auto-created default world; does not add systems, queues or commands to it.
    public class DefaultWorldCommandQueueTests
    {
        [UnityTest]
        public IEnumerator DefaultWorldHasOneProcessorLastInTheCollectionPhaseAndOneQueue()
        {
            yield return null;

            var world = World.DefaultGameObjectInjectionWorld;
            Assert.That(world, Is.Not.Null, "Entities default world bootstrap is expected in Play Mode.");

            var collection = world.GetExistingSystemManaged<CombatCommandCollectionSystemGroup>();
            var processor = world.GetExistingSystem<CombatCommandProcessorSystem>();
            Assert.That(processor, Is.Not.EqualTo(SystemHandle.Null));
            using (var members = collection.GetAllSystems(Allocator.Temp))
            {
                Assert.That(members.Contains(processor), Is.True);
                Assert.That(members[members.Length - 1], Is.EqualTo(processor), "The processor runs after every producer.");
            }

            var queueQuery = world.EntityManager.CreateEntityQuery(typeof(CombatCommandQueue));
            Assert.That(queueQuery.CalculateEntityCount(), Is.EqualTo(1));
            var queueEntity = queueQuery.GetSingletonEntity();
            Assert.That(world.EntityManager.HasBuffer<CombatCommandSubmission>(queueEntity), Is.True);
            Assert.That(world.EntityManager.HasBuffer<CombatCommandResult>(queueEntity), Is.True);

            // The processor runs on every tick even without combatants.
            yield return new UnityEngine.WaitForSeconds(0.25f);
            var clock = world.EntityManager.CreateEntityQuery(typeof(SimulationClock)).GetSingleton<SimulationClock>();
            Assert.That(queueQuery.GetSingleton<CombatCommandQueue>().LastProcessedTick, Is.EqualTo(clock.Tick));
        }
    }
}
