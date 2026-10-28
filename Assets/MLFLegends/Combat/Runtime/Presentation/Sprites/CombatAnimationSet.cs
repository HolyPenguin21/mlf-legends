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
    /// Priority: strike → airborne jump → landing recovery → run → idle. Loops use the config cycle (range/T/curve).
    /// <list type="bullet">
    /// <item>Strikes: the config splits the clip range into Preparation / Active / Recovery frames; with T and the curve that
    /// gives the phase times, which <see cref="TryCreateStrikeSchedule"/> quantizes into the simulation windows
    /// (<see cref="CombatStrikeSchedule"/>, published by the scene). Drawing reads the window phase fixed in the fighter's
    /// <see cref="CombatAttackState.Timing"/> and shows the frame at the strike's elapsed time, kept inside that phase's
    /// frames: the drawn phase always equals the simulated one, and a drawn frame never causes a hit.</item>
    /// <item>Jump: <c>rise</c> frames follow ascent progress (1 − vy/JumpImpulse), <c>fall</c> frames descent progress, both
    /// from the movement owner's vertical velocity, so the drawing never changes the trajectory and no landing pose is shown
    /// in the air. Landing frames play inside the movement owner's frozen recovery window. Height comes from CombatPosition only.</item>
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
        Segment m_Rise;
        Segment m_Fall;
        Segment m_Land;

        public SpriteAnimationTiming Idle { get; private set; }
        public SpriteAnimationTiming Run { get; private set; }
        public SpriteAnimationTiming Jump { get; private set; }

        /// <summary>Ticks the landing frames play after touch-down (config share of the jump clip, ≥ 1).</summary>
        public uint LandingTicks { get; private set; }
        public int LandingStartFrame => m_Land.First;
        public int LandingEndFrame => m_Land.End - 1;

        /// <summary>Simulation windows derived from this set's strike clips (the table a scene publishes for them).</summary>
        public CombatStrikeSchedule StrikeSchedule { get; private set; }

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

        /// <summary>True for the five strike clips: only they have Preparation / Active / Recovery phases.</summary>
        public static bool IsAttackClip(string clipId)
        {
            foreach (var kind in s_Attacks)
            {
                if (AttackClip(kind) == clipId)
                    return true;
            }

            return false;
        }

        public SpriteAnimationTiming Attack(CombatAttackKind kind) => kind > 0 && (int)kind < m_Attacks.Length ? m_Attacks[(int)kind] : null;

        /// <summary>
        /// Builds the set from the atlas definition and the per-clip config settings (<paramref name="settingsFor"/> returns
        /// null when a clip has none). All-or-nothing: any missing/invalid clip, phase, marker or range adds an error and returns null.
        /// </summary>
        public static CombatAnimationSet Create(SpriteAtlasDefinition definition, Func<string, CharacterAnimationSettings> settingsFor,
            string path, List<string> errors, List<string> warnings = null)
        {
            var before = errors.Count;
            var set = new CombatAnimationSet();
            set.Idle = Timing(definition, settingsFor, path, IdleClip, true, false, errors, warnings, out _);
            set.Run = Timing(definition, settingsFor, path, RunClip, true, false, errors, warnings, out _);
            set.Jump = Timing(definition, settingsFor, path, JumpClip, false, false, errors, warnings, out var jumpClip);
            if (set.Jump != null)
            {
                var jumpPath = path + "." + JumpClip;
                var n = set.Jump.FrameCount;
                var firstLanding = settingsFor(JumpClip).LandingStartFrame;
                var ok = TrySegment(jumpClip, "rise", n, jumpPath, errors, out set.m_Rise)
                    & TrySegment(jumpClip, "fall", n, jumpPath, errors, out set.m_Fall);
                if (firstLanding == -1)
                    ok &= TrySegment(jumpClip, "land", n, jumpPath, errors, out set.m_Land);
                else if (firstLanding < 0 || firstLanding >= n)
                {
                    errors.Add(jumpPath + ".LandingStartFrame: must be -1 (manifest land marker) or inside StartFrame..EndFrame");
                    ok = false;
                }
                else
                    set.m_Land = new Segment { First = firstLanding, End = n };
                if (ok && !(set.m_Rise.End <= set.m_Fall.First && set.m_Fall.End <= set.m_Land.First))
                    errors.Add(jumpPath + ".markers: rise, fall and land must be disjoint and in this order");
                else if (ok)
                {
                    var seconds = End(set.Jump, set.m_Land) - set.Jump.FrameStartSeconds(set.m_Land.First);
                    // Round up to a complete tick, never finish recovery before the configured frames have played.
                    set.LandingTicks = (uint)Math.Max(1, Math.Ceiling(seconds * SimulationTiming.TickRateHz - 1e-9));
                    for (var f = 0; f < n; f++)
                    {
                        if (!Covers(set.m_Rise, f) && !Covers(set.m_Fall, f) && !Covers(set.m_Land, f))
                            warnings?.Add(jumpPath + $": local frame {f} ({definition.Frames[(int)set.Jump.Frame(f)].Id}) is in no rise/fall/land marker and is never shown (ECS takeoff is immediate)");
                    }
                }
            }

            if (TryStrikes(definition, settingsFor, path, errors, warnings, set.m_Attacks, out var schedule))
            {
                schedule.LandingRecoveryTicks = set.LandingTicks;
                set.StrikeSchedule = schedule;
            }
            return errors.Count == before ? set : null;
        }

        /// <summary>
        /// Simulation windows of the five strikes from their clip phases, duration and curve only (idle/run/jump are not
        /// needed). The same derivation <see cref="Create"/> uses, so the published table and the drawn phases agree.
        /// </summary>
        public static bool TryCreateStrikeSchedule(SpriteAtlasDefinition definition, Func<string, CharacterAnimationSettings> settingsFor,
            string path, List<string> errors, List<string> warnings, out CombatStrikeSchedule schedule) =>
            TryStrikes(definition, settingsFor, path, errors, warnings, new SpriteAnimationTiming[s_Attacks.Length + 1], out schedule);

        static bool TryStrikes(SpriteAtlasDefinition definition, Func<string, CharacterAnimationSettings> settingsFor, string path,
            List<string> errors, List<string> warnings, SpriteAnimationTiming[] timings, out CombatStrikeSchedule schedule)
        {
            var before = errors.Count;
            schedule = default;
            foreach (var kind in s_Attacks)
            {
                var clipId = AttackClip(kind);
                var clipPath = path + "." + clipId;
                var timing = Timing(definition, settingsFor, path, clipId, false, true, errors, warnings, out var clip);
                if (timing == null)
                    continue;
                if (!timing.TryStrikeTiming(CombatAttackTuning.Get(kind).ChainDelayTicks, out var strike, out var error))
                {
                    errors.Add(clipPath + ": " + error);
                    continue;
                }

                const double hz = SimulationTiming.TickRateHz;
                if (strike.WindupTicks == 0)
                    warnings?.Add(clipPath + $": Preparation lasts {timing.ActiveStartSeconds:0.####} s, less than one {hz} Hz tick: hits can resolve on the strike's first tick and no Preparation tick is simulated or drawn");
                if (strike.RecoveryTicks == 0)
                    warnings?.Add(clipPath + $": Recovery lasts {timing.DurationSeconds - timing.RecoveryStartSeconds:0.####} s and ends inside the last Active tick: no Recovery tick is simulated or drawn");
                if (clip.Markers.TryGetValue("strike_pose", out var pose) && pose.Length > 0
                    && (pose[0] != timing.PreparationEnd + 1 || pose[pose.Length - 1] != timing.ActiveEnd))
                    warnings?.Add(clipPath + $": Active frames {timing.PreparationEnd + 1}..{timing.ActiveEnd} differ from the manifest's strike_pose {pose[0]}..{pose[pose.Length - 1]} (the config decides)");
                timings[(int)kind] = timing;
                schedule.Set(kind, strike);
            }

            return errors.Count == before;
        }

        static SpriteAnimationTiming Timing(SpriteAtlasDefinition definition, Func<string, CharacterAnimationSettings> settingsFor,
            string path, string clipId, bool looping, bool phases, List<string> errors, List<string> warnings, out SpriteAtlasClip clip)
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

            return SpriteAnimationTiming.Create(clip, settings, clipPath, errors, warnings, looping, phases);
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
                errors.Add(path + $".markers.{marker}: frame {frames[frames.Length - 1]} is outside EndFrame {frameCount - 1}");
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

        /// <summary>
        /// Local strike frame on the tick <paramref name="elapsed"/> ticks after the strike's first tick: the frame at clip time
        /// <c>elapsed / TickRateHz</c>, kept inside the frames of the phase <paramref name="strike"/> has on that tick.
        /// </summary>
        public static int StrikeLocalFrame(SpriteAnimationTiming timing, in CombatStrikeTiming strike, uint elapsed)
        {
            var local = timing.LocalFrameAt(Seconds(elapsed));
            switch (strike.PhaseAt(elapsed))
            {
                case CombatAttackPhase.Windup:
                    return Math.Min(local, timing.PreparationEnd);
                case CombatAttackPhase.Active:
                    return Math.Max(timing.PreparationEnd + 1, Math.Min(local, timing.ActiveEnd));
                default:
                    return Math.Max(timing.ActiveEnd + 1, local);
            }
        }

        /// <summary>Atlas frame of a fighter without motion/attack state (manual worlds): idle from the battle's first tick.</summary>
        public uint IdleFrame(uint tick, uint battleStartTick) => Idle.FrameAt(Seconds((long)tick - battleStartTick - 1));

        /// <summary>Atlas frame of a fighter on <paramref name="tick"/> (the last completed tick).</summary>
        public uint FrameFor(uint tick, uint battleStartTick, in CombatMotion motion, in CombatAttackState attack)
        {
            if (attack.IsAttacking)
            {
                var timing = m_Attacks[(int)attack.Kind];
                return timing.Frame(StrikeLocalFrame(timing, attack.Timing, tick - attack.StartTick));
            }

            if (!motion.Grounded)
            {
                var v = motion.VerticalVelocity / CombatMovementTuning.JumpImpulse;
                return v > 0f ? Fit(Jump, m_Rise, 1 - v) : Fit(Jump, m_Fall, -v);
            }

            if (motion.IsLandingRecovery(tick))
                return Fit(Jump, m_Land, (tick - motion.LandTick) / (double)motion.LandingRecoveryTicks);

            const uint grace = CombatMovementTuning.MoveStreakGraceTicks;
            if (motion.MoveStreakStartTick != SimulationClock.NoTick && tick - motion.LastMoveTick <= grace)
                return Run.FrameAt(motion.MoveStreakDistance / CombatMovementTuning.MoveSpeed);

            var landing = motion.LandTick != SimulationClock.NoTick && motion.LastMoveTick < motion.LandTick
                && attack.EndTick <= motion.LandTick;
            long idleSince = (long)battleStartTick + 1;
            idleSince = Math.Max(idleSince, attack.EndTick);
            if (motion.LastMoveTick != SimulationClock.NoTick && motion.LastMoveTick >= motion.LandTick)
                idleSince = Math.Max(idleSince, (long)motion.LastMoveTick + grace + 1);
            if (landing)
                idleSince = Math.Max(idleSince, (long)motion.LandTick + motion.LandingRecoveryTicks);
            return Idle.FrameAt(Seconds(tick - idleSince));
        }
    }
}
