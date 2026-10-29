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

        byte m_PreciseActiveWindow;
        public bool PreciseActiveWindow { get => m_PreciseActiveWindow != 0; set => m_PreciseActiveWindow = value ? (byte)1 : (byte)0; }
        public double ActiveStartTicks, RecoveryStartTicks;

        public void ActiveWindow(uint elapsed, out float lo, out float hi)
        {
            lo = PreciseActiveWindow ? (float)Math.Max(0, ActiveStartTicks - elapsed) : 0;
            hi = PreciseActiveWindow ? (float)Math.Min(1, RecoveryStartTicks - elapsed) : 1;
        }

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
            timing.PreciseActiveWindow = true;
            timing.ActiveStartTicks = activeStart * hz;
            timing.RecoveryStartTicks = recoveryStart * hz;
            return true;
        }

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>
    /// The World's character combat table (the one publication table, #69): per strike slot the windows and the damage/hit
    /// box every newly started strike copies, landing recovery copied at contact and the fighters' body used at spawn. Starts
    /// with <see cref="Default"/> (<see cref="CombatAttackTuning"/> windows/values and <see cref="CombatBodyTuning"/>, used by
    /// worlds that publish nothing: tests and benchmarks); a scene publishes the table of its character: windows from the
    /// slot clips' animation phases (frames, duration, curve), damage/reach/body from the character's combat settings.
    /// </summary>
    /// <remarks>
    /// Singleton created/destroyed by CombatActionSystem; read there for strike starts, by CombatMovementSystem at landing
    /// and by BattleLifecycleSystem at spawn. Written only through <see cref="Publish"/> on the main thread outside fixed
    /// steps (e.g. CombatSmokeBootstrap), so a change takes effect from the next strike start (timing AND damage/reach); a
    /// strike already running keeps what it started with; a new body applies to fighters spawned by the next start/restart.
    /// The whole roster uses one character type (one table per World).
    /// </remarks>
    public struct CombatStrikeSchedule : IComponentData
    {
        public CombatStrikeTiming Hand1;
        public CombatStrikeTiming Hand2;
        public CombatStrikeTiming Foot1;
        public CombatStrikeTiming Foot2;
        public CombatStrikeTiming PhysicalAbility;

        public CombatStrikeParameters Hand1Strike;
        public CombatStrikeParameters Hand2Strike;
        public CombatStrikeParameters Foot1Strike;
        public CombatStrikeParameters Foot2Strike;
        public CombatStrikeParameters PhysicalAbilityStrike;

        /// <summary>Body of every fighter spawned while this table is current (vertical cylinder from CombatPosition).</summary>
        public CombatBody Body;

        /// <summary>Jump landing frames' duration quantized to ticks. 0 = no animation config published, no artificial delay.</summary>
        public uint LandingRecoveryTicks;

        /// <summary>Successful <see cref="Publish"/> calls in this World; 0 = still the default table.</summary>
        public uint Revision;

        public static CombatStrikeSchedule Default => new CombatStrikeSchedule
        {
            Hand1 = CombatAttackTuning.Get(CombatAttackKind.Hand1).DefaultTiming,
            Hand2 = CombatAttackTuning.Get(CombatAttackKind.Hand2).DefaultTiming,
            Foot1 = CombatAttackTuning.Get(CombatAttackKind.Foot1).DefaultTiming,
            Foot2 = CombatAttackTuning.Get(CombatAttackKind.Foot2).DefaultTiming,
            PhysicalAbility = CombatAttackTuning.Get(CombatAttackKind.PhysicalAbility).DefaultTiming,
            Hand1Strike = CombatAttackTuning.Get(CombatAttackKind.Hand1).Parameters,
            Hand2Strike = CombatAttackTuning.Get(CombatAttackKind.Hand2).Parameters,
            Foot1Strike = CombatAttackTuning.Get(CombatAttackKind.Foot1).Parameters,
            Foot2Strike = CombatAttackTuning.Get(CombatAttackKind.Foot2).Parameters,
            PhysicalAbilityStrike = CombatAttackTuning.Get(CombatAttackKind.PhysicalAbility).Parameters,
            Body = CombatBodyTuning.Default,
        };

        public CombatStrikeParameters GetStrike(CombatAttackKind kind)
        {
            switch (kind)
            {
                case CombatAttackKind.Hand1: return Hand1Strike;
                case CombatAttackKind.Hand2: return Hand2Strike;
                case CombatAttackKind.Foot1: return Foot1Strike;
                case CombatAttackKind.Foot2: return Foot2Strike;
                case CombatAttackKind.PhysicalAbility: return PhysicalAbilityStrike;
                default: return default;
            }
        }

        public void SetStrike(CombatAttackKind kind, in CombatStrikeParameters strike)
        {
            switch (kind)
            {
                case CombatAttackKind.Hand1: Hand1Strike = strike; break;
                case CombatAttackKind.Hand2: Hand2Strike = strike; break;
                case CombatAttackKind.Foot1: Foot1Strike = strike; break;
                case CombatAttackKind.Foot2: Foot2Strike = strike; break;
                case CombatAttackKind.PhysicalAbility: PhysicalAbilityStrike = strike; break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        public CombatStrikeTiming Get(CombatAttackKind kind)
        {
            switch (kind)
            {
                case CombatAttackKind.Hand1: return Hand1;
                case CombatAttackKind.Hand2: return Hand2;
                case CombatAttackKind.Foot1: return Foot1;
                case CombatAttackKind.Foot2: return Foot2;
                case CombatAttackKind.PhysicalAbility: return PhysicalAbility;
                default: return default;
            }
        }

        public void Set(CombatAttackKind kind, in CombatStrikeTiming timing)
        {
            switch (kind)
            {
                case CombatAttackKind.Hand1: Hand1 = timing; break;
                case CombatAttackKind.Hand2: Hand2 = timing; break;
                case CombatAttackKind.Foot1: Foot1 = timing; break;
                case CombatAttackKind.Foot2: Foot2 = timing; break;
                case CombatAttackKind.PhysicalAbility: PhysicalAbility = timing; break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        /// <summary>
        /// Every strike has a valid timing whose chain point matches its series rule (finisher = no chain) and supported
        /// damage/hit box values; the body is inside <see cref="CombatCharacterLimits"/>.
        /// </summary>
        public bool IsValid
        {
            get
            {
                for (var kind = CombatAttackKind.Hand1; kind <= CombatAttackKind.PhysicalAbility; kind++)
                {
                    var timing = Get(kind);
                    if (!timing.IsValid || timing.CanChain != CombatAttackTuning.Get(kind).CanChain
                        || !CombatCharacterLimits.IsValid(GetStrike(kind))
                        || (GetStrike(kind).SelfImpulse > 0 && GetStrike(kind).LaunchTick >= timing.End)
                        || GetStrike(kind).SelfImpulse / Body.Mass > CombatImpulseTuning.MaxExternalSpeed
                        || Unity.Mathematics.math.length(GetStrike(kind).OnHitImpulse) / Body.Mass > CombatImpulseTuning.MaxExternalSpeed)
                        return false;
                }

                return CombatCharacterLimits.IsValid(Body);
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
