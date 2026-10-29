using Unity.Entities;
using Unity.Mathematics;

namespace MLFLegends.Combat
{
    /// <summary>The sole fixed-tick integration owner of CombatPosition and CombatMotion.</summary>
    /// <remarks>
    /// Collection has already published an accepted command or implicit None for every enabled actor;
    /// CombatActionSystem has already started/ended this tick's strike and CombatFacingSystem has turned the actor.
    /// During a strike, planar speed is scaled once by its frozen authoritative phase (50%/25%/25%); Jump is ignored.
    /// At physical contact, the published jump landing duration is copied into CombatMotion and controlled planar movement
    /// and Jump are suppressed until it ends. Gravity is never scaled. Movement never writes CombatFacing or CombatAttackState.
    /// Besides kinetics it records what actually happened (landing tick, last planar move, movement streak and its
    /// distance) for presentation; a stop of at most <see cref="CombatMovementTuning.MoveStreakGraceTicks"/> continues
    /// the streak, a landing restarts it.
    /// Managed orchestration of one synchronous Burst job, using the existing spatial owner's reusable native scratch.
    /// Stage 04 collision corrections must extend this owner rather than introduce another position writer.
    /// Stage 04.4: it also owns the World's single <see cref="CombatImpulse"/> input buffer and the physical X/Z
    /// <see cref="CombatMotion.ExternalVelocity"/>. Every entry present at this call is consumed exactly once (applied, dropped
    /// or rejected, see CombatContactDiagnostics) after a successful solve; one integrator, no second clock or queue owner.
    /// A fighter whose InCombat is disabled, or whose whole entity is Disabled, loses its external velocity here; impulses
    /// addressed to it are dropped, so re-enabling never releases an old impulse.
    /// </remarks>
    [UpdateInGroup(typeof(CombatStateUpdateSystemGroup))]
    public partial struct CombatMovementSystem : ISystem
    {
        Entity m_Impulses;

        public void OnCreate(ref SystemState state)
        {
            // One impulse input per World; a tag component cannot go through CreateSingleton(value).
            using (var existing = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CombatImpulseQueue>()))
                if (!existing.IsEmptyIgnoreFilter)
                    throw new System.InvalidOperationException("Only one CombatMovementSystem may own a World impulse input.");
            m_Impulses = state.EntityManager.CreateEntity(typeof(CombatImpulseQueue), typeof(CombatImpulse));
            state.EntityManager.SetName(m_Impulses, nameof(CombatImpulseQueue));
            state.RequireForUpdate<SimulationClockRunning>();
        }

        public void OnDestroy(ref SystemState state)
        {
            if (state.EntityManager.Exists(m_Impulses))
                state.EntityManager.DestroyEntity(m_Impulses);
        }

