using System;
using System.Collections.Generic;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Immutable per-character presentation timing of every combat state: the eight Stage 05.1 clips, resolved once per
    /// config/atlas revision and shared by the whole roster. Selects one atlas frame per fighter from authoritative ECS state
    /// only (<see cref="CombatAttackState"/>, <see cref="CombatMotion"/>, the clock); it keeps no per-fighter history and
    /// never feeds anything back into the simulation.
    /// </summary>
    /// <remarks>
    /// Priority: strike → airborne jump → run → landing → idle. Loops (idle, run) use the config cycle (N/T/curve). Play-once
    /// clips are <em>fitted</em> to simulation windows: the config N/T/curve fixes each frame's share of the clip, and every
    /// segment of frames is stretched over its authoritative window, so a visual edit can never move a hit.
    /// <list type="bullet">
    /// <item>Strikes: frames before <c>strike_pose</c> span Windup, the <c>strike_pose</c> frames span exactly the Active
    /// ticks (when damage resolves), the rest span Recovery (<see cref="CombatAttackTuning"/>). The config T should equal
    /// the strike's total; a mismatch is reported (it cannot change timing).</item>
    /// <item>Jump: <c>rise</c> frames follow ascent progress (1 − vy/JumpImpulse), <c>fall</c> frames descent progress, both
    /// from the movement owner's vertical velocity, so the drawing never changes the trajectory and no landing pose is shown
    /// in the air. <c>land</c> frames play after touch-down for their config share of T, only if the fighter neither moved
    /// nor struck since landing. Height comes from CombatPosition only.</item>
    /// <item>Run: stride phase = streak distance / (MoveSpeed · T), i.e. T is the cycle at full speed and slower diagonal
    /// motion strides proportionally slower. A stop of at most MoveStreakGraceTicks keeps run (no idle flicker).</item>
    /// <item>Idle restarts at frame 0 when entered (end of strike, landing, run stop).</item>
    /// </list>
    /// </remarks>
    public sealed class CombatAnimationSet
    {
        public const string IdleClip = "idle_combat";
        public const string RunClip = "run";
        public const string JumpClip = "jump";

        /// <summary>Every clip the set needs; others (relaxed idle, walk, dash, cast) are neither required nor read.</summary>
        public static readonly string[] ClipIds =
        {
            IdleClip, RunClip, JumpClip, "punch_left", "punch_right", "kick_left", "kick_right", "kick_spin",
        };

        static readonly CombatAttackKind[] s_Attacks =
        {
            CombatAttackKind.PunchLeft, CombatAttackKind.PunchRight, CombatAttackKind.KickLeft, CombatAttackKind.KickRight,
            CombatAttackKind.KickSpin,
        };

        struct Segment
        {
            public int First;
            public int End; // exclusive
        }

        readonly SpriteAnimationTiming[] m_Attacks = new SpriteAnimationTiming[s_Attacks.Length + 1];
        readonly Segment[] m_Strikes = new Segment[s_Attacks.Length + 1];
        Segment m_Rise;
        Segment m_Fall;
        Segment m_Land;

        public SpriteAnimationTiming Idle { get; private set; }
        public SpriteAnimationTiming Run { get; private set; }
        public SpriteAnimationTiming Jump { get; private set; }

        /// <summary>Ticks the landing frames play after touch-down (config share of the jump clip, ≥ 1).</summary>
        public uint LandingTicks { get; private set; }

        CombatAnimationSet()
        {
        }

        public static string AttackClip(CombatAttackKind kind)
        {
            switch (kind)
            {
                case CombatAttackKind.PunchLeft: return "punch_left";
                case CombatAttackKind.PunchRight: return "punch_right";
                case CombatAttackKind.KickLeft: return "kick_left";
                case CombatAttackKind.KickRight: return "kick_right";
                case CombatAttackKind.KickSpin: return "kick_spin";
                default: return null;
            }
        }

        public SpriteAnimationTiming Attack(CombatAttackKind kind) => kind > 0 && (int)kind < m_Attacks.Length ? m_Attacks[(int)kind] : null;

        /// <summary>
        /// Builds the set from the atlas definition and the per-clip config settings (<paramref name="settingsFor"/> returns
        /// null when a clip has none). All-or-nothing: any missing/invalid clip, marker or count adds an error and returns null.
        /// </summary>
        public static CombatAnimationSet Create(SpriteAtlasDefinition definition, Func<string, CharacterAnimationSettings> settingsFor,
            string path, List<string> errors, List<string> warnings = null)
        {
            var before = errors.Count;
            var set = new CombatAnimationSet();
            set.Idle = Timing(definition, settingsFor, path, IdleClip, true, errors, warnings, out _);
            set.Run = Timing(definition, settingsFor, path, RunClip, true, errors, warnings, out _);
            set.Jump = Timing(definition, settingsFor, path, JumpClip, false, errors, warnings, out var jumpClip);
            if (set.Jump != null)
            {
                var jumpPath = path + "." + JumpClip;
                var n = set.Jump.FrameCount;
                var ok = TrySegment(jumpClip, "rise", n, jumpPath, errors, out set.m_Rise)
                    & TrySegment(jumpClip, "fall", n, jumpPath, errors, out set.m_Fall)
                    & TrySegment(jumpClip, "land", n, jumpPath, errors, out set.m_Land);
                if (ok && !(set.m_Rise.End <= set.m_Fall.First && set.m_Fall.End <= set.m_Land.First))
                    errors.Add(jumpPath + ".markers: rise, fall and land must be disjoint and in this order");
                else if (ok)
                {
                    var seconds = End(set.Jump, set.m_Land) - set.Jump.FrameStartSeconds(set.m_Land.First);
                    set.LandingTicks = (uint)Math.Max(1, Math.Round(seconds * SimulationTiming.TickRateHz));
                    for (var f = 0; f < n; f++)
                    {
                        if (!Covers(set.m_Rise, f) && !Covers(set.m_Fall, f) && !Covers(set.m_Land, f))
                            warnings?.Add(jumpPath + $": local frame {f} ({definition.Frames[(int)set.Jump.Frame(f)].Id}) is in no rise/fall/land marker and is never shown (ECS takeoff is immediate)");
                    }
                }
            }

            foreach (var kind in s_Attacks)
            {
                var clipId = AttackClip(kind);
                var clipPath = path + "." + clipId;
                var timing = Timing(definition, settingsFor, path, clipId, false, errors, warnings, out var clip);
                if (timing == null || !TrySegment(clip, "strike_pose", timing.FrameCount, clipPath, errors, out var strike))
                    continue;
                if (strike.First < 1 || strike.End >= timing.FrameCount)
                {
                    errors.Add(clipPath + $": strike_pose [{strike.First}..{strike.End - 1}] needs at least one windup frame before it and one recovery frame after it within FrameCount {timing.FrameCount}");
                    continue;
                }

                var total = CombatAttackTuning.Get(kind).TotalTicks / (double)SimulationTiming.TickRateHz;
                if (Math.Abs(timing.DurationSeconds - total) > 0.5 / SimulationTiming.TickRateHz)
                    warnings?.Add(clipPath + $".TotalDurationSeconds: {timing.DurationSeconds:0.####} s differs from the authoritative strike {total:0.####} s ({CombatAttackTuning.Get(kind).TotalTicks} ticks); frames are fitted to the combat windows, so T only sets frame shares");
                set.m_Attacks[(int)kind] = timing;
                set.m_Strikes[(int)kind] = strike;
            }

            return errors.Count == before ? set : null;
        }

        static SpriteAnimationTiming Timing(SpriteAtlasDefinition definition, Func<string, CharacterAnimationSettings> settingsFor,
            string path, string clipId, bool looping, List<string> errors, List<string> warnings, out SpriteAtlasClip clip)
        {
            clip = null;
            var clipPath = path + "." + clipId;
            if (!definition.TryGetClipIndex(clipId, out var index))
            {
                errors.Add(clipPath + ": clip missing from manifest");
                return null;
            }

            clip = definition.Clips[index];
            var settings = settingsFor(clipId);
            if (settings == null)
            {
                errors.Add(clipPath + ": animation settings missing");
                return null;
            }

            return SpriteAnimationTiming.Create(clip, settings, clipPath, errors, warnings, looping);
        }

        /// <summary>A marker as one ascending run of consecutive local frames inside the first <paramref name="frameCount"/>.</summary>
        static bool TrySegment(SpriteAtlasClip clip, string marker, int frameCount, string path, List<string> errors, out Segment segment)
        {
            segment = default;
            if (!clip.Markers.TryGetValue(marker, out var frames) || frames.Length == 0)
            {
                errors.Add(path + $".markers.{marker}: missing (required to fit the clip to its simulation state)");
                return false;
            }

            for (var i = 1; i < frames.Length; i++)
            {
                if (frames[i] != frames[i - 1] + 1)
                {
                    errors.Add(path + $".markers.{marker}: frames must be consecutive and ascending");
                    return false;
                }
            }

            if (frames[frames.Length - 1] >= frameCount)
            {
                errors.Add(path + $".markers.{marker}: frame {frames[frames.Length - 1]} is outside FrameCount {frameCount}");
                return false;
            }

            segment = new Segment { First = frames[0], End = frames[frames.Length - 1] + 1 };
            return true;
        }

        static bool Covers(Segment segment, int frame) => frame >= segment.First && frame < segment.End;

        static double End(SpriteAnimationTiming timing, Segment segment) =>
            segment.End < timing.FrameCount ? timing.FrameStartSeconds(segment.End) : timing.DurationSeconds;

        /// <summary>Frame of <paramref name="segment"/> at <paramref name="progress"/> (0..1) of its window, by config shares.</summary>
        static uint Fit(SpriteAnimationTiming timing, Segment segment, double progress)
        {
            progress = Math.Max(0, Math.Min(1, progress));
            var start = timing.FrameStartSeconds(segment.First);
            var local = timing.LocalFrameAt(start + (End(timing, segment) - start) * progress);
            return timing.Frame(Math.Max(segment.First, Math.Min(segment.End - 1, local)));
        }

        static double Seconds(long ticks) => ticks / (double)SimulationTiming.TickRateHz;

        /// <summary>Atlas frame of a fighter without motion/attack state (manual worlds): idle from the battle's first tick.</summary>
        public uint IdleFrame(uint tick, uint battleStartTick) => Idle.FrameAt(Seconds((long)tick - battleStartTick - 1));

        /// <summary>Atlas frame of a fighter on <paramref name="tick"/> (the last completed tick).</summary>
        public uint FrameFor(uint tick, uint battleStartTick, in CombatMotion motion, in CombatAttackState attack)
        {
            if (attack.IsAttacking)
            {
                var timing = m_Attacks[(int)attack.Kind];
                var strike = CombatAttackTuning.Get(attack.Kind);
                var segment = m_Strikes[(int)attack.Kind];
                var elapsed = (double)(tick - attack.StartTick);
                if (elapsed < strike.WindupTicks)
                    return Fit(timing, new Segment { First = 0, End = segment.First }, elapsed / strike.WindupTicks);
                elapsed -= strike.WindupTicks;
                if (elapsed < strike.ActiveTicks)
                    return Fit(timing, segment, elapsed / strike.ActiveTicks);
                elapsed -= strike.ActiveTicks;
                return Fit(timing, new Segment { First = segment.End, End = timing.FrameCount }, elapsed / strike.RecoveryTicks);
            }

            if (!motion.Grounded)
            {
                var v = motion.VerticalVelocity / CombatMovementTuning.JumpImpulse;
                return v > 0f ? Fit(Jump, m_Rise, 1 - v) : Fit(Jump, m_Fall, -v);
            }

            const uint grace = CombatMovementTuning.MoveStreakGraceTicks;
            if (motion.MoveStreakStartTick != SimulationClock.NoTick && tick - motion.LastMoveTick <= grace)
                return Run.FrameAt(motion.MoveStreakDistance / CombatMovementTuning.MoveSpeed);

            var landing = motion.LandTick != SimulationClock.NoTick && motion.LastMoveTick < motion.LandTick
                && attack.EndTick <= motion.LandTick;
            if (landing && tick - motion.LandTick < LandingTicks)
                return Fit(Jump, m_Land, (tick - motion.LandTick) / (double)LandingTicks);

            long idleSince = (long)battleStartTick + 1;
            idleSince = Math.Max(idleSince, attack.EndTick);
            if (motion.LastMoveTick != SimulationClock.NoTick)
                idleSince = Math.Max(idleSince, (long)motion.LastMoveTick + grace + 1);
            if (landing)
                idleSince = Math.Max(idleSince, (long)motion.LandTick + LandingTicks);
            return Idle.FrameAt(Seconds(tick - idleSince));
        }
    }
}
