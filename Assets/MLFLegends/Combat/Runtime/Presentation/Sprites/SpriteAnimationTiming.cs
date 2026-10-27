using System;
using System.Collections.Generic;
using UnityEngine;

namespace MLFLegends.Combat
{
    /// <summary>Immutable shared clip timing. Integrates unweighted cubic Hermite speed analytically at load;
    /// finds frame boundaries once by bisection. Runtime is modulo + binary search, no curve evaluation/allocation.</summary>
    public sealed class SpriteAnimationTiming
    {
        readonly int[] m_Frames;
        readonly double[] m_StartSeconds;
        public int FrameCount => m_Frames.Length;
        public double DurationSeconds { get; }
        public double FrameStartSeconds(int localFrame) => m_StartSeconds[localFrame];
        public double FrameDurationSeconds(int localFrame) =>
            (localFrame + 1 < FrameCount ? m_StartSeconds[localFrame + 1] : DurationSeconds) - m_StartSeconds[localFrame];

        SpriteAnimationTiming(int[] frames, double[] starts, double duration)
        {
            m_Frames = frames;
            m_StartSeconds = starts;
            DurationSeconds = duration;
        }

        public uint FrameAt(double elapsedSeconds)
        {
            if (!Finite(elapsedSeconds) || elapsedSeconds <= 0)
                return (uint)m_Frames[0];
            var phase = elapsedSeconds % DurationSeconds;
            var lo = 0;
            var hi = m_StartSeconds.Length;
            while (lo + 1 < hi)
            {
                var mid = (lo + hi) / 2;
                if (phase >= m_StartSeconds[mid]) lo = mid;
                else hi = mid;
            }
            return (uint)m_Frames[lo];
        }

        struct Segment
        {
            public double Start, Width, A, B, C, D, AreaBefore;
            public double Speed(double u) => ((A * u + B) * u + C) * u + D;
            public double Area(double u) => Width * u * (D + u * (C / 2 + u * (B / 3 + u * A / 4)));
        }

        static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);

        public static SpriteAnimationTiming Create(SpriteAtlasClip clip, CharacterAnimationSettings settings,
            string path, List<string> errors, List<string> warnings = null)
        {
            var before = errors.Count;
            if (clip == null || settings == null)
            {
                errors.Add(path + ": clip/settings missing");
                return null;
            }
            if (!clip.Loop || clip.HoldLast)
                errors.Add(path + ": idle playback requires loop=true and hold_last=false");
            var n = settings.FrameCount;
            var duration = settings.TotalDurationSeconds;
            if (n < 1 || n > clip.Frames.Count)
                errors.Add(path + $".FrameCount: {n}, available 1..{clip.Frames.Count}");
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
            if (Math.Abs((double)keys[0].value - keys[keys.Length - 1].value) >
                1e-5 * Math.Max(keys[0].value, keys[keys.Length - 1].value))
                warnings?.Add(path + ".SpeedCurve: speed differs at loop endpoints; the cycle has a speed discontinuity");
            var frames = new int[n];
            var starts = new double[n];
            for (var f = 0; f < n; f++)
            {
                frames[f] = clip.Frames[f];
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
            }
            return new SpriteAnimationTiming(frames, starts, duration);
        }
    }
}
