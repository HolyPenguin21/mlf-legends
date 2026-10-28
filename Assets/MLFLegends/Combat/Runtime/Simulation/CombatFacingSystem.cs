using Unity.Burst;
using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>The sole fixed-tick owner of CombatFacing after spawn: faces the sign of the accepted MoveX.</summary>
    /// <remarks>
    /// MoveX +1 faces Right, -1 faces Left, 0 (idle, Z-only, implicit None) keeps the previous facing; airborne
    /// actors turn too. Runs after CombatActionSystem and before CombatMovementSystem, so every later State owner sees
    /// this tick's facing. Strike lock (Stage 05.1): while a strike runs the actor keeps its facing, except on the first
    /// tick of each strike (series start or chained follow-up), where that tick's MoveX may still turn it before the hit
    /// box is evaluated. Actors without CombatAttackState are never locked.
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
            var tick = SystemAPI.TryGetSingleton<SimulationClock>(out var clock) ? clock.Tick : SimulationClock.NoTick;
            var attacks = SystemAPI.GetComponentLookup<CombatAttackState>(true);
            var motions = SystemAPI.GetComponentLookup<CombatMotion>(true);
            foreach (var (facing, applied, entity) in
                SystemAPI.Query<RefRW<CombatFacing>, RefRO<AppliedCombatCommand>>().WithAll<InCombat>().WithEntityAccess())
            {
                if (motions.TryGetComponent(entity, out var motion) && motion.IsLandingRecovery(tick))
                    continue;
                if (attacks.TryGetComponent(entity, out var attack) && attack.IsAttacking && attack.StartTick != tick)
                    continue;
                var moveX = applied.ValueRO.Command.MoveX;
                if (moveX != 0)
                    facing.ValueRW.Value = moveX > 0 ? FacingDirection.Right : FacingDirection.Left;
            }
        }
    }
}
