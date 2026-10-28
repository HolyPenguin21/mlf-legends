namespace MLFLegends.Combat
{
    /// <summary>Provisional shared movement tunables for Stage 02.1; not approved balance values.</summary>
    public static class CombatMovementTuning
    {
        /// <summary>Horizontal world units per simulated second, including while airborne.</summary>
        public const float MoveSpeed = 4f;

        // Applied once to planar displacement, from CombatAttackState.Timing's authoritative phase.
        public const float PreparationMoveMultiplier = 0.5f;
        public const float ActiveMoveMultiplier = 0.25f;
        public const float RecoveryMoveMultiplier = 0.25f;

        public static float AttackMoveMultiplier(CombatAttackPhase phase)
        {
            switch (phase)
            {
                case CombatAttackPhase.Windup: return PreparationMoveMultiplier;
                case CombatAttackPhase.Active: return ActiveMoveMultiplier;
                case CombatAttackPhase.Recovery: return RecoveryMoveMultiplier;
                default: return 1f;
            }
        }

        /// <summary>Upward world units per simulated second assigned by a grounded Jump Pressed.</summary>
        public const float JumpImpulse = 8f;

        /// <summary>Downward acceleration, world units per simulated second squared.</summary>
        public const float Gravity = 20f;

        /// <summary>
        /// Ticks without planar movement that still continue a movement streak (Stage 05.1): run keeps its stride phase
        /// across a direction change or a short release instead of flickering to idle.
        /// </summary>
        public const uint MoveStreakGraceTicks = 6;
    }
}
