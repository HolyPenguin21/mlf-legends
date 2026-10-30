using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Semantic strike slots (#69). Values are stable: they appear in replays and logs. Explicit migration of the Stage 05.1
    /// ids: 1 PunchLeft → Hand1, 2 PunchRight → Hand2, 3 KickLeft → Foot1, 4 KickRight → Foot2, 5 KickSpin → PhysicalAbility
    /// (physical_ability in the v2 art, kick_spin in v1). Which clip a slot plays is per-character data.
    /// </summary>
    public enum CombatAttackKind : byte
    {
        None = 0,
        Hand1 = 1,
        Hand2 = 2,
        Foot1 = 3,
        Foot2 = 4,
        PhysicalAbility = 5,
    }

    /// <summary>
    /// Graphic/movement phase of a strike. Hit eligibility independently reads the frozen damage interval; an explicit
    /// override may end before graphic Active ends or continue into Recovery.
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

        /// <summary>One series intention (hand or foot) accepted during Active/Recovery; resolved only at execution.</summary>
        public CommandButtons Buffered;

        /// <summary>
        /// Bounded position of the current strike in its series (#69): 0 first, 1 second, then 2 (slot-2 strike) and 3 (slot-1
        /// strike) alternate, so the sequence is 1, 1, 2, 1, 2, … for any length; 0 when free or for the physical ability.
        /// </summary>
        public byte SeriesStep;

        /// <summary>First (windup) tick of the current strike.</summary>
        public uint StartTick;

        /// <summary>Phase boundaries of the current strike, fixed at its start; default when free.</summary>
        public CombatStrikeTiming Timing;

        /// <summary>
        /// Damage and hit box of the current strike, copied from the World's table with <see cref="Timing"/> when it starts
        /// (#69): Action, Movement and Hit read one version of this execution, an Inspector edit acts on the next strike.
        /// </summary>
        public CombatStrikeParameters Strike;

        /// <summary>First free tick after the last series ended; 0 = no series ended in this battle yet.</summary>
        public uint EndTick;

        public bool IsAttacking => Kind != CombatAttackKind.None;
    }

    /// <summary>
    /// Per-character combat values of one strike slot (#69): damage and the point-target hit box along the facing. Edited in
    /// the character's config entry, published in <see cref="CombatStrikeSchedule"/>, frozen per execution in
    /// <see cref="CombatAttackState.Strike"/>. The target point is its accepted body waist; body radius never widens the box.
    /// </summary>
    public struct CombatStrikeParameters
    {
        /// <summary>HP removed from each valid target, once per strike.</summary>
        public int Damage;
        public float GuardDamage;

        public int SwingEffect, ImpactEffect; // immutable World resource handles, 0 = disabled

        /// <summary>Hit box along the attacker's facing, world units from its position (forward distance, inclusive).</summary>
        public float ReachMin;
        public float ReachMax;

        /// <summary>Largest |ΔZ| (depth) between attacker and target, world units.</summary>
        public float DepthHalfWidth;

        /// <summary>Largest |ΔY| (altitude) between attacker and target, world units.</summary>
        public float HeightTolerance;

        // Frozen with this execution. J is world-units * mass / second, elevation is above horizontal.
        public float HitOffsetY;
        public float SelfImpulse, ElevationDegrees;
        public ushort LaunchTick;
        public float CompactHeight; // 0 = keep the spawn height
        public ushort CompactStart, CompactEnd; // [start,end), sampled at the tick boundary
        public Unity.Mathematics.float3 OnHitImpulse; // local forward X, up Y, outward Z

    }

    /// <summary>Minimal Hit-owner feedback fact. 0 = no positive-damage confirmation in this battle.</summary>
    public struct CombatDamageFeedback : IComponentData
    {
        public uint LastDamageTick;
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
