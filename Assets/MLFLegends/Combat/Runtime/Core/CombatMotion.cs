using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>Compact jump state; CombatPosition remains the only authoritative position.</summary>
    /// <remarks>
    /// Initialised by BattleLifecycleSystem.Spawn, then written only by CombatMovementSystem.
    /// GroundHeight is the spawn Y, a temporary flat landing support until Stage 04 ground resolution
    /// extends the same position owner. Destroyed with the combatant on despawn/end/restart.
    /// </remarks>
    public struct CombatMotion : IComponentData
    {
        public float GroundHeight;
        public float VerticalVelocity;
        byte m_Grounded;

        // Byte storage follows Core's blittable component contract (no bool fields).
        public bool Grounded
        {
            get => m_Grounded != 0;
            set => m_Grounded = (byte)(value ? 1 : 0);
        }
    }
}
