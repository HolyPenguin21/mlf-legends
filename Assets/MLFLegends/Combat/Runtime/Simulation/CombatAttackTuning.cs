namespace MLFLegends.Combat
{
    /// <summary>
    /// Reach, damage, series rule and default timing of one strike. Ticks are fixed simulation ticks (<see cref="SimulationTiming"/>).
    /// </summary>
    /// <remarks>
    /// The Windup/Active/Recovery ticks are only the <see cref="CombatStrikeSchedule.Default"/> windows of a World that publishes
    /// no table (tests, benchmarks). A scene publishes the windows derived from the animation phases; a running strike reads
    /// its own <see cref="CombatAttackState.Timing"/>, never these fields.
    /// </remarks>
    public struct CombatAttackDefinition
    {
        public byte WindupTicks;
        public byte ActiveTicks;
        public byte RecoveryTicks;

        /// <summary>
        /// Recovery ticks that always play before a buffered follow-up may start the next strike of the series;
        /// <see cref="CombatAttackTuning.NoChain"/> for a finisher.
        /// </summary>
        public byte ChainDelayTicks;

        /// <summary>HP removed from each valid target, once per strike.</summary>
        public int Damage;

        /// <summary>Hit box along the attacker's facing, world units from its position (forward distance, inclusive).</summary>
        public float ReachMin;
        public float ReachMax;

        /// <summary>Largest |ΔZ| (depth) between attacker and target, world units.</summary>
        public float DepthHalfWidth;

        /// <summary>Largest |ΔY| (altitude) between attacker and target, world units.</summary>
        public float HeightTolerance;

        public int TotalTicks => WindupTicks + ActiveTicks + RecoveryTicks;

        public bool CanChain => ChainDelayTicks != CombatAttackTuning.NoChain;

        /// <summary>The default windows as a strike timing (chain point = Windup + Active + ChainDelay).</summary>
        public CombatStrikeTiming DefaultTiming => CombatStrikeTiming.FromTicks(WindupTicks, ActiveTicks, RecoveryTicks, ChainDelayTicks);
    }

    /// <summary>
    /// Provisional Stage 05.1 strike data and series rules: reach, damage, chain delays, series length and the default
    /// windows of worlds without a published <see cref="CombatStrikeSchedule"/>. In a scene the windows come from the
    /// strike clips' Preparation/Active/Recovery frames (CharacterAnimationConfig). Not approved balance values.
    /// </summary>
    /// <remarks>
    /// Default windows follow the delivered art's frame proportions (punch 29, kick 33, spin 45 ticks at 60 Hz). Reach
    /// matches the drawn fist/foot (2 world units per 128 px nominal height).
    /// </remarks>
    public static class CombatAttackTuning
    {
        public const byte NoChain = byte.MaxValue;

        /// <summary>Hit points of every spawned combatant.</summary>
        public const int MaxHealth = 100;

        /// <summary>Strikes in one series at most; the last one always plays its full recovery.</summary>
        public const int MaxSeriesLength = 4;

        public static CombatAttackDefinition Get(CombatAttackKind kind)
        {
            switch (kind)
            {
                case CombatAttackKind.PunchLeft:
                    return Strike(13, 5, 11, 4, 4, 1.0f);
                case CombatAttackKind.PunchRight:
                    return Strike(9, 9, 11, 4, 4, 1.0f);
                case CombatAttackKind.KickLeft:
                case CombatAttackKind.KickRight:
                    return Strike(11, 5, 17, 6, 7, 1.3f);
                case CombatAttackKind.KickSpin:
                    return Strike(19, 6, 20, NoChain, 12, 1.45f);
                default:
                    return default;
            }
        }

        static CombatAttackDefinition Strike(byte windup, byte active, byte recovery, byte chainDelay, int damage, float reach) =>
            new CombatAttackDefinition
            {
                WindupTicks = windup, ActiveTicks = active, RecoveryTicks = recovery, ChainDelayTicks = chainDelay,
                Damage = damage, ReachMin = 0f, ReachMax = reach, DepthHalfWidth = 0.4f, HeightTolerance = 0.9f,
            };

        /// <summary>Phase of the default windows on the tick <paramref name="elapsed"/> ticks after the strike's first tick; None once it ended.</summary>
        public static CombatAttackPhase PhaseAt(in CombatAttackDefinition strike, uint elapsed) => strike.DefaultTiming.PhaseAt(elapsed);

        /// <summary>One deterministic intention for Player/AI: exact hand L/R, foot L/R/spin, then general hand/foot.</summary>
        public static CommandButtons StrikeButton(CommandButtons pressed) =>
            (pressed & CommandButtons.PunchLeft) != 0 ? CommandButtons.PunchLeft
            : (pressed & CommandButtons.PunchRight) != 0 ? CommandButtons.PunchRight
            : (pressed & CommandButtons.KickLeft) != 0 ? CommandButtons.KickLeft
            : (pressed & CommandButtons.KickRight) != 0 ? CommandButtons.KickRight
            : (pressed & CommandButtons.KickSpin) != 0 ? CommandButtons.KickSpin
            : (pressed & CommandButtons.Attack) != 0 ? CommandButtons.Attack
            : (pressed & CommandButtons.Kick) != 0 ? CommandButtons.Kick
            : CommandButtons.None;

        /// <summary>
        /// The strike <paramref name="button"/> performs after <paramref name="previous"/> (None = first of a series): the
        /// first strike is left, each follow-up uses the other side, and a Kick after <see cref="CombatAttackKind.KickRight"/>
        /// is the <see cref="CombatAttackKind.KickSpin"/> finisher.
        /// </summary>
        public static CombatAttackKind Select(CommandButtons button, CombatAttackKind previous)
        {
            // Exact requests survive buffering unchanged, including repeated right-side strikes and a free-state spin.
            switch (button)
            {
                case CommandButtons.PunchLeft: return CombatAttackKind.PunchLeft;
                case CommandButtons.PunchRight: return CombatAttackKind.PunchRight;
                case CommandButtons.KickLeft: return CombatAttackKind.KickLeft;
                case CommandButtons.KickRight: return CombatAttackKind.KickRight;
                case CommandButtons.KickSpin: return CombatAttackKind.KickSpin;
            }
            var left = previous != CombatAttackKind.PunchLeft && previous != CombatAttackKind.KickLeft;
            if (button == CommandButtons.Attack)
                return left ? CombatAttackKind.PunchLeft : CombatAttackKind.PunchRight;
            if (button != CommandButtons.Kick)
                return CombatAttackKind.None;
            if (previous == CombatAttackKind.KickRight)
                return CombatAttackKind.KickSpin;
            return left ? CombatAttackKind.KickLeft : CombatAttackKind.KickRight;
        }
    }
}
