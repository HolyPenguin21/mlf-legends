using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;

namespace MLFLegends.Combat
{
    /// <summary>GPU instance record (24 bytes); layout mirrored by <c>SpriteInstance</c> in CombatSprite.shader.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SpriteInstanceGpu
    {
        /// <summary>The combatant's <see cref="CombatPosition"/> (support point), copied, never written back.</summary>
        public float3 Position;

        /// <summary>Render-only tie-break push towards the camera (world units along the view direction), ≥ 0.</summary>
        public float DepthOffset;

        public uint Frame;

        /// <summary>+1 right, −1 left, from <see cref="CombatFacing"/>.</summary>
        public float Facing;
    }

    /// <summary>
    /// Read-only extraction of the sprite instances of one combat World: every <see cref="InCombat"/>-enabled combatant of
    /// the active battle, with its <see cref="CombatPosition"/>, <see cref="CombatFacing"/>, selected frame and depth
    /// tie-break, grouped by atlas page. Owned by <see cref="CombatSpriteRenderer"/>; usable without a graphics device.
    /// </summary>
    /// <remarks>
    /// Two queries per World, created on the first run against it and released on World change/<see cref="Dispose"/> (a
    /// disposed World released them already). Never writes components, adds entities or makes structural changes. Buffers
    /// are reused; after warm-up a run allocates no managed memory (native Temp only). Frame selections are keyed by
    /// <see cref="CombatantId"/> and dropped on a new battle number or World, so no ID history is kept.
    /// </remarks>
    public sealed class CombatSpriteExtraction : IDisposable
    {
        public static readonly int InstanceStride = Marshal.SizeOf<SpriteInstanceGpu>();

        static readonly ProfilerMarker s_Marker = new ProfilerMarker("CombatSpriteExtraction.Run");

        World m_World;
        EntityQuery m_Sessions;
        EntityQuery m_Combatants;
        uint m_BoundBattle;
        readonly Dictionary<uint, uint> m_Selections = new Dictionary<uint, uint>();

        NativeList<SortItem> m_Sort;
        NativeList<float> m_Keys;
        NativeList<uint> m_SortIds;
        NativeList<float> m_Offsets;
        NativeList<SpriteInstanceGpu> m_Instances;
        NativeList<uint> m_InstanceIds;
        int[] m_PageCounts = Array.Empty<int>();
        int[] m_PageStarts = Array.Empty<int>();
        float3[] m_PageMin = Array.Empty<float3>();
        float3[] m_PageMax = Array.Empty<float3>();
        int m_PageCount;
        bool m_Disposed;

        struct SortItem
        {
            public float Key;
            public uint Id;
            public int Index;
        }

        /// <summary>Farther first; equal depth: higher ID first, so the lowest ID ends up in front. Total order (IDs are unique).</summary>
        struct BackToFront : IComparer<SortItem>
        {
            public int Compare(SortItem a, SortItem b)
            {
                var byKey = b.Key.CompareTo(a.Key);
                return byKey != 0 ? byKey : b.Id.CompareTo(a.Id);
            }
        }

        public CombatSpriteExtraction(int initialCapacity = 64)
        {
            m_Sort = new NativeList<SortItem>(initialCapacity, Allocator.Persistent);
            m_Keys = new NativeList<float>(initialCapacity, Allocator.Persistent);
            m_SortIds = new NativeList<uint>(initialCapacity, Allocator.Persistent);
            m_Offsets = new NativeList<float>(initialCapacity, Allocator.Persistent);
            m_Instances = new NativeList<SpriteInstanceGpu>(initialCapacity, Allocator.Persistent);
            m_InstanceIds = new NativeList<uint>(initialCapacity, Allocator.Persistent);
        }

        /// <summary>World of the last run (null without a live one).</summary>
        public World BoundWorld => m_World;

        public int Count => m_Instances.Length;

        /// <summary>Instances grouped by page; within a page in back-to-front order (visibility comes from the GPU depth test).</summary>
        public NativeArray<SpriteInstanceGpu>.ReadOnly Instances => m_Instances.AsArray().AsReadOnly();

        /// <summary>Same data as <see cref="Instances"/> for the GPU upload (GraphicsBuffer.SetData takes a NativeArray).</summary>
        internal NativeArray<SpriteInstanceGpu> UploadArray => m_Instances.AsArray();

        /// <summary><see cref="CombatantId"/> value of each entry of <see cref="Instances"/>.</summary>
        public NativeArray<uint>.ReadOnly InstanceIds => m_InstanceIds.AsArray().AsReadOnly();

