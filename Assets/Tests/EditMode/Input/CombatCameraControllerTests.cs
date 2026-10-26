using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Entities;
using UnityEditor;
using UnityEngine;

namespace MLFLegends.Tests.EditMode
{
    // Stage 02.4 (#22): the scene camera controller against a real isolated battle World (replay driver: real clock,
    // lifecycle, processor, facing, movement). Refresh is the controller's own LateUpdate body with explicit input and
    // unscaled delta; real LateUpdate/Input System actions are covered by the PlayMode suite.
    public class CombatCameraControllerTests
    {
        const float Dt = 1f / 60f;

        ReplayWorld m_Replay;
        GameObject m_Object;
        CombatCameraController m_Camera;

        [SetUp]
        public void SetUp()
        {
            m_Replay = new ReplayWorld("Camera");
            m_Replay.Request(BattleRequest.Start(BattleSetup.Default));
            m_Object = new GameObject("Combat camera", typeof(Camera));
            m_Camera = m_Object.AddComponent<CombatCameraController>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(m_Object);
            m_Replay?.Dispose();
            m_Replay = null;
        }

        void Refresh(Vector2 pan = default, float zoom = 0f, float steps = 0f, bool recenter = false, float dt = Dt, World world = null) =>
            m_Camera.Refresh(world ?? m_Replay.World,
                new CombatCameraController.FrameInput { Pan = pan, Zoom = zoom, ZoomSteps = steps, Recenter = recenter }, dt);

