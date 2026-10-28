using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>The melee strikes of Stage 05.1. Values are stable: they appear in replays and logs.</summary>
    public enum CombatAttackKind : byte
    {
        None = 0,
        PunchLeft = 1,
        PunchRight = 2,
        KickLeft = 3,
        KickRight = 4,
        KickSpin = 5,
    }

    /// <summary>
    /// Phase of a strike on one fixed tick: Windup (Preparation: no hits, planar movement allowed) → Active (hits resolve) →
    /// Recovery (return to stance, no hits).
    /// </summary>
    public enum CombatAttackPhase : byte
    {
        None = 0,
        Windup = 1,
        Active = 2,
        Recovery = 3,
    }

    /// <summary>
    /// Authoritative strike state of a combatant: the current strike of a series, its fixed phase timing, the one buffered
    /// follow-up press and when the last series ended. The phase of a tick is <c>Timing.PhaseAt(tick - StartTick)</c>; the
    /// timing is copied from the World's <see cref="CombatStrikeSchedule"/> when the strike starts and never changes while it
    /// runs, so neither a config edit nor the drawn frame can move a hit.
    /// </summary>
    /// <remarks>
    /// Initialised (free) by BattleLifecycleSystem.Spawn, afterwards written only by CombatActionSystem on the fixed tick.
    /// Facing, movement and hit resolution read it. Destroyed with the combatant on despawn/end/restart, so a pending
    /// follow-up never survives into another battle.
    /// </remarks>
    public struct CombatAttackState : IComponentData
    {
        /// <summary>Current strike; <see cref="CombatAttackKind.None"/> when the fighter is free.</summary>
        public CombatAttackKind Kind;

        /// <summary>One exact OR general strike intention accepted during Active/Recovery; resolved only at execution.</summary>
        public CommandButtons Buffered;

        /// <summary>Strikes in the current series, the current one included; 0 when free.</summary>
        public byte SeriesLength;

        /// <summary>First (windup) tick of the current strike.</summary>
        public uint StartTick;

        /// <summary>Phase boundaries of the current strike, fixed at its start; default when free.</summary>
        public CombatStrikeTiming Timing;

        /// <summary>First free tick after the last series ended; 0 = no series ended in this battle yet.</summary>
        public uint EndTick;

        public bool IsAttacking => Kind != CombatAttackKind.None;
    }

    /// <summary>Hit points. Written only by CombatHitSystem after spawn; 0 does not defeat yet (later stage).</summary>
    public struct CombatHealth : IComponentData
    {
        public int Current;
        public int Max;
    }

    /// <summary>
    /// Hit ledger of the attacker's current strike: every target already hit by it. Cleared by CombatActionSystem when a
    /// strike starts, appended only by CombatHitSystem; a target is damaged at most once per strike.
    /// </summary>
    [InternalBufferCapacity(4)]
    public struct CombatHitRecord : IBufferElementData
    {
        public CombatantId Target;
    }
}
