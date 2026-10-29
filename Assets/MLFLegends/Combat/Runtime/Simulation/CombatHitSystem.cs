using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace MLFLegends.Combat
{
    /// <summary>Counters of the last Hit call that had Active attackers. Diagnostics only, never gameplay state.</summary>
    public struct CombatHitDiagnostics
    {
        public uint Tick, BattleNumber;
        public int ActiveAttackers, Queries, Candidates, InsideTests, AcceptedHits;
        public long EntriesVisited;
        public bool UsedFullScan, KernelRanManaged;
    }

    /// <summary>The single fixed-tick hit resolution owner: the only writer of <see cref="CombatHealth"/> and of hit ledgers.</summary>
    /// <remarks>
    /// Runs last in State, after movement and the final spatial snapshot, so it tests this tick's final positions. Every
    /// in-combat attacker whose strike is in its Active phase on this tick (by the timing fixed in its CombatAttackState when
    /// the strike started) hits every in-combat combatant of another team that is not already in the strike's ledger and lies
    /// inside the strike's box: forward distance along the attacker's facing within [ReachMin, ReachMax], |ΔZ| ≤ DepthHalfWidth,
    /// |ΔY| ≤ HeightTolerance (closed bounds, target point = CombatPosition), using the damage/box frozen in the attacker's
    /// CombatAttackState.Strike when the strike started (#69: per-character values). Each such target loses Damage HP (clamped at 0)
    /// once and joins the ledger, so several Active ticks of one strike never hit it twice; the next strike starts with an
    /// empty ledger. Allies and the attacker itself are never valid targets. Attackers and targets are visited in CombatantId
    /// order, so the result never depends on chunk order. No defeat, stagger, knockback or guard yet.
    /// Stage 04.5: managed orchestration gathers Active attackers, builds one conservative point-target AABB per attacker and
    /// asks the World's only CombatSpatialIndexSystem for one atomic batch (membership validated once). A Burst job then runs
    /// the unchanged Inside/filter/ledger/HP rules on the Id-sorted candidates. Body radius only widens the broadphase, never
    /// the hit rule. A missing/stale snapshot with Active attackers is a production invariant failure: it throws before any
    /// HP/ledger write, never silently falls back to a full scan. Native scratch belongs to this system (World lifetime),
    /// is cleared after every call (no Entity survives a tick) and is disposed in OnDestroy.
    /// </remarks>
    [UpdateInGroup(typeof(CombatStateUpdateSystemGroup))]
    [UpdateAfter(typeof(CombatMovementSystem))]
    public partial struct CombatHitSystem : ISystem
    {
        internal struct Attacker
        {
            public uint Id;
            public Entity Entity;
            public byte Team;
            public float3 Position;
            public CombatAttackKind Kind;
            public CombatStrikeParameters Strike; // Frozen at strike start (CombatAttackState.Strike), never the live table.
            public float Facing;
        }

        struct ById : IComparer<Attacker>
        {
            public int Compare(Attacker a, Attacker b) => a.Id.CompareTo(b.Id);
        }

        NativeList<Attacker> m_Attackers;
        NativeList<CombatSpatialBounds> m_Queries;
        NativeList<CombatSpatialBody> m_Candidates;
        NativeList<int2> m_Ranges;
        NativeArray<CombatHitDiagnostics> m_Diagnostics;

        /// <summary>Last call with Active attackers (stays in its own Tick/BattleNumber epoch).</summary>
        public CombatHitDiagnostics LastHits;

        public void OnCreate(ref SystemState state)
        {
            m_Attackers = new NativeList<Attacker>(1, Allocator.Persistent);
            m_Queries = new NativeList<CombatSpatialBounds>(1, Allocator.Persistent);
            m_Candidates = new NativeList<CombatSpatialBody>(1, Allocator.Persistent);
            m_Ranges = new NativeList<int2>(1, Allocator.Persistent);
            m_Diagnostics = new NativeArray<CombatHitDiagnostics>(1, Allocator.Persistent);
            state.RequireForUpdate<SimulationClockRunning>();
            state.RequireForUpdate<SimulationClock>();
        }

        public void OnDestroy(ref SystemState state)
        {
            if (m_Diagnostics.IsCreated) m_Diagnostics.Dispose();
            if (m_Ranges.IsCreated) m_Ranges.Dispose();
            if (m_Candidates.IsCreated) m_Candidates.Dispose();
            if (m_Queries.IsCreated) m_Queries.Dispose();
            if (m_Attackers.IsCreated) m_Attackers.Dispose();
        }

        /// <summary>Exact closed strike box around a point target; Hit and its oracles share this rule.</summary>
        public static bool Inside(in CombatAttackDefinition strike, float3 attacker, float facing, float3 target) =>
            Inside(strike.Parameters, attacker, facing, target);

        /// <summary>Broadphase of the default values of a slot (benchmarks/oracles); Hit uses the frozen execution values.</summary>
        public static CombatSpatialBounds StrikeQuery(in CombatAttackDefinition strike, float3 attacker, float facing) =>
            StrikeQuery(strike.Parameters, attacker, facing);

        /// <summary>Exact closed strike box around a point target; Hit and its oracles share this rule.</summary>
        public static bool Inside(in CombatStrikeParameters strike, float3 attacker, float facing, float3 target)
        {
            var delta = target - attacker;
            var forward = delta.x * facing;
            return forward >= strike.ReachMin && forward <= strike.ReachMax
                && math.abs(delta.z) <= strike.DepthHalfWidth && math.abs(delta.y) <= strike.HeightTolerance;
        }

        /// <summary>
        /// Conservative broadphase of <see cref="Inside"/>: every target point inside the box lies in this AABB, hence in any
        /// body AABB containing that point. Padded by a few float ulps so subtraction rounding in Inside cannot escape it.
        /// </summary>
        public static CombatSpatialBounds StrikeQuery(in CombatStrikeParameters strike, float3 attacker, float facing)
        {
            var lo = facing > 0 ? attacker.x + strike.ReachMin : attacker.x - strike.ReachMax;
            var hi = facing > 0 ? attacker.x + strike.ReachMax : attacker.x - strike.ReachMin;
            var extent = new float3(math.max(math.abs(strike.ReachMin), math.abs(strike.ReachMax)), strike.HeightTolerance, strike.DepthHalfWidth);
            var pad = (math.abs(attacker) + extent) * 1e-6f + 1e-6f;
            return new CombatSpatialBounds
            {
                Min = new float3(lo - pad.x, attacker.y - strike.HeightTolerance - pad.y, attacker.z - strike.DepthHalfWidth - pad.z),
                Max = new float3(hi + pad.x, attacker.y + strike.HeightTolerance + pad.y, attacker.z + strike.DepthHalfWidth + pad.z),
            };
        }

        // Managed orchestration: the managed index API is never called from Burst.
        public void OnUpdate(ref SystemState state)
        {
            state.Dependency.Complete();
            var tick = SystemAPI.GetSingleton<SimulationClock>().Tick;
            try
            {
                foreach (var (attack, facing, position, team, id, entity) in
                    SystemAPI.Query<RefRO<CombatAttackState>, RefRO<CombatFacing>, RefRO<CombatPosition>, RefRO<CombatTeam>, RefRO<CombatantId>>()
                        .WithAll<InCombat, CombatHitRecord>().WithEntityAccess())
                {
                    var kind = attack.ValueRO.Kind;
                    if (kind == CombatAttackKind.None || attack.ValueRO.Timing.PhaseAt(tick - attack.ValueRO.StartTick) != CombatAttackPhase.Active)
                        continue;
                    m_Attackers.Add(new Attacker
                    {
                        Id = id.ValueRO.Value, Entity = entity, Team = team.ValueRO.Value, Position = position.ValueRO.Value, Kind = kind,
                        Strike = attack.ValueRO.Strike,
                        Facing = facing.ValueRO.Value == FacingDirection.Left ? -1f : 1f,
                    });
                }
                if (m_Attackers.Length == 0)
                    return;

                m_Attackers.Sort(new ById());
                for (var i = 0; i < m_Attackers.Length; i++)
                {
                    var a = m_Attackers[i];
                    m_Queries.Add(StrikeQuery(a.Strike, a.Position, a.Facing));
                }
                var index = state.World.GetExistingSystemManaged<CombatSpatialIndexSystem>();
                if (index == null || !index.TryQueryAabbBatch(m_Queries.AsArray(), m_Candidates, m_Ranges))
                    throw new InvalidOperationException(
                        "Hit needs this tick's final spatial snapshot for its Active attackers; no HP or ledger was written.");

                var session = SystemAPI.TryGetSingleton<BattleSession>(out var current) ? current.BattleNumber : 0u;
                m_Diagnostics[0] = new CombatHitDiagnostics
                {
                    Tick = tick, BattleNumber = session, ActiveAttackers = m_Attackers.Length, Queries = m_Queries.Length,
                    Candidates = m_Candidates.Length, EntriesVisited = index.LastQueryEntriesVisited, UsedFullScan = index.LastQueryUsedFullScan,
                };
                state.Dependency = new CombatHitJob
                {
                    Attackers = m_Attackers.AsArray(), Candidates = m_Candidates.AsArray(), Ranges = m_Ranges.AsArray(),
                    Health = SystemAPI.GetComponentLookup<CombatHealth>(), Ledgers = SystemAPI.GetBufferLookup<CombatHitRecord>(),
                    Diagnostics = m_Diagnostics,
                }.Schedule(state.Dependency);
                state.Dependency.Complete();
                LastHits = m_Diagnostics[0];
            }
            finally
            {
                // Copies of Entity rows never outlive the call (Despawn/End/Restart/World replacement).
                m_Attackers.Clear(); m_Queries.Clear(); m_Candidates.Clear(); m_Ranges.Clear();
            }
        }
    }

    // Explicit Burst kernel: no ECS queries or managed API; writes only HP and ledgers through the lookups.
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
    internal struct CombatHitJob : IJob
    {
        [ReadOnly] public NativeArray<CombatHitSystem.Attacker> Attackers;
        [ReadOnly] public NativeArray<CombatSpatialBody> Candidates;
        [ReadOnly] public NativeArray<int2> Ranges;
        public ComponentLookup<CombatHealth> Health;
        public BufferLookup<CombatHitRecord> Ledgers;
        public NativeArray<CombatHitDiagnostics> Diagnostics;

        [BurstDiscard]
        static void MarkManaged(ref CombatHitDiagnostics d) => d.KernelRanManaged = true;

        public void Execute()
        {
            var d = Diagnostics[0];
            MarkManaged(ref d);
            for (var a = 0; a < Attackers.Length; a++)
            {
                var attacker = Attackers[a];
                var strike = attacker.Strike;
                var ledger = Ledgers[attacker.Entity];
                var range = Ranges[a];
                for (var c = range.x; c < range.x + range.y; c++)
                {
                    var target = Candidates[c];
                    if (target.Team.Value == attacker.Team || target.Id.Value == attacker.Id || !Health.HasComponent(target.Entity))
                        continue;
                    d.InsideTests++;
                    if (!CombatHitSystem.Inside(strike, attacker.Position, attacker.Facing, target.Position) || Contains(ledger, target.Id.Value))
                        continue;
                    ledger.Add(new CombatHitRecord { Target = target.Id });
                    var hp = Health[target.Entity];
                    hp.Current = math.max(0, hp.Current - strike.Damage);
                    Health[target.Entity] = hp;
                    d.AcceptedHits++;
                }
            }
            Diagnostics[0] = d;
        }

        static bool Contains(DynamicBuffer<CombatHitRecord> ledger, uint id)
        {
            for (var i = 0; i < ledger.Length; i++)
            {
                if (ledger[i].Target.Value == id)
                    return true;
            }

            return false;
        }
    }
}
