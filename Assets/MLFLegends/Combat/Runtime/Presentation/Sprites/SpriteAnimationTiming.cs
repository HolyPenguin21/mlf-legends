using System;
using System.Collections.Generic;
using UnityEngine;

namespace MLFLegends.Combat
{
    /// <summary>Immutable shared clip timing of the configured range StartFrame..EndFrame. Integrates unweighted cubic
    /// Hermite speed analytically at load; finds frame boundaries once by bisection. Runtime is modulo + binary search,
    /// no curve evaluation/allocation. Local frame <c>f</c> here is clip frame <c>StartFrame + f</c>.</summary>
    /// <remarks>A strike clip (<c>phases: true</c>) also carries its validated Preparation / Active / Recovery frame ranges
    /// and their start times, from which <see cref="TryStrikeTiming"/> derives the simulation windows.</remarks>
    public sealed class SpriteAnimationTiming
    {
        readonly int[] m_Frames;
        readonly double[] m_StartSeconds;
        public int FrameCount => m_Frames.Length;
        public double DurationSeconds { get; }

        /// <summary>True for a looping cycle (idle, run); false for a play-once clip fitted to state windows.</summary>
        public bool Looping { get; }

        /// <summary>True when created with phases (strike clips): the three phase ranges below are valid.</summary>
        public bool HasPhases { get; }

        /// <summary>Last Preparation local frame (Preparation = 0..this).</summary>
        public int PreparationEnd { get; }

        /// <summary>Last Active local frame (Active = PreparationEnd + 1..this; Recovery = this + 1..FrameCount - 1).</summary>
        public int ActiveEnd { get; }

        /// <summary>Clip time at which Active begins (start of local frame PreparationEnd + 1).</summary>
        public double ActiveStartSeconds => m_StartSeconds[PreparationEnd + 1];

        /// <summary>Clip time at which Recovery begins (start of local frame ActiveEnd + 1).</summary>
        public double RecoveryStartSeconds => m_StartSeconds[ActiveEnd + 1];

        /// <summary>Atlas frame index of local frame <paramref name="localFrame"/>.</summary>
        public uint Frame(int localFrame) => (uint)m_Frames[localFrame];
        public double FrameStartSeconds(int localFrame) => m_StartSeconds[localFrame];
        public double FrameDurationSeconds(int localFrame) =>
            (localFrame + 1 < FrameCount ? m_StartSeconds[localFrame + 1] : DurationSeconds) - m_StartSeconds[localFrame];

        SpriteAnimationTiming(int[] frames, double[] starts, double duration, bool looping, bool phases, int preparationEnd, int activeEnd)
        {
            m_Frames = frames;
            m_StartSeconds = starts;
            DurationSeconds = duration;
            Looping = looping;
            HasPhases = phases;
            PreparationEnd = preparationEnd;
            ActiveEnd = activeEnd;
        }

        /// <summary>
        /// Simulation windows of this strike clip: its Active/Recovery start times and duration quantized to fixed ticks
        /// (<see cref="CombatStrikeTiming.TryFromSeconds"/>). False without phases or when quantization fails.
        /// </summary>
        public bool TryStrikeTiming(byte chainDelay, out CombatStrikeTiming timing, out string error)
        {
            timing = default;
            error = null;
            if (!HasPhases)
            {
                error = "no Preparation/Active/Recovery phases";
                return false;
            }

            return CombatStrikeTiming.TryFromSeconds(ActiveStartSeconds, RecoveryStartSeconds, DurationSeconds, chainDelay, out timing, out error);
        }

        /// <summary>Atlas frame at <paramref name="elapsedSeconds"/> of the repeating cycle.</summary>
        public uint FrameAt(double elapsedSeconds)
        {
            if (!Finite(elapsedSeconds) || elapsedSeconds <= 0)
                return (uint)m_Frames[0];
            return (uint)m_Frames[LocalFrameAt(elapsedSeconds % DurationSeconds)];
        }

        /// <summary>Local frame shown at <paramref name="seconds"/> of one pass, clamped to the clip (no wrap).</summary>
        public int LocalFrameAt(double seconds)
        {
            if (!Finite(seconds) || seconds <= 0)
                return 0;
            var phase = seconds;
            var lo = 0;
            var hi = m_StartSeconds.Length;
            while (lo + 1 < hi)
            {
                var mid = (lo + hi) / 2;
                if (phase >= m_StartSeconds[mid]) lo = mid;
                else hi = mid;
            }
            return lo;
        }

