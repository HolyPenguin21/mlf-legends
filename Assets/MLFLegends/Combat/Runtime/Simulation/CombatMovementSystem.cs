using Unity.Burst;
using Unity.Entities;
using Unity.Collections;
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
    /// One synchronous Burst pass with bounded arena contacts; no allocations, structural changes or second clock.
    /// Stage 04 collision corrections must extend this owner rather than introduce another position writer.
    /// </remarks>
    [BurstCompile]
    [UpdateInGroup(typeof(CombatStateUpdateSystemGroup))]
    public partial struct CombatMovementSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimulationClockRunning>();
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public void OnUpdate(ref SystemState state)
        {
            const float diagonalScale = 0.7071067811865475f; // 1/sqrt(2), for the validated {-1,0,1} axes
            const float dt = SimulationTiming.TickDuration;
            var tick = SystemAPI.TryGetSingleton<SimulationClock>(out var clock) ? clock.Tick : SimulationClock.NoTick;
            var attacks = SystemAPI.GetComponentLookup<CombatAttackState>(true);
            var landingTicks = SystemAPI.TryGetSingleton<CombatStrikeSchedule>(out var schedule) ? schedule.LandingRecoveryTicks : 0u;
            var bodies = SystemAPI.GetComponentLookup<CombatBody>(true);
            var hasArena = SystemAPI.TryGetSingleton<CombatArenaState>(out var arena);
            var legacy = !hasArena || !arena.Required;
            var shapes = hasArena ? SystemAPI.GetSingletonBuffer<CombatArenaShape>(true).AsNativeArray() : default;
            var diagnostics = new CombatArenaDiagnostics { Tick = tick, Revision = hasArena ? arena.Revision : 0 };
            foreach (var (position, motion, applied, entity) in
                SystemAPI.Query<RefRW<CombatPosition>, RefRW<CombatMotion>, RefRO<AppliedCombatCommand>>().WithAll<InCombat>()
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
                var body = !legacy && bodies.TryGetComponent(entity, out var actualBody) ? actualBody : CombatBodyTuning.Default;
                if (!legacy && (!arena.Valid || !bodies.HasComponent(entity)))
                {
                    diagnostics.FailedBodies++; diagnostics.LastFailure = !arena.Valid ? CombatArenaFailure.ArenaUnavailable : CombatArenaFailure.InvalidBody;
                    continue; // Required production arena is invalid: freeze, never use spawn support.
                }
                var result = CombatArenaGeometry.Move(shapes, body, next,
                    new float2(command.MoveX * distance, command.MoveZ * distance), kinetic.VerticalVelocity,
                    kinetic.Grounded, (command.Pressed & CommandButtons.Jump) != 0, landingTicks, legacy, kinetic.GroundHeight);
                if (result.Failure != CombatArenaFailure.None)
                {
                    diagnostics.FailedBodies++; diagnostics.LastFailure = result.Failure;
                    continue;
                }
                next = result.Position;
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
                distance = result.ControlledDistance;
                var moved = distance > CombatArenaTuning.Skin;

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

                position.ValueRW.Value = next;
                motion.ValueRW = kinetic;
            }
            if (hasArena) SystemAPI.SetSingleton(diagnostics);
        }
    }
}
