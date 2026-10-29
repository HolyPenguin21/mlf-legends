using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace MLFLegends.Combat
{
    /// <summary>Sole runtime Facing writer. Nearest opponent uses a gathered start-of-tick X/Z snapshot.</summary>
    [UpdateInGroup(typeof(CombatStateUpdateSystemGroup))]
    [UpdateBefore(typeof(CombatMovementSystem))]
    public partial struct CombatFacingSystem : ISystem
    {
        internal struct Opponent { public Entity Entity; public uint Id; public byte Team; public float3 Position; }
        internal struct Participant
        {
            public Entity Entity; public uint Id; public byte Team; public float3 Position;
            public sbyte MoveX; public bool HasIdentity, Locked;
        }
        NativeList<Opponent> m_Opponents;
        NativeList<Participant> m_Participants;
        NativeArray<byte> m_Managed;
        public bool LastKernelRanManaged;
        public double LastMicroseconds;
        public int LastParticipants, LastOpponents;

        public void OnCreate(ref SystemState state)
        {
            m_Managed = new NativeArray<byte>(1, Allocator.Persistent);
            m_Opponents = new NativeList<Opponent>(64, Allocator.Persistent);
            m_Participants = new NativeList<Participant>(64, Allocator.Persistent);
            state.RequireForUpdate<SimulationClockRunning>();
        }
        public void OnDestroy(ref SystemState state) { m_Opponents.Dispose(); m_Participants.Dispose(); m_Managed.Dispose(); }

        public void OnUpdate(ref SystemState state)
        {
            state.Dependency.Complete(); var start = Stopwatch.GetTimestamp();
            m_Opponents.Clear(); m_Participants.Clear();
            var tick = SystemAPI.TryGetSingleton<SimulationClock>(out var clock) ? clock.Tick : SimulationClock.NoTick;
            var attacks = SystemAPI.GetComponentLookup<CombatAttackState>(true);
            var motions = SystemAPI.GetComponentLookup<CombatMotion>(true);
            var teams = SystemAPI.GetComponentLookup<CombatTeam>(true);
            var ids = SystemAPI.GetComponentLookup<CombatantId>(true);
            var positions = SystemAPI.GetComponentLookup<CombatPosition>(true);
            try
            {
                foreach (var (id, team, position, entity) in SystemAPI.Query<RefRO<CombatantId>, RefRO<CombatTeam>, RefRO<CombatPosition>>()
                    .WithAll<InCombat>().WithEntityAccess())
                    m_Opponents.Add(new Opponent { Entity = entity, Id = id.ValueRO.Value, Team = team.ValueRO.Value, Position = position.ValueRO.Value });
                if (m_Opponents.Length > CombatContactTuning.MaxBodies)
                    throw new System.InvalidOperationException("Facing roster exceeds the supported battle bound.");
                foreach (var (applied, entity) in SystemAPI.Query<RefRO<AppliedCombatCommand>>().WithAll<CombatFacing, InCombat>().WithEntityAccess())
                {
                    var locked = attacks.TryGetComponent(entity, out var attack) && attack.IsAttacking && attack.StartTick != tick;
                    locked |= motions.TryGetComponent(entity, out var motion) && motion.IsLandingRecovery(tick);
                    var identity = ids.TryGetComponent(entity, out var id) && teams.TryGetComponent(entity, out _)
                        && positions.HasComponent(entity);
                    m_Participants.Add(new Participant { Entity = entity, Id = identity ? id.Value : 0,
                        Team = identity ? teams[entity].Value : (byte)0, Position = identity ? positions[entity].Value : default,
                        MoveX = applied.ValueRO.Command.MoveX, HasIdentity = identity, Locked = locked });
                }
                m_Managed[0] = 0;
                new NearestFacingJob { Managed = m_Managed, Opponents = m_Opponents.AsArray(), Participants = m_Participants.AsArray(),
                    Facings = SystemAPI.GetComponentLookup<CombatFacing>() }.Schedule().Complete();
                LastKernelRanManaged = m_Managed[0] != 0;
                LastParticipants = m_Participants.Length; LastOpponents = m_Opponents.Length;
            }
            finally
            {
                m_Opponents.Clear(); m_Participants.Clear();
                LastMicroseconds = (Stopwatch.GetTimestamp() - start) * 1e6 / Stopwatch.Frequency;
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        internal struct NearestFacingJob : IJob
        {
            [ReadOnly] public NativeArray<Opponent> Opponents;
            [ReadOnly] public NativeArray<Participant> Participants;
            public ComponentLookup<CombatFacing> Facings;
            public NativeArray<byte> Managed;
            [BurstDiscard] static void MarkManaged(ref byte flag) => flag = 1;
            public void Execute()
            {
                byte flag = 0; MarkManaged(ref flag); Managed[0] = flag;
                foreach (var actor in Participants)
                {
                    if (actor.Locked) continue;
                    var facing = Facings[actor.Entity];
                    var nearest = float.PositiveInfinity; var bestId = uint.MaxValue; var dx = 0f; var found = false;
                    if (actor.HasIdentity)
                        foreach (var enemy in Opponents)
                        {
                            if (enemy.Entity == actor.Entity || enemy.Team == actor.Team) continue;
                            var delta = enemy.Position.xz - actor.Position.xz; var distance = math.lengthsq(delta);
                            if (distance < nearest || (distance == nearest && enemy.Id < bestId))
                            { nearest = distance; bestId = enemy.Id; dx = delta.x; found = true; }
                        }
                    if (found)
                    {
                        if (math.abs(dx) > 1e-6f) facing.Value = dx > 0 ? FacingDirection.Right : FacingDirection.Left;
                    }
                    else if (actor.MoveX != 0) facing.Value = actor.MoveX > 0 ? FacingDirection.Right : FacingDirection.Left;
                    Facings[actor.Entity] = facing;
                }
            }
        }
    }
}