        public void OnUpdate(ref SystemState state)
        {
            state.Dependency.Complete();
            var owner = state.World.GetOrCreateSystemManaged<CombatSpatialIndexSystem>();
            var rows = owner.BeginMovement();
            try
            {
                const float diagonalScale = 0.7071067811865475f; // 1/sqrt(2), for the validated {-1,0,1} axes
                const float dt = SimulationTiming.TickDuration;
                var tick = SystemAPI.TryGetSingleton<SimulationClock>(out var clock) ? clock.Tick : SimulationClock.NoTick;
                var attacks = SystemAPI.GetComponentLookup<CombatAttackState>(true);
                var landingTicks = SystemAPI.TryGetSingleton<CombatStrikeSchedule>(out var schedule) ? schedule.LandingRecoveryTicks : 0u;
                var bodies = SystemAPI.GetComponentLookup<CombatBody>(true);
                var hasArena = SystemAPI.TryGetSingleton<CombatArenaState>(out var arena);
                var legacy = !hasArena || !arena.Required;
                var shapes = hasArena ? SystemAPI.GetSingletonBuffer<CombatArenaShape>(true).AsNativeArray() : owner.EmptyShapes;
                var ids = SystemAPI.GetComponentLookup<CombatantId>(true);
                var teams = SystemAPI.GetComponentLookup<CombatTeam>(true);
                var controlled = SystemAPI.TryGetSingleton<BattleSession>(out var session) ? session.HumanCombatant.Value : 0;
                var diagnostics = new CombatArenaDiagnostics { Tick = tick, Revision = hasArena ? arena.Revision : 0 };
                foreach (var (position, motion, applied, entity) in
                    SystemAPI.Query<RefRO<CombatPosition>, RefRO<CombatMotion>, RefRO<AppliedCombatCommand>>().WithAll<InCombat>()
                        .WithEntityAccess())
                {
                    var command = applied.ValueRO.Command;
                    var next = position.ValueRO.Value;
                    var kinetic = motion.ValueRO;
                    var moveMultiplier = 1f;
                    if (attacks.TryGetComponent(entity, out var attack) && attack.IsAttacking)
                    {
                        moveMultiplier = CombatMovementTuning.AttackMoveMultiplier(attack.Timing.PhaseAt(tick - attack.StartTick));
                        command.Pressed &= ~CommandButtons.Jump;
                    }
                    if (kinetic.IsLandingRecovery(tick))
                        command.Pressed = CommandButtons.None;

                    if (kinetic.IsLandingRecovery(tick)) { command.MoveX = 0; command.MoveZ = 0; }
                    var distance = CombatMovementTuning.MoveSpeed * dt * moveMultiplier;
                    if (command.MoveX != 0 && command.MoveZ != 0) distance *= diagonalScale;
                    var hasBody = bodies.TryGetComponent(entity, out var actualBody);
                    var body = !legacy && hasBody ? actualBody : CombatBodyTuning.Default;
                    var frozen = !legacy && (!arena.Valid || !hasBody);
                    var failure = frozen ? (!arena.Valid ? CombatArenaFailure.ArenaUnavailable : CombatArenaFailure.InvalidBody) : CombatArenaFailure.None;
                    rows.Add(new CombatContactActor
                    {
                        Entity = entity, Id = ids.TryGetComponent(entity, out var id) ? id.Value : command.Combatant.Value,
                        Team = teams.TryGetComponent(entity, out var team) ? team.Value : (byte)0,
                        Collidable = hasBody && ids.HasComponent(entity) && teams.HasComponent(entity),
                        Controlled = ids.HasComponent(entity) && id.Value == controlled,
                        Legacy = legacy, Frozen = frozen, Body = actualBody, MovementBody = body,
                        Start = next, Motion = kinetic, Desired = new float2(command.MoveX * distance, command.MoveZ * distance),
                        Velocity = kinetic.ExternalVelocity,
                        Jump = (command.Pressed & CommandButtons.Jump) != 0,
                        Result = new CombatArenaMove { Position = next, Velocity = kinetic.VerticalVelocity,
                            Grounded = kinetic.Grounded, GroundHeight = kinetic.GroundHeight, Failure = failure },
                    });
                }
                var pending = SystemAPI.GetBuffer<CombatImpulse>(m_Impulses);
                owner.CalculateMovement(shapes, pending.AsNativeArray(), tick, landingTicks);
                var positions = SystemAPI.GetComponentLookup<CombatPosition>();
                var motions = SystemAPI.GetComponentLookup<CombatMotion>();
                for (var i = 0; i < rows.Length; i++)
                {
                    var row = rows[i];
                    var result = row.Result;
                    if (row.Frozen)
                    {
                        if (result.Failure != CombatArenaFailure.None)
                        { diagnostics.FailedBodies++; diagnostics.LastFailure = result.Failure; }
                        if (math.any(row.Motion.ExternalVelocity != 0))
                        {
                            var rolledBack = row.Motion;
                            rolledBack.ExternalVelocity = float2.zero; // Rollback publishes no stored velocity for later release.
                            motions[row.Entity] = rolledBack;
                        }
                        continue;
                    }
                    var next = result.Position;
                    var kinetic = row.Motion;
                    kinetic.ExternalVelocity = row.VelocityOut;
                    kinetic.VerticalVelocity = result.Velocity;
                    kinetic.Grounded = result.Grounded;
                    if (!legacy) kinetic.GroundHeight = result.GroundHeight;
                    var landed = result.Landed;
                    if (landed)
                    {
                        kinetic.LandTick = tick;
                        kinetic.LandingRecoveryTicks = landingTicks;
                        // Contact cancels the old run streak; the first post-recovery move starts a new one.
                        kinetic.MoveStreakStartTick = SimulationClock.NoTick;
                        kinetic.MoveStreakDistance = 0f;
                    }

                    // Physical displacement after contacts, excluding initial depenetration; no wall-pressure run accumulation.
                    var distance = result.ControlledDistance;
                    var moved = distance > CombatContactTuning.ControlledDistanceEpsilon;

                    if (landed && moved)
                    {
                        kinetic.MoveStreakStartTick = tick;
                        kinetic.MoveStreakDistance = distance;
                    }
                    else if (moved)
                    {
                        if (kinetic.MoveStreakStartTick == SimulationClock.NoTick
                            || tick - kinetic.LastMoveTick > CombatMovementTuning.MoveStreakGraceTicks + 1)
                        {
                            kinetic.MoveStreakStartTick = tick;
                            kinetic.MoveStreakDistance = 0f;
                        }
                        kinetic.MoveStreakDistance += distance;
                    }

                    if (moved)
                        kinetic.LastMoveTick = tick;

                    positions[row.Entity] = new CombatPosition { Value = next };
                    motions[row.Entity] = kinetic;
                }
                if (hasArena) SystemAPI.SetSingleton(diagnostics);
                // Consumption boundary: only after a complete solve and write. An exception above leaves the input unconsumed.
                SystemAPI.GetBuffer<CombatImpulse>(m_Impulses).Clear();
                foreach (var outOfCombat in SystemAPI.Query<RefRW<CombatMotion>>().WithDisabled<InCombat>())
                    if (math.any(outOfCombat.ValueRO.ExternalVelocity != 0)) outOfCombat.ValueRW.ExternalVelocity = float2.zero;
                // A whole-entity Disabled fighter is invisible to every default query (including the one above): clear it
                // explicitly, so removing Disabled never releases a stored impulse either (#69). Vertical state unchanged.
                foreach (var disabled in SystemAPI.Query<RefRW<CombatMotion>>().WithAll<Disabled>()
                    .WithOptions(EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IgnoreComponentEnabledState))
                    if (math.any(disabled.ValueRO.ExternalVelocity != 0)) disabled.ValueRW.ExternalVelocity = float2.zero;
            }
            finally { owner.EndMovement(); }
        }
    }
}