        public int PageCount => m_PageCount;

        public int PageStart(int page) => m_PageStarts[page];

        public int PageInstances(int page) => m_PageCounts[page];

        /// <summary>Axis-aligned box of the support points on <paramref name="page"/> (only meaningful with instances).</summary>
        public (float3 min, float3 max) PageSupportBounds(int page) => (m_PageMin[page], m_PageMax[page]);

        /// <summary>Largest tie-break push of the last run, world units.</summary>
        public float MaxDepthOffset { get; private set; }

        /// <summary>Number of explicit frame selections currently held (bounded by the battle roster).</summary>
        public int SelectionCount => m_Selections.Count;

        public void Select(CombatantId combatant, uint frame) => m_Selections[combatant.Value] = frame;

        public void ClearSelection(CombatantId combatant) => m_Selections.Remove(combatant.Value);

        public void ClearSelections() => m_Selections.Clear();

        /// <summary>Empties the last result (no instances, no pages) but keeps the World binding and selections.</summary>
        public void Clear()
        {
            if (m_Disposed)
                throw new ObjectDisposedException(nameof(CombatSpriteExtraction));
            m_Instances.Clear();
            m_InstanceIds.Clear();
            MaxDepthOffset = 0f;
            Array.Clear(m_PageCounts, 0, m_PageCounts.Length);
            m_PageCount = 0;
        }

        /// <summary>
        /// Rebuilds the instances from <paramref name="world"/> (null or disposed → none). <paramref name="defaultFrame"/> is
        /// drawn without a selection; selections outside the atlas fall back to it.
        /// </summary>
        public void Run(World world, SpriteAtlas atlas, in SpriteCardBasis basis, float depthSeparation, uint defaultFrame)
        {
            if (m_Disposed)
                throw new ObjectDisposedException(nameof(CombatSpriteExtraction));
            using var scope = s_Marker.Auto();
            m_Instances.Clear();
            m_InstanceIds.Clear();
            MaxDepthOffset = 0f;
            m_PageCount = atlas != null ? atlas.PageCount : 0;
            EnsurePageArrays(m_PageCount);
            Array.Clear(m_PageCounts, 0, m_PageCount);
            Array.Clear(m_PageStarts, 0, m_PageCount);

            if (world != null && !world.IsCreated)
                world = null;
            if (world != m_World)
                Bind(world);
            if (atlas == null || world == null || m_Sessions.CalculateEntityCount() != 1)
                return;
            var session = m_Sessions.GetSingleton<BattleSession>();
            if (session.BattleNumber != m_BoundBattle)
            {
                // A later battle of this World drops the selections; the first battle seen keeps those made before it.
                if (m_BoundBattle != 0)
                    m_Selections.Clear();
                m_BoundBattle = session.BattleNumber;
            }

            if (!session.IsActive)
                return;

            using var ids = m_Combatants.ToComponentDataArray<CombatantId>(Allocator.Temp);
            using var positions = m_Combatants.ToComponentDataArray<CombatPosition>(Allocator.Temp);
            using var facings = m_Combatants.ToComponentDataArray<CombatFacing>(Allocator.Temp);
            var count = ids.Length;
            if (count == 0)
                return;

            m_Keys.ResizeUninitialized(count);
            m_SortIds.ResizeUninitialized(count);
            m_Offsets.ResizeUninitialized(count);
            for (var i = 0; i < count; i++)
            {
                m_Keys[i] = basis.DepthKey(positions[i].Value);
                m_SortIds[i] = ids[i].Value;
            }

            MaxDepthOffset = ComputeDepthOffsets(m_Keys.AsArray(), m_SortIds.AsArray(), depthSeparation, m_Offsets.AsArray(), m_Sort);

            var frameCount = (uint)atlas.FrameCount;
            var fallback = defaultFrame < frameCount ? defaultFrame : 0u;
            var framePages = atlas.FramePages;
            m_Instances.ResizeUninitialized(count);
            m_InstanceIds.ResizeUninitialized(count);
            // Not `using var`: element writes on a using-declared struct do not compile (CS1654).
            var frames = new NativeArray<uint>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (var i = 0; i < count; i++)
            {
                var frame = m_Selections.TryGetValue(ids[i].Value, out var selected) && selected < frameCount ? selected : fallback;
                frames[i] = frame;
                m_PageCounts[framePages[(int)frame]]++;
            }

            var start = 0;
            for (var p = 0; p < m_PageCount; p++)
            {
                m_PageStarts[p] = start;
                start += m_PageCounts[p];
                m_PageMin[p] = new float3(float.PositiveInfinity);
                m_PageMax[p] = new float3(float.NegativeInfinity);
            }

            // Group by page in back-to-front order (m_Sort holds it), so the buffer layout is independent of chunk order.
            var cursor = new NativeArray<int>(m_PageCount, Allocator.Temp);
            for (var s = 0; s < count; s++)
            {
                var index = m_Sort[s].Index;
                var frame = frames[index];
                var page = framePages[(int)frame];
                var slot = m_PageStarts[page] + cursor[page]++;
                var position = positions[index].Value;
                m_Instances[slot] = new SpriteInstanceGpu
                {
                    Position = position,
                    DepthOffset = m_Offsets[index],
                    Frame = frame,
                    Facing = facings[index].Value == FacingDirection.Left ? -1f : 1f,
                };
                m_InstanceIds[slot] = ids[index].Value;
                m_PageMin[page] = math.min(m_PageMin[page], position);
                m_PageMax[page] = math.max(m_PageMax[page], position);
            }

            cursor.Dispose();
            frames.Dispose();
        }

