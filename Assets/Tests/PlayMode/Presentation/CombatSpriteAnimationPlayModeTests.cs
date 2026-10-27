#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using static MLFLegends.Tests.PlayMode.SpritePlayModeSupport;
using Object = UnityEngine.Object;

namespace MLFLegends.Tests.PlayMode
{
    public class CombatSpriteAnimationPlayModeTests
    {
        readonly List<Object> m_Owned = new List<Object>();
        float m_TimeScale;
        CharacterAnimationConfig m_Config;
        SpriteAtlasCatalog m_Catalog;
        CombatSpriteRenderer m_Renderer;
        Camera m_Camera;

        [SetUp] public void SetUp() => m_TimeScale = Time.timeScale;
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Time.timeScale = m_TimeScale;
            DestroyAll(m_Owned.ToArray());
            m_Owned.Clear();
            yield return null;
            yield return EndBattle();
        }

        void Create()
        {
            var pages = TechnicalAtlasFixture.CreatePages();
            m_Owned.AddRange(pages);
            var json = TechnicalAtlasFixture.ManifestJson.Replace("tech_idle", "idle_combat")
                .Replace("{ \"id\": \"tech_b\", \"duration_ms\": 62.5 }", "{ \"id\": \"tech_c\", \"duration_ms\": 62.5 }");
            var manifest = new TextAsset(json);
            m_Owned.Add(manifest);
            m_Catalog = ScriptableObject.CreateInstance<SpriteAtlasCatalog>();
            m_Owned.Add(m_Catalog);
            m_Catalog.Set(manifest, TechnicalAtlasFixture.Bind(pages));
            m_Config = ScriptableObject.CreateInstance<CharacterAnimationConfig>();
            m_Owned.Add(m_Config);
            SetTiming(2, 1.2);
            m_Camera = CreateCamera("Animation camera", Vector3.zero, 6);
            m_Owned.Add(m_Camera);
            var go = new GameObject("Animated sprites");
            go.SetActive(false);
            m_Renderer = go.AddComponent<CombatSpriteRenderer>();
            m_Renderer.ConfigureAnimation(Shader.Find(ShaderName), m_Camera, m_Config, TechnicalAtlasFixture.CharacterId);
            m_Owned.Add(go);
            go.SetActive(true);
        }

        void SetTiming(int count, double duration) => m_Config.Set(new CharacterAnimationEntry
        {
            CharacterId = TechnicalAtlasFixture.CharacterId, AtlasCatalog = m_Catalog,
            Animations = new[] { new CharacterAnimationSettings { FrameCount = count, TotalDurationSeconds = duration } },
        });

        void AssertCurrentFrame(int count)
        {
            m_Renderer.Refresh();
            var expected = Session.RelativeTick(Tick) % 72 < 36 ? 0u : 2u;
            Assert.That(m_Renderer.Stats.Instances, Is.EqualTo(count));
            foreach (var instance in m_Renderer.Extraction.Instances)
                Assert.That(instance.Frame, Is.EqualTo(expected), "One battle-time phase; visibility/position never changes it.");
            Assert.That(m_Renderer.Extraction.PageInstances(expected == 0 ? 0 : 1), Is.EqualTo(count));
        }

        [UnityTest]
        public IEnumerator BattleClockDrivesIdleFor2_100_400AcrossPauseVisibilityAndRendererDisable([Values(0, 49, 199)] int allies)
        {
            Create();
            yield return RestartBattle(Duel(float3.zero, FacingDirection.Right, new float3(3, 0, 0), FacingDirection.Left, allies));
            var count = 2 + 2 * allies;
            var atlas = m_Renderer.Atlas;
            var builds = m_Renderer.TimingBuilds;
            var tick = Tick;
            yield return Until(() => Tick >= tick + 40);
            AssertCurrentFrame(count);
            Assert.That(m_Renderer.SetFrame(Session.HumanCombatant, "tech_a"), Is.False, "Manual API cannot override active animation.");
            Assert.That(m_Renderer.SetDefaultFrame("tech_a"), Is.False);
            Assert.That(m_Renderer.SetClipFrame(Session.HumanCombatant, "idle_combat", 0), Is.False);
            Assert.That(m_Renderer.TimingBuilds, Is.EqualTo(builds));
            Assert.That(m_Renderer.Atlas, Is.SameAs(atlas), "No parse/reload every frame.");
            Time.timeScale = 0;
            tick = Tick;
            m_Renderer.Refresh();
            m_Renderer.TryGetInstance(Session.HumanCombatant, out var before);
            for (var i = 0; i < 5; i++) yield return null;
            m_Renderer.Refresh();
            m_Renderer.TryGetInstance(Session.HumanCombatant, out var after);
            Assert.That(Tick, Is.EqualTo(tick));
            Assert.That(after, Is.EqualTo(before));
            Time.timeScale = m_TimeScale;
            m_Camera.transform.position += Vector3.right * 10000;
            m_Renderer.enabled = false;
            Assert.That(m_Renderer.LiveGpuResources, Is.Zero);
            yield return Until(() => Tick >= tick + 160);
            m_Renderer.enabled = true;
            m_Camera.transform.position -= Vector3.right * 10000;
            AssertCurrentFrame(count);
            Time.timeScale = 0;
            var battle = Session.BattleNumber;
            BattleRequest.Submit(Manager, BattleRequest.Restart(BattleSetup.Default, Owner));
            yield return Until(() => Session.BattleNumber > battle);
            Assert.That(Session.RelativeTick(Tick), Is.Zero);
            AssertCurrentFrame(2);
            BattleRequest.Submit(Manager, BattleRequest.End(Owner));
            yield return Until(() => !Session.IsActive);
            m_Renderer.Refresh();
            Assert.That(m_Renderer.Stats.Instances, Is.Zero);
        }

