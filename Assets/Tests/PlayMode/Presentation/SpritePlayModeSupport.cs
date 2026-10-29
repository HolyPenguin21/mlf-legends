using System;
using System.Collections;
using System.IO;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MLFLegends.Tests.PlayMode
{
    /// <summary>
    /// Stage 03 (#38) PlayMode helpers: a battle in the auto-created default World (requested with an owner token, so only
    /// this suite's battle is ended), a renderer + camera created from code, and RenderTexture readback for graphics tests.
    /// </summary>
    static class SpritePlayModeSupport
    {
        public const uint Owner = 0x5_0338;
        public const string ShaderName = "MLF Legends/Combat Sprite";

        public static World DefaultWorld => World.DefaultGameObjectInjectionWorld;

        public static EntityManager Manager => DefaultWorld.EntityManager;

        public static T Singleton<T>() where T : unmanaged, IComponentData
        {
            using var query = Manager.CreateEntityQuery(typeof(T));
            return query.GetSingleton<T>();
        }

        public static BattleSession Session => Singleton<BattleSession>();

        public static uint Tick => Singleton<SimulationClock>().Tick;

        /// <summary>Pauses/resumes the default World's movement owner (static renderer poses only).</summary>
        public static void SetMovementEnabled(bool enabled)
        {
            if (DefaultWorld == null || !DefaultWorld.IsCreated) return;
            var movement = DefaultWorld.GetExistingSystem<CombatMovementSystem>();
            if (movement != SystemHandle.Null) DefaultWorld.Unmanaged.ResolveSystemStateRef(movement).Enabled = enabled;
        }

        /// <summary>
        /// Since 04.3 bodies collide. The run/idle fixtures move even IDs along +Z inside the 1-unit ally formation, so put
        /// idle (odd) fighters .5 to the side and use small bodies: every mover gets a free lane without passing anyone.
        /// </summary>
        public static void SeparateRunLanes()
        {
            using var query = Manager.CreateEntityQuery(typeof(CombatantId), typeof(CombatPosition), typeof(CombatBody));
            using var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var e in entities)
            {
                Manager.SetComponentData(e, new CombatBody { Radius = .1f, Height = 2, Mass = 1 });
                if ((Manager.GetComponentData<CombatantId>(e).Value & 1) == 1)
                    Manager.SetComponentData(e, new CombatPosition { Value = Manager.GetComponentData<CombatPosition>(e).Value + new float3(.5f, 0, 0) });
            }
        }

        public static bool HasGraphicsDevice => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>Graphics checks are NOT RUN without a device (-nographics); a headless PASS must not stand in for them.</summary>
        public static void RequireGraphicsDevice()
        {
            if (!HasGraphicsDevice)
                Assert.Ignore("NOT RUN: no graphics device (batchmode -nographics / Null device). Run PlayMode without -nographics.");
        }

        public static BattleSetup Duel(float3 human, FacingDirection humanFacing, float3 ai, FacingDirection aiFacing, int alliesPerTeam = 0) => new BattleSetup
        {
            Human = new CombatantSetup { Team = 1, Position = human, Facing = humanFacing },
            Ai = new CombatantSetup { Team = 2, Position = ai, Facing = aiFacing },
            HumanAllies = (ushort)alliesPerTeam,
            AiAllies = (ushort)alliesPerTeam,
        };

        /// <summary>Restarts the default World's battle with <paramref name="setup"/> and waits for its first simulated tick.</summary>
        public static IEnumerator RestartBattle(BattleSetup setup)
        {
            var number = Session.BattleNumber;
            BattleRequest.Submit(Manager, BattleRequest.Restart(setup, Owner));
            yield return Until(() => Session.IsActive && Session.BattleNumber > number && Session.RelativeTick(Tick) >= 1);
        }

        public static IEnumerator EndBattle()
        {
            if (DefaultWorld == null || !DefaultWorld.IsCreated || !Session.IsActive)
                yield break;
            BattleRequest.Submit(Manager, BattleRequest.End(Owner));
            yield return Until(() => !Session.IsActive);
        }

        public static IEnumerator Until(Func<bool> condition, float seconds = 10f)
        {
            var deadline = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline), "Condition not reached in time.");
                yield return null;
            }
        }

        /// <summary>ECS position of every in-combat combatant by ID.</summary>
        public static (uint id, float3 position, FacingDirection facing)[] Combatants()
        {
            using var query = Manager.CreateEntityQuery(typeof(CombatantId), typeof(CombatPosition), typeof(CombatFacing), typeof(InCombat));
            using var ids = query.ToComponentDataArray<CombatantId>(Allocator.Temp);
            using var positions = query.ToComponentDataArray<CombatPosition>(Allocator.Temp);
            using var facings = query.ToComponentDataArray<CombatFacing>(Allocator.Temp);
            return Enumerable.Range(0, ids.Length).Select(i => (ids[i].Value, positions[i].Value, facings[i].Value)).ToArray();
        }

        /// <summary>An orthographic camera at the calibration angle (40°, 0°) looking at <paramref name="lookAt"/>.</summary>
        public static Camera CreateCamera(string name, Vector3 lookAt, float orthographicSize, RenderTexture target = null, float pitch = 40f)
        {
            var camera = new GameObject(name).AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = orthographicSize;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 1000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.allowMSAA = false;
            camera.allowHDR = false;
            var rotation = Quaternion.Euler(pitch, 0f, 0f);
            camera.transform.SetPositionAndRotation(lookAt - rotation * Vector3.forward * 30f, rotation);
            camera.targetTexture = target;
            if (target != null)
                camera.enabled = false; // rendered explicitly by the test
            return camera;
        }

        public static CombatSpriteRenderer CreateRenderer(Camera camera, string name = "Combat sprites")
        {
            var gameObject = new GameObject(name);
            gameObject.SetActive(false);
            var renderer = gameObject.AddComponent<CombatSpriteRenderer>();
            renderer.Configure(Shader.Find(ShaderName), camera, CombatSpriteRenderer.AtlasSource.TechnicalFixture);
            gameObject.SetActive(true);
            return renderer;
        }

        public static RenderTexture CreateTarget(int size = 256) =>
            new RenderTexture(new RenderTextureDescriptor(size, size, RenderTextureFormat.ARGB32, 24) { sRGB = true, msaaSamples = 1 })
            { name = "Sprite test target", filterMode = FilterMode.Point };

        /// <summary>Queues the renderer's draws and renders the camera into its target, then reads the target back.</summary>
        public static Color32[] RenderAndRead(CombatSpriteRenderer renderer, Camera camera, Texture2D readback)
        {
            renderer.Refresh();
            camera.Render();
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = camera.targetTexture;
                readback.ReadPixels(new Rect(0, 0, readback.width, readback.height), 0, 0, false);
                readback.Apply(false);
            }
            finally { RenderTexture.active = previous; }
            return readback.GetPixels32();
        }

        /// <summary>Writes the readback as PNG evidence under TestResults/graphics (not committed by the test).</summary>
        public static string SaveEvidence(Texture2D readback, string name)
        {
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "TestResults", "graphics"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name + ".png");
            File.WriteAllBytes(path, readback.EncodeToPNG());
            return path;
        }

        public static void DestroyAll(params Object[] objects)
        {
            foreach (var o in objects)
            {
                if (o == null)
                    continue;
                if (o is Component component)
                    Object.Destroy(component.gameObject);
                else if (o is RenderTexture texture)
                {
                    texture.Release();
                    Object.Destroy(texture);
                }
                else
                    Object.Destroy(o);
            }
        }
    }

    /// <summary>Opt-in fixture producer through the existing queue; never an authoritative state writer.</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(CombatCommandCollectionSystemGroup))]
    [UpdateBefore(typeof(CombatCommandProcessorSystem))]
    public partial class SpriteRunTestProducer : SystemBase
    {
        public bool Move = true;
        public bool AlternateDirection;

        /// <summary>Stage 05.1: when nonzero, fighters with ID % StrikeModulo == 1 press Attack on relative ticks t with t % StrikeEvery == 1.</summary>
        public uint StrikeEvery;
        public uint StrikeModulo = 4;

        protected override void OnUpdate()
        {
            var session = SystemAPI.GetSingleton<BattleSession>();
            if (!session.IsActive) return;
            var tick = SystemAPI.GetSingleton<SimulationClock>().Tick;
            var queue = SystemAPI.GetSingletonEntity<CombatCommandQueue>();
            var submissions = EntityManager.GetBuffer<CombatCommandSubmission>(queue);
            var strike = StrikeEvery != 0 && session.RelativeTick(tick) % StrikeEvery == 1;
            foreach (var id in SystemAPI.Query<RefRO<CombatantId>>().WithAll<InCombat>())
                if (Move && (id.ValueRO.Value & 1) == 0)
                    submissions.Add(new CombatCommandSubmission(new CombatCommand
                    {
                        Combatant = id.ValueRO, Tick = tick, MoveZ = (sbyte)(AlternateDirection && (tick & 1) != 0 ? -1 : 1), Source = CommandSource.AI,
                    }));
                else if (strike && id.ValueRO.Value % StrikeModulo == 1)
                    submissions.Add(new CombatCommandSubmission(new CombatCommand
                    {
                        Combatant = id.ValueRO, Tick = tick, Pressed = CommandButtons.Attack, Source = CommandSource.AI,
                    }));
        }

        public static SpriteRunTestProducer Install(World world)
        {
            var producer = world.CreateSystemManaged<SpriteRunTestProducer>();
            var group = world.GetExistingSystemManaged<CombatCommandCollectionSystemGroup>();
            group.AddSystemToUpdateList(producer);
            group.SortSystems();
            return producer;
        }

        public static void Remove(World world, SpriteRunTestProducer producer)
        {
            if (producer == null || world == null || !world.IsCreated) return;
            world.GetExistingSystemManaged<CombatCommandCollectionSystemGroup>().RemoveSystemFromUpdateList(producer);
            world.DestroySystemManaged(producer);
        }
    }

}
