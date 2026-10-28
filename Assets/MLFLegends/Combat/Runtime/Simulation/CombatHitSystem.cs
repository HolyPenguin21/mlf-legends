using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace MLFLegends.Combat
{
    /// <summary>The single fixed-tick hit resolution owner: the only writer of <see cref="CombatHealth"/> and of hit ledgers.</summary>
    /// <remarks>
    /// Runs last in State, after movement, so it tests this tick's final positions. Every in-combat attacker whose strike is
    /// in its Active phase on this tick hits every in-combat combatant of another team that is not already in the strike's
    /// ledger and lies inside the strike's box: forward distance along the attacker's facing within [ReachMin, ReachMax],
    /// |ΔZ| ≤ DepthHalfWidth, |ΔY| ≤ HeightTolerance. Each such target loses Damage HP (clamped at 0) once and joins the
    /// ledger, so several Active ticks of one strike never hit it twice; the next strike starts with an empty ledger.
    /// Allies and the attacker itself are never valid targets. Attackers and targets are visited in CombatantId order, so the
    /// result never depends on chunk order. No defeat, stagger, knockback or guard yet. Brute force O(attackers × combatants)
    /// with Temp scratch released here; a spatial index belongs to Stage 04. Presentation is never consulted.
    /// </remarks>
    [BurstCompile]
    [UpdateInGroup(typeof(CombatStateUpdateSystemGroup))]
    [UpdateAfter(typeof(CombatMovementSystem))]
    public partial struct CombatHitSystem : ISystem
    {
        EntityQuery m_Targets;

        struct Fighter
        {
            public uint Id;
            public Entity Entity;
            public byte Team;
            public float3 Position;
            public CombatAttackKind Kind;
            public float Facing;
        }

        struct ById : IComparer<Fighter>
        {
            public int Compare(Fighter a, Fighter b) => a.Id.CompareTo(b.Id);
        }

        public void OnCreate(ref SystemState state)
        {
            m_Targets = SystemAPI.QueryBuilder().WithAll<CombatantId, CombatTeam, CombatPosition, CombatHealth, InCombat>().Build();
            state.RequireForUpdate<SimulationClockRunning>();
            state.RequireForUpdate<SimulationClock>();
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public void OnUpdate(ref SystemState state)
        {
            var tick = SystemAPI.GetSingleton<SimulationClock>().Tick;
            var attackers = new NativeList<Fighter>(Allocator.Temp);
            foreach (var (attack, facing, position, team, id, entity) in
                SystemAPI.Query<RefRO<CombatAttackState>, RefRO<CombatFacing>, RefRO<CombatPosition>, RefRO<CombatTeam>, RefRO<CombatantId>>()
                    .WithAll<InCombat, CombatHitRecord>().WithEntityAccess())
            {
                var kind = attack.ValueRO.Kind;
                if (kind == CombatAttackKind.None
                    || CombatAttackTuning.PhaseAt(CombatAttackTuning.Get(kind), tick - attack.ValueRO.StartTick) != CombatAttackPhase.Active)
                    continue;
                attackers.Add(new Fighter
                {
                    Id = id.ValueRO.Value, Entity = entity, Team = team.ValueRO.Value, Position = position.ValueRO.Value, Kind = kind,
                    Facing = facing.ValueRO.Value == FacingDirection.Left ? -1f : 1f,
                });
            }

            if (attackers.Length == 0)
            {
                attackers.Dispose();
                return;
            }

            var ids = m_Targets.ToComponentDataArray<CombatantId>(Allocator.Temp);
            var teams = m_Targets.ToComponentDataArray<CombatTeam>(Allocator.Temp);
            var positions = m_Targets.ToComponentDataArray<CombatPosition>(Allocator.Temp);
            var entities = m_Targets.ToEntityArray(Allocator.Temp);
            var targets = new NativeList<Fighter>(ids.Length, Allocator.Temp);
            for (var i = 0; i < ids.Length; i++)
                targets.Add(new Fighter { Id = ids[i].Value, Entity = entities[i], Team = teams[i].Value, Position = positions[i].Value });
            attackers.Sort(new ById());
            targets.Sort(new ById());

            var health = SystemAPI.GetComponentLookup<CombatHealth>();
            var ledgers = SystemAPI.GetBufferLookup<CombatHitRecord>();
            for (var a = 0; a < attackers.Length; a++)
            {
                var attacker = attackers[a];
                var strike = CombatAttackTuning.Get(attacker.Kind);
                var ledger = ledgers[attacker.Entity];
                for (var t = 0; t < targets.Length; t++)
                {
                    var target = targets[t];
                    if (target.Team == attacker.Team || target.Id == attacker.Id || !Inside(strike, attacker, target.Position)
                        || Contains(ledger, target.Id))
                        continue;
                    ledger.Add(new CombatHitRecord { Target = new CombatantId { Value = target.Id } });
                    var hp = health[target.Entity];
                    hp.Current = math.max(0, hp.Current - strike.Damage);
                    health[target.Entity] = hp;
                }
            }

            // Main-thread Temp is reclaimed only at frame end and this runs once per catch-up tick: release here.
            targets.Dispose();
            entities.Dispose();
            positions.Dispose();
            teams.Dispose();
            ids.Dispose();
            attackers.Dispose();
        }

        static bool Inside(in CombatAttackDefinition strike, in Fighter attacker, float3 target)
        {
            var delta = target - attacker.Position;
            var forward = delta.x * attacker.Facing;
            return forward >= strike.ReachMin && forward <= strike.ReachMax
                && math.abs(delta.z) <= strike.DepthHalfWidth && math.abs(delta.y) <= strike.HeightTolerance;
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
