using Unity.Burst;
using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>The sole fixed-tick integration owner of CombatPosition and CombatMotion.</summary>
    /// <remarks>
    /// Collection has already published an accepted command or implicit None for every enabled actor;
    /// CombatActionSystem has already started/ended this tick's strike and CombatFacingSystem has turned the actor.
    /// While a strike runs (Stage 05.1) the accepted Move and Jump are ignored: no planar movement and no takeoff;
    /// gravity still integrates. Movement never writes CombatFacing or CombatAttackState.
    /// Besides kinetics it records what actually happened (landing tick, last planar move, movement streak and its
    /// distance) for presentation; a stop of at most <see cref="CombatMovementTuning.MoveStreakGraceTicks"/> continues
    /// the streak, a landing restarts it.
    /// One linear, synchronous Burst pass; no allocations, structural changes, entity caches or second clock.
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
            foreach (var (position, motion, applied, entity) in
                SystemAPI.Query<RefRW<CombatPosition>, RefRW<CombatMotion>, RefRO<AppliedCombatCommand>>().WithAll<InCombat>()
                    .WithEntityAccess())
            {
                var command = applied.ValueRO.Command;
                if (attacks.TryGetComponent(entity, out var attack) && attack.IsAttacking)
                {
                    command.MoveX = 0;
                    command.MoveZ = 0;
                    command.Pressed &= ~CommandButtons.Jump;
                }

                var next = position.ValueRO.Value;
                var kinetic = motion.ValueRO;
                var distance = CombatMovementTuning.MoveSpeed * dt;
                if (command.MoveX != 0 && command.MoveZ != 0)
                    distance *= diagonalScale;
                next.x += command.MoveX * distance;
                next.z += command.MoveZ * distance;
                var moved = command.MoveX != 0 || command.MoveZ != 0;

                if (kinetic.Grounded && (command.Pressed & CommandButtons.Jump) != 0)
                {
                    kinetic.Grounded = false;
                    kinetic.VerticalVelocity = CombatMovementTuning.JumpImpulse;
                }

                var landed = false;
                if (!kinetic.Grounded)
                {
                    // Semi-implicit Euler: gravity, then height, then clamp to this actor's spawn support.
                    kinetic.VerticalVelocity -= CombatMovementTuning.Gravity * dt;
                    next.y += kinetic.VerticalVelocity * dt;
                    if (next.y <= kinetic.GroundHeight)
                    {
                        next.y = kinetic.GroundHeight;
                        kinetic.VerticalVelocity = 0f;
                        kinetic.Grounded = true;
                        landed = true;
                    }
                }

                if (landed)
                {
                    kinetic.LandTick = tick;
                    kinetic.MoveStreakStartTick = moved ? tick : SimulationClock.NoTick;
                    kinetic.MoveStreakDistance = moved ? distance : 0f;
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
        }
    }
}
