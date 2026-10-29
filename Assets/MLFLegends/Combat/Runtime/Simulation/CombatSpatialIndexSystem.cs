using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Jobs;

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
        // Both insertion and query use floor and closed [min,max] cells. No casts toward zero, epsilon or clamping.
        internal static bool TryCells(in CombatSpatialBounds bounds, out int2 min, out int2 max, out long count)
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
    /// Membership validation includes enable bits (not structural changes). Use TryQueryAabbBatch for per-fighter
    /// queries: one atomic main-thread call validates membership once, then visits the queried cells for all bounds.
    /// No geometry/bounds full scan except the explicit enormous-query fallback.
    /// </remarks>
    [UpdateInGroup(typeof(CombatStateUpdateSystemGroup))]
    [UpdateAfter(typeof(CombatMovementSystem))]
    [UpdateBefore(typeof(CombatHitSystem))]
    public partial class CombatSpatialIndexSystem : SystemBase
    {
        NativeList<CombatSpatialBody> m_Bodies;
        NativeParallelMultiHashMap<int2, int> m_Cells;
        NativeList<uint> m_Seen;
        NativeList<CombatContactActor> m_Movement;
        NativeList<int2> m_ContactPairs;
        NativeArray<CombatContactDiagnostics> m_ContactDiagnostics;
        NativeList<CombatArenaShape> m_EmptyShapes;
        NativeList<CombatImpulse> m_Impulses;
        public CombatContactDiagnostics LastContacts { get; private set; }
        public double LastMovementKernelMicroseconds { get; private set; }
        public long MovementScratchCapacityBytes => !m_Allocated ? 0 : (long)m_Movement.Capacity * UnsafeUtility.SizeOf<CombatContactActor>()
            + (long)m_ContactPairs.Capacity * UnsafeUtility.SizeOf<int2>() + UnsafeUtility.SizeOf<CombatContactDiagnostics>()
            + (long)m_EmptyShapes.Capacity * UnsafeUtility.SizeOf<CombatArenaShape>()
            + (long)m_Impulses.Capacity * UnsafeUtility.SizeOf<CombatImpulse>();
        internal NativeArray<CombatArenaShape> EmptyShapes => m_EmptyShapes.AsArray();

        struct ByMovementId : IComparer<CombatContactActor>
        {
            public int Compare(CombatContactActor a, CombatContactActor b) => a.Id.CompareTo(b.Id);
        }

        // Internal synchronous scratch lease: never a public snapshot, never read as yesterday's final state.
        internal NativeList<CombatContactActor> BeginMovement()
        {
            m_Published = false;
            m_Movement.Clear(); m_ContactPairs.Clear();
            return m_Movement;
        }

        internal void EndMovement()
        {
            // Do not retain Entity/pair rows across pause, Despawn, End, Restart or World replacement.
            m_Movement.Clear(); m_ContactPairs.Clear(); m_Cells.Clear(); m_Impulses.Clear();
        }

        // impulses: Movement's pending input of this tick, copied into owner scratch for the job (consumed by the caller).
        internal void CalculateMovement(NativeArray<CombatArenaShape> shapes, NativeArray<CombatImpulse> impulses, uint tick, uint landingTicks)
        {
            m_Movement.Sort(new ByMovementId());
            var session = m_Session.CalculateEntityCount() == 1 ? m_Session.GetSingleton<BattleSession>() : default;
            var active = session.IsActive;
            var expected = m_ExpectedFighters.CalculateEntityCount();
            if (active && (expected != m_Fighters.CalculateEntityCount() || expected != m_Movement.Length))
                throw new InvalidOperationException("Incomplete contact inputs: every enabled fighter needs Id/Team/Body/Position/Motion/AppliedCommand.");
            if (m_Movement.Length > CombatContactTuning.MaxBodies)
                throw new InvalidOperationException("Contact roster exceeds the battle limit; no bodies were dropped.");
            m_Impulses.Clear(); m_Impulses.AddRange(impulses);
            var physics = impulses.Length > 0;
            var fastest = CombatImpulseTuning.MaxExternalSpeed;
            for (var i = 0; i < m_Movement.Length; i++)
            {
                var speed = math.length(m_Movement[i].Velocity);
                physics |= speed > 0;
                fastest = math.max(fastest, speed);
            }
            long entries = 0;
            for (var i = 0; i < m_Movement.Length; i++)
            {
                var a = m_Movement[i];
                if (i > 0 && a.Id == m_Movement[i - 1].Id)
                    throw new InvalidOperationException("Duplicate movement CombatantId.");
                if (active && !a.Collidable)
                    throw new InvalidOperationException("An enabled movement row is missing its contact identity/body/team.");
                if (!a.Collidable) continue; // Explicit isolated fixtures without a BattleSession retain their legacy path.
                if (a.Id == 0)
                    throw new InvalidOperationException("Invalid movement CombatantId; no partial output was written.");
                if (!CombatSpatialBounds.TryFromBody(a.Start, a.Body, out var bounds))
                    throw new InvalidOperationException("Invalid contact body; no fighter was dropped.");
                // Covers initial world depenetration, the whole proposed path and the total body correction budget,
                // including any accepted impulse of this tick and transfer through a group (CombatContactJob.CorrectionBudget).
                var margin = math.length(a.Desired) * 2 + 2 * CombatContactTuning.CorrectionBudget
                    + (physics ? 3 * fastest * SimulationTiming.TickDuration : 0);
                if (!math.isfinite(margin))
                    throw new InvalidOperationException("Non-finite movement/external velocity; no partial output was written.");
                bounds.Min -= new float3(margin, 0, margin); bounds.Max += new float3(margin, 0, margin);
                if (!CombatSpatialBounds.TryCells(bounds, out _, out _, out var count)
                    || count > CombatSpatialTuning.MaxBuildEntries - entries)
                    throw new InvalidOperationException("Swept contact grid capacity/work range exceeded.");
                entries += count;
            }
            if (entries > m_Cells.Capacity) m_Cells.Capacity = Grow(m_Cells.Capacity, (int)entries, CombatSpatialTuning.MaxBuildEntries);
            var n = m_Movement.Length;
            var maxPairs = n * (n - 1) / 2;
            if (maxPairs > m_ContactPairs.Capacity) m_ContactPairs.Capacity = math.max(maxPairs, m_ContactPairs.Capacity * 2);
            m_Seen.Resize(n, NativeArrayOptions.ClearMemory);
            var start = Stopwatch.GetTimestamp();
            new CombatContactJob { Actors = m_Movement, Cells = m_Cells, Seen = m_Seen, Pairs = m_ContactPairs,
                Shapes = shapes, Impulses = m_Impulses.AsArray(), Diagnostics = m_ContactDiagnostics, Tick = tick, LandingTicks = landingTicks,
                BattleNumber = session.BattleNumber, Physics = physics }.Schedule().Complete();
            LastMovementKernelMicroseconds = MicrosSince(start);
            LastContacts = m_ContactDiagnostics[0];
        }
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
        public long LastQueryEntriesVisited { get; private set; }
        public long LastQueryDuplicatesEliminated { get; private set; }
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
            // Count every enabled fighter, including partial fixtures missing any required spatial data.
            m_ExpectedFighters = GetEntityQuery(ComponentType.ReadOnly<CombatantId>(), ComponentType.ReadOnly<InCombat>());
            try
            {
                m_Bodies = new NativeList<CombatSpatialBody>(1, Allocator.Persistent);
                m_Cells = new NativeParallelMultiHashMap<int2, int>(1, Allocator.Persistent);
                m_Seen = new NativeList<uint>(1, Allocator.Persistent);
                m_Movement = new NativeList<CombatContactActor>(1, Allocator.Persistent);
                m_ContactPairs = new NativeList<int2>(1, Allocator.Persistent);
                m_ContactDiagnostics = new NativeArray<CombatContactDiagnostics>(1, Allocator.Persistent);
                m_EmptyShapes = new NativeList<CombatArenaShape>(1, Allocator.Persistent);
                m_Impulses = new NativeList<CombatImpulse>(1, Allocator.Persistent);
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
            if (m_Impulses.IsCreated) m_Impulses.Dispose();
            if (m_EmptyShapes.IsCreated) m_EmptyShapes.Dispose();
            if (m_ContactDiagnostics.IsCreated) m_ContactDiagnostics.Dispose();
            if (m_ContactPairs.IsCreated) m_ContactPairs.Dispose();
            if (m_Movement.IsCreated) m_Movement.Dispose();
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
            var started = Stopwatch.GetTimestamp();
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
                if (!CombatSpatialBounds.TryCells(m_Bodies[i].Bounds, out _, out _, out var count)
                    || count > CombatSpatialTuning.MaxBuildEntries - entries)
                    throw new InvalidOperationException("Spatial body footprint exceeds the representable grid/memory work range.");
                entries += count;
            }
            if (entries > m_Cells.Capacity)
                m_Cells.Capacity = Grow(m_Cells.Capacity, (int)entries, CombatSpatialTuning.MaxBuildEntries);
            var occupied = 0;
            for (var i = 0; i < m_Bodies.Length; i++)
            {
                CombatSpatialBounds.TryCells(m_Bodies[i].Bounds, out var min, out var max, out _);
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
                || EntityManager.EntityOrderVersion != m_EntityOrderVersion || m_ExpectedFighters.CalculateEntityCount() != m_Bodies.Length
                || m_Fighters.CalculateEntityCount() != m_Bodies.Length)
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
            ResetQueryDiagnostics();
            if (!results.IsCreated)
                throw new ArgumentException("Spatial query requires an allocated caller-owned results buffer.", nameof(results));
            results.Clear();
            ValidateQuery(bounds);
            if (!IsCurrent())
                return false;
            AppendQuery(bounds, results);
            LastQueryCandidates = results.Length;
            LastQueryMicroseconds = MicrosSince(started);
            return true;
        }

        /// <summary>
        /// Atomic batch of the same AABB query. Validates live membership once for the entire main-thread call, with no
        /// callbacks or escaping view. Each caller-owned range is (offset, count) into the caller-owned flat results.
        /// Every range is independently unique and Id-sorted. Diagnostic counters aggregate all queries in the batch.
        /// Outputs are cleared on stale state or invalid input. No fixed limit on queries/candidates, no hidden buffers.
        /// </summary>
        public bool TryQueryAabbBatch(NativeArray<CombatSpatialBounds> queries, NativeList<CombatSpatialBody> results,
            NativeList<int2> ranges)
        {
            var started = Stopwatch.GetTimestamp();
            ResetQueryDiagnostics();
            if (!results.IsCreated || !ranges.IsCreated)
                throw new ArgumentException("Spatial batch requires allocated caller-owned results and ranges buffers.");
            results.Clear();
            ranges.Clear();
            if (!queries.IsCreated)
                throw new ArgumentException("Spatial batch requires an allocated bounds array.", nameof(queries));
            for (var i = 0; i < queries.Length; i++)
                ValidateQuery(queries[i]); // All inputs before any partial output.
            if (!IsCurrent())
                return false;
            for (var i = 0; i < queries.Length; i++)
            {
                var offset = results.Length;
                AppendQuery(queries[i], results);
                ranges.Add(new int2(offset, results.Length - offset));
            }
            LastQueryCandidates = results.Length;
            LastQueryMicroseconds = MicrosSince(started);
            return true;
        }

        void ResetQueryDiagnostics()
        {
            LastQueryCandidates = 0;
            LastQueryEntriesVisited = LastQueryDuplicatesEliminated = 0;
            LastQueryUsedFullScan = false;
            LastQueryMicroseconds = 0;
        }

        static void ValidateQuery(in CombatSpatialBounds bounds)
        {
            if (!bounds.IsValid)
                throw new ArgumentException("Spatial query requires finite ordered AABB bounds.", nameof(bounds));
        }

        // Called only after one complete live-membership check. A batch never yields or calls consumer code between
        // queries, so a read lease/version owner and repeated O(N) membership scans are unnecessary.
        void AppendQuery(in CombatSpatialBounds bounds, NativeList<CombatSpatialBody> results)
        {
            var offset = results.Length;
            if (!CombatSpatialBounds.TryCells(bounds, out var min, out var max, out var cells) || cells > CombatSpatialTuning.MaxQueryCells)
            {
                LastQueryUsedFullScan = true; // Scan every row, no capacity-limited candidate set.
                for (var i = 0; i < m_Bodies.Length; i++)
                {
                    LastQueryEntriesVisited++;
                    if (bounds.Overlaps(m_Bodies[i].Bounds))
                        results.Add(m_Bodies[i]);
                }
                return; // Snapshot already sorted by Id; this appended range is sorted as well.
            }
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
            results.AsArray().GetSubArray(offset, results.Length - offset).Sort(new ById());
        }
    }
}
