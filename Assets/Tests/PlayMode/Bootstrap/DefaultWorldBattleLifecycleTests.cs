using System;
using System.Collections;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace MLFLegends.Tests.PlayMode
{
    // Battles in the auto-created default world, driven like a scene does it: through CombatSmokeBootstrap.
    // Every test ends its battle. Paused tests wait for lifecycle state with an unscaled deadline;
    // simulation assertions wait for a real fixed tick. No test advances the default world manually.
    public class DefaultWorldBattleLifecycleTests
    {
        static World DefaultWorld => World.DefaultGameObjectInjectionWorld;

        static EntityManager Manager => DefaultWorld.EntityManager;

        static T Singleton<T>() where T : unmanaged, IComponentData
        {
            using var query = Manager.CreateEntityQuery(typeof(T));
            return query.GetSingleton<T>();
        }

        static BattleSession Session => Singleton<BattleSession>();

        static uint Tick => Singleton<SimulationClock>().Tick;

        static uint[] LiveIds
        {
            get
            {
                using var query = Manager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new ComponentType[] { typeof(CombatantId) },
                    Options = EntityQueryOptions.IgnoreComponentEnabledState,
                });
                using var ids = query.ToComponentDataArray<CombatantId>(Allocator.Temp);
                return ids.ToArray().Select(id => id.Value).OrderBy(v => v).ToArray();
            }
        }

        // Waits until at least one fixed step ran after this call, i.e. requests submitted before it were applied.
        static IEnumerator NextTick()
        {
            var tick = Tick;
            var deadline = Time.realtimeSinceStartup + 5f;
            while (Tick == tick)
            {
                Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline), "No fixed step ran (Time.timeScale = 0?).");
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator DefaultWorldHasOneLifecycleOwnerAndTheCompleteFixedStepOrder()
        {
            yield return null;
            Assert.That(DefaultWorld, Is.Not.Null, "Entities default world bootstrap is expected in Play Mode.");

            var fixedStep = DefaultWorld.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
            var lifecycle = DefaultWorld.GetExistingSystem<BattleLifecycleSystem>();
            Assert.That(lifecycle, Is.Not.EqualTo(SystemHandle.Null));
            using (var members = fixedStep.GetAllSystems(Allocator.Temp))
            {
                var order = new[]
                {
                    members.IndexOf(DefaultWorld.GetExistingSystem<BeginFixedStepSimulationEntityCommandBufferSystem>()),
                    members.IndexOf(lifecycle),
                    members.IndexOf(DefaultWorld.GetExistingSystem<CombatClockSystem>()),
                    members.IndexOf(DefaultWorld.GetExistingSystem<CombatCommandCollectionSystemGroup>()),
                    members.IndexOf(DefaultWorld.GetExistingSystem<CombatStateUpdateSystemGroup>()),
                    members.IndexOf(DefaultWorld.GetExistingSystem<CombatPublicationSystemGroup>()),
                    members.IndexOf(DefaultWorld.GetExistingSystem<EndFixedStepSimulationEntityCommandBufferSystem>()),
                };
                Assert.That(order, Is.All.GreaterThanOrEqualTo(0));
                Assert.That(order, Is.Ordered.Ascending.And.Unique,
                    "Begin ECB -> Lifecycle -> Clock -> Collection -> State -> Publication -> End ECB.");
            }

            var initialization = DefaultWorld.GetExistingSystemManaged<InitializationSystemGroup>();
            using (var members = initialization.GetAllSystems(Allocator.Temp))
            {
                var proxy = TypeManager.GetSystemTypeIndex<BattleLifecycleInitializationProxy>();
                Assert.That(members.AsArray().ToArray().Count(s => DefaultWorld.Unmanaged.GetSystemTypeIndex(s) == proxy), Is.EqualTo(1),
                    "Paused frames reach the lifecycle through exactly one proxy.");
                Assert.That(members.IndexOf(lifecycle), Is.EqualTo(-1), "The lifecycle itself has one group, FixedStep.");
                Assert.That(members.IndexOf(DefaultWorld.GetExistingSystem<CombatClockSystem>()), Is.EqualTo(-1),
                    "Initialization must not advance the clock.");
            }
            using (var all = DefaultWorld.Unmanaged.GetAllSystems(Allocator.Temp))
            {
                var lifecycleType = TypeManager.GetSystemTypeIndex<BattleLifecycleSystem>();
                Assert.That(all.ToArray().Count(s => DefaultWorld.Unmanaged.GetSystemTypeIndex(s) == lifecycleType), Is.EqualTo(1),
                    "Exactly one lifecycle instance in the default world.");
            }
            using (var sessions = Manager.CreateEntityQuery(typeof(BattleSession)))
                Assert.That(sessions.CalculateEntityCount(), Is.EqualTo(1));
            using (var members = DefaultWorld.GetExistingSystemManaged<CombatStateUpdateSystemGroup>().GetAllSystems(Allocator.Temp))
            {
                // Counted by system type so a second instance of either owner fails; other future owners are allowed.
                var types = members.AsArray().ToArray().Select(DefaultWorld.Unmanaged.GetSystemTypeIndex).ToArray();
                var owners = new[]
                {
                    TypeManager.GetSystemTypeIndex<CombatActionSystem>(), TypeManager.GetSystemTypeIndex<CombatFacingSystem>(),
                    TypeManager.GetSystemTypeIndex<CombatMovementSystem>(), TypeManager.GetSystemTypeIndex<CombatHitSystem>(),
                };
                foreach (var owner in owners)
                    Assert.That(types.Count(t => t == owner), Is.EqualTo(1), "Exactly one auto-created instance of each State owner.");
                for (var i = 1; i < owners.Length; i++)
                    Assert.That(System.Array.IndexOf(types, owners[i - 1]), Is.LessThan(System.Array.IndexOf(types, owners[i])),
                        "Default world orders strike → facing → movement → hit, as isolated replay and benchmarks do.");
                Assert.That(members.AsArray().ToArray(), Is.EqualTo(new[]
                {
                    DefaultWorld.GetExistingSystem<CombatActionSystem>(), DefaultWorld.GetExistingSystem<CombatFacingSystem>(),
                    DefaultWorld.GetExistingSystem<CombatMovementSystem>(), DefaultWorld.GetExistingSystem<CombatHitSystem>(),
                }), "Stage 05.1 State holds exactly the strike, facing, movement and hit owners.");
            }
            foreach (var phase in new[] { DefaultWorld.GetExistingSystemManaged<CombatCommandCollectionSystemGroup>(),
                         (ComponentSystemGroup)DefaultWorld.GetExistingSystemManaged<CombatPublicationSystemGroup>() })
            {
                using var members = phase.GetAllSystems(Allocator.Temp);
                Assert.That(members.AsArray().ToArray(), Has.None.EqualTo(DefaultWorld.GetExistingSystem<CombatFacingSystem>())
                    .And.None.EqualTo(DefaultWorld.GetExistingSystem<CombatMovementSystem>())
                    .And.None.EqualTo(DefaultWorld.GetExistingSystem<CombatActionSystem>())
                    .And.None.EqualTo(DefaultWorld.GetExistingSystem<CombatHitSystem>()), $"No State owner in {phase.GetType().Name}.");
            }
            Assert.That(Session.State, Is.EqualTo(BattleState.Idle), "Nothing starts a battle without a request.");
        }

        static IEnumerator Until(Func<bool> condition)
        {
            var deadline = Time.realtimeSinceStartup + 5f;
            while (!condition())
            {
                Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline), "Lifecycle did not reach the expected state.");
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator DefaultWorldJumpFreezesDuringTimeScaleZeroAndPausedRestartClearsMotion()
        {
            yield return NextTick();
            var scale = Time.timeScale;
            try
            {
                Time.timeScale = 0;
                yield return null;
                var number = Session.BattleNumber;
                BattleRequest.Submit(Manager, BattleRequest.Start(BattleSetup.Default));
                yield return Until(() => Session.BattleNumber == number + 1);
                Entity human;
                using (var actors = Manager.CreateEntityQuery(typeof(CombatantId)))
                using (var entities = actors.ToEntityArray(Allocator.Temp))
                    human = entities.ToArray().Single(e => Manager.GetComponentData<CombatantId>(e).Equals(Session.HumanCombatant));
                using (var queue = Manager.CreateEntityQuery(typeof(CombatCommandQueue)))
                    Manager.GetBuffer<CombatCommandSubmission>(queue.GetSingletonEntity()).Add(new CombatCommandSubmission(new CombatCommand
                    {
                        Combatant = Session.HumanCombatant, Tick = Tick + 1, MoveX = -1,
                        Pressed = CommandButtons.Jump, Source = CommandSource.Player,
                    }));
                Assert.That(Manager.GetComponentData<CombatFacing>(human).Value, Is.EqualTo(FacingDirection.Right), "Spawned from setup.");
                Time.timeScale = 1;
                yield return NextTick();
                Assert.That(Manager.GetComponentData<CombatMotion>(human).Grounded, Is.False);
                Assert.That(Manager.GetComponentData<CombatFacing>(human).Value, Is.EqualTo(FacingDirection.Left), "Accepted -X turns on its tick.");
                Time.timeScale = 0;
                yield return null; // settle time propagation, then capture the paused boundary
                var position = Manager.GetComponentData<CombatPosition>(human);
                var motion = Manager.GetComponentData<CombatMotion>(human);
                var tick = Tick;
                for (var i = 0; i < 5; i++)
                    yield return null;
                Assert.That(Tick, Is.EqualTo(tick));
                Assert.That(Manager.GetComponentData<CombatPosition>(human), Is.EqualTo(position));
                Assert.That(Manager.GetComponentData<CombatMotion>(human), Is.EqualTo(motion));
                Assert.That(Manager.GetComponentData<CombatFacing>(human).Value, Is.EqualTo(FacingDirection.Left));
                BattleRequest.Submit(Manager, BattleRequest.Restart(BattleSetup.Default));
                yield return Until(() => Session.BattleNumber == number + 2);
                Assert.That(Manager.Exists(human), Is.False);
                Assert.That(Tick, Is.EqualTo(tick));
                using (var actors = Manager.CreateEntityQuery(typeof(CombatMotion)))
                using (var motions = actors.ToComponentDataArray<CombatMotion>(Allocator.Temp))
                    foreach (var fresh in motions)
                        Assert.That((fresh.Grounded, fresh.VerticalVelocity), Is.EqualTo((true, 0f)));
                using (var actors = Manager.CreateEntityQuery(typeof(CombatTeam), typeof(CombatFacing)))
                using (var teams = actors.ToComponentDataArray<CombatTeam>(Allocator.Temp))
                using (var facings = actors.ToComponentDataArray<CombatFacing>(Allocator.Temp))
                    for (var i = 0; i < teams.Length; i++)
                        Assert.That(facings[i].Value, Is.EqualTo(teams[i].Value == BattleSetup.Default.Human.Team
                            ? BattleSetup.Default.Human.Facing : BattleSetup.Default.Ai.Facing), "Paused restart spawns setup facing, no turn leaks.");
                BattleRequest.Submit(Manager, BattleRequest.End());
                yield return Until(() => !Session.IsActive);
                using (var actors = Manager.CreateEntityQuery(typeof(CombatMotion)))
                    Assert.That(actors.CalculateEntityCount(), Is.Zero);
            }
            finally
            {
                Time.timeScale = scale;
                BattleRequest.Submit(Manager, BattleRequest.End());
            }
            yield return Until(() => !Session.IsActive);
        }

        [UnityTest]
        public IEnumerator PausedFramesApplyStartRestartAndEndForDuelAndLargeRosterWithoutSimulation()
        {
            yield return NextTick(); // consume the rate manager's mandatory first step before pausing
            var scale = Time.timeScale;
            try
            {
                Time.timeScale = 0;
                yield return null;
                var tick = Tick;
                foreach (var allies in new ushort[] { 0, 199 })
                {
                    var setup = BattleSetup.Default;
                    setup.HumanAllies = setup.AiAllies = allies;
                    var number = Session.BattleNumber;
                    BattleRequest.Submit(Manager, BattleRequest.Start(setup, owner: 101));
                    yield return Until(() => Session.BattleNumber == number + 1);
                    Assert.That(LiveIds.Length, Is.EqualTo(setup.CombatantCount));
                    Assert.That(Tick, Is.EqualTo(tick));
                    var oldId = Session.AiCombatant.Value;
                    BattleRequest.Submit(Manager, BattleRequest.Restart(setup, owner: 102));
                    yield return Until(() => Session.BattleNumber == number + 2);
                    Assert.That(Session.Owner, Is.EqualTo(102u));
                    Assert.That(LiveIds.Min(), Is.GreaterThan(oldId));
                    Assert.That(Session.RelativeTick(Tick), Is.Zero);
                    BattleRequest.Submit(Manager, BattleRequest.End(owner: 101));
                    yield return Until(() => Session.LastOutcome == BattleRequestOutcome.NotOwner);
                    Assert.That(Session.IsActive, Is.True);
                    BattleRequest.Submit(Manager, BattleRequest.End(owner: 102));
                    yield return Until(() => !Session.IsActive);
                    Assert.That(Tick, Is.EqualTo(tick));
                    Assert.That(LiveIds, Is.Empty);
                }
                Time.timeScale = scale;
                yield return NextTick();
                Assert.That(LiveIds, Is.Empty, "Unpause does not replay any processed request.");
            }
            finally
            {
                Time.timeScale = scale;
                BattleRequest.Submit(Manager, BattleRequest.End());
            }
            yield return Until(() => !Session.IsActive);
        }

        [UnityTest]
        public IEnumerator DisablingBootstrapOnPauseEndsItsBattleBeforeUnpause()
        {
            yield return NextTick();
            var scale = Time.timeScale;
            var bootstrap = new GameObject("Paused bootstrap", typeof(CombatSmokeBootstrap));
            try
            {
                yield return NextTick();
                Time.timeScale = 0;
                yield return null;
                var tick = Tick;
                bootstrap.SetActive(false);
                yield return Until(() => !Session.IsActive);
                Assert.That(LiveIds, Is.Empty);
                Assert.That(Tick, Is.EqualTo(tick));
                bootstrap.SetActive(true);
                yield return Until(() => Session.IsActive);
                Assert.That(LiveIds.Length, Is.EqualTo(2));
                Assert.That(Tick, Is.EqualTo(tick));
                Time.timeScale = scale;
                yield return NextTick();
                Assert.That(Session.RelativeTick(Tick), Is.GreaterThanOrEqualTo(1u));
            }
            finally
            {
                Time.timeScale = scale;
                Object.Destroy(bootstrap);
            }
            yield return Until(() => !Session.IsActive);
        }

        [UnityTest]
        public IEnumerator SceneBootstrapStartsTwoCombatantsAndLeavingAndReturningLeaksNothing()
        {
            yield return null;
            var first = new GameObject("Combat bootstrap 1").AddComponent<CombatSmokeBootstrap>();
            GameObject second = null, returning = null;
            try
            {
                yield return NextTick();
                var battle = Session;
                Assert.That(battle.State, Is.EqualTo(BattleState.Active));
                Assert.That(LiveIds, Is.EqualTo(new[] { battle.HumanCombatant.Value, battle.AiCombatant.Value }));
                Assert.That(battle.BattleStartTick, Is.LessThanOrEqualTo(Tick));

                yield return new WaitForSeconds(0.1f);
                Assert.That(battle.RelativeTick(Tick), Is.GreaterThanOrEqualTo(1u), "The battle is simulated on the shared clock.");
                using (var humanQuery = Manager.CreateEntityQuery(typeof(CombatantId), typeof(AppliedCombatCommand)))
                    Assert.That(humanQuery.CalculateEntityCount(), Is.EqualTo(2), "Both combatants are consumers of the command processor.");

                // A second bootstrap replaces the fighters instead of duplicating them.
                second = new GameObject("Combat bootstrap 2", typeof(CombatSmokeBootstrap));
                yield return NextTick();
                Assert.That(LiveIds.Length, Is.EqualTo(2));
                Assert.That(LiveIds.Min(), Is.GreaterThan(battle.AiCombatant.Value));

                // Leaving the scene: bootstraps are destroyed, the battle ends.
                Object.Destroy(first.gameObject);
                Object.Destroy(second);
                yield return null; // Destroy (and OnDisable) happens at the end of this frame
                yield return NextTick();
                Assert.That(LiveIds, Is.Empty);
                Assert.That(Session.State, Is.EqualTo(BattleState.Idle));
                var tickAfterLeaving = Tick;
                var lastId = battle.AiCombatant.Value + 2;

                // Returning: a fresh battle in the same World with new IDs and the clock still running.
                returning = new GameObject("Combat bootstrap 3", typeof(CombatSmokeBootstrap));
                yield return NextTick();
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
            yield return NextTick();
            Assert.That(LiveIds, Is.Empty, "The test leaves no combatant in the default world.");
        }

#if UNITY_EDITOR
        const string SmokeScenePath = "Assets/Scenes/CombatSmoke.unity";

        static IEnumerator LoadSmokeSceneAdditive()
        {
            var load = EditorSceneManager.LoadSceneAsyncInPlayMode(SmokeScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            yield return Until(() => load.isDone);
        }

        static Scene[] SmokeScenes() => Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
            .Where(scene => scene.path == SmokeScenePath && scene.isLoaded).ToArray();

        // The real scene asset; the async order of a scene switch: the next scene is enabled before the previous one
        // is disabled, so the previous bootstrap's end arrives after the next bootstrap's restart.
        [UnityTest]
        public IEnumerator SmokeSceneStartsItsBattleAndASceneSwitchNeitherStopsNorDuplicatesIt() =>
            SmokeSceneSwitch(paused: false);

        [UnityTest]
        public IEnumerator PausedSmokeSceneSwitchAndFinalUnloadDoNotNeedAFixedTick() =>
            SmokeSceneSwitch(paused: true);

        static IEnumerator SmokeSceneSwitch(bool paused)
        {
            yield return null;
            Assert.That(Session.State, Is.EqualTo(BattleState.Idle));
            var scale = Time.timeScale;
            uint pausedTick = 0;
            try
            {
                yield return LoadSmokeSceneAdditive();
                yield return NextTick();
                var first = Session;
                Assert.That(first.State, Is.EqualTo(BattleState.Active));
                Assert.That(first.Owner, Is.Not.Zero);
                Assert.That(LiveIds, Is.EqualTo(new[] { first.HumanCombatant.Value, first.AiCombatant.Value }));
                Entity human;
                using (var actors = Manager.CreateEntityQuery(typeof(CombatantId)))
                using (var entities = actors.ToEntityArray(Allocator.Temp))
                    human = entities.ToArray().Single(e => Manager.GetComponentData<CombatantId>(e).Value == first.HumanCombatant.Value);
                Assert.That(Manager.GetComponentData<CombatTeam>(human).Value, Is.EqualTo(1));
                Assert.That(Manager.GetComponentData<CombatPosition>(human).Value.x, Is.EqualTo(-2f));
                Assert.That(Manager.GetComponentData<CombatFacing>(human).Value, Is.EqualTo(FacingDirection.Right));

                if (paused)
                    Time.timeScale = 0;
                yield return null;
                pausedTick = Tick;
                // Switch: load the next copy, then unload the previous one.
                yield return LoadSmokeSceneAdditive();
                var previous = SmokeScenes().First();
                var unload = SceneManager.UnloadSceneAsync(previous);
                yield return Until(() => unload.isDone);
                yield return Until(() => Session.Owner != first.Owner);
                yield return null; // allow the old scene's queued End to reach Initialization

                var next = Session;
                if (paused)
                    Assert.That(Tick, Is.EqualTo(pausedTick));
                Assert.That(SmokeScenes().Length, Is.EqualTo(1));
                Assert.That(next.State, Is.EqualTo(BattleState.Active), "The late end of the unloaded scene did not stop the next battle.");
                Assert.That(next.Owner, Is.Not.EqualTo(first.Owner));
                Assert.That(next.BattleNumber, Is.EqualTo(first.BattleNumber + 1));
                Assert.That(LiveIds, Is.EqualTo(new[] { next.HumanCombatant.Value, next.AiCombatant.Value }));
                Assert.That(next.HumanCombatant.Value, Is.GreaterThan(first.AiCombatant.Value));
                foreach (var scene in SmokeScenes())
                    SceneManager.UnloadSceneAsync(scene);
                yield return Until(() => SmokeScenes().Length == 0 && !Session.IsActive);
                if (paused)
                    Assert.That(Tick, Is.EqualTo(pausedTick));
                Assert.That(LiveIds, Is.Empty);
            }
            finally
            {
                Time.timeScale = scale;
                foreach (var scene in SmokeScenes())
                    SceneManager.UnloadSceneAsync(scene);
            }
            yield return NextTick();
            Assert.That(SmokeScenes(), Is.Empty);
            Assert.That(LiveIds, Is.Empty, "Leaving the scene ends its battle.");
            Assert.That(Session.State, Is.EqualTo(BattleState.Idle));
        }
#endif
    }
}
