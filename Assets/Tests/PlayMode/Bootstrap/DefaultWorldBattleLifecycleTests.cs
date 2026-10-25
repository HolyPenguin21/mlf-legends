using System.Collections;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;

namespace MLFLegends.Tests.PlayMode
{
    // Battles in the auto-created default world, driven like a scene does it: through CombatSmokeBootstrap.
    // Every test ends its battle, so the default world is left without combatants.
    public class DefaultWorldBattleLifecycleTests
    {
        static World DefaultWorld => World.DefaultGameObjectInjectionWorld;

        static EntityManager Manager => DefaultWorld.EntityManager;

        static BattleSession Session => Manager.CreateEntityQuery(typeof(BattleSession)).GetSingleton<BattleSession>();

        static uint Tick => Manager.CreateEntityQuery(typeof(SimulationClock)).GetSingleton<SimulationClock>().Tick;

        static uint[] LiveIds
        {
            get
            {
                var query = Manager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new ComponentType[] { typeof(CombatantId) },
                    Options = EntityQueryOptions.IgnoreComponentEnabledState,
                });
                using var ids = query.ToComponentDataArray<CombatantId>(Allocator.Temp);
                return ids.ToArray().Select(id => id.Value).OrderBy(v => v).ToArray();
            }
        }

        [UnityTest]
        public IEnumerator DefaultWorldHasOneLifecycleOwnerThatRunsBeforeTheFixedStep()
        {
            yield return null;
            Assert.That(DefaultWorld, Is.Not.Null, "Entities default world bootstrap is expected in Play Mode.");

            var simulation = DefaultWorld.GetExistingSystemManaged<SimulationSystemGroup>();
            var fixedStep = DefaultWorld.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
            var lifecycle = DefaultWorld.GetExistingSystem<BattleLifecycleSystem>();
            Assert.That(lifecycle, Is.Not.EqualTo(SystemHandle.Null));
            using (var members = simulation.GetAllSystems(Allocator.Temp))
            {
                var lifecycleIndex = members.IndexOf(lifecycle);
                Assert.That(lifecycleIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(lifecycleIndex, Is.LessThan(members.IndexOf(fixedStep.SystemHandle)),
                    "Requests are applied between fixed steps, before the next tick.");
            }

            Assert.That(Manager.CreateEntityQuery(typeof(BattleSession)).CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(Session.State, Is.EqualTo(BattleState.Idle), "Nothing starts a battle without a request.");
        }

        [UnityTest]
        public IEnumerator SceneBootstrapStartsTwoCombatantsAndLeavingAndReturningLeaksNothing()
        {
            yield return null;
            var first = new GameObject("Combat bootstrap 1").AddComponent<CombatSmokeBootstrap>();
            GameObject second = null, returning = null;
            try
            {
                yield return null;
                var battle = Session;
                Assert.That(battle.State, Is.EqualTo(BattleState.Active));
                Assert.That(LiveIds, Is.EqualTo(new[] { battle.HumanCombatant.Value, battle.AiCombatant.Value }));
                Assert.That(battle.BattleStartTick, Is.LessThanOrEqualTo(Tick));

                yield return new WaitForSeconds(0.1f);
                Assert.That(battle.RelativeTick(Tick), Is.GreaterThanOrEqualTo(1u), "The battle is simulated on the shared clock.");
                var humanQuery = Manager.CreateEntityQuery(typeof(CombatantId), typeof(AppliedCombatCommand));
                Assert.That(humanQuery.CalculateEntityCount(), Is.EqualTo(2), "Both combatants are consumers of the command processor.");

                // A second bootstrap replaces the fighters instead of duplicating them.
                second = new GameObject("Combat bootstrap 2", typeof(CombatSmokeBootstrap));
                yield return null;
                Assert.That(LiveIds.Length, Is.EqualTo(2));
                Assert.That(LiveIds.Min(), Is.GreaterThan(battle.AiCombatant.Value));

                // Leaving the scene: bootstraps are destroyed, the battle ends.
                Object.Destroy(first.gameObject);
                Object.Destroy(second);
                yield return null;
                yield return null;
                Assert.That(LiveIds, Is.Empty);
                Assert.That(Session.State, Is.EqualTo(BattleState.Idle));
                var tickAfterLeaving = Tick;
                var lastId = battle.AiCombatant.Value + 2;

                // Returning: a fresh battle in the same World with new IDs and the clock still running.
                returning = new GameObject("Combat bootstrap 3", typeof(CombatSmokeBootstrap));
                yield return null;
                Assert.That(LiveIds.Length, Is.EqualTo(2));
                Assert.That(LiveIds.Min(), Is.GreaterThan(lastId));
                Assert.That(Session.BattleStartTick, Is.GreaterThanOrEqualTo(tickAfterLeaving));
                Assert.That(Session.BattleNumber, Is.EqualTo(battle.BattleNumber + 2));
            }
            finally
            {
                if (first != null)
                    Object.Destroy(first.gameObject);
                if (second != null)
                    Object.Destroy(second);
                if (returning != null)
                    Object.Destroy(returning);
            }

            yield return null;
            yield return null;
            Assert.That(LiveIds, Is.Empty, "The test leaves no combatant in the default world.");
        }
    }
}