        /// <summary>
        /// Depth tie-break for cards with view-depth <paramref name="keys"/> (larger = farther): back to front by (key, ID),
        /// every card is pushed at least <paramref name="separation"/> in front of the previous one; cards already that far
        /// apart keep offset 0. Equal depths are therefore ordered by ID (lowest in front) whatever the input order.
        /// Returns the largest push (bounded by count × separation).
        /// </summary>
        public static float ComputeDepthOffsets(NativeArray<float> keys, NativeArray<uint> ids, float separation, NativeArray<float> offsets)
        {
            var sort = new NativeList<SortItem>(keys.Length, Allocator.Temp);
            var max = ComputeDepthOffsets(keys, ids, separation, offsets, sort);
            sort.Dispose();
            return max;
        }

        static float ComputeDepthOffsets(NativeArray<float> keys, NativeArray<uint> ids, float separation, NativeArray<float> offsets, NativeList<SortItem> sort)
        {
            sort.Clear();
            for (var i = 0; i < keys.Length; i++)
                sort.Add(new SortItem { Key = keys[i], Id = ids[i], Index = i });
            sort.Sort(new BackToFront());
            var previous = float.PositiveInfinity;
            var max = 0f;
            for (var s = 0; s < sort.Length; s++)
            {
                var item = sort[s];
                var rendered = math.min(item.Key, previous - separation);
                previous = rendered;
                // Keys are view depths, so the gap is the push along the view direction.
                var offset = item.Key - rendered;
                offsets[item.Index] = offset;
                max = math.max(max, offset);
            }

            return max;
        }

        void Bind(World world)
        {
            // Selections made before the first World is bound stay; leaving a World drops them (IDs are World-scoped).
            Release(clearSelections: m_World != null);
            m_World = world;
            if (world == null)
                return;
            var manager = world.EntityManager;
            m_Sessions = manager.CreateEntityQuery(ComponentType.ReadOnly<BattleSession>());
            m_Combatants = manager.CreateEntityQuery(ComponentType.ReadOnly<CombatantId>(), ComponentType.ReadOnly<CombatPosition>(),
                ComponentType.ReadOnly<CombatFacing>(), ComponentType.ReadOnly<InCombat>());
        }

        void Release(bool clearSelections)
        {
            if (m_World != null && m_World.IsCreated)
            {
                m_Sessions.Dispose();
                m_Combatants.Dispose();
            }

            m_World = null;
            m_Sessions = default;
            m_Combatants = default;
            m_BoundBattle = 0;
            if (clearSelections)
                m_Selections.Clear();
        }

        void EnsurePageArrays(int pageCount)
        {
            if (m_PageCounts.Length >= pageCount)
                return;
            m_PageCounts = new int[pageCount];
            m_PageStarts = new int[pageCount];
            m_PageMin = new float3[pageCount];
            m_PageMax = new float3[pageCount];
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;
            m_Disposed = true;
            Release(clearSelections: true);
            m_Sort.Dispose();
            m_Keys.Dispose();
            m_SortIds.Dispose();
            m_Offsets.Dispose();
            m_Instances.Dispose();
            m_InstanceIds.Dispose();
        }
    }
}
