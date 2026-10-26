using Unity.Burst;
using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>The sole fixed-tick integration owner of CombatPosition and CombatMotion.</summary>
    /// <remarks>
    /// Collection has already published an accepted command or implicit None for every enabled actor.
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
            foreach (var (position, motion, applied) in
                SystemAPI.Query<RefRW<CombatPosition>, RefRW<CombatMotion>, RefRO<AppliedCombatCommand>>().WithAll<InCombat>())
            {
                var command = applied.ValueRO.Command;
                var next = position.ValueRO.Value;
                var kinetic = motion.ValueRO;
                var distance = CombatMovementTuning.MoveSpeed * dt;
                if (command.MoveX != 0 && command.MoveZ != 0)
                    distance *= diagonalScale;
                next.x += command.MoveX * distance;
                next.z += command.MoveZ * distance;

                if (kinetic.Grounded && (command.Pressed & CommandButtons.Jump) != 0)
                {
                    kinetic.Grounded = false;
                    kinetic.VerticalVelocity = CombatMovementTuning.JumpImpulse;
                }

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
                    }
                }

                position.ValueRW.Value = next;
                motion.ValueRW = kinetic;
            }
        }
    }
}
