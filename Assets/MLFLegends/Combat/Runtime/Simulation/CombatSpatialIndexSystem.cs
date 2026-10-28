using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace MLFLegends.Combat
{
    /// <summary>World-space closed AABB. Touching faces, edges and points count as overlap.</summary>
    public struct CombatSpatialBounds
    {
        public float3 Min;
        public float3 Max;

        public bool IsValid => math.all(math.isfinite(Min)) && math.all(math.isfinite(Max)) && math.all(Min <= Max);
        public bool Overlaps(in CombatSpatialBounds other) => math.all(Min <= other.Max) && math.all(Max >= other.Min);

        public static bool TryFromBody(float3 position, in CombatBody body, out CombatSpatialBounds bounds)
        {
            bounds = default;
            if (!math.all(math.isfinite(position)) || !math.isfinite(body.Radius) || !math.isfinite(body.Height)
                || !math.isfinite(body.Mass) || body.Radius <= 0f || body.Height <= 0f || body.Mass <= 0f)
                return false;
            bounds = new CombatSpatialBounds
            {
                Min = position - new float3(body.Radius, 0f, body.Radius),
                Max = position + new float3(body.Radius, body.Height, body.Radius),
            };
            return bounds.IsValid; // Also rejects overflow in computed bounds, not only invalid inputs.
        }
    }

    /// <summary>A read-only-by-copy row of the final-position snapshot; never authoritative combat state.</summary>
    public struct CombatSpatialBody
    {
        public Entity Entity;
        public CombatantId Id;
        public CombatTeam Team;
        public float3 Position;
        public CombatBody Body;
        public CombatSpatialBounds Bounds;
    }

    public struct CombatSpatialSnapshotInfo
    {
        public uint Tick;
        public uint BattleNumber;
        public int BodyCount;
        public int OccupiedCells;
        public int Entries;
        public int BodyCapacity;
        public int EntryCapacity;
        public long NativePayloadCapacityBytes;
    }

    /// <summary>One World-owned native grid. Copies final positions after Movement; writes no gameplay components.</summary>
    /// <remarks>
    /// Main-thread API. TryQueryAabb clears/fills a reusable caller-owned buffer with copies in ascending Id order.
    /// Copies are only current until rebuild, membership/epoch change or disposal. Retaining Entity across that boundary
    /// is not supported. No borrowed NativeArray escapes, so a retained result cannot access disposed owner memory.
    /// Every public read validates clock/session and live membership, even when Initialization ran while paused.
    /// Membership validation is O(N), deliberately including enable bits (not structural changes). Grid traversal then
    /// visits only queried cells. No geometry/bounds full scan except the explicit enormous-query fallback.
    /// </remarks>
    [UpdateInGroup(typeof(CombatStateUpdateSystemGroup))]
    [UpdateAfter(typeof(CombatMovementSystem))]
    [UpdateBefore(typeof(CombatHitSystem))]
    public partial class CombatSpatialIndexSystem : SystemBase
    {
        NativeList<CombatSpatialBody> m_Bodies;
        NativeParallelMultiHashMap<int2, int> m_Cells;
        NativeList<uint> m_Seen;
        EntityQuery m_Session;
        EntityQuery m_Clock;
        EntityQuery m_Fighters;
        EntityQuery m_ExpectedFighters;
        CombatSpatialSnapshotInfo m_Info;
        uint m_QueryStamp;
        int m_EntityOrderVersion;
        bool m_Allocated;
        bool m_Published;
        uint m_LastBuildTick;
        uint m_LastBuildBattle;

        public long BuildCount { get; private set; }
        public double LastBuildMicroseconds { get; private set; }
        public double LastQueryMicroseconds { get; private set; }
        public int LastQueryCandidates { get; private set; }
        public int LastQueryEntriesVisited { get; private set; }
        public int LastQueryDuplicatesEliminated { get; private set; }
        public bool LastQueryUsedFullScan { get; private set; }

        struct ById : IComparer<CombatSpatialBody>
        {
            public int Compare(CombatSpatialBody a, CombatSpatialBody b) => a.Id.Value.CompareTo(b.Id.Value);
        }

        protected override void OnCreate()
        {
            var existing = World.GetExistingSystemManaged<CombatSpatialIndexSystem>();
            if (existing != null && !ReferenceEquals(existing, this))
                throw new InvalidOperationException("Only one CombatSpatialIndexSystem may own a World grid.");
            m_Session = GetEntityQuery(ComponentType.ReadOnly<BattleSession>());
            m_Clock = GetEntityQuery(ComponentType.ReadOnly<SimulationClock>());
            m_Fighters = GetEntityQuery(ComponentType.ReadOnly<CombatantId>(), ComponentType.ReadOnly<CombatTeam>(),
                ComponentType.ReadOnly<CombatPosition>(), ComponentType.ReadOnly<CombatBody>(), ComponentType.ReadOnly<InCombat>());
            m_ExpectedFighters = GetEntityQuery(ComponentType.ReadOnly<CombatantId>(), ComponentType.ReadOnly<CombatTeam>(),
                ComponentType.ReadOnly<CombatPosition>(), ComponentType.ReadOnly<InCombat>());
            try
            {
                m_Bodies = new NativeList<CombatSpatialBody>(1, Allocator.Persistent);
                m_Cells = new NativeParallelMultiHashMap<int2, int>(1, Allocator.Persistent);
                m_Seen = new NativeList<uint>(1, Allocator.Persistent);
                m_Allocated = true;
            }
            catch
            {
                DisposeStorage();
                throw;
            }
        }

        protected override void OnDestroy()
        {
            m_Published = false;
            m_Allocated = false;
            DisposeStorage();
        }

        void DisposeStorage()
        {
            if (m_Seen.IsCreated) m_Seen.Dispose();
            if (m_Cells.IsCreated) m_Cells.Dispose();
            if (m_Bodies.IsCreated) m_Bodies.Dispose();
        }

        bool TryEpoch(out uint tick, out uint battle)
        {
            tick = battle = 0;
            if (!m_Allocated || m_Session.CalculateEntityCount() != 1 || m_Clock.CalculateEntityCount() != 1)
                return false;
            var session = m_Session.GetSingleton<BattleSession>();
            tick = m_Clock.GetSingleton<SimulationClock>().Tick;
            battle = session.BattleNumber;
            return session.IsActive && tick != SimulationClock.NoTick && tick > session.BattleStartTick;
        }

        protected override void OnUpdate()
        {
            if (!TryEpoch(out var tick, out var battle) || m_ExpectedFighters.CalculateEntityCount() != m_Fighters.CalculateEntityCount())
            {
                m_Published = false;
                return;
            }
            // No second rebuild if a caller manually updates this system twice in one actual fixed tick.
            if (tick == m_LastBuildTick && battle == m_LastBuildBattle)
                return;
            m_LastBuildTick = tick;
            m_LastBuildBattle = battle;
            m_Published = false; // A validation/capacity exception must never expose a partially built snapshot.
            var started = Stopwatch.GetTimestamp();
            m_Bodies.Clear();
            m_Cells.Clear();
            foreach (var (id, team, position, body, entity) in
                SystemAPI.Query<RefRO<CombatantId>, RefRO<CombatTeam>, RefRO<CombatPosition>, RefRO<CombatBody>>()
                    .WithAll<InCombat>().WithEntityAccess())
            {
                if (!id.ValueRO.IsValid || !CombatSpatialBounds.TryFromBody(position.ValueRO.Value, body.ValueRO, out var bounds))
                    throw new InvalidOperationException($"Invalid spatial body/bounds for combatant {id.ValueRO.Value}.");
                m_Bodies.Add(new CombatSpatialBody
                {
                    Entity = entity, Id = id.ValueRO, Team = team.ValueRO, Position = position.ValueRO.Value,
                    Body = body.ValueRO, Bounds = bounds,
                });
            }
            m_Bodies.Sort(new ById());
            long entries = 0;
            for (var i = 0; i < m_Bodies.Length; i++)
            {
                if (i > 0 && m_Bodies[i - 1].Id.Equals(m_Bodies[i].Id))
                    throw new InvalidOperationException("Duplicate CombatantId in spatial snapshot.");
                if (!TryCells(m_Bodies[i].Bounds, out _, out _, out var count)
                    || count > CombatSpatialTuning.MaxBuildEntries - entries)
                    throw new InvalidOperationException("Spatial body footprint exceeds the representable grid/memory work range.");
                entries += count;
            }
            if (entries > m_Cells.Capacity)
                m_Cells.Capacity = Grow(m_Cells.Capacity, (int)entries, CombatSpatialTuning.MaxBuildEntries);
            var occupied = 0;
            for (var i = 0; i < m_Bodies.Length; i++)
            {
                TryCells(m_Bodies[i].Bounds, out var min, out var max, out _);
                // long induction variables avoid wrapping at int.MaxValue (including a one-cell range at that edge).
                for (long x = min.x; x <= max.x; x++)
                for (long z = min.y; z <= max.y; z++)
                {
                    var cell = new int2((int)x, (int)z);
                    if (!m_Cells.ContainsKey(cell))
                        occupied++;
                    m_Cells.Add(cell, i);
                }
            }
            m_Seen.Resize(m_Bodies.Length, NativeArrayOptions.ClearMemory);
            // Resize can retain old stamps when the length did not change.
            for (var i = 0; i < m_Seen.Length; i++)
                m_Seen[i] = 0;
            m_QueryStamp = 0;
            m_Info = new CombatSpatialSnapshotInfo
            {
                Tick = tick, BattleNumber = battle, BodyCount = m_Bodies.Length, OccupiedCells = occupied, Entries = (int)entries,
                BodyCapacity = m_Bodies.Capacity, EntryCapacity = m_Cells.Capacity,
                // Payload lower bound, explicitly excludes hash buckets, allocator metadata and ECS storage.
                NativePayloadCapacityBytes = (long)m_Bodies.Capacity * UnsafeUtility.SizeOf<CombatSpatialBody>()
                    + (long)m_Seen.Capacity * sizeof(uint) + (long)m_Cells.Capacity * (UnsafeUtility.SizeOf<int2>() + sizeof(int)),
            };
            m_EntityOrderVersion = EntityManager.EntityOrderVersion;
            m_Published = true;
            BuildCount++;
            LastBuildMicroseconds = MicrosSince(started);
        }

        static int Grow(int capacity, int required, int limit) =>
            (int)Math.Min(limit, Math.Max((long)required, (long)capacity * 2));

        static double MicrosSince(long start) => (Stopwatch.GetTimestamp() - start) * 1e6 / Stopwatch.Frequency;

        bool IsCurrent()
        {
            if (!m_Published || !TryEpoch(out var tick, out var battle) || tick != m_Info.Tick || battle != m_Info.BattleNumber
                || EntityManager.EntityOrderVersion != m_EntityOrderVersion || m_Fighters.CalculateEntityCount() != m_Bodies.Length)
                return false;
            // Enable changes do not change EntityOrderVersion. Count alone misses one disable + one enable.
            for (var i = 0; i < m_Bodies.Length; i++)
                if (!EntityManager.IsComponentEnabled<InCombat>(m_Bodies[i].Entity))
                    return false;
            return true;
        }

        public bool TryGetSnapshotInfo(out CombatSpatialSnapshotInfo info)
        {
            info = default;
            if (!IsCurrent())
                return false;
            info = m_Info;
            return true;
        }

        /// <summary>
        /// Closed body-AABB overlap, a conservative test for the cylinder. Unique ascending Ids. Build pairs by querying
        /// each body bounds and retaining only candidate.Id > source.Id: each unordered pair then appears exactly once.
        /// False means no current snapshot; output is cleared. Invalid query bounds throw, never silently truncate.
        /// </summary>
        public bool TryQueryAabb(in CombatSpatialBounds bounds, NativeList<CombatSpatialBody> results)
        {
            var started = Stopwatch.GetTimestamp();
            results.Clear();
            LastQueryCandidates = LastQueryEntriesVisited = LastQueryDuplicatesEliminated = 0;
            LastQueryUsedFullScan = false;
            LastQueryMicroseconds = 0;
            if (!bounds.IsValid)
                throw new ArgumentException("Spatial query requires finite ordered AABB bounds.", nameof(bounds));
            if (!IsCurrent())
                return false;
            if (!TryCells(bounds, out var min, out var max, out var cells) || cells > CombatSpatialTuning.MaxQueryCells)
            {
                LastQueryUsedFullScan = true; // Explicit diagnostic; scan every row, no capacity-limited candidate set.
                for (var i = 0; i < m_Bodies.Length; i++)
                {
                    LastQueryEntriesVisited++;
                    if (bounds.Overlaps(m_Bodies[i].Bounds))
                        results.Add(m_Bodies[i]);
                }
            }
            else
            {
                m_QueryStamp = unchecked(m_QueryStamp + 1);
                if (m_QueryStamp == 0)
                {
                    for (var i = 0; i < m_Seen.Length; i++)
                        m_Seen[i] = 0;
                    m_QueryStamp = 1;
                }
                for (long x = min.x; x <= max.x; x++)
                for (long z = min.y; z <= max.y; z++)
                {
                    if (!m_Cells.TryGetFirstValue(new int2((int)x, (int)z), out var row, out var iterator))
                        continue;
                    do
                    {
                        LastQueryEntriesVisited++;
                        if (m_Seen[row] == m_QueryStamp)
                        {
                            LastQueryDuplicatesEliminated++;
                            continue;
                        }
                        m_Seen[row] = m_QueryStamp;
                        if (bounds.Overlaps(m_Bodies[row].Bounds))
                            results.Add(m_Bodies[row]);
                    } while (m_Cells.TryGetNextValue(out row, ref iterator));
                }
                results.Sort(new ById());
            }
            LastQueryCandidates = results.Length;
            LastQueryMicroseconds = MicrosSince(started);
            return true;
        }

        // Both insertion and query use floor and closed [min,max] cells. No casts toward zero, epsilon or clamping.
        static bool TryCells(in CombatSpatialBounds bounds, out int2 min, out int2 max, out long count)
        {
            min = max = default;
            count = 0;
            var x0 = Math.Floor((double)bounds.Min.x / CombatSpatialTuning.CellSize);
            var z0 = Math.Floor((double)bounds.Min.z / CombatSpatialTuning.CellSize);
            var x1 = Math.Floor((double)bounds.Max.x / CombatSpatialTuning.CellSize);
            var z1 = Math.Floor((double)bounds.Max.z / CombatSpatialTuning.CellSize);
            if (x0 < int.MinValue || z0 < int.MinValue || x1 > int.MaxValue || z1 > int.MaxValue)
                return false;
            min = new int2((int)x0, (int)z0);
            max = new int2((int)x1, (int)z1);
            var width = (long)max.x - min.x + 1;
            var depth = (long)max.y - min.y + 1;
            if (width > long.MaxValue / depth)
                return false;
            count = width * depth;
            return true;
        }
    }
}