        struct Segment
        {
            public double Start, Width, A, B, C, D, AreaBefore;
            public double Speed(double u) => ((A * u + B) * u + C) * u + D;
            public double Area(double u) => Width * u * (D + u * (C / 2 + u * (B / 3 + u * A / 4)));
        }

        static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);

        /// <param name="looping">True for a cycle (needs loop=true, hold_last=false); false for a play-once clip
        /// (needs loop=false) whose frames are fitted to simulation windows by <see cref="CombatAnimationSet"/>.</param>
        /// <param name="phases">True for a strike clip: validates and keeps its Preparation / Active / Recovery ranges.</param>
        public static SpriteAnimationTiming Create(SpriteAtlasClip clip, CharacterAnimationSettings settings,
            string path, List<string> errors, List<string> warnings = null, bool looping = true, bool phases = false)
        {
            var before = errors.Count;
            if (clip == null || settings == null)
            {
                errors.Add(path + ": clip/settings missing");
                return null;
            }
            if (looping && (!clip.Loop || clip.HoldLast))
                errors.Add(path + ": looping playback requires loop=true and hold_last=false");
            if (!looping && clip.Loop)
                errors.Add(path + ": state-fitted playback requires loop=false");
            var first = settings.StartFrame;
            var n = settings.FrameCount;
            var duration = settings.TotalDurationSeconds;
            if (settings.EndFrame < first || settings.EndFrame >= clip.Frames.Count)
                errors.Add(path + $".EndFrame: {settings.EndFrame} is outside the clip's local frames {first}..{clip.Frames.Count - 1} " +
                    "(0-based clip indices; was the atlas clip shortened?)");
            else if (phases)
            {
                var range = $"StartFrame {first}..EndFrame {settings.EndFrame}";
                if (n < 3)
                    errors.Add(path + $": a strike needs at least 3 frames (Preparation, Active, Recovery), {range} has {n}");
                else if (settings.PreparationEndFrame < first || settings.PreparationEndFrame >= settings.EndFrame)
                    errors.Add(path + $".PreparationEndFrame: {settings.PreparationEndFrame}; Preparation {first}..{settings.PreparationEndFrame} must hold at least one frame inside {range}");
                else if (settings.ActiveEndFrame < settings.ActiveStartFrame)
                    errors.Add(path + $".ActiveEndFrame: {settings.ActiveEndFrame}; Active {settings.ActiveStartFrame}..{settings.ActiveEndFrame} must hold at least one frame");
                else if (settings.ActiveEndFrame >= settings.EndFrame)
                    errors.Add(path + $".ActiveEndFrame: {settings.ActiveEndFrame}; Recovery {settings.RecoveryStartFrame}..{settings.EndFrame} must hold at least one frame (EndFrame {settings.EndFrame})");
            }
            // Bound the domain so 80 binary subdivision steps and double arithmetic resolve <0.1ms.
            if (!Finite(duration) || duration <= 0 || duration > 86400)
                errors.Add(path + ".TotalDurationSeconds: must be finite, >0 and <=86400 (one day)");
            var keys = settings.SpeedCurve?.keys;
            if (keys == null || keys.Length < 2 || keys[0].time != 0 || keys[keys.Length - 1].time != 1)
            {
                errors.Add(path + ".SpeedCurve: needs at least two keys, first at 0 and last at 1");
                return null;
            }
            for (var i = 0; i < keys.Length; i++)
            {
                var k = keys[i];
                if (!Finite(k.time) || !Finite(k.value) || !Finite(k.inTangent) || !Finite(k.outTangent) ||
                    k.value < 0 || k.weightedMode != WeightedMode.None || (i > 0 && k.time <= keys[i - 1].time))
                    errors.Add(path + $".SpeedCurve.keys[{i}]: require finite ordered keys, nonnegative speed and unweighted finite tangents (no Constant/step tangents)");
            }
            if (errors.Count != before)
                return null;
            // Relative speed has no absolute amplitude. Normalize before polynomial arithmetic so tiny/huge
            // but equivalent curves have the same validation and cannot underflow the integral.
            double amplitude = 0;
            foreach (var key in keys)
                amplitude = Math.Max(amplitude, Math.Max(key.value, Math.Max(Math.Abs((double)key.inTangent), Math.Abs((double)key.outTangent))));
            if (amplitude == 0)
            {
                errors.Add(path + ".SpeedCurve: integrated speed must be >0");
                return null;
            }
            var segments = new Segment[keys.Length - 1];
            double total = 0;
            for (var i = 0; i < segments.Length; i++)
            {
                var left = keys[i];
                var right = keys[i + 1];
                double width = (double)right.time - left.time;
                double a = left.value / amplitude, b = right.value / amplitude,
                    m0 = width * (left.outTangent / amplitude), m1 = width * (right.inTangent / amplitude);
                var s = new Segment { Start = left.time, Width = width, A = 2 * a - 2 * b + m0 + m1,
                    B = -3 * a + 3 * b - 2 * m0 - m1, C = m0, D = a, AreaBefore = total };
                // Use a LOCAL coefficient scale: other segments or unused endpoint tangents must not
                // hide a negative overshoot in a small-amplitude segment.
                var scale = Math.Max(Math.Max(Math.Abs(s.A), Math.Abs(s.B)), Math.Max(Math.Abs(s.C), Math.Abs(s.D)));
                var q = s;
                if (scale > 0) { q.A /= scale; q.B /= scale; q.C /= scale; q.D /= scale; }
                var minimum = Math.Min(q.Speed(0), q.Speed(1));
                if (q.A == 0)
                {
                    if (q.B != 0)
                    {
                        var root = -q.C / (2 * q.B);
                        if (root > 0 && root < 1) minimum = Math.Min(minimum, q.Speed(root));
                    }
                }
                else
                {
                    var discriminant = 4 * q.B * q.B - 12 * q.A * q.C;
                    if (discriminant >= 0)
                    {
                        var root = Math.Sqrt(discriminant);
                        var x = (-2 * q.B - root) / (6 * q.A);
                        var y = (-2 * q.B + root) / (6 * q.A);
                        if (x > 0 && x < 1) minimum = Math.Min(minimum, q.Speed(x));
                        if (y > 0 && y < 1) minimum = Math.Min(minimum, q.Speed(y));
                    }
                }
                if (minimum < -1e-14)
                    errors.Add(path + $".SpeedCurve: negative speed between keys {i} and {i + 1}");
                total += s.Area(1);
                segments[i] = s;
            }
            if (!Finite(total) || total <= 0)
                errors.Add(path + ".SpeedCurve: integrated speed must be finite and >0");
            if (errors.Count != before)
                return null;
            if (looping && Math.Abs((double)keys[0].value - keys[keys.Length - 1].value) >
                1e-5 * Math.Max(keys[0].value, keys[keys.Length - 1].value))
                warnings?.Add(path + ".SpeedCurve: speed differs at loop endpoints; the cycle has a speed discontinuity");
            var frames = new int[n];
            var starts = new double[n];
            for (var f = 0; f < n; f++)
            {
                frames[f] = clip.Frames[first + f];
                if (f == 0) continue;
                var target = total * ((double)f / n);
                // Lower-bound integral: a zero-speed plateau on a frame boundary belongs to the next frame.
                var lo = 0d;
                var hi = 1d;
                for (var step = 0; step < 80; step++)
                {
                    var middle = (lo + hi) * 0.5;
                    double area = total;
                    for (var j = 0; j < segments.Length; j++)
                    {
                        var s = segments[j];
                        if (middle > s.Start + s.Width) continue;
                        area = s.AreaBefore + s.Area(Math.Max(0, Math.Min(1, (middle - s.Start) / s.Width)));
                        break;
                    }
                    if (area >= target) hi = middle;
                    else lo = middle;
                }
                starts[f] = hi * duration;
                // Mathematically impossible for a valid curve (frame areas are equal and >0); guards numeric collapse,
                // which would make frame order and phase boundaries ambiguous.
                if (!(starts[f] > starts[f - 1]))
                    errors.Add(path + $".SpeedCurve: local frame {f - 1} has zero duration; frames and phases would be ambiguous");
            }
            if (errors.Count != before)
                return null;
            return new SpriteAnimationTiming(frames, starts, duration, looping, phases,
                phases ? settings.PreparationEndFrame - first : 0, phases ? settings.ActiveEndFrame - first : 0);
        }
    }
}
