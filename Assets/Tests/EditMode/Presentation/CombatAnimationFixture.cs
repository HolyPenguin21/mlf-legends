using System.Collections.Generic;
using MLFLegends.Combat;
using NUnit.Framework;

namespace MLFLegends.Tests.EditMode
{
    /// <summary>
    /// Stage 05.1 test fixture over <see cref="TechnicalAtlasFixture.CombatManifestJson"/>: the technical atlas frames
    /// (tech_a = 0 page 0, tech_b = 1 page 0, tech_c = 2 page 1, same order as the technical atlas) with all eight combat
    /// clips, so a set built here indexes the technical atlas directly. Idle draws tech_a, run tech_c, every jump frame tech_b;
    /// strikes draw windup tech_a, active tech_c, recovery tech_b.
    /// </summary>
    static class CombatAnimationFixture
    {
        public const uint TechA = 0, TechB = 1, TechC = 2;

        public static string Manifest(string idleFrames = "tech_a") => TechnicalAtlasFixture.CombatManifestJson(idleFrames);

        public static SpriteAtlasDefinition Definition(string manifest = null)
        {
            var errors = new List<string>();
            var definition = SpriteAtlasManifest.Parse(manifest ?? Manifest(), errors);
            Assert.That(definition, Is.Not.Null, string.Join("\n", errors));
            return definition;
        }

        /// <summary>Settings for every clip: all frames, strikes at their authoritative total, others 0.5 s (idle: <paramref name="idleSeconds"/>).</summary>
        public static CharacterAnimationSettings Settings(SpriteAtlasDefinition definition, string clipId, double idleSeconds = 0.5)
        {
            if (!definition.TryGetClipIndex(clipId, out var index))
                return null;
            double seconds = clipId == CombatAnimationSet.IdleClip ? idleSeconds : 0.5;
            for (var kind = CombatAttackKind.PunchLeft; kind <= CombatAttackKind.KickSpin; kind++)
                if (CombatAnimationSet.AttackClip(kind) == clipId)
                    seconds = CombatAttackTuning.Get(kind).TotalTicks / (double)SimulationTiming.TickRateHz;
            return new CharacterAnimationSettings { ClipId = clipId, FrameCount = definition.Clips[index].Frames.Count, TotalDurationSeconds = seconds };
        }

        public static CombatAnimationSet Set(string manifest = null, double idleSeconds = 0.5)
        {
            var definition = Definition(manifest);
            var errors = new List<string>();
            var set = CombatAnimationSet.Create(definition, clip => Settings(definition, clip, idleSeconds), "fixture", errors);
            Assert.That(set, Is.Not.Null, string.Join("\n", errors));
            return set;
        }
    }
}