        [UnityTest, Category("Graphics")]
        public IEnumerator AnimatedPageChangesMatchTheIndependentlySelectedManualFrame([Values(1, 2, 3)] int pixelsPerSourcePixel)
        {
            RequireGraphicsDevice();
            Create();
            SetTiming(2, 0.2);
            var target = CreateTarget(256);
            var referenceTarget = CreateTarget(256);
            var readback = new Texture2D(256, 256, TextureFormat.RGBA32, false, false);
            var referenceReadback = new Texture2D(256, 256, TextureFormat.RGBA32, false, false);
            m_Owned.AddRange(new Object[] { target, referenceTarget, readback, referenceReadback });
            m_Camera.targetTexture = target;
            m_Camera.orthographicSize = 256f / (2 * pixelsPerSourcePixel / (2f / 96f));
            var referenceCamera = CreateCamera("Animation reference", Vector3.zero, m_Camera.orthographicSize, referenceTarget);
            var reference = CreateRenderer(referenceCamera);
            m_Owned.Add(referenceCamera);
            m_Owned.Add(reference);
            yield return RestartBattle(Duel(float3.zero, FacingDirection.Left, new float3(50, 0, 0), FacingDirection.Right));
            foreach (var secondHalf in new[] { false, true })
            {
                Time.timeScale = m_TimeScale;
                yield return Until(() => { var t = Session.RelativeTick(Tick) % 12; return secondHalf ? t >= 8 && t <= 10 : t >= 2 && t <= 4; });
                Time.timeScale = 0;
                var frame = secondHalf ? "tech_c" : "tech_a";
                reference.SetDefaultFrame(frame);
                yield return null; // retire all draws queued before the frozen tick
                var actual = RenderAndRead(m_Renderer, m_Camera, readback);
                var expected = RenderAndRead(reference, referenceCamera, referenceReadback);
                Assert.That(actual, Is.EqualTo(expected), "Animation selects the same frame/page/trim/flip as an independent tick-window oracle.");
                SaveEvidence(readback, "43-animation-" + pixelsPerSourcePixel + "px-" + frame);
            }
        }

        [UnityTest]
        public IEnumerator ConfigEditsInvalidateTimingWithoutReloadingAtlasAndErrorsRecoverOnce()
        {
            Create();
            yield return RestartBattle(BattleSetup.Default);
            m_Renderer.Refresh();
            var atlas = m_Renderer.Atlas;
            var builds = m_Renderer.TimingBuilds;
            SetTiming(1, 0.4);
            m_Renderer.Refresh();
            Assert.That(m_Renderer.Atlas, Is.SameAs(atlas));
            Assert.That(m_Renderer.TimingBuilds, Is.EqualTo(builds + 1));
            foreach (var instance in m_Renderer.Extraction.Instances) Assert.That(instance.Frame, Is.Zero);
            SetTiming(3, 1.2);
            LogAssert.Expect(LogType.Error, new Regex("animation rejected.*\\n.*FrameCount"));
            m_Renderer.Refresh();
            m_Renderer.Refresh();
            Assert.That(m_Renderer.Stats.Instances, Is.Zero);
            SetTiming(2, 1.2);
            AssertCurrentFrame(2);
            var manifest = m_Catalog.Manifest;
            var bindings = m_Catalog.Pages.ToArray();
            var missingClip = new TextAsset(manifest.text.Replace("idle_combat", "unavailable_idle"));
            m_Owned.Add(missingClip);
            m_Catalog.Set(missingClip, bindings);
            LogAssert.Expect(LogType.Error, new Regex("animation rejected.*\\n.*clip missing"));
            m_Renderer.Refresh();
            m_Renderer.Refresh();
            Assert.That(m_Renderer.Stats.Instances, Is.Zero, "No first-clip fallback.");
            m_Catalog.Set(manifest, bindings);
            AssertCurrentFrame(2);
            using (var empty = new World("animation replacement"))
            {
                m_Renderer.BindWorld(empty);
                m_Renderer.Refresh();
                Assert.That(m_Renderer.Stats.Instances, Is.Zero);
            }
            m_Renderer.Refresh();
            Assert.That(m_Renderer.BoundWorld, Is.Null);
            m_Renderer.BindDefaultWorld();
            AssertCurrentFrame(2);
        }
    }
}
#endif