        void Set(string property, float value)
        {
            var serialized = new SerializedObject(m_Camera);
            serialized.FindProperty(property).floatValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        CombatantRecord Human => m_Replay.Records.Last().Combatant(m_Replay.Records.Last().HumanId);

        static readonly Vector3 s_Offset = new Vector3(0f, 1f, 0f);

        void AssertView(Vector3 focus, Vector3 pan)
        {
            var rotation = Quaternion.Euler(40f, 0f, 0f);
            Assert.That(Quaternion.Angle(m_Object.transform.rotation, rotation), Is.LessThan(1e-3f), "The angle never changes.");
            var expected = focus + s_Offset + pan - rotation * Vector3.forward * 30f;
            Assert.That(Vector3.Distance(m_Object.transform.position, expected), Is.LessThan(1e-4f),
                $"{m_Object.transform.position} vs {expected}");
            var camera = m_Object.GetComponent<Camera>();
            Assert.That(camera.orthographic, Is.True);
            Assert.That(camera.orthographicSize, Is.EqualTo(m_Camera.OrthographicSize));
        }

        [Test]
        public void FollowsTheHumansEcsPositionAfterMovementAndJumpAtAFixedAngle()
        {
            for (uint r = 1; r <= 30; r++)
                m_Replay.Script.Player(r, r, ReplayRole.Human, moveX: 1, moveZ: (sbyte)(r > 20 ? 1 : 0),
                    pressed: r == 5 ? CommandButtons.Jump : CommandButtons.None);
            var airborne = false;
            for (var i = 0; i < 30; i++)
            {
                m_Replay.Step();
                Refresh();
                var position = Human.Position;
                Assert.That(m_Camera.HasTarget, Is.True);
                Assert.That(m_Camera.Focus, Is.EqualTo(new Vector3(position.x, 0f, position.z)), "X/Z of the ECS position; altitude not followed by default.");
                AssertView(m_Camera.Focus, Vector3.zero);
                airborne |= position.y > 0f;
            }

            Assert.That(airborne, Is.True);
            Assert.That(Human.Position.x, Is.GreaterThan(BattleSetup.Default.Human.Position.x));

            Set("m_AltitudeFollow", 1f);
            m_Replay.Script.Player(31, 31, ReplayRole.Human, pressed: CommandButtons.Jump);
            m_Replay.Step(3);
            Refresh();
            Assert.That(Human.Position.y, Is.GreaterThan(0f));
            Assert.That(m_Camera.Focus.y, Is.EqualTo(Human.Position.y), "Altitude follow reads the same ECS Y.");
        }

        [Test]
        public void PanAndZoomAreClampedAndNeverRotateTheView()
        {
            m_Replay.Step();
            Refresh();
            var focus = m_Camera.Focus;

            Refresh(pan: new Vector2(1f, 0f), dt: 0.5f);
            Assert.That(m_Camera.PanOffset, Is.EqualTo(new Vector3(6f * 0.5f, 0f, 0f)), "Pan speed scales with the orthographic size.");
            AssertView(focus, m_Camera.PanOffset);
            Refresh(pan: new Vector2(0f, 1f), dt: 0.5f);
            Assert.That(m_Camera.PanOffset, Is.EqualTo(new Vector3(3f, 0f, 3f)), "Stick Y pans along world Z (yaw 0).");
            for (var i = 0; i < 100; i++)
                Refresh(pan: new Vector2(5f, 5f), dt: 1f);
            Assert.That(m_Camera.PanOffset.magnitude, Is.EqualTo(20f).Within(1e-4f), "Pan is clamped to MaxPan.");
            AssertView(focus, m_Camera.PanOffset);
            Refresh(recenter: true);
            Assert.That(m_Camera.PanOffset, Is.EqualTo(Vector3.zero));

            Refresh(steps: 1f);
            Assert.That(m_Camera.OrthographicSize, Is.EqualTo(6f / 1.15f).Within(1e-5f), "One wheel notch.");
            Refresh(zoom: 1f, dt: 10f);
            Assert.That(m_Camera.OrthographicSize, Is.EqualTo(2f), "Clamped to the minimum.");
            Refresh(zoom: -1f, dt: 10f);
            Assert.That(m_Camera.OrthographicSize, Is.EqualTo(20f), "Clamped to the maximum.");
            Refresh(steps: -100f);
            Assert.That(m_Camera.OrthographicSize, Is.EqualTo(20f));
            AssertView(focus, Vector3.zero);

            var size = m_Camera.OrthographicSize;
            Refresh(pan: new Vector2(float.NaN, 1f), zoom: float.NaN, steps: float.PositiveInfinity);
            Refresh(pan: Vector2.one, zoom: 1f, dt: -1f);
            Refresh(pan: Vector2.one, zoom: 1f, dt: float.NaN);
            Assert.That(m_Camera.PanOffset, Is.EqualTo(Vector3.zero), "Non-finite input and non-positive time do nothing.");
            Assert.That(m_Camera.OrthographicSize, Is.EqualTo(size));
            AssertView(focus, Vector3.zero);
        }

        [Test]
        public void PanFollowsTheConfiguredYawOnTheGroundPlane()
        {
            Set("m_Yaw", 90f);
            m_Replay.Step();
            Refresh(pan: new Vector2(1f, 0f), dt: 1f);
            Assert.That(Vector3.Distance(m_Camera.PanOffset, new Vector3(0f, 0f, -6f)), Is.LessThan(1e-4f), "Screen right at yaw 90° is world −Z.");
            Assert.That(Quaternion.Angle(m_Object.transform.rotation, Quaternion.Euler(40f, 90f, 0f)), Is.LessThan(1e-3f));
        }

        [Test]
        public void RestartRebindsAndDropsPanWhileEndDespawnDefeatAndWorldDisposalKeepTheLastView()
        {
            m_Replay.Step();
            Refresh(pan: Vector2.one, steps: 1f, dt: 0.25f);
            var firstHuman = m_Replay.Session.HumanCombatant;
            var size = m_Camera.OrthographicSize;
            Assert.That(m_Camera.PanOffset, Is.Not.EqualTo(Vector3.zero));

            var setup = BattleSetup.Default;
            setup.Human.Position = new Unity.Mathematics.float3(5f, 0f, 3f);
            m_Replay.Request(BattleRequest.Restart(setup));
            m_Replay.Step();
            Refresh();
            Assert.That(m_Replay.Session.HumanCombatant, Is.Not.EqualTo(firstHuman));
            Assert.That(m_Camera.HasTarget, Is.True);
            Assert.That(m_Camera.Focus, Is.EqualTo(new Vector3(5f, 0f, 3f)), "Bound to the new human.");
            Assert.That(m_Camera.PanOffset, Is.EqualTo(Vector3.zero), "Transient pan dropped.");
            Assert.That(m_Camera.OrthographicSize, Is.EqualTo(size), "User zoom kept.");

            void AssertHeld(string why)
            {
                var position = m_Object.transform.position;
                Refresh();
                Assert.That(m_Camera.HasTarget, Is.False, why);
                Assert.That(m_Camera.Focus, Is.EqualTo(new Vector3(5f, 0f, 3f)), why);
                Assert.That(m_Object.transform.position, Is.EqualTo(position), why);
            }

            Entity entity;
            using (var actors = m_Replay.Manager.CreateEntityQuery(typeof(CombatantId), typeof(CombatPosition)))
            using (var entities = actors.ToEntityArray(Unity.Collections.Allocator.Temp))
                entity = entities.ToArray().Single(e => m_Replay.Manager.GetComponentData<CombatantId>(e).Equals(m_Replay.Session.HumanCombatant));
            m_Replay.Manager.SetComponentEnabled<InCombat>(entity, false); // defeat stand-in
            AssertHeld("defeated");
            m_Replay.Manager.SetComponentEnabled<InCombat>(entity, true);
            Refresh();
            Assert.That(m_Camera.HasTarget, Is.True);

            m_Replay.Request(BattleRequest.Despawn(m_Replay.Session.HumanCombatant));
            m_Replay.Step();
            AssertHeld("despawned");
            m_Replay.Request(BattleRequest.End());
            m_Replay.Step();
            AssertHeld("ended");

            m_Replay.Dispose();
            AssertHeld("World disposed");
            m_Camera.Refresh(null, default, Dt);
            Assert.That(m_Camera.HasTarget, Is.False);

            m_Replay = new ReplayWorld("Camera 2");
            m_Replay.Request(BattleRequest.Start(BattleSetup.Default));
            m_Replay.Step();
            Refresh();
            Assert.That(m_Camera.HasTarget, Is.True, "A new World binds again.");
            Assert.That(m_Camera.Focus, Is.EqualTo((Vector3)BattleSetup.Default.Human.Position));
        }

        [Test]
        public void CameraUpdatesNeverChangeAuthoritativeStateOrTheClock()
        {
            var setup = BattleSetup.Default;
            setup.HumanAllies = setup.AiAllies = 199;
            m_Replay.Dispose();
            m_Replay = new ReplayWorld("Camera read-only");
            m_Replay.Request(BattleRequest.Start(setup));
            m_Replay.Script.Player(1, 1, ReplayRole.Human, moveX: 1, pressed: CommandButtons.Jump);
            m_Replay.Step(3);

            string[] Snapshot()
            {
                using var combatants = m_Replay.Manager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new ComponentType[] { typeof(CombatantId) },
                    Options = EntityQueryOptions.IgnoreComponentEnabledState,
                });
                var session = m_Replay.Session;
                var record = ReplayWorld.Capture(m_Replay.Manager, combatants, m_Replay.QueueEntity, m_Replay.Tick, session);
                return record.Raw.Concat(new[]
                {
                    $"tick {m_Replay.Tick} session {session.State} {session.BattleNumber} {session.BattleStartTick} {session.LastOutcome}",
                    $"queue {m_Replay.Queue.BattleStartTick} {m_Replay.Queue.LastProcessedTick} {m_Replay.SubmissionCount} {m_Replay.PendingCount}",
                    $"entities {m_Replay.EntityCount}",
                }).ToArray();
            }

            var before = Snapshot();
            Assert.That(before.Length, Is.GreaterThan(400));
            // Paused presentation: frames with pan/zoom but no fixed step.
            for (var i = 0; i < 50; i++)
                Refresh(pan: new Vector2(1f, -1f), zoom: 0.5f, steps: i % 2, recenter: i == 25);
            Assert.That(Snapshot(), Is.EqualTo(before), "Camera-only frames leave every combatant, the session, queue and clock untouched.");
            Assert.That(m_Camera.HasTarget, Is.True);
        }
    }
}
