using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Entities;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MLFLegends.Combat
{
    /// <summary>One read-only batched consumer owned by CombatSpriteRenderer. No event GameObjects or second clock.</summary>
    public sealed class CombatEffectsPresentation : IDisposable
    {
        public const int MaxAlive = 2048;
        [StructLayout(LayoutKind.Sequential)]
        public struct Instance
        {
            public float3 Position;
            public float DepthOffset;
            public uint Frame;
            public float Facing, Scale, Rotation;
        }
        struct Row
        {
            public Instance Instance;
            public int Atlas, Page;
            public ulong Sequence;
        }
        struct ByPage : IComparer<Row>
        {
            public int Compare(Row a, Row b)
            {
                var n = a.Atlas.CompareTo(b.Atlas); if (n != 0) return n;
                n = a.Page.CompareTo(b.Page); return n != 0 ? n : a.Sequence.CompareTo(b.Sequence);
            }
        }
        public struct Counters
        {
            public ulong Spawned, Expired, Retired, HistoryDropped, ResourceDropped;
            public int Alive, Dropped, MissingAnchors, MissingBindings, Submissions, BufferCapacity;
        }
        NativeArray<Row> m_Rows = new NativeArray<Row>(MaxAlive, Allocator.Persistent);
        readonly Instance[] m_Upload = new Instance[MaxAlive];
        World m_World;
        EntityQuery m_Streams, m_Clocks, m_Sessions;
        Material m_Material;
        MaterialPropertyBlock[] m_Blocks = Array.Empty<MaterialPropertyBlock>();
        GraphicsBuffer m_Buffer;
        bool m_ShaderErrorReported;
        public Counters Stats { get; private set; }
        public long NativeCapacityBytes => m_Rows.IsCreated ? (long)m_Rows.Length * UnsafeUtility.SizeOf<Row>() : 0;
        public int LiveGpuResources => (m_Material != null ? 1 : 0) + (m_Buffer != null ? 1 : 0);
        public Instance GetInstance(int i) => m_Upload[i];

        void Bind(World world)
        {
            if (m_World == world) return;
            if (m_World != null && m_World.IsCreated) { m_Streams.Dispose(); m_Clocks.Dispose(); m_Sessions.Dispose(); }
            foreach (var block in m_Blocks) block?.Clear();
            m_World = world;
            if (world == null) return;
            var em = world.EntityManager;
            m_Streams = em.CreateEntityQuery(typeof(CombatEffectStream), typeof(CombatEffectFact), typeof(CombatEffectResources));
            m_Clocks = em.CreateEntityQuery(typeof(SimulationClock));
            m_Sessions = em.CreateEntityQuery(typeof(BattleSession));
        }
        public void Refresh(World world, CombatSpriteExtraction bodies, in SpriteCardBasis basis, float nominalHeight,
            float depthBias, Camera camera, Shader shader, Mesh mesh, int layer, bool submit)
        {
            Stats = default;
            if (world != null && !world.IsCreated) world = null;
            Bind(world);
            if (world == null || m_Streams.CalculateEntityCount() != 1 || m_Clocks.CalculateEntityCount() != 1
                || m_Sessions.CalculateEntityCount() != 1) return;
            var session = m_Sessions.GetSingleton<BattleSession>(); if (!session.IsActive) return;
            var em = world.EntityManager;
            var entity = m_Streams.GetSingletonEntity(); var stream = em.GetComponentData<CombatEffectStream>(entity);
            var facts = em.GetBuffer<CombatEffectFact>(entity, true);
            var resources = em.GetComponentObject<CombatEffectResources>(entity);
            var now = m_Clocks.GetSingleton<SimulationClock>().Tick;
            var stats = new Counters { Spawned = stream.VisualPublished, Retired = stream.Expired, HistoryDropped = stream.Dropped, ResourceDropped = resources.CapacityDropped };
            var count = 0;
            for (var i = 0; i < stream.Count; i++)
            {
                var fact = facts[(stream.First + i) % CombatEffectStream.Capacity];
                if (fact.Epoch != stream.Epoch || fact.BattleNumber != session.BattleNumber || fact.Binding == 0) continue;
                if (now >= fact.End) { stats.Expired++; continue; }
                if (now < fact.Time) continue;
                var binding = resources.Get(fact.Binding);
                if (binding == null || binding.Atlas == null || binding.Atlas.IsDisposed) { stats.MissingBindings++; continue; }
                var origin = fact.Position; var elapsed = now - (double)fact.StartTick; var bodyElapsed = elapsed;
                var depth = depthBias;
                if (fact.Kind == CombatEffectKind.Swing)
                {
                    if (!em.Exists(fact.Attacker) || !em.HasComponent<CombatAttackState>(fact.Attacker)
                        || !em.HasComponent<CombatPosition>(fact.Attacker) || !em.HasComponent<InCombat>(fact.Attacker)
                        || !em.IsComponentEnabled<InCombat>(fact.Attacker) || em.HasComponent<Disabled>(fact.Attacker)) continue;
                    var attack = em.GetComponentData<CombatAttackState>(fact.Attacker);
                    if (!attack.IsAttacking || attack.StartTick != fact.StartTick) continue;
                    origin = em.GetComponentData<CombatPosition>(fact.Attacker).Value;
                    // Read the matching actor's bounded depth push, never a roster/page-wide maximum.
                    if (bodies != null) for (var b = 0; b < bodies.Count; b++)
                        if (bodies.InstanceIds[b] == fact.AttackerId) { depth += bodies.Instances[b].DepthOffset; break; }
                }
                else
                {
                    bodyElapsed = fact.Time - (fact.StartTick - 1d); // historical contact pose, never current extraction
                    // Fixed contact flashes cannot inherit a later target's tie group. Reserve the existing bounded
                    // body push budget for Front, keeping the same depth when that target moves/despawns.
                }
                var settings = binding.Settings;
                var sign = settings.Mirror == CombatEffectMirror.Facing ? fact.Facing : 1f;
                var relativeLayer = settings.Layer; var rotation = 0f;
                if (settings.Anchor != CombatEffectAnchor.Offset)
                {
                    var local = fact.Kind == CombatEffectKind.Swing
                        ? CombatAnimationSet.StrikeLocalFrame(binding.BodyTiming, fact.Timing, (uint)Math.Max(0, bodyElapsed))
                        : binding.BodyTiming.LocalFrameAt(bodyElapsed / SimulationTiming.TickRateHz);
                    if (!binding.AnchorVisible[local])
                    { stats.MissingAnchors++; if (settings.MissingAnchor == CombatEffectMissingAnchor.Skip) continue; }
                    else
                    {
                        var pixel = binding.AnchorPixels[local]; pixel.x *= fact.Facing;
                        origin = SpriteCardMath.CardToWorld(basis, origin, pixel, nominalHeight / binding.BodyNominalPixels);
                        relativeLayer = binding.AnchorLayers[local];
                        var direction = binding.AnchorDirections[local]; direction.x *= fact.Facing;
                        rotation = math.atan2(direction.y, direction.x) - (sign < 0 ? math.PI : 0);
                    }
                }
                if (fact.Kind == CombatEffectKind.Impact && relativeLayer == SpriteAnchorLayer.Front)
                    depth += CombatSpriteExtraction.DepthOffsetBudget;
                // World forward offset and frame geometry are each reflected once. No physical Position writer.
                origin += new float3(settings.Offset.x * sign, settings.Offset.y, settings.Offset.z);
                if (count == MaxAlive) { stats.Dropped++; continue; } // oldest retained live facts win, stable publication sequence
                var progress = (now - fact.Time) / (fact.End - fact.Time);
                var frame = binding.FrameAt(progress);
                m_Rows[count++] = new Row { Atlas = binding.AtlasIndex, Page = binding.Atlas.FramePages[(int)frame], Sequence = fact.Sequence,
                    Instance = new Instance { Position = origin, Frame = frame, Facing = sign,
                        Scale = nominalHeight / binding.Atlas.Definition.NominalHeightPx * settings.Scale,
                        DepthOffset = depth + (relativeLayer == SpriteAnchorLayer.Front ? .004f : -.004f), Rotation = rotation } };
            }
            m_Rows.GetSubArray(0, count).Sort(new ByPage());
            for (var i = 0; i < count; i++) m_Upload[i] = m_Rows[i].Instance;
            stats.Alive = count;
            if (submit && count > 0 && camera != null && mesh != null && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
            {
                if (shader == null || !shader.isSupported)
                {
                    stats.Dropped += count;
                    if (!m_ShaderErrorReported) Debug.LogError("Combat effect shader missing or unsupported; live VFX dropped (see effect counters).");
                    m_ShaderErrorReported = true; Stats = stats; return;
                }
                m_ShaderErrorReported = false;
                if (m_Material == null) m_Material = new Material(shader) { name = "Combat Effects (shared)", hideFlags = HideFlags.DontSave };
                else m_Material.shader = shader;
                if (m_Buffer == null) m_Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxAlive, Marshal.SizeOf<Instance>());
                m_Buffer.SetData(m_Upload, 0, 0, count);
                var batch = 0;
                for (var start = 0; start < count;)
                {
                    var row = m_Rows[start]; var end = start + 1;
                    while (end < count && m_Rows[end].Atlas == row.Atlas && m_Rows[end].Page == row.Page) end++;
                    if (batch == m_Blocks.Length)
                    { Array.Resize(ref m_Blocks, math.max(4, m_Blocks.Length * 2)); }
                    var block = m_Blocks[batch] ??= new MaterialPropertyBlock(); batch++;
                    var atlas = resources.Atlases[row.Atlas]; var texture = atlas.GetPage(row.Page);
                    var expectedSize = atlas.Definition.Pages[row.Page].Size;
                    if (texture == null || texture.width != expectedSize.x || texture.height != expectedSize.y)
                    {
                        block.Clear(); stats.MissingBindings += end - start; stats.Dropped += end - start; start = end; continue;
                    }
                    block.SetTexture("_MainTex", texture); block.SetBuffer("_SpriteFrames", atlas.GetFrameBuffer());
                    block.SetBuffer("_EffectInstances", m_Buffer); block.SetInteger("_InstanceOffset", start);
                    block.SetVector("_CardRight", new Vector4(basis.Right.x, basis.Right.y, basis.Right.z, 0));
                    block.SetVector("_CardUp", new Vector4(basis.Up.x, basis.Up.y, basis.Up.z, 0));
                    block.SetVector("_CardGround", new Vector4(basis.Ground.x, basis.Ground.y, basis.Ground.z, 0));
                    block.SetVector("_CardView", new Vector4(basis.ViewDirection.x, basis.ViewDirection.y, basis.ViewDirection.z, 0));
                    var min = new float3(float.PositiveInfinity); var max = new float3(float.NegativeInfinity);
                    for (var i = start; i < end; i++)
                    {
                        var instance = m_Upload[i]; var rect = atlas.Frames[(int)instance.Frame].CardRect;
                        // Radius encloses any anchor-oriented rotation, including the bend at support height.
                        var radius = math.length(new float2(math.max(math.abs(rect.x), math.abs(rect.z)), math.max(math.abs(rect.y), math.abs(rect.w)))) * instance.Scale;
                        var extent = radius * (math.abs(basis.Right) + math.abs(basis.Up) + math.abs(basis.Ground));
                        var center = instance.Position - basis.ViewDirection * instance.DepthOffset;
                        min = math.min(min, center - extent); max = math.max(max, center + extent);
                    }
                    Graphics.RenderMeshPrimitives(new RenderParams(m_Material) { camera = camera, matProps = block, layer = layer,
                        worldBounds = new Bounds((Vector3)((min + max) * .5f), (Vector3)(max - min + .01f)),
                        shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false }, mesh, 0, end - start);
                    stats.Submissions++; start = end;
                }
            }
            stats.BufferCapacity = m_Buffer?.count ?? 0;
            Stats = stats;
        }
        public void Dispose()
        {
            Bind(null); if (m_Rows.IsCreated) m_Rows.Dispose();
            m_Buffer?.Release(); m_Buffer = null;
            if (m_Material != null)
                if (Application.isPlaying) UnityEngine.Object.Destroy(m_Material); else UnityEngine.Object.DestroyImmediate(m_Material);
            m_Material = null;
            foreach (var block in m_Blocks) block?.Clear();
            m_Blocks = Array.Empty<MaterialPropertyBlock>(); Stats = default;
        }
    }
}

