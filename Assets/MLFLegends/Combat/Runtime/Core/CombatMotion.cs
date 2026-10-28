using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>Compact jump/locomotion state; CombatPosition remains the only authoritative position.</summary>
    /// <remarks>
    /// Initialised by BattleLifecycleSystem.Spawn, then written only by CombatMovementSystem.
    /// GroundHeight is the spawn Y, a temporary flat landing support until Stage 04 ground resolution
    /// extends the same position owner. Destroyed with the combatant on despawn/end/restart.
    /// The tick fields record what the movement owner actually did (absolute ticks, 0 = never in this battle);
    /// presentation selects run/jump/landing from them instead of keeping its own per-fighter history.
    /// </remarks>
    public struct CombatMotion : IComponentData
    {
        public float GroundHeight;
        public float VerticalVelocity;
        byte m_Grounded;

        /// <summary>Tick on which the fighter last touched down after a jump.</summary>
        public uint LandTick;

        /// <summary>Landing window copied from the published animation schedule at contact. Zero in worlds without a config.</summary>
        public uint LandingRecoveryTicks;

        /// <summary>The contact tick is the first landing tick; free on LandTick + LandingRecoveryTicks.</summary>
        public bool IsLandingRecovery(uint tick) => Grounded && LandTick != 0 && tick - LandTick < LandingRecoveryTicks;

        /// <summary>Last tick with a nonzero planar (X/Z) displacement.</summary>
        public uint LastMoveTick;

        /// <summary>
        /// First tick of the current planar movement streak: a stop shorter than
        /// <see cref="CombatMovementTuning.MoveStreakGraceTicks"/> continues the streak; a landing restarts it.
        /// </summary>
        public uint MoveStreakStartTick;

        /// <summary>Planar distance moved since <see cref="MoveStreakStartTick"/>, world units.</summary>
        public float MoveStreakDistance;

        // Byte storage follows Core's blittable component contract (no bool fields).
        public bool Grounded
        {
            get => m_Grounded != 0;
            set => m_Grounded = (byte)(value ? 1 : 0);
        }
    }
}
