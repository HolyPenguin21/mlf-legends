using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;

namespace MLFLegends.Combat
{
    /// <summary>GPU instance record (32 bytes); layout mirrored by <c>SpriteInstance</c> in CombatSprite.shader.</summary>
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
        public float DamageFactor;
        public float GuardFeedback; // signed flash: block/timely > 0, break < 0; stride remains 32 bytes
    }

    /// <summary>
    /// Read-only extraction of the sprite instances of one combat World: every <see cref="InCombat"/>-enabled combatant of
    /// the active battle, with its <see cref="CombatPosition"/>, <see cref="CombatFacing"/>, selected frame and depth
    /// tie-break, grouped by atlas page. Owned by <see cref="CombatSpriteRenderer"/>; usable without a graphics device.
    /// </summary>
    /// <remarks>
    /// Four read-only queries per World, created on the first run against it and released on World change/<see cref="Dispose"/> (a
    /// disposed World released them already). Never writes components, adds entities or makes structural changes. Buffers
    /// are reused; after warm-up a run allocates no managed memory (native Temp only). Frame selections are keyed by
    /// <see cref="CombatantId"/> and dropped on a new battle number or World, so no ID history is kept.
    /// </remarks>
    public sealed class CombatSpriteExtraction : IDisposable
    {
        // 1/16 world unit along the view ray: below the fixed 0.21-world-unit wall regression clearance.
        // At 400 coincident cards the gap is 0.0625/399 = 0.0001566, above both D24's and float depth's
        // worst step over the test camera's 1000-unit range (0.0001193). Never grows with roster size.
        public const float DepthOffsetBudget = 0.0625f;

        public static readonly int InstanceStride = Marshal.SizeOf<SpriteInstanceGpu>();

        static readonly ProfilerMarker s_Marker = new ProfilerMarker("CombatSpriteExtraction.Run");

        World m_World;
        EntityQuery m_Sessions;
        EntityQuery m_Clocks;
        EntityQuery m_Combatants;
        EntityQuery m_Animated;
        EntityQuery m_EffectResources;
        uint m_BoundBattle;
        readonly Dictionary<uint, uint> m_Selections = new Dictionary<uint, uint>();

        NativeList<SortItem> m_Sort;
        NativeList<float> m_Keys;
        NativeList<uint> m_SortIds;
        NativeList<float> m_Offsets;
        NativeList<float4> m_Footprints;
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
            m_Footprints = new NativeList<float4>(initialCapacity, Allocator.Persistent);
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
        /// drawn without a selection; selections outside the atlas fall back to it. With <paramref name="animations"/> every
        /// fighter's frame comes from its authoritative CombatMotion/CombatAttackState and the clock
        /// (<see cref="CombatAnimationSet.FrameFor"/>); a fighter lacking them (manual worlds) shows idle from battle start.
        /// </summary>
        public void Run(World world, SpriteAtlas atlas, in SpriteCardBasis basis, float depthSeparation, uint defaultFrame, float pixelWorldSize = 2f / 96f, CombatAnimationSet animations = null,
            float2 imageOffsetCard = default, uint damageDurationTicks = 9)
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

            uint tick = m_Clocks.CalculateEntityCount() == 1 ? m_Clocks.GetSingleton<SimulationClock>().Tick : 0;
            if (animations != null)
            {
                if (m_Clocks.CalculateEntityCount() != 1)
                    return; // Invalid/incomplete World: no animation, never a wall-clock fallback.
                tick = m_Clocks.GetSingleton<SimulationClock>().Tick;
                if (tick < session.BattleStartTick)
                    return; // Guard unsigned underflow on invalid session data.
                defaultFrame = animations.IdleFrame(tick, session.BattleStartTick);
            }

            using var entities = m_Combatants.ToEntityArray(Allocator.Temp);
            using var ids = m_Combatants.ToComponentDataArray<CombatantId>(Allocator.Temp);
            using var positions = m_Combatants.ToComponentDataArray<CombatPosition>(Allocator.Temp);
            using var facings = m_Combatants.ToComponentDataArray<CombatFacing>(Allocator.Temp);
            var resources = m_EffectResources.CalculateEntityCount() == 1
                ? world.EntityManager.GetComponentObject<CombatEffectResources>(m_EffectResources.GetSingletonEntity()) : null;
            var count = ids.Length;
            if (count == 0)
                return;

            // State of animated fighters, joined by CombatantId: the core query (and thus manual/partial Worlds) is unchanged.
            using var animatedIds = animations != null ? m_Animated.ToComponentDataArray<CombatantId>(Allocator.Temp) : default;
            using var motions = animations != null ? m_Animated.ToComponentDataArray<CombatMotion>(Allocator.Temp) : default;
            using var attacks = animations != null ? m_Animated.ToComponentDataArray<CombatAttackState>(Allocator.Temp) : default;
            using var animatedIndex = new NativeHashMap<uint, int>(animations != null ? animatedIds.Length : 0, Allocator.Temp);
            if (animations != null)
                for (var i = 0; i < animatedIds.Length; i++)
                    animatedIndex.TryAdd(animatedIds[i].Value, i);

            m_Keys.ResizeUninitialized(count);
            m_SortIds.ResizeUninitialized(count);
            m_Offsets.ResizeUninitialized(count);
            for (var i = 0; i < count; i++)
            {
                m_Keys[i] = basis.DepthKey(positions[i].Value);
                m_SortIds[i] = ids[i].Value;
            }

            var frameCount = (uint)atlas.FrameCount;
            var fallback = defaultFrame < frameCount ? defaultFrame : 0u;
            var framePages = atlas.FramePages;
            m_Instances.ResizeUninitialized(count);
            m_InstanceIds.ResizeUninitialized(count);
            // Not `using var`: element writes on a using-declared struct do not compile (CS1654).
            var frames = new NativeArray<uint>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            m_Footprints.ResizeUninitialized(count);
            var screenUp = math.normalize(math.cross(basis.ViewDirection, basis.Right));
            for (var i = 0; i < count; i++)
            {
                var frame = animations == null && m_Selections.TryGetValue(ids[i].Value, out var selected) && selected < frameCount ? selected : fallback;
                if (animations != null && animatedIndex.TryGetValue(ids[i].Value, out var state))
                {
                    var actor = entities[i]; var em = world.EntityManager;
                    var guard = em.HasComponent<CombatGuardState>(actor) ? em.GetComponentData<CombatGuardState>(actor) : default;
                    var stability = em.HasComponent<CombatStability>(actor) ? em.GetComponentData<CombatStability>(actor) : default;
                    SpriteAnimationTiming block = null;
                    if (guard.Settings.BlockTiming != 0) block = resources?.BlockTiming(guard.Settings.BlockTiming);
                    var frozenAttack = resources?.Get(attacks[state].Strike.SwingEffect)?.BodyTiming
                        ?? resources?.Get(attacks[state].Strike.ImpactEffect)?.BodyTiming;
                    var animated = animations.FrameFor(tick, session.BattleStartTick, motions[state], attacks[state], guard, stability, block, frozenAttack);
                    frame = animated < frameCount ? animated : fallback;
                }
                frames[i] = frame;
                m_PageCounts[framePages[(int)frame]]++;
                var rect = atlas.Frames[(int)frame].CardRect + new float4(imageOffsetCard, imageOffsetCard);
                var facing = facings[i].Value == FacingDirection.Left ? -1f : 1f;
                var a = SpriteCardMath.CardToWorld(basis, positions[i].Value, new float2(rect.x * facing, rect.y), pixelWorldSize);
                var b = SpriteCardMath.CardToWorld(basis, positions[i].Value, new float2(rect.z * facing, rect.w), pixelWorldSize);
                var pa = new float2(math.dot(a, basis.Right), math.dot(a, screenUp));
                var pb = new float2(math.dot(b, basis.Right), math.dot(b, screenUp));
                m_Footprints[i] = new float4(math.min(pa, pb), math.max(pa, pb));
            }

            MaxDepthOffset = ComputeDepthOffsets(m_Keys.AsArray(), m_SortIds.AsArray(), depthSeparation,
                m_Offsets.AsArray(), m_Sort, m_Footprints.AsArray());

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
                    GuardFeedback = world.EntityManager.HasComponent<CombatStability>(entities[index])
                        ? GuardFactor(tick, world.EntityManager.GetComponentData<CombatStability>(entities[index]), damageDurationTicks) : 0,
                    DamageFactor = world.EntityManager.HasComponent<CombatDamageFeedback>(entities[index])
                        ? DamageFactor(tick, world.EntityManager.GetComponentData<CombatDamageFeedback>(entities[index]).LastDamageTick, damageDurationTicks) : 0,
                };
                m_InstanceIds[slot] = ids[index].Value;
                m_PageMin[page] = math.min(m_PageMin[page], position);
                m_PageMax[page] = math.max(m_PageMax[page], position);
            }

            cursor.Dispose();
            frames.Dispose();
        }

        public static float GuardFactor(uint tick, in CombatStability resource, uint duration)
        {
            var amount = DamageFactor(tick, resource.LastOutcomeTick, duration);
            return resource.LastOutcome == CombatGuardOutcome.Break ? -amount
                : resource.LastOutcome == CombatGuardOutcome.Timely ? amount * 2
                : resource.LastOutcome == CombatGuardOutcome.Block ? amount : 0;
        }

        public static float DamageFactor(uint tick, uint last, uint duration)
        {
            if (last == 0 || tick < last || duration == 0 || tick - last >= duration) return 0;
            var value = 1 - (tick - last) / (float)duration;
            return value * value * (3 - 2 * value);
        }

        /// <summary>
        /// Stable (depth, ID) ordering within connected overlapping card footprints. No footprint means all cards overlap.
        /// Separation is a requested gap, compressed per component so total push never exceeds DepthOffsetBudget.
        /// Disconnected screen-overlap components cannot influence one another; edges require a depth gap within the budget.
        /// </summary>
        public static float ComputeDepthOffsets(NativeArray<float> keys, NativeArray<uint> ids, float separation,
            NativeArray<float> offsets, NativeArray<float4> footprints = default)
        {
            var sort = new NativeList<SortItem>(keys.Length, Allocator.Temp);
            try { return ComputeDepthOffsets(keys, ids, separation, offsets, sort, footprints); }
            finally { sort.Dispose(); }
        }

        static int Root(NativeArray<int> parents, int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }
            return index;
        }

        static float ComputeDepthOffsets(NativeArray<float> keys, NativeArray<uint> ids, float separation,
            NativeArray<float> offsets, NativeList<SortItem> sort, NativeArray<float4> footprints)
        {
            sort.Clear();
            for (var i = 0; i < keys.Length; i++)
                sort.Add(new SortItem { Key = keys[i], Id = ids[i], Index = i });
            sort.Sort(new BackToFront());
            // Native scratch only; this is the existing depth policy, not simulation/spatial state.
            var parents = new NativeArray<int>(keys.Length, Allocator.Temp);
            var sizes = new NativeArray<int>(keys.Length, Allocator.Temp);
            var previous = new NativeArray<double>(keys.Length, Allocator.Temp);
            try
            {
                for (var i = 0; i < keys.Length; i++)
                {
                    parents[i] = i;
                    previous[i] = double.PositiveInfinity;
                }
                for (var i = 0; i < sort.Length; i++)
                for (var j = i + 1; j < sort.Length; j++)
                {
                    if ((double)sort[i].Key - sort[j].Key > DepthOffsetBudget)
                        break; // Such cards cannot overtake one another with a bounded push.
                    var a = sort[i].Index;
                    var b = sort[j].Index;
                    if (footprints.IsCreated)
                    {
                        var ra = footprints[a];
                        var rb = footprints[b];
                        if (ra.x >= rb.z || rb.x >= ra.z || ra.y >= rb.w || rb.y >= ra.w)
                            continue;
                    }
                    var ar = Root(parents, a);
                    var br = Root(parents, b);
                    if (ar != br)
                        parents[br] = ar;
                }
                for (var i = 0; i < keys.Length; i++)
                    sizes[Root(parents, i)]++;
                var max = 0f;
                for (var i = 0; i < sort.Length; i++)
                {
                    var item = sort[i];
                    var group = Root(parents, item.Index);
                    var gap = math.min(math.max(0f, separation), DepthOffsetBudget / math.max(1, sizes[group] - 1));
                    var rendered = System.Math.Min(item.Key, previous[group] - gap);
                    previous[group] = rendered;
                    // Double recurrence avoids a rounding error per link; only the final bounded offset is a float.
                    var offset = math.clamp((float)(item.Key - rendered), 0f, DepthOffsetBudget);
                    offsets[item.Index] = offset;
                    max = math.max(max, offset);
                }
                return max;
            }
            finally
            {
                previous.Dispose();
                sizes.Dispose();
                parents.Dispose();
            }
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
            m_Clocks = manager.CreateEntityQuery(ComponentType.ReadOnly<SimulationClock>());
            m_Combatants = manager.CreateEntityQuery(ComponentType.ReadOnly<CombatantId>(), ComponentType.ReadOnly<CombatPosition>(),
                ComponentType.ReadOnly<CombatFacing>(), ComponentType.ReadOnly<InCombat>());
            m_EffectResources = manager.CreateEntityQuery(ComponentType.ReadOnly<CombatEffectResources>());
            m_Animated = manager.CreateEntityQuery(ComponentType.ReadOnly<CombatantId>(), ComponentType.ReadOnly<CombatMotion>(),
                ComponentType.ReadOnly<CombatAttackState>(), ComponentType.ReadOnly<InCombat>());
        }

        void Release(bool clearSelections)
        {
            if (m_World != null && m_World.IsCreated)
            {
                m_Sessions.Dispose();
                m_Clocks.Dispose();
                m_Combatants.Dispose();
                m_Animated.Dispose();
                m_EffectResources.Dispose();
            }

            m_World = null;
            m_Sessions = default;
            m_Clocks = default;
            m_Combatants = default;
            m_Animated = default;
            m_EffectResources = default;
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
            m_Footprints.Dispose();
            m_Instances.Dispose();
            m_InstanceIds.Dispose();
        }
    }
}
