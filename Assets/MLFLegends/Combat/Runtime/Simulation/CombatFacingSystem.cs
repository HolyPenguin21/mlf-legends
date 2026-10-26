using Unity.Burst;
using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>The sole fixed-tick owner of CombatFacing after spawn: faces the sign of the accepted MoveX.</summary>
    /// <remarks>
    /// MoveX +1 faces Right, -1 faces Left, 0 (idle, Z-only, implicit None) keeps the previous facing; airborne
    /// actors turn too. Runs before CombatMovementSystem so every later State owner sees this tick's facing.
    /// No action locks yet: future attack/guard owners must define any turn restriction explicitly in this owner.
    /// One linear, synchronous Burst pass; no allocations, structural changes or second command path.
    /// </remarks>
    [BurstCompile]
    [UpdateInGroup(typeof(CombatStateUpdateSystemGroup))]
    [UpdateBefore(typeof(CombatMovementSystem))]
    public partial struct CombatFacingSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimulationClockRunning>();
        }

        [BurstCompile(CompileSynchronously = true)]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (facing, applied) in
                SystemAPI.Query<RefRW<CombatFacing>, RefRO<AppliedCombatCommand>>().WithAll<InCombat>())
            {
                var moveX = applied.ValueRO.Command.MoveX;
                if (moveX != 0)
                    facing.ValueRW.Value = moveX > 0 ? FacingDirection.Right : FacingDirection.Left;
            }
        }
    }
}
