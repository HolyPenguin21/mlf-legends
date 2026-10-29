using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Owner of the battle lifecycle and the single place that creates and destroys combatant entities and
    /// allocates <see cref="CombatantId"/>. Applies <see cref="BattleRequest"/>s between fixed steps, including paused frames.
    /// </summary>
    /// <remarks>
    /// Runs in Initialization (even on paused frames; updated there by <see cref="BattleLifecycleInitializationProxy"/>,
    /// the same instance) and at the start of every fixed step, before
    /// <see cref="CombatClockSystem"/> advances the tick. <see cref="SimulationClock.Tick"/> is still the last
    /// completed tick: a battle started here has <c>BattleStartTick = Tick</c> and its first simulated tick
    /// is <c>Tick + 1</c>, the next real fixed step. Because it runs before every catch-up step, a request made during tick T (e.g. an
    /// end of battle decided in the state-update phase) is applied before tick T + 1 runs any combat phase.
    /// Start/restart order: check every precondition (including <see cref="CombatCommandQueue.CanResetForBattle"/>)
    /// → destroy the previous battle's combatants → <see cref="CombatCommandQueue.ResetForBattle"/> → spawn the
    /// new combatants with fresh IDs. Never touches the <see cref="World"/>, the clock or entities without
    /// <see cref="CombatantId"/>. Lifetime equals the World; <c>OnCreate</c> throws if a
    /// <see cref="BattleSession"/> already exists, so a world never has two lifecycle owners. See BOOTSTRAP.md.
    /// </remarks>
    [BurstCompile]
    // The SAME instance drains the SAME buffer at both boundaries. Initialization never advances time.
    // Only one [UpdateInGroup] is allowed (CS0579): FixedStep here, Initialization through the proxy.
    // Normal fixed-step ordering places us after the OrderFirst BeginFixedStep ECB; the clock follows us.
    // Fixed-only CombatClockSystem owns the dependency; no cross-group ordering constraint belongs here.
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    public partial struct BattleLifecycleSystem : ISystem
    {
        Entity m_Session;
        EntityArchetype m_CombatantArchetype;
        EntityQuery m_Combatants;
        EntityQuery m_Clock;
        EntityQuery m_RunningClock;
        EntityQuery m_Arena;
        EntityQuery m_Table;

        public void OnCreate(ref SystemState state)
        {
            // Throws if a session already exists in this world.
            m_Session = state.EntityManager.CreateSingleton(new BattleSession(), nameof(BattleSession));
            state.EntityManager.AddComponentData(m_Session, new CombatantIdAllocator());
            state.EntityManager.AddBuffer<BattleRequest>(m_Session);

            // Human and AI combatants are identical; AppliedCombatCommand is added by the command processor. Attack state
            // starts free (default) and the hit ledger empty; both die with the entity, so no strike outlives its battle.
            m_CombatantArchetype = state.EntityManager.CreateArchetype(
                typeof(CombatantId), typeof(CombatTeam), typeof(CombatPosition), typeof(CombatFacing), typeof(InCombat), typeof(CombatMotion), typeof(CombatBody),
                typeof(CombatAttackState), typeof(CombatHealth), typeof(CombatHitRecord));
            m_Combatants = SystemAPI.QueryBuilder().WithAll<CombatantId>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState | EntityQueryOptions.IncludeDisabledEntities).Build();
            m_Clock = SystemAPI.QueryBuilder().WithAll<SimulationClock>().Build();
            m_RunningClock = SystemAPI.QueryBuilder().WithAll<SimulationClock, SimulationClockRunning>().Build();

            m_Arena = SystemAPI.QueryBuilder().WithAll<CombatArenaState, CombatArenaShape>().Build();
            m_Table = SystemAPI.QueryBuilder().WithAll<CombatStrikeSchedule>().Build();

            state.RequireForUpdate<SimulationClock>();
            state.RequireForUpdate<CombatCommandQueue>();
        }

        public void OnDestroy(ref SystemState state)
        {
            // The owner leaves no combatants and no session behind (only relevant if the World outlives the system).
            if (!m_Combatants.IsEmptyIgnoreFilter)
                state.EntityManager.DestroyEntity(m_Combatants);
            if (state.EntityManager.Exists(m_Session))
                state.EntityManager.DestroyEntity(m_Session);
        }

        [BurstCompile(CompileSynchronously = true)]
        public void OnUpdate(ref SystemState state)
        {
            var buffer = SystemAPI.GetBuffer<BattleRequest>(m_Session);
            if (buffer.Length == 0)
                return;

            // Copied first: spawning and destroying are structural changes that invalidate the buffer.
            var requests = buffer.ToNativeArray(Allocator.Temp);
            buffer.Clear();

            var entityManager = state.EntityManager;
            var session = entityManager.GetComponentData<BattleSession>(m_Session);
            var allocator = entityManager.GetComponentData<CombatantIdAllocator>(m_Session);
            for (var i = 0; i < requests.Length; i++)
                session.SetOutcome(Apply(entityManager, requests[i], ref session, ref allocator));

            entityManager.SetComponentData(m_Session, session);
            entityManager.SetComponentData(m_Session, allocator);
            requests.Dispose();
        }

        BattleRequestOutcome Apply(EntityManager entityManager, in BattleRequest request,
            ref BattleSession session, ref CombatantIdAllocator allocator)
        {
            switch (request.Kind)
            {
                case BattleRequestKind.Start:
                case BattleRequestKind.Restart:
                    return Start(entityManager, request, ref session, ref allocator);
                case BattleRequestKind.End:
                    if (!session.IsActive)
                        return BattleRequestOutcome.NotActive;
                    if (request.Owner != 0 && request.Owner != session.Owner)
                        return BattleRequestOutcome.NotOwner;
                    var tick = CurrentTick;
                    if (!CombatCommandQueue.CanResetForBattle(entityManager, tick))
                        return BattleRequestOutcome.CommandQueueRejected;
                    DestroyCombatants(entityManager, tick);
                    session.Finish();
                    return BattleRequestOutcome.Ended;
                case BattleRequestKind.Despawn:
                    if (!session.IsActive)
                        return BattleRequestOutcome.NotActive;
                    return Despawn(entityManager, request.Combatant) ? BattleRequestOutcome.Despawned : BattleRequestOutcome.UnknownCombatant;
                default:
                    return BattleRequestOutcome.None;
            }
        }

        BattleRequestOutcome Start(EntityManager entityManager, in BattleRequest request,
            ref BattleSession session, ref CombatantIdAllocator allocator)
        {
            // Every check before any side effect: a rejected (re)start leaves the running battle untouched.
            var setup = request.Setup;
            if (!setup.IsValid)
                return BattleRequestOutcome.InvalidSetup;
            if (session.IsActive && request.Kind != BattleRequestKind.Restart)
                return BattleRequestOutcome.AlreadyActive;
            if (m_RunningClock.IsEmpty)
                return BattleRequestOutcome.ClockStopped;
            var battleStartTick = CurrentTick;
            if (!CombatCommandQueue.CanResetForBattle(entityManager, battleStartTick))
                return BattleRequestOutcome.CommandQueueRejected;
            // One reservation for the whole battle: human = first, AI = first + 1, then the human's and the AI's allies.
            if (!allocator.TryReserve((uint)setup.CombatantCount, out var first))
                return BattleRequestOutcome.IdsExhausted;

            // Also drops the previous battle's command boundary, pending commands and results.
            DestroyCombatants(entityManager, battleStartTick);
            var human = Spawn(entityManager, new CombatantId { Value = first }, setup.Human, diagnosticName: "Combatant Human");
            var ai = Spawn(entityManager, new CombatantId { Value = first + 1 }, setup.Ai, diagnosticName: "Combatant AI");
            var next = first + 2;
            for (var i = 0; i < setup.HumanAllies; i++)
                Spawn(entityManager, new CombatantId { Value = next++ }, BattleSetup.AllyOf(setup.Human, i), diagnosticName: default);
            for (var i = 0; i < setup.AiAllies; i++)
                Spawn(entityManager, new CombatantId { Value = next++ }, BattleSetup.AllyOf(setup.Ai, i), diagnosticName: default);
            session.Begin(battleStartTick, request.Owner, human, ai);
            return BattleRequestOutcome.Started;
        }

        /// <summary>Last completed tick at either boundary; this system never advances the clock.</summary>
        uint CurrentTick => m_Clock.GetSingleton<SimulationClock>().Tick;

        /// <summary>
        /// Destroys every combatant, then clears the command queue at <paramref name="tick"/>. Callers check
        /// <see cref="CombatCommandQueue.CanResetForBattle"/> first, so the reset cannot throw after the destroy.
        /// </summary>
        void DestroyCombatants(EntityManager entityManager, uint tick)
        {
            if (!m_Combatants.IsEmptyIgnoreFilter)
                entityManager.DestroyEntity(m_Combatants);

            CombatCommandQueue.ResetForBattle(entityManager, tick);
        }

        /// <remarks>
        /// Only leaders receive one of two constant diagnostic role names. Entity names live in a process-wide
        /// store until domain reload; appending a never-reused ID would grow that store on every battle.
        /// Allies stay unnamed. CombatantId is the only authoritative identity, never this display name.
        /// </remarks>
        CombatantId Spawn(EntityManager entityManager, CombatantId id, in CombatantSetup setup, FixedString64Bytes diagnosticName)
        {
            var entity = entityManager.CreateEntity(m_CombatantArchetype); // InCombat starts enabled
            entityManager.SetComponentData(entity, id);
            entityManager.SetComponentData(entity, new CombatTeam { Value = setup.Team });
            entityManager.SetComponentData(entity, new CombatPosition { Value = setup.Position });
            // The character's published body (#69); a World without the table (partial fixtures) uses the default body.
            var body = m_Table.CalculateEntityCount() == 1 ? m_Table.GetSingleton<CombatStrikeSchedule>().Body : CombatBodyTuning.Default;
            var motion = new CombatMotion { GroundHeight = setup.Position.y, Grounded = true };
            if (m_Arena.CalculateEntityCount() == 1)
            {
                var arenaEntity = m_Arena.GetSingletonEntity();
                var arena = entityManager.GetComponentData<CombatArenaState>(arenaEntity);
                if (arena.Required)
                {
                    motion.Grounded = arena.Valid && CombatArenaGeometry.Support(
                        entityManager.GetBuffer<CombatArenaShape>(arenaEntity, true).AsNativeArray(), setup.Position,
                        body.Radius, setup.Position.y, setup.Position.y, true, out _);
                }
            }
            entityManager.SetComponentData(entity, motion);
            entityManager.SetComponentData(entity, body);
            entityManager.SetComponentData(entity, new CombatFacing { Value = setup.Facing });
            entityManager.SetComponentData(entity, new CombatHealth { Current = CombatAttackTuning.MaxHealth, Max = CombatAttackTuning.MaxHealth });

            if (!diagnosticName.IsEmpty)
                entityManager.SetName(entity, diagnosticName);

            return id;
        }

        bool Despawn(EntityManager entityManager, CombatantId id)
        {
            var ids = m_Combatants.ToComponentDataArray<CombatantId>(Allocator.Temp);
            var entities = m_Combatants.ToEntityArray(Allocator.Temp);
            var found = Entity.Null;
            for (var i = 0; i < ids.Length; i++)
            {
                if (ids[i].Value == id.Value)
                {
                    found = entities[i];
                    break;
                }
            }

            ids.Dispose();
            entities.Dispose();
            if (found == Entity.Null)
                return false;

            entityManager.DestroyEntity(found);
            return true;
        }
    }
}
