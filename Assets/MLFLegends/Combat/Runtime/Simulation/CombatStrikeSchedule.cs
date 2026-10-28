using System;
using Unity.Collections;
using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Phase boundaries of one strike execution, in ticks elapsed since its first (Preparation/Windup) tick:
    /// Windup <c>[0, ActiveStart)</c> → Active <c>[ActiveStart, RecoveryStart)</c> → Recovery <c>[RecoveryStart, End)</c>.
    /// Copied into <see cref="CombatAttackState.Timing"/> when the strike starts and fixed for its whole execution.
    /// </summary>
    public struct CombatStrikeTiming
    {
        /// <summary><see cref="ChainPoint"/> of a finisher: a buffered press never starts a follow-up.</summary>
        public const ushort NoChain = ushort.MaxValue;

        /// <summary>Largest <see cref="End"/>: about 18 minutes at 60 Hz, far beyond any strike.</summary>
        public const ushort MaxTicks = ushort.MaxValue - 1;

        /// <summary>First Active tick (hits resolve); 0 when the clip's preparation is shorter than one tick.</summary>
        public ushort ActiveStart;

        /// <summary>First Recovery tick; always greater than <see cref="ActiveStart"/>, so Active has at least one tick.</summary>
        public ushort RecoveryStart;

        /// <summary>First tick after the strike: the fighter is free on it unless a follow-up starts.</summary>
        public ushort End;

        /// <summary>First tick on which a buffered press starts the next strike of the series; <see cref="NoChain"/> for a finisher.</summary>
        public ushort ChainPoint;

        public bool CanChain => ChainPoint != NoChain;

        public int WindupTicks => ActiveStart;
        public int ActiveTicks => RecoveryStart - ActiveStart;
        public int RecoveryTicks => End - RecoveryStart;

        public bool IsValid => ActiveStart < RecoveryStart && RecoveryStart <= End && End <= MaxTicks
            && (ChainPoint == NoChain || (ChainPoint >= RecoveryStart && ChainPoint <= End));

        /// <summary>Phase on the tick <paramref name="elapsed"/> ticks after the strike's first tick; None once it ended.</summary>
        public CombatAttackPhase PhaseAt(uint elapsed)
        {
            if (elapsed < ActiveStart)
                return CombatAttackPhase.Windup;
            if (elapsed < RecoveryStart)
                return CombatAttackPhase.Active;
            return elapsed < End ? CombatAttackPhase.Recovery : CombatAttackPhase.None;
        }

        /// <summary>
        /// Timing from tick counts. <paramref name="chainDelay"/> Recovery ticks play before a buffered follow-up may start
        /// (<see cref="CombatAttackTuning.NoChain"/> for a finisher); a delay longer than Recovery chains on the first tick
        /// after it, so a short Recovery never removes the series.
        /// </summary>
        public static CombatStrikeTiming FromTicks(int windup, int active, int recovery, byte chainDelay)
        {
            var timing = new CombatStrikeTiming
            {
                ActiveStart = (ushort)windup, RecoveryStart = (ushort)(windup + active), End = (ushort)(windup + active + recovery),
            };
            timing.ChainPoint = chainDelay == CombatAttackTuning.NoChain
                ? NoChain
                : (ushort)Math.Min(timing.RecoveryStart + chainDelay, timing.End);
            return timing;
        }

        /// <summary>
        /// Quantizes phase boundaries given in seconds of the clip (Active <c>[activeStart, recoveryStart)</c>, clip length
        /// <paramref name="duration"/>) to fixed ticks. Tick <c>e</c> simulates the clip interval <c>[e, e + 1) / TickRateHz</c>;
        /// a tick is Active when its interval overlaps the Active interval, so an Active phase shorter than a tick, or one
        /// that crosses several boundaries inside one tick, still gets at least one Active tick and is never skipped.
        /// Windup or Recovery shorter than a tick can have zero ticks (reported by the caller as a warning).
        /// </summary>
        public static bool TryFromSeconds(double activeStart, double recoveryStart, double duration, byte chainDelay,
            out CombatStrikeTiming timing, out string error)
        {
            // Absorbs double rounding of boundaries that are exact tick multiples (e.g. 29/60 s * 60 = 28.999999999999996).
            const double epsilon = 1e-6;
            timing = default;
            error = null;
            if (!Finite(activeStart) || !Finite(recoveryStart) || !Finite(duration)
                || activeStart < 0 || recoveryStart <= activeStart || duration <= recoveryStart)
            {
                error = $"phase boundaries must satisfy 0 <= active start ({activeStart}) < recovery start ({recoveryStart}) < duration ({duration})";
                return false;
            }

            const double hz = SimulationTiming.TickRateHz;
            if (duration * hz > MaxTicks)
            {
                error = $"strike lasts {duration} s, more than {MaxTicks} ticks";
                return false;
            }

            var active = (int)Math.Floor(activeStart * hz + epsilon);
            var recovery = Math.Max(active + 1, (int)Math.Ceiling(recoveryStart * hz - epsilon));
            var end = Math.Max(recovery, (int)Math.Ceiling(duration * hz - epsilon));
            timing = FromTicks(active, recovery - active, end - recovery, chainDelay);
            return true;
        }

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>
    /// The World's combat timing table: strike windows every newly started strike copies and landing recovery copied at contact. Starts with
    /// <see cref="Default"/> (<see cref="CombatAttackTuning"/> windows, used by worlds that publish nothing: tests and
    /// benchmarks); a scene publishes the table derived from the character's animation phases (frames, duration, curve).
    /// </summary>
    /// <remarks>
    /// Singleton created/destroyed by CombatActionSystem; read there for strike starts and by CombatMovementSystem at landing.
    /// Written only through <see cref="Publish"/> on the main thread outside fixed steps (e.g. CombatSmokeBootstrap), so a
    /// change takes effect from the next strike start; a strike already running keeps the timing it started with.
    /// </remarks>
    public struct CombatStrikeSchedule : IComponentData
    {
        public CombatStrikeTiming PunchLeft;
        public CombatStrikeTiming PunchRight;
        public CombatStrikeTiming KickLeft;
        public CombatStrikeTiming KickRight;
        public CombatStrikeTiming KickSpin;

        /// <summary>Jump landing frames' duration quantized to ticks. 0 = no animation config published, no artificial delay.</summary>
        public uint LandingRecoveryTicks;

        /// <summary>Successful <see cref="Publish"/> calls in this World; 0 = still the default table.</summary>
        public uint Revision;

        public static CombatStrikeSchedule Default => new CombatStrikeSchedule
        {
            PunchLeft = CombatAttackTuning.Get(CombatAttackKind.PunchLeft).DefaultTiming,
            PunchRight = CombatAttackTuning.Get(CombatAttackKind.PunchRight).DefaultTiming,
            KickLeft = CombatAttackTuning.Get(CombatAttackKind.KickLeft).DefaultTiming,
            KickRight = CombatAttackTuning.Get(CombatAttackKind.KickRight).DefaultTiming,
            KickSpin = CombatAttackTuning.Get(CombatAttackKind.KickSpin).DefaultTiming,
        };

        public CombatStrikeTiming Get(CombatAttackKind kind)
        {
            switch (kind)
            {
                case CombatAttackKind.PunchLeft: return PunchLeft;
                case CombatAttackKind.PunchRight: return PunchRight;
                case CombatAttackKind.KickLeft: return KickLeft;
                case CombatAttackKind.KickRight: return KickRight;
                case CombatAttackKind.KickSpin: return KickSpin;
                default: return default;
            }
        }

        public void Set(CombatAttackKind kind, in CombatStrikeTiming timing)
        {
            switch (kind)
            {
                case CombatAttackKind.PunchLeft: PunchLeft = timing; break;
                case CombatAttackKind.PunchRight: PunchRight = timing; break;
                case CombatAttackKind.KickLeft: KickLeft = timing; break;
                case CombatAttackKind.KickRight: KickRight = timing; break;
                case CombatAttackKind.KickSpin: KickSpin = timing; break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        /// <summary>Every strike has a valid timing whose chain point matches its series rule (finisher = no chain).</summary>
        public bool IsValid
        {
            get
            {
                for (var kind = CombatAttackKind.PunchLeft; kind <= CombatAttackKind.KickSpin; kind++)
                {
                    var timing = Get(kind);
                    if (!timing.IsValid || timing.CanChain != CombatAttackTuning.Get(kind).CanChain)
                        return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Replaces the World's table (main thread, outside a fixed step). Strikes that already started keep their timing;
        /// the next strike start uses <paramref name="schedule"/>. Returns the new <see cref="Revision"/>.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="schedule"/> is not <see cref="IsValid"/>.</exception>
        /// <exception cref="InvalidOperationException">The World has no CombatActionSystem (no table).</exception>
        public static uint Publish(EntityManager entityManager, CombatStrikeSchedule schedule)
        {
            if (!schedule.IsValid)
                throw new ArgumentException("CombatStrikeSchedule.Publish: invalid strike timing.", nameof(schedule));
            // A query built from an EntityManager is Persistent until the World is disposed: released before any throw.
            var builder = new EntityQueryBuilder(Allocator.Temp);
            var query = builder.WithAllRW<CombatStrikeSchedule>().Build(entityManager);
            builder.Dispose();
            var entity = query.CalculateEntityCount() == 1 ? query.GetSingletonEntity() : Entity.Null;
            query.Dispose();
            if (entity == Entity.Null)
                throw new InvalidOperationException("CombatStrikeSchedule.Publish requires exactly one table (CombatActionSystem).");
            schedule.Revision = entityManager.GetComponentData<CombatStrikeSchedule>(entity).Revision + 1;
            entityManager.SetComponentData(entity, schedule);
            return schedule.Revision;
        }
    }
}
