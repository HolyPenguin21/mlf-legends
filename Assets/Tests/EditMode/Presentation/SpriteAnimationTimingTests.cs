using System;
using System.Collections.Generic;
using MLFLegends.Combat;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MLFLegends.Tests.EditMode
{
    public class SpriteAnimationTimingTests
    {
        static SpriteAtlasClip Clip(int count = 6, bool loop = true)
        {
            var frames = new int[count];
            var durations = new float[count];
            for (var i = 0; i < count; i++) { frames[i] = i; durations[i] = i % 2 == 0 ? 62.5f : 999f; }
            return new SpriteAtlasClip("idle_combat", frames, durations, loop, false, new Dictionary<string, int[]>());
        }

        static SpriteAnimationTiming Timing(AnimationCurve curve, int n = 6, double duration = 1.2)
        {
            var errors = new List<string>();
            var timing = SpriteAnimationTiming.Create(Clip(), new CharacterAnimationSettings
                { FrameCount = n, TotalDurationSeconds = duration, SpeedCurve = curve }, "test/idle_combat", errors);
            Assert.That(timing, Is.Not.Null, string.Join("\n", errors));
            return timing;
        }

        [Test]
        public void UniformCurveUsesConfiguredDurationAndFrameCountWithExactHalfOpenBoundaries()
        {
            var timing = Timing(AnimationCurve.Linear(0, 1, 1, 1));
            for (var i = 0; i < 6; i++)
            {
                Assert.That(timing.FrameStartSeconds(i), Is.EqualTo(i * 0.2).Within(1e-12));
                Assert.That(timing.FrameDurationSeconds(i), Is.EqualTo(0.2).Within(1e-12));
                Assert.That(timing.FrameAt(timing.FrameStartSeconds(i)), Is.EqualTo((uint)i));
                if (i > 0) Assert.That(timing.FrameAt(timing.FrameStartSeconds(i) - 1e-8), Is.EqualTo((uint)(i - 1)));
            }
            Assert.That(timing.FrameAt(1.2), Is.EqualTo(0u));
            Assert.That(timing.FrameAt(123456 * 1.2 + 0.41), Is.EqualTo(2u));
            Assert.That(timing.FrameAt(0.71), Is.EqualTo(timing.FrameAt(0.71)), "No accumulating clock.");
            var truncated = Timing(AnimationCurve.Linear(0, 1, 1, 1), 2, 10);
            Assert.That(truncated.FrameAt(4.99), Is.EqualTo(0u));
            Assert.That(truncated.FrameAt(5), Is.EqualTo(1u));
            Assert.That(truncated.FrameAt(10), Is.EqualTo(0u));
        }

        [Test]
        public void IntegratedLinearSpeedHasSquareRootBoundariesIndependentOfAmplitude()
        {
            var a = Timing(AnimationCurve.Linear(0, 0, 1, 2));
            var b = Timing(AnimationCurve.Linear(0, 0, 1, 0.000000000000002f));
            for (var i = 1; i < 6; i++)
            {
                var expected = 1.2 * Math.Sqrt(i / 6d);
                Assert.That(a.FrameStartSeconds(i), Is.EqualTo(expected).Within(1e-10));
                Assert.That(b.FrameStartSeconds(i), Is.EqualTo(expected).Within(1e-7));
            }
        }

        [Test]
        public void ZeroSpeedPlateauHoldsProgressWithoutChangingCycleLength()
        {
            var curve = new AnimationCurve(new Keyframe(0, 1, 0, 0), new Keyframe(0.25f, 0, 0, 0),
                new Keyframe(0.75f, 0, 0, 0), new Keyframe(1, 1, 0, 0));
            var timing = Timing(curve, 2, 2);
            // Exactly half the integrated speed is reached at the START of the plateau.
            Assert.That(timing.FrameStartSeconds(1), Is.EqualTo(0.5).Within(1e-5));
            Assert.That(timing.FrameAt(1), Is.EqualTo(1u));
            Assert.That(timing.FrameAt(2), Is.EqualTo(0u));
        }

        [TestCase(1f)]
        [TestCase(1e-15f)]
        [TestCase(1e15f)]
        public void NegativeTangentOvershootIsRejectedAtEverySpeedAmplitude(float amplitude)
        {
            var errors = new List<string>();
            var curve = new AnimationCurve(new Keyframe(0, amplitude, 0, -100 * amplitude), new Keyframe(1, amplitude, -100 * amplitude, 0));
            var timing = SpriteAnimationTiming.Create(Clip(), new CharacterAnimationSettings
                { FrameCount = 6, TotalDurationSeconds = 1.2, SpeedCurve = curve }, "mage/idle", errors);
            Assert.That(timing, Is.Null);
            Assert.That(string.Join("\n", errors), Does.Contain("negative speed between keys"));
        }

        [Test]
        public void UnusedEndpointTangentsCannotHideNegativeOvershoot()
        {
            var settings = new CharacterAnimationSettings { FrameCount = 6, SpeedCurve = new AnimationCurve(
                new Keyframe(0, 1, 1e38f, -10), new Keyframe(1, 1, -10, 1e38f)) };
            AssertRejected(settings, "negative speed between keys");
        }

        [TestCase(1f)]
        [TestCase(1e-20f)]
        public void EndpointSpeedDiscontinuityWarningIsRelativeToCurveAmplitude(float amplitude)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var timing = SpriteAnimationTiming.Create(Clip(), new CharacterAnimationSettings
                { FrameCount = 6, SpeedCurve = AnimationCurve.Linear(0, amplitude, 1, 2 * amplitude) }, "loop", errors, warnings);
            Assert.That(timing, Is.Not.Null, string.Join("\n", errors));
            Assert.That(warnings.Count, Is.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("discontinuity"));
        }

        [Test]
        public void InvalidCountDurationCurveAndNonLoopClipAreRejected()
        {
            var valid = new CharacterAnimationSettings { FrameCount = 6 };
            foreach (var n in new[] { 0, -1, 7 })
            {
                valid.FrameCount = n;
                AssertRejected(valid, "FrameCount");
            }
            valid.FrameCount = 6;
            foreach (var t in new[] { 0d, -1, double.NaN, double.PositiveInfinity, 86401 })
            {
                valid.TotalDurationSeconds = t;
                AssertRejected(valid, "TotalDurationSeconds");
            }
            valid.TotalDurationSeconds = 1;
            valid.SpeedCurve = AnimationCurve.Linear(0, 0, 1, 0);
            AssertRejected(valid, "integrated speed");
            valid.SpeedCurve = AnimationCurve.Linear(0.1f, 1, 1, 1);
            AssertRejected(valid, "first at 0");
            var weighted = new Keyframe(0, 1) { weightedMode = WeightedMode.Out };
            valid.SpeedCurve = new AnimationCurve(weighted, new Keyframe(1, 1));
            AssertRejected(valid, "unweighted");
            valid.SpeedCurve = new AnimationCurve(new Keyframe(0, 1, 0, float.PositiveInfinity), new Keyframe(1, 1));
            AssertRejected(valid, "finite");
            valid.SpeedCurve = AnimationCurve.Linear(0, 1, 1, 1);
            var errors = new List<string>();
            Assert.That(SpriteAnimationTiming.Create(Clip(loop: false), valid, "idle", errors), Is.Null);
            Assert.That(string.Join("\n", errors), Does.Contain("loop=true"));
        }

        static void AssertRejected(CharacterAnimationSettings settings, string diagnostic)
        {
            var errors = new List<string>();
            Assert.That(SpriteAnimationTiming.Create(Clip(), settings, "character/idle", errors), Is.Null);
            Assert.That(string.Join("\n", errors), Does.Contain(diagnostic));
        }

        [Test]
        public void ConfigValidatesSelectedIdsWithoutUnrelatedFailuresAndCopiesRuntimeInputs()
        {
            var config = ScriptableObject.CreateInstance<CharacterAnimationConfig>();
            var catalog = ScriptableObject.CreateInstance<SpriteAtlasCatalog>();
            try
            {
                var settings = new CharacterAnimationSettings { FrameCount = 6, TotalDurationSeconds = 1.2 };
                var entry = new CharacterAnimationEntry { CharacterId = "mage", AtlasCatalog = catalog, Animations = new[] { settings } };
                config.Set(entry, new CharacterAnimationEntry { CharacterId = "unconfigured" });
                var version = config.Version;
                settings.FrameCount = 999;
                Assert.That(config.TryResolve("mage", "idle_combat", out var bound, out var copy, out _), Is.True);
                Assert.That(bound, Is.SameAs(catalog));
                Assert.That(copy.FrameCount, Is.EqualTo(6));
                config.Set(entry, entry);
                Assert.That(config.Version, Is.GreaterThan(version));
                Assert.That(config.TryResolve("mage", "idle_combat", out _, out _, out var error), Is.False);
                Assert.That(error, Does.Contain("duplicate CharacterId"));
                entry.Animations = new[] { new CharacterAnimationSettings(), new CharacterAnimationSettings() };
                config.Set(entry);
                Assert.That(config.TryResolve("mage", "idle_combat", out _, out _, out error), Is.False);
                Assert.That(error, Does.Contain("duplicate ClipId"));
            }
            finally { Object.DestroyImmediate(config); Object.DestroyImmediate(catalog); }
        }
    }
}
