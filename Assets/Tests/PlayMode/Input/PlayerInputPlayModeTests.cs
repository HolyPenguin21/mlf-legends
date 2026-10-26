#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace MLFLegends.Tests.PlayMode
{
    // Stage 02.3 + 02.4 (#22) in the real Unity player loop and the auto-created default world, through the existing
    // technical scene CombatSmoke (bootstrap + PlayerInputAdapter + Main Camera with CombatCameraController).
    // InputTestFixture supplies virtual devices: in a [UnityTest] its queued events are processed by the real native
    // PreUpdate input update of the next frame, so the frame order input → fixed ticks → LateUpdate camera is the real one.
    // Time.captureFramerate fixes the frame delta (30/60/120 FPS = 2/1/0-or-1 ticks per frame). No test adds systems to
    // the default world or advances it manually. Adapters/cameras are destroyed before the fixture restores the Input
    // System; scenes are unloaded and the battle ended afterwards.
    public class PlayerInputPlayModeTests : InputTestFixture
    {
        const string SmokeScenePath = "Assets/Scenes/CombatSmoke.unity";

        Keyboard m_Keyboard;
        float m_TimeScale;
        int m_CaptureFramerate;

        static World DefaultWorld => World.DefaultGameObjectInjectionWorld;

        static EntityManager Manager => DefaultWorld.EntityManager;

        static T Singleton<T>() where T : unmanaged, IComponentData
        {
            using var query = Manager.CreateEntityQuery(typeof(T));
            return query.GetSingleton<T>();
        }

        static BattleSession Session => Singleton<BattleSession>();

        static uint Tick => Singleton<SimulationClock>().Tick;

        static PlayerInputCommandSystem Producer => DefaultWorld.GetExistingSystemManaged<PlayerInputCommandSystem>();

        static Entity HumanEntity()
        {
            using var query = Manager.CreateEntityQuery(typeof(CombatantId), typeof(CombatPosition));
            using var entities = query.ToEntityArray(Allocator.Temp);
            var human = Session.HumanCombatant.Value;
            return entities.ToArray().Single(e => Manager.GetComponentData<CombatantId>(e).Value == human);
        }

        static CombatCommandResult[] Results
        {
            get
            {
                using var query = Manager.CreateEntityQuery(typeof(CombatCommandQueue));
                return Manager.GetBuffer<CombatCommandResult>(query.GetSingletonEntity()).AsNativeArray().ToArray();
            }
        }

        static float Step => CombatMovementTuning.MoveSpeed * SimulationTiming.TickDuration;

        // Full keyboard state per event (see the EditMode PlayerInputProducerTests): queued now, processed by the next
        // frame's PreUpdate input update.
        readonly System.Collections.Generic.HashSet<Key> m_Keys = new System.Collections.Generic.HashSet<Key>();

        void Down(Key key)
        {
            m_Keys.Add(key);
            InputSystem.QueueStateEvent(m_Keyboard, new KeyboardState(m_Keys.ToArray()));
        }

        void Up(Key key)
        {
            m_Keys.Remove(key);
            InputSystem.QueueStateEvent(m_Keyboard, new KeyboardState(m_Keys.ToArray()));
        }

        public override void Setup()
        {
            base.Setup();
            m_Keyboard = InputSystem.AddDevice<Keyboard>();
            m_Keys.Clear();
            m_TimeScale = Time.timeScale;
            m_CaptureFramerate = Time.captureFramerate;
        }

        public override void TearDown()
        {
            Time.timeScale = m_TimeScale;
            Time.captureFramerate = m_CaptureFramerate;
            // Release every capture/camera action clone while the fixture's Input System is still the active one.
            foreach (var adapter in Object.FindObjectsByType<PlayerInputAdapter>(FindObjectsInactive.Include))
                Object.DestroyImmediate(adapter);
            foreach (var camera in Object.FindObjectsByType<CombatCameraController>(FindObjectsInactive.Include))
                Object.DestroyImmediate(camera);
            base.TearDown();
        }

        [UnityTearDown]
        public IEnumerator UnloadScenesAndEndTheBattle()
        {
            foreach (var scene in SmokeScenes())
                SceneManager.UnloadSceneAsync(scene);
            yield return Until(() => SmokeScenes().Length == 0);
            if (DefaultWorld != null && DefaultWorld.IsCreated)
            {
                BattleRequest.Submit(Manager, BattleRequest.End());
                yield return Until(() => !Session.IsActive);
            }
        }

        static Scene[] SmokeScenes() => Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
            .Where(scene => scene.path == SmokeScenePath && scene.isLoaded).ToArray();

        static IEnumerator Until(Func<bool> condition)
        {
            var deadline = Time.realtimeSinceStartup + 10f;
            while (!condition())
            {
                Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline), "Condition not reached in time.");
                yield return null;
            }
        }

        static IEnumerator LoadSmokeScene()
        {
            var load = EditorSceneManager.LoadSceneAsyncInPlayMode(SmokeScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            yield return Until(() => load.isDone);
        }

        /// <summary>Loads CombatSmoke and waits until its battle runs and its adapter drives the producer.</summary>
        IEnumerator StartSmokeBattle()
        {
            yield return LoadSmokeScene();
            var number = Session.BattleNumber;
            yield return Until(() => Session.IsActive && Session.BattleNumber > number && Session.RelativeTick(Tick) >= 1);
            Assert.That(Producer.Source, Is.Not.Null, "The scene adapter attached its capture.");
        }

        static PlayerInputAdapter Adapter(Scene scene) => scene.GetRootGameObjects()
            .SelectMany(o => o.GetComponentsInChildren<PlayerInputAdapter>(true)).Single();

        static CombatCameraController CameraOf(Scene scene) => scene.GetRootGameObjects()
            .SelectMany(o => o.GetComponentsInChildren<CombatCameraController>(true)).Single();

        // ---- real player loop ----

        [Test]
        public void PlayerLoopProcessesInputBeforeTheFixedTicksAndRunsLateUpdateAfterThem()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            int Phase(Type type) => Array.FindIndex(loop.subSystemList, s => s.type == type);
            int Inside(Type phase, Type system) => Array.FindIndex(loop.subSystemList[Phase(phase)].subSystemList, s => s.type == system);

            Assert.That(Phase(typeof(PreUpdate)), Is.LessThan(Phase(typeof(Update))));
            Assert.That(Phase(typeof(Update)), Is.LessThan(Phase(typeof(PreLateUpdate))));
            Assert.That(Inside(typeof(PreUpdate), typeof(PreUpdate.NewInputUpdate)), Is.GreaterThanOrEqualTo(0),
                "Dynamic-update input processing (Input System default) runs in PreUpdate.");
            Assert.That(Inside(typeof(Update), typeof(SimulationSystemGroup)), Is.GreaterThanOrEqualTo(0),
                "The default world's simulation (with FixedStep) runs in Update.");
            Assert.That(Inside(typeof(Update), typeof(SimulationSystemGroup)),
                Is.GreaterThan(Inside(typeof(Update), typeof(Update.ScriptRunBehaviourUpdate))));
            Assert.That(Inside(typeof(PreLateUpdate), typeof(PreLateUpdate.ScriptRunBehaviourLateUpdate)), Is.GreaterThanOrEqualTo(0),
                "The camera's LateUpdate runs after the frame's simulation.");

            // The project keeps the Input System default (no settings asset): ProcessEventsInDynamicUpdate, no manual update.
            Assert.That(EditorBuildSettings.TryGetConfigObject<InputSettings>("com.unity.input.settings", out _), Is.False,
                "No project InputSettings override.");
            Assert.That(new InputSettings().updateMode, Is.EqualTo(InputSettings.UpdateMode.ProcessEventsInDynamicUpdate));

            var collection = DefaultWorld.GetExistingSystemManaged<CombatCommandCollectionSystemGroup>();
            using var members = collection.GetAllSystems(Allocator.Temp);
            var types = members.AsArray().ToArray().Select(DefaultWorld.Unmanaged.GetSystemTypeIndex).ToArray();
            var producer = TypeManager.GetSystemTypeIndex<PlayerInputCommandSystem>();
            Assert.That(types.Count(t => t == producer), Is.EqualTo(1), "One auto-created player producer in the default world.");
            Assert.That(Array.IndexOf(types, producer),
                Is.LessThan(Array.IndexOf(types, TypeManager.GetSystemTypeIndex<CombatCommandProcessorSystem>())));
            Assert.That(Producer.Source, Is.Null, "Without a scene adapter the producer is inert.");
        }

        /// <summary>
        /// A tap queued in frame f is processed by frame f+1's PreUpdate and reaches the first fixed tick that runs after it
        /// (in frame f+1, or later when f+1 has no tick): the airborne velocity is exactly that of a jump on that tick.
        /// D held from frame f+1 to g moves the fighter on exactly the ticks of those frames.
        /// </summary>
        [UnityTest]
        public IEnumerator TapAndHoldReachTheRightTicksThroughTheRealPlayerLoop([Values(30, 60, 120)] int fps)
        {
            yield return StartSmokeBattle();
            Time.captureFramerate = fps;
            yield return null;
            yield return null;
            var human = HumanEntity();
            var camera = CameraOf(SmokeScenes().Single());
            var sawZeroTickFrame = false;
            var sawTickFrame = false;

            for (var attempt = 0; attempt < (fps == 120 ? 4 : 1); attempt++)
            {
                yield return Until(() => Manager.GetComponentData<CombatMotion>(human).Grounded);
                for (var i = 0; i < attempt; i++)
                    yield return null; // shift the tap by a frame: both tick parities at 120 FPS

                Down(Key.K);
                Up(Key.K);
                yield return null; // frame f+1: processed in its PreUpdate; its ticks have not run yet at this point
                var before = Tick;
                Assert.That(Manager.GetComponentData<CombatMotion>(human).Grounded, Is.True);
                yield return null;
                if (Tick == before)
                    sawZeroTickFrame = true;
                else
                    sawTickFrame = true;
                yield return Until(() => Tick > before);

                var airborneTicks = Tick - before;
                var expected = CombatMovementTuning.JumpImpulse;
                for (var i = 0; i < airborneTicks; i++)
                    expected -= CombatMovementTuning.Gravity * SimulationTiming.TickDuration;
                var motion = Manager.GetComponentData<CombatMotion>(human);
                Assert.That(motion.Grounded, Is.False, $"fps {fps} attempt {attempt}: the tap jumped");
                Assert.That(motion.VerticalVelocity, Is.EqualTo(expected).Within(1e-4f),
                    $"fps {fps}: the jump started on the first tick after the tap's frame ({airborneTicks} ticks ago)");
            }

            if (fps == 120)
                Assert.That(sawZeroTickFrame && sawTickFrame, Is.True, "Taps landed in both 0-tick and 1-tick frames.");

            // Hold D from frame f+1 to g.
            yield return Until(() => Manager.GetComponentData<CombatMotion>(human).Grounded);
            var x0 = Manager.GetComponentData<CombatPosition>(human).Value.x;
            Down(Key.D);
            yield return null;
            var first = Tick;
            for (var i = 0; i < 12; i++)
                yield return null;
            Up(Key.D);
            yield return null;
            var last = Tick;
            yield return null;
            yield return Until(() => Tick > last);
            var moved = last - first;
            Assert.That(moved, Is.GreaterThan(0));
            Assert.That(Manager.GetComponentData<CombatPosition>(human).Value.x, Is.EqualTo(x0 + moved * Step).Within(1e-4f),
                $"fps {fps}: moved on exactly the {moved} ticks of the held frames");
            Assert.That(Manager.GetComponentData<CombatFacing>(human).Value, Is.EqualTo(FacingDirection.Right));

            // The camera's LateUpdate of the previous frame followed the ECS position of that frame's last tick.
            var position = Manager.GetComponentData<CombatPosition>(human).Value;
            Assert.That(camera.HasTarget, Is.True);
            Assert.That(camera.Focus, Is.EqualTo(new Vector3(position.x, 0f, position.z)));
            Assert.That(camera.GetComponent<Camera>().orthographic, Is.True);
            Assert.That(Quaternion.Angle(camera.transform.rotation, camera.ViewRotation), Is.LessThan(1e-3f));
        }

        [UnityTest]
        public IEnumerator PauseCollectsNoPressWhilePanAndZoomStillWorkAndResumeUsesTheLiveHold()
        {
            yield return StartSmokeBattle();
            var human = HumanEntity();
            var camera = CameraOf(SmokeScenes().Single());
            yield return null;
            Time.timeScale = 0f;
            yield return null;
            yield return null;
            var tick = Tick;
            var position = Manager.GetComponentData<CombatPosition>(human);
            var rotation = camera.transform.rotation;
            var size = camera.OrthographicSize;

            Down(Key.K);
            Up(Key.K);
            yield return null;
            Down(Key.D);
            Down(Key.RightArrow);
            Down(Key.Equals);
            for (var i = 0; i < 10; i++)
                yield return null;
            Up(Key.RightArrow);
            Up(Key.Equals);
            yield return null;
            yield return null;

            Assert.That(Tick, Is.EqualTo(tick), "Paused: no fixed tick.");
            Assert.That(Manager.GetComponentData<CombatPosition>(human), Is.EqualTo(position), "Paused: the fighter does not move.");
            Assert.That(camera.PanOffset.x, Is.GreaterThan(0f), "Pan works on pause (unscaled time).");
            Assert.That(camera.OrthographicSize, Is.LessThan(size), "Zoom works on pause.");
            Assert.That(camera.transform.rotation, Is.EqualTo(rotation), "Fixed angle.");
            Assert.That(Producer.Source.PendingPressed, Is.EqualTo(CommandButtons.None), "No combat press collected on pause.");

            Time.timeScale = 1f;
            yield return Until(() => Tick > tick + 2);
            var motion = Manager.GetComponentData<CombatMotion>(human);
            Assert.That(motion.Grounded, Is.True, "The paused tap never jumps after resume.");
            var applied = Manager.GetComponentData<AppliedCombatCommand>(human).Command;
            Assert.That((applied.MoveX, applied.Pressed, applied.Source), Is.EqualTo(((sbyte)1, CommandButtons.None, CommandSource.Player)),
                "Resume: the live hold, no synthetic press.");
            Assert.That(Manager.GetComponentData<CombatPosition>(human).Value.x, Is.GreaterThan(position.Value.x));
            Up(Key.D);
            yield return null;
        }

        [UnityTest]
        public IEnumerator FocusLossStopsTheFighterOnTheNextTickAndDropsPresses()
        {
            yield return StartSmokeBattle();
            var human = HumanEntity();
            var adapter = Adapter(SmokeScenes().Single());
            Down(Key.D);
            yield return Until(() => Manager.GetComponentData<AppliedCombatCommand>(human).Command.MoveX == 1);

            Down(Key.J);
            adapter.SendMessage("OnApplicationFocus", false);
            Assert.That(adapter.Capture.PendingPressed, Is.EqualTo(CommandButtons.None));
            var tick = Tick;
            yield return Until(() => Tick > tick);
            var x = Manager.GetComponentData<CombatPosition>(human).Value.x;
            Assert.That(Manager.GetComponentData<AppliedCombatCommand>(human).Command.IsNone, Is.True, "Unfocused: neutral command.");
            tick = Tick;
            yield return Until(() => Tick > tick + 1);
            Assert.That(Manager.GetComponentData<CombatPosition>(human).Value.x, Is.EqualTo(x), "Not moving while unfocused.");

            adapter.SendMessage("OnApplicationFocus", true);
            tick = Tick;
            yield return Until(() => Tick > tick);
            var applied = Manager.GetComponentData<AppliedCombatCommand>(human).Command;
            Assert.That((applied.MoveX, applied.Held, applied.Pressed),
                Is.EqualTo(((sbyte)1, CommandButtons.Attack, CommandButtons.None)), "Focus back: live hold, no old press.");
            Up(Key.D);
            Up(Key.J);
            yield return null;
        }

        [UnityTest]
        public IEnumerator AdditiveSceneSwitchAndAdapterReenableKeepOneProducerAndOnePlayerCommandPerTick()
        {
            yield return StartSmokeBattle();
            var firstScene = SmokeScenes().Single();
            var firstAdapter = Adapter(firstScene);
            yield return LoadSmokeScene();
            var secondScene = SmokeScenes().Single(s => s != firstScene);
            var secondAdapter = Adapter(secondScene);
            Assert.That(Producer.Source, Is.SameAs(secondAdapter.Capture), "The latest enabled adapter drives the producer.");

            using (var all = DefaultWorld.Unmanaged.GetAllSystems(Allocator.Temp))
            {
                var producer = TypeManager.GetSystemTypeIndex<PlayerInputCommandSystem>();
                Assert.That(all.ToArray().Count(s => DefaultWorld.Unmanaged.GetSystemTypeIndex(s) == producer), Is.EqualTo(1),
                    "Two scenes, two adapters, one producer.");
            }

            var unload = SceneManager.UnloadSceneAsync(firstScene);
            yield return Until(() => unload.isDone);
            Assert.That(firstAdapter == null, Is.True);
            Assert.That(Producer.Source, Is.SameAs(secondAdapter.Capture), "The unloaded adapter did not detach its successor.");
            yield return Until(() => Session.IsActive);
            var human = HumanEntity();

            Down(Key.D);
            var tick = Tick;
            yield return Until(() => Tick > tick + 1);
            var player = Results.Where(r => r.Command.Source == CommandSource.Player).ToArray();
            Assert.That(player.Select(r => r.Outcome), Is.EqualTo(new[] { CommandOutcome.Accepted }), "One Player command per tick.");
            Assert.That(player[0].Command.Combatant, Is.EqualTo(Session.HumanCombatant));
            Assert.That(Manager.GetComponentData<AppliedCombatCommand>(human).Command.MoveX, Is.EqualTo(1));

            secondAdapter.enabled = false;
            Assert.That(Producer.Source, Is.Null, "Disabled adapter: detached.");
            tick = Tick;
            yield return Until(() => Tick > tick + 1);
            Assert.That(Results.Where(r => r.Command.Source == CommandSource.Player), Is.Empty);
            Assert.That(Manager.GetComponentData<AppliedCombatCommand>(human).WasSubmitted, Is.False);

            Down(Key.K);
            yield return null;
            secondAdapter.enabled = true;
            Assert.That(Producer.Source, Is.SameAs(secondAdapter.Capture));
            Assert.That(secondAdapter.Capture.PendingPressed, Is.EqualTo(CommandButtons.None));
            yield return null; // Move (a Value action) reads an already-held key at the first input update after enabling
            tick = Tick;
            yield return Until(() => Tick > tick);
            var applied = Manager.GetComponentData<AppliedCombatCommand>(human).Command;
            Assert.That((applied.MoveX, applied.Held, applied.Pressed), Is.EqualTo(((sbyte)1, CommandButtons.Jump, CommandButtons.None)),
                "Re-enabled: a new capture, the live state, no press from before.");
            Up(Key.D);
            Up(Key.K);
            yield return null;
        }

        [UnityTest]
        public IEnumerator CameraRebindsOnRestartAndHoldsItsViewWhenTheBattleEnds()
        {
            yield return StartSmokeBattle();
            var scene = SmokeScenes().Single();
            var camera = CameraOf(scene);
            Down(Key.LeftArrow);
            yield return null;
            yield return null;
            Up(Key.LeftArrow);
            yield return null;
            Assert.That(camera.PanOffset.x, Is.LessThan(0f));
            var first = Session.HumanCombatant;

            var bootstrap = scene.GetRootGameObjects().SelectMany(o => o.GetComponentsInChildren<CombatSmokeBootstrap>()).Single();
            bootstrap.enabled = false;
            yield return Until(() => !Session.IsActive);
            yield return null;
            var held = camera.transform.position;
            Assert.That(camera.HasTarget, Is.False);
            yield return null;
            Assert.That(camera.transform.position, Is.EqualTo(held), "No target: the last view is kept.");

            bootstrap.enabled = true;
            yield return Until(() => Session.IsActive && !Session.HumanCombatant.Equals(first));
            yield return null;
            yield return null;
            Assert.That(camera.HasTarget, Is.True);
            Assert.That(camera.PanOffset, Is.EqualTo(Vector3.zero), "A new battle drops the transient pan.");
            var position = Manager.GetComponentData<CombatPosition>(HumanEntity()).Value;
            Assert.That(camera.Focus, Is.EqualTo(new Vector3(position.x, 0f, position.z)));
        }
    }
}
#endif
