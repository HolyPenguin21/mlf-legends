#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using static MLFLegends.Tests.PlayMode.SpritePlayModeSupport;

namespace MLFLegends.Tests.PlayMode
{
    [Category("Graphics")]
    public class CombatStrikeEffectsGraphicsTests
    {
        [UnityTest]
        public IEnumerator TechnicalSwingAndImpactUseSeparatePagesAndReenableDoesNotRestartAge()
        {
            RequireGraphicsDevice();
            using var w = StrikeEffectsPlayModeSupport.Create(2, out var actors, out _);
            var target = CreateTarget(); var camera = CreateCamera("Strike effects", new Vector3(.5f, .8f, 0), 2, target);
            var renderer = CreateRenderer(camera); renderer.BindWorld(w);
            var readback = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            try
            {
                StrikeEffectsPlayModeSupport.Submit(w, actors, 1, false); StrikeEffectsPlayModeSupport.Step(w, 1);
                renderer.DrawEffects = false; var without = RenderAndRead(renderer, camera, readback);
                renderer.DrawEffects = true; var withEffects = RenderAndRead(renderer, camera, readback);
                Assert.That(withEffects.Zip(without, (a, b) => !a.Equals(b)).Count(v => v), Is.GreaterThan(5));
                Assert.That(renderer.Stats.Effects.Alive, Is.EqualTo(2));
                Assert.That(renderer.Stats.Effects.Submissions, Is.EqualTo(2), "Shared technical atlas has distinct Swing/Impact pages.");
                var spawned = renderer.Stats.Effects.Spawned;
                var frame = renderer.Effects.GetInstance(0).Frame;
                renderer.Refresh(); camera.Render();
                Assert.That(renderer.Stats.Effects.Spawned, Is.EqualTo(spawned));
                Assert.That(renderer.Effects.GetInstance(0).Frame, Is.EqualTo(frame));
                renderer.enabled = false; renderer.enabled = true;
                renderer.Refresh(); Assert.That(renderer.Effects.GetInstance(0).Frame, Is.EqualTo(frame), "Same frozen clock, same effect age.");
                for (uint t = 2; t <= 20; t++) StrikeEffectsPlayModeSupport.Step(w, t);
                renderer.Refresh(); Assert.That(renderer.Stats.Effects.Alive, Is.Zero);
                renderer.enabled = false; renderer.enabled = true; renderer.Refresh();
                Assert.That(renderer.Stats.Effects.Alive, Is.Zero, "Expired retained facts never become new effects on enable.");
                SaveEvidence(readback, "stage05-3-technical-swing-impact");
                yield return null;
            }
            finally { DestroyAll(renderer, camera, target, readback); }
        }
    }
}
#endif
