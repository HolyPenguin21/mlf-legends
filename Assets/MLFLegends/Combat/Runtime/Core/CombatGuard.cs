using System;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace MLFLegends.Combat
{
    /// <summary>Prepared per-entry values. All deadlines use numerical movement time: tick T integrates [T-1,T].</summary>
    [Serializable]
    public struct CombatGuardParameters
    {
        [Tooltip("Spawn maximum stability, resource units; fixed for this fighter's lifetime.")]
        public float MaxStability;
        [Tooltip("Positive dimensionless divisor: normal cost = strike GuardDamage / Strength.")]
        public float Strength;
        [Tooltip("Inclusive frontal half cone in X/Z, degrees (0..90); normalized dot tolerance 1e-6.")]
        public float HalfAngleDegrees;
        [Tooltip("Resource units restored per simulation second in grounded free state.")]
        public float RegenPerSecond;
        [Tooltip("Controlled movement fraction in guard; external impulses and gravity are unchanged.")]
        public float MoveMultiplier;
        public float ExitMoveMultiplier, BrokenMoveMultiplier, BlockedImpulseMultiplier;
        [Tooltip("Fixed ticks at 60 Hz, after last positive guard pressure.")]
        public uint RegenDelayTicks;
        public uint ExitTicks, BreakTicks, TimelyTicks, AdvantageTicks;
        public float TimelyCostMultiplier;
        [HideInInspector] public int BlockTiming;

        public static CombatGuardParameters Default => new CombatGuardParameters {
            MaxStability = 100, Strength = 1, HalfAngleDegrees = 80, RegenPerSecond = 20,
            MoveMultiplier = .35f, ExitMoveMultiplier = 0, BrokenMoveMultiplier = 0, BlockedImpulseMultiplier = .25f,
            RegenDelayTicks = 30, ExitTicks = 6, BreakTicks = 24, TimelyTicks = 6, AdvantageTicks = 12, TimelyCostMultiplier = .25f,
        };
        public bool IsValid => Range(MaxStability, .001f, 1000000) && Range(Strength, .001f, 1000000)
            && Range(HalfAngleDegrees, 0, 90) && Range(RegenPerSecond, 0, 1000000)
            && Range(MoveMultiplier, 0, 1) && Range(ExitMoveMultiplier, 0, 1) && Range(BrokenMoveMultiplier, 0, 1)
            && Range(BlockedImpulseMultiplier, 0, 1) && Range(TimelyCostMultiplier, 0, 1)
            && RegenDelayTicks <= 360000 && ExitTicks <= 360000 && BreakTicks <= 360000
            && TimelyTicks <= 360000 && AdvantageTicks <= 360000;
        static bool Range(float x, float lo, float hi) => math.isfinite(x) && x >= lo && x <= hi;
    }

    public enum CombatGuardMode : byte { None, Held, Exit }
    public enum CombatGuardOutcome : byte { None, Block, Timely, Break }

    /// <summary>Action-owned intent/timeline. Facing stays in CombatFacing; no resource writes here.</summary>
    public struct CombatGuardState : IComponentData
    {
        public CombatGuardMode Mode;
        public uint StartTick;
        public double ExitUntil;
        public CombatGuardParameters Settings;
        public bool FreshEntry, RequireFreshPress, WaitRelease;
        public uint SeenBreak, ConsumedAdvantage;
        public bool Effective(in CombatStability result, double time, bool grounded) => grounded && Mode == CombatGuardMode.Held
            && time >= StartTick - 1d && SeenBreak == result.BreakSequence;
        public bool Locked(in CombatStability result, uint tick) => result.Broken(tick - 1d)
            || Mode == CombatGuardMode.Exit || (Mode == CombatGuardMode.Held && StartTick != tick);
    }

    /// <summary>Hit-owned resource/outcomes. No independent "guard enabled" flag.</summary>
    public struct CombatStability : IComponentData
    {
        public double Current, Max, LastPressure, BreakUntil, AdvantageUntil, LastTimelyTime;
        public uint LastRegenTick, BreakSequence, AdvantageSequence, TimelyEntryTick, LastOutcomeTick;
        public CombatGuardOutcome LastOutcome;
        public bool Broken(double time) => BreakSequence != 0 && time < BreakUntil;
        public bool Advantage(in CombatGuardState intent, double time) => AdvantageSequence != intent.ConsumedAdvantage
            && AdvantageSequence != 0 && time < AdvantageUntil && !Broken(time);
        public static CombatStability Spawn(float max) => new CombatStability { Current = max, Max = max, LastPressure = -1e20 };
    }

    /// <summary>Rules invoked by Hit's strict Burst jobs. Double resource arithmetic, stable contact order, no render clock.</summary>
    public static class CombatGuardResolution
    {
        public static bool Front(float3 targetToAttacker, float facing, float halfAngle)
        {
            var length = math.length(targetToAttacker.xz);
            return length > 1e-6f && targetToAttacker.x * facing / length + 1e-6f >= math.cos(math.radians(halfAngle));
        }
        public static CombatGuardOutcome Resolve(ref CombatStability resource, in CombatGuardState guard,
            bool grounded, float3 targetToAttacker, float facing, float guardDamage, double time, uint tick)
        {
            if (!guard.Effective(resource, time, grounded) || !Front(targetToAttacker, facing, guard.Settings.HalfAngleDegrees))
                return CombatGuardOutcome.None;
            var positive = guardDamage > 0;
            var timely = positive && guard.FreshEntry && resource.TimelyEntryTick != guard.StartTick
                && time >= guard.StartTick - 1d && time < guard.StartTick - 1d + guard.Settings.TimelyTicks;
            var cost = (double)guardDamage / guard.Settings.Strength * (timely ? guard.Settings.TimelyCostMultiplier : 1d);
            if (positive) resource.LastPressure = time;
            resource.Current = math.max(0d, resource.Current - cost);
            var outcome = CombatGuardOutcome.Block;
            if (positive && resource.Current <= 0)
            {
                outcome = CombatGuardOutcome.Break; resource.BreakSequence++;
                resource.BreakUntil = time + guard.Settings.BreakTicks; resource.AdvantageUntil = 0;
            }
            else if (timely)
            {
                outcome = CombatGuardOutcome.Timely; resource.TimelyEntryTick = guard.StartTick;
                resource.LastTimelyTime = time; resource.AdvantageUntil = time + guard.Settings.AdvantageTicks;
                resource.AdvantageSequence++;
            }
            // Keep the strongest feedback of a simultaneous batch (Break > Timely > Block).
            if (resource.LastOutcomeTick != tick || (byte)outcome > (byte)resource.LastOutcome)
                resource.LastOutcome = outcome;
            resource.LastOutcomeTick = tick;
            return outcome;
        }
        public static void Regenerate(ref CombatStability resource, in CombatGuardState guard, uint tick, bool free)
        {
            if (resource.LastRegenTick == tick) return;
            resource.LastRegenTick = tick;
            var now = tick - 1d;
            if (!free || guard.Mode != CombatGuardMode.None || resource.Broken(now)
                || now < resource.LastPressure + guard.Settings.RegenDelayTicks) return;
            resource.Current = math.min(resource.Max, resource.Current + (double)guard.Settings.RegenPerSecond / SimulationTiming.TickRateHz);
        }
    }
}
