using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MLFLegends.Combat
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GroundShadowInstanceGpu
    {
        public float4 PositionRadius, TangentX, TangentZ, ClipX, ClipZ;
    }

    /// <summary>Read-only projection onto the existing arena; owned and submitted by CombatSpriteRenderer.</summary>
    public sealed class CombatGroundShadows : IDisposable
    {
        World m_World;
        EntityQuery m_Actors, m_Arena, m_Session;
        NativeList<GroundShadowInstanceGpu> m_Instances = new NativeList<GroundShadowInstanceGpu>(64, Allocator.Persistent);
        GraphicsBuffer m_Buffer;
        Material m_Material;
        Mesh m_Mesh;
        float3 m_Min, m_Max;
        static readonly int s_Instances = Shader.PropertyToID("_GroundShadows");
        static readonly int s_Color = Shader.PropertyToID("_ShadowColor");
        public static readonly int Stride = Marshal.SizeOf<GroundShadowInstanceGpu>();
        public int Count => m_Instances.Length;
        public int Capacity => m_Buffer != null ? m_Buffer.count : 0;
        public int LiveGpuResources => (m_Buffer != null ? 1 : 0) + (m_Material != null ? 1 : 0) + (m_Mesh != null ? 1 : 0);
        public NativeArray<GroundShadowInstanceGpu>.ReadOnly Instances => m_Instances.AsArray().AsReadOnly();
        public Bounds ProjectedBounds => new Bounds((Vector3)((m_Min + m_Max) * .5f), (Vector3)(m_Max - m_Min));

        public void Extract(World world, float radiusMultiplier, float bias)
        {
            m_Instances.Clear(); m_Min = new float3(float.PositiveInfinity); m_Max = new float3(float.NegativeInfinity);
            if (world != null && !world.IsCreated) world = null;
            if (world != m_World) Bind(world);
            if (world == null || m_Session.CalculateEntityCount() != 1 || !m_Session.GetSingleton<BattleSession>().IsActive
                || m_Arena.CalculateEntityCount() != 1) return;
            var arenaEntity = m_Arena.GetSingletonEntity(); var manager = world.EntityManager;
            var arena = manager.GetComponentData<CombatArenaState>(arenaEntity);
            if (!arena.Required || !arena.Valid) return; // No guessed support from stale spawn height or sprite altitude.
            var shapes = manager.GetBuffer<CombatArenaShape>(arenaEntity, true).AsNativeArray();
            using var positions = m_Actors.ToComponentDataArray<CombatPosition>(Allocator.Temp);
            using var bodies = m_Actors.ToComponentDataArray<CombatBody>(Allocator.Temp);
            for (var i = 0; i < positions.Length; i++)
            {
                var radius = bodies[i].Radius * math.max(0, radiusMultiplier);
                if (radius <= 0 || !TryProject(shapes, positions[i].Value, radius, bias, out var instance)) continue;
                m_Instances.Add(instance);
                var center = instance.PositionRadius.xyz;
                var extent = (math.abs(instance.TangentX.xyz) + math.abs(instance.TangentZ.xyz)) * radius + new float3(.001f);
                m_Min = math.min(m_Min, center - extent); m_Max = math.max(m_Max, center + extent);
            }
        }

        public static bool TryProject(NativeArray<CombatArenaShape> shapes, float3 position, float radius, float bias,
            out GroundShadowInstanceGpu result)
        {
            result = default; var highest = float.NegativeInfinity; var selected = -1;
            for (var i = 0; i < shapes.Length; i++)
            {
                var shape = shapes[i]; var p = shape.Local(position.xz);
                if (math.any(math.abs(p) > shape.HalfSize) || (shape.Kind == CombatArenaKind.Ramp && shape.Slope > CombatArenaTuning.MaxSlope)) continue;
                var height = shape.Kind == CombatArenaKind.Box ? shape.Top : shape.Bottom + (p.x + shape.HalfSize.x) * shape.Slope;
                if (height <= position.y + CombatArenaTuning.Skin && height > highest) { highest = height; selected = i; }
            }
            if (selected < 0) return false;
            var s = shapes[selected]; var slope = s.Kind == CombatArenaKind.Ramp ? s.Slope : 0;
            var tangentX = math.normalize(new float3(s.AxisX.x, slope, s.AxisX.y));
            var tangentZ = new float3(-s.AxisX.y, 0, s.AxisX.x);
            var normal = math.normalize(math.cross(tangentZ, tangentX));
            var center = new float3(position.x, highest, position.z) + normal * math.max(0, bias);
            var axisZ = new float2(-s.AxisX.y, s.AxisX.x);
            result = new GroundShadowInstanceGpu
            {
                PositionRadius = new float4(center, radius), TangentX = new float4(tangentX, 0), TangentZ = new float4(tangentZ, 0),
                ClipX = new float4(s.AxisX, math.dot(s.Center, s.AxisX), s.HalfSize.x),
                ClipZ = new float4(axisZ, math.dot(s.Center, axisZ), s.HalfSize.y),
            };
            return true;
        }

        public int Submit(Camera camera, Shader shader, Color color, int layer)
        {
            if (Count == 0 || camera == null || shader == null || !shader.isSupported || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return 0;
            if (m_Material == null) m_Material = new Material(shader) { name = "Combat Ground Shadow (shared)", hideFlags = HideFlags.DontSave };
            else if (m_Material.shader != shader) m_Material.shader = shader;
            if (m_Mesh == null)
            {
                m_Mesh = new Mesh { name = "Combat Shadow Quad", hideFlags = HideFlags.DontSave };
                m_Mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0) };
                m_Mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
                m_Mesh.UploadMeshData(true);
            }
            if (m_Buffer == null || m_Buffer.count < Count)
            {
                m_Buffer?.Release(); m_Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, math.max(64, math.ceilpow2(Count)), Stride);
            }
            m_Buffer.SetData(m_Instances.AsArray(), 0, 0, Count);
            m_Material.SetBuffer(s_Instances, m_Buffer); m_Material.SetColor(s_Color, color);
            var render = new RenderParams(m_Material) { camera = camera, worldBounds = ProjectedBounds,
                shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false, layer = layer };
            Graphics.RenderMeshPrimitives(render, m_Mesh, 0, Count);
            return 1;
        }

        void Bind(World world)
        {
            ReleaseQueries(); m_World = world;
            if (world == null) return;
            var manager = world.EntityManager;
            m_Actors = manager.CreateEntityQuery(typeof(CombatPosition), typeof(CombatBody), typeof(InCombat));
            m_Arena = manager.CreateEntityQuery(typeof(CombatArenaState), typeof(CombatArenaShape));
            m_Session = manager.CreateEntityQuery(typeof(BattleSession));
        }
        void ReleaseQueries()
        {
            if (m_World != null && m_World.IsCreated) { m_Actors.Dispose(); m_Arena.Dispose(); m_Session.Dispose(); }
            m_World = null;
        }
        static void Destroy(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj); else UnityEngine.Object.DestroyImmediate(obj);
        }
        public void Dispose()
        {
            ReleaseQueries(); if (m_Instances.IsCreated) m_Instances.Dispose(); m_Buffer?.Release(); m_Buffer = null;
            Destroy(m_Material); Destroy(m_Mesh); m_Material = null; m_Mesh = null;
        }
    }
}
