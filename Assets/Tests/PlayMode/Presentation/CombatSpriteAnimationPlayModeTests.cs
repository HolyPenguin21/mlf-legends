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
    // Stage 03.3 / 05.1: the renderer's CharacterConfig mode in the default World with the technical combat manifest
    // (TechnicalAtlasFixture.CombatManifestJson): idle tech_a/tech_c, run tech_b/tech_c, strikes windup tech_a /
    // strike tech_c / recovery tech_b. Expected frames are independent tick-window oracles of the documented rules.
    public class CombatSpriteAnimationPlayModeTests
    {
        readonly List<Object> m_Owned = new List<Object>();
        float m_TimeScale;
        CharacterAnimationConfig m_Config;
        SpriteAtlasCatalog m_Catalog;
        CombatSpriteRenderer m_Renderer;
        Camera m_Camera;
        SpriteRunTestProducer m_Producer;

        [SetUp] public void SetUp() => m_TimeScale = Time.timeScale;
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Time.timeScale = m_TimeScale;
            SpriteRunTestProducer.Remove(DefaultWorld, m_Producer);
            m_Producer = null;
            DestroyAll(m_Owned.ToArray());
            m_Owned.Clear();
            yield return null;
            yield return EndBattle();
        }

        void Create()
        {
            var pages = TechnicalAtlasFixture.CreatePages();
            m_Owned.AddRange(pages);
            var manifest = new TextAsset(TechnicalAtlasFixture.CombatManifestJson("tech_a,tech_c", "tech_b,tech_c"));
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

        /// <summary>Every combat clip: idle N/T as given, run 2 frames / 0.3 s, jump 6 / 0.5 s, strikes 3 frames (one per phase) at the default windows' total.</summary>
        static CharacterAnimationSettings[] Clips(int idleCount, double idleDuration, CharacterAnimationSettings run = null)
        {
            var clips = new List<CharacterAnimationSettings>
            {
                new CharacterAnimationSettings { EndFrame = idleCount - 1, TotalDurationSeconds = idleDuration },
                run ?? new CharacterAnimationSettings { ClipId = "run", EndFrame = 1, TotalDurationSeconds = 0.3 },
                new CharacterAnimationSettings { ClipId = "jump", EndFrame = 5, TotalDurationSeconds = 0.5 },
            };
            for (var kind = CombatAttackKind.PunchLeft; kind <= CombatAttackKind.KickSpin; kind++)
                clips.Add(new CharacterAnimationSettings { ClipId = CombatAnimationSet.AttackClip(kind), EndFrame = 2, PreparationEndFrame = 0, ActiveEndFrame = 1,
                    TotalDurationSeconds = CombatAttackTuning.Get(kind).TotalTicks / (double)SimulationTiming.TickRateHz });
            return clips.ToArray();
        }

        void SetTiming(int count, double duration, CharacterAnimationSettings run = null) => m_Config.Set(new CharacterAnimationEntry
        {
            CharacterId = TechnicalAtlasFixture.CharacterId, AtlasCatalog = m_Catalog, Animations = Clips(count, duration, run),
        });

        /// <summary>Idle of a fighter that never left it: frame 0 from the battle's first tick, T = 1.2 s (36 ticks per frame).</summary>
        static uint IdleFrame(uint relative) => relative == 0 || (relative - 1) % 72 < 36 ? 0u : 2u;

        void AssertCurrentFrame(int count)
        {
            m_Renderer.Refresh();
            var expected = IdleFrame(Session.RelativeTick(Tick));
            Assert.That(m_Renderer.Stats.Instances, Is.EqualTo(count));
            foreach (var instance in m_Renderer.Extraction.Instances)
                Assert.That(instance.Frame, Is.EqualTo(expected), "Idle since the battle start; visibility/position never changes it.");
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

        /// <summary>Even IDs run from the first tick (stride phase = distance), odd IDs stay idle.</summary>
        void AssertMixed(int count)
        {
            m_Renderer.Refresh();
            var relative = Session.RelativeTick(Tick);
            var run = relative % 18 < 9 ? 1u : 2u;
            Assert.That(m_Renderer.Stats.Instances, Is.EqualTo(count));
            for (var i = 0; i < m_Renderer.Extraction.Count; i++)
            {
                var id = m_Renderer.Extraction.InstanceIds[i];
                Assert.That(m_Renderer.Extraction.Instances[i].Frame, Is.EqualTo((id & 1) == 0 ? run : IdleFrame(relative)));
            }
        }

        [UnityTest]
        public IEnumerator MixedIdleRunUsesActualMovementAndKeepsIndependentCachedTiming([Values(0, 49, 199)] int allies)
        {
            Create();
            m_Producer = SpriteRunTestProducer.Install(DefaultWorld);
            yield return RestartBattle(Duel(float3.zero, FacingDirection.Right, new float3(3, 0, 0), FacingDirection.Left, allies));
            var count = 2 + 2 * allies;
            var idle = m_Renderer.AnimationTiming;
            var run = m_Renderer.RunAnimationTiming;
            var builds = m_Renderer.TimingBuilds;
            var tick = Tick;
            yield return Until(() => Tick >= tick + 40);
            AssertMixed(count);
            Time.timeScale = 0;
            m_Renderer.Refresh();
            var frozen = m_Renderer.Extraction.Instances.ToArray();
            for (var i = 0; i < 3; i++) yield return null;
            m_Renderer.Refresh();
            Assert.That(m_Renderer.Extraction.Instances.ToArray(), Is.EqualTo(frozen));
            Assert.That(m_Renderer.TimingBuilds, Is.EqualTo(builds));
            Assert.That(m_Renderer.AnimationTiming, Is.SameAs(idle));
            Assert.That(m_Renderer.RunAnimationTiming, Is.SameAs(run));
            Time.timeScale = m_TimeScale;
            m_Renderer.enabled = false;
            tick = Tick;
            m_Camera.transform.position += Vector3.right * 10000;
            yield return Until(() => Tick >= tick + 100);
            m_Renderer.enabled = true;
            m_Camera.transform.position -= Vector3.right * 10000;
            AssertMixed(count);
            var atlas = m_Renderer.Atlas;
            SetTiming(2, 1.2, new CharacterAnimationSettings
                { ClipId = "run", EndFrame = 0, TotalDurationSeconds = 0.9, SpeedCurve = AnimationCurve.Linear(0, 0.5f, 1, 1.5f) });
            // Loop speed discontinuity is intentional for this edit and is only a warning.
            LogAssert.Expect(LogType.Warning, new Regex("run.*speed differs"));
            m_Renderer.Refresh();
            Assert.That(m_Renderer.Atlas, Is.SameAs(atlas), "A timing edit never reloads page/frame buffers.");
            Assert.That(m_Renderer.AnimationTiming.DurationSeconds, Is.EqualTo(1.2));
            Assert.That(m_Renderer.AnimationTiming.FrameStartSeconds(1), Is.EqualTo(0.6).Within(1e-10));
            Assert.That(m_Renderer.RunAnimationTiming.DurationSeconds, Is.EqualTo(0.9));
            m_Producer.Move = false;
            tick = Tick;
            yield return Until(() => Tick > tick + CombatMovementTuning.MoveStreakGraceTicks + 1);
            m_Renderer.Refresh();
            foreach (var instance in m_Renderer.Extraction.Instances.ToArray().Where((_, i) => (m_Renderer.Extraction.InstanceIds[i] & 1) == 0))
                Assert.That(instance.Frame, Is.Zero, "After the stop grace the former runners enter idle at its first frame (no raw-input dependency).");
        }

        [UnityTest]
        public IEnumerator StrikesShowTheirFramesOnTheSimulationWindowsForEveryRosterSize([Values(0, 49, 199)] int allies)
        {
            Create();
            m_Producer = SpriteRunTestProducer.Install(DefaultWorld);
            m_Producer.Move = false;
            m_Producer.StrikeEvery = 40;
            m_Producer.StrikeModulo = 2; // odd IDs strike: every roster size has one
            yield return RestartBattle(Duel(float3.zero, FacingDirection.Right, new float3(3, 0, 0), FacingDirection.Left, allies));
            var punch = CombatAttackTuning.Get(CombatAttackKind.PunchLeft);
            var seen = new HashSet<(uint, CombatAttackPhase)>();
            var start = Tick;
            yield return Until(() =>
            {
                m_Renderer.Refresh();
                var relative = Session.RelativeTick(Tick);
                var phase = relative == 0 ? CombatAttackPhase.None : CombatAttackTuning.PhaseAt(punch, (relative - 1) % 40);
                var striker = phase == CombatAttackPhase.Windup ? 0u : phase == CombatAttackPhase.Active ? 2u : phase == CombatAttackPhase.Recovery ? 1u : 0u;
                for (var i = 0; i < m_Renderer.Extraction.Count; i++)
                {
                    var id = m_Renderer.Extraction.InstanceIds[i];
                    var frame = m_Renderer.Extraction.Instances[i].Frame;
                    if (id % 2 == 1)
                    {
                        Assert.That(frame, Is.EqualTo(striker), $"r{relative} {phase}");
                        seen.Add((frame, phase));
                    }
                }
                return Tick - start > 90;
            });
            Assert.That(seen.Select(s => s.Item2), Is.SupersetOf(new[] { CombatAttackPhase.Windup, CombatAttackPhase.Active, CombatAttackPhase.Recovery }));
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
                // Idle T = 0.2 s from the first battle tick: tech_a on (r − 1) % 12 < 6, tech_c otherwise.
                yield return Until(() => { var t = (Session.RelativeTick(Tick) + 11) % 12; return secondHalf ? t >= 8 && t <= 10 : t >= 2 && t <= 4; });
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

        [UnityTest, Category("Graphics")]
        public IEnumerator MixedIdleRunStrikeMatchesManualFramesAcrossPagesAndDepth([Values(1, 2, 3)] int pixelsPerSourcePixel)
        {
            RequireGraphicsDevice();
            Create();
            m_Producer = SpriteRunTestProducer.Install(DefaultWorld);
            m_Producer.AlternateDirection = true; // keep the moving actor in view while testing its accepted commands
            m_Producer.StrikeEvery = 40;
            var target = CreateTarget(256);
            var referenceTarget = CreateTarget(256);
            var readback = new Texture2D(256, 256, TextureFormat.RGBA32, false, false);
            var referenceReadback = new Texture2D(256, 256, TextureFormat.RGBA32, false, false);
            m_Owned.AddRange(new Object[] { target, referenceTarget, readback, referenceReadback });
            m_Camera.targetTexture = target;
            m_Camera.orthographicSize = 256f * (2f / 96f) / (2 * pixelsPerSourcePixel);
            var referenceCamera = CreateCamera("Mixed animation reference", Vector3.zero, m_Camera.orthographicSize, referenceTarget);
            var reference = CreateRenderer(referenceCamera);
            m_Owned.Add(referenceCamera);
            m_Owned.Add(reference);
            // Four fighters: one of them (ID % 4 == 1) strikes, the even ones run, the other one idles.
            yield return RestartBattle(Duel(new float3(-0.4f, 0, 0), FacingDirection.Left, new float3(0.4f, 0, 0), FacingDirection.Right, 1));
            var punch = CombatAttackTuning.Get(CombatAttackKind.PunchLeft);
            foreach (var active in new[] { true, false })
            {
                Time.timeScale = m_TimeScale;
                // Strikes repeat every 40 ticks (a slow first frame may skip one window): Active = strike ticks 14..16,
                // Recovery = strike ticks 20..23. Runner and idle frames follow their own independent tick oracles.
                yield return Until(() =>
                {
                    var relative = Session.RelativeTick(Tick);
                    var e = (relative + 39) % 40;
                    return relative >= 1 && (active ? e >= 14 && e <= 16 : e >= 20 && e <= 23);
                });
                Time.timeScale = 0;
                var relativeNow = Session.RelativeTick(Tick);
                Assert.That(CombatAttackTuning.PhaseAt(punch, (relativeNow - 1) % 40), Is.EqualTo(active ? CombatAttackPhase.Active : CombatAttackPhase.Recovery));
                var runFrame = relativeNow % 18 < 9 ? "tech_b" : "tech_c";
                var idleFrame = IdleFrame(relativeNow) == 0 ? "tech_a" : "tech_c";
                reference.Refresh(); // bind this battle before storing manual selections (new battles clear old selections)
                foreach (var actor in Combatants())
                    reference.SetFrame(new CombatantId { Value = actor.id }, (actor.id & 1) == 0 ? runFrame
                        : actor.id % 4 == 1 ? (active ? "tech_c" : "tech_b") : idleFrame);
                yield return null;
                var actual = RenderAndRead(m_Renderer, m_Camera, readback);
                var expected = RenderAndRead(reference, referenceCamera, referenceReadback);
                Assert.That(actual, Is.EqualTo(expected), "Per-combatant selection precedes trim/depth/page grouping and anchor transform.");
                SaveEvidence(readback, "48-mixed-" + pixelsPerSourcePixel + "px-" + (active ? "active" : "recovery"));
            }
        }

        [UnityTest]
        public IEnumerator CharacterSwitchRebindsItsOwnAtlasGeometryFramesAndTimingWithoutCrossCache()
        {
            Create();
            var original = m_Config.Characters[0];
            var json = m_Catalog.Manifest.text.Replace("technical_fixture", "second_character")
                .Replace("\"source_size\": [192, 192]", "\"source_size\": [256, 256]")
                .Replace("\"nominal_height_px\": 96", "\"nominal_height_px\": 160")
                .Replace("\"pivot\": [96, 160]", "\"pivot\": [128, 200]")
                .Replace("\"trim_offset\": [30, 40]", "\"trim_offset\": [42, 64]");
            var pagesStart = json.IndexOf("\"pages\":");
            var framesStart = json.IndexOf("\"frames\":");
            json = json.Substring(0, pagesStart) + "\"pages\":[{\"file\":\"technical_page0.png\",\"width\":256,\"height\":256}]," + json.Substring(framesStart);
            json = json.Replace("\"page\": 1, \"rect\": [8, 8, 50, 100]", "\"page\": 0, \"rect\": [120, 8, 50, 100]");
            // A different frame table (two records rather than three) and only one page instead of two.
            var bStart = json.IndexOf("\"tech_b\": {");
            var cStart = json.IndexOf("\"tech_c\": {");
            Assert.That(bStart, Is.GreaterThan(0));
            Assert.That(cStart, Is.GreaterThan(bStart));
            json = json.Remove(bStart, cStart - bStart);
            var clipsStart = json.IndexOf("\"clips\":");
            var once = "{\"frames\":[{\"id\":\"tech_a\",\"duration_ms\":50},{\"id\":\"tech_c\",\"duration_ms\":50},{\"id\":\"tech_a\",\"duration_ms\":50}],\"loop\":false,\"hold_last\":false,\"markers\":{\"strike_pose\":[1]}}";
            json = json.Substring(0, clipsStart) + "\"clips\": {" +
                "\"idle_combat\":{\"frames\":[{\"id\":\"tech_c\",\"duration_ms\":99}],\"loop\":true,\"hold_last\":false,\"markers\":{}}," +
                "\"run\":{\"frames\":[{\"id\":\"tech_a\",\"duration_ms\":33}],\"loop\":true,\"hold_last\":false,\"markers\":{}}," +
                "\"jump\":{\"frames\":[{\"id\":\"tech_a\",\"duration_ms\":50},{\"id\":\"tech_c\",\"duration_ms\":50},{\"id\":\"tech_a\",\"duration_ms\":50}],\"loop\":false,\"hold_last\":false,\"markers\":{\"rise\":[0],\"fall\":[1],\"land\":[2]}}," +
                string.Join(",", CombatAnimationSet.ClipIds.Skip(3).Select(c => $"\"{c}\":{once}")) + "},\"compatible_transitions\":[]}";
            var manifest = new TextAsset(json);
            var secondCatalog = ScriptableObject.CreateInstance<SpriteAtlasCatalog>();
            var page0 = new Texture2D(256, 256, TextureFormat.RGBA32, false, false);
            page0.SetPixels32(Enumerable.Repeat(TechnicalAtlasFixture.Blue, 256 * 256).ToArray());
            page0.Apply(false, true);
            m_Owned.AddRange(new Object[] { manifest, secondCatalog, page0 });
            secondCatalog.Set(manifest, new[] { new SpriteAtlasPageBinding(TechnicalAtlasFixture.Page0File, page0) });
            var second = Clips(1, 2.5, new CharacterAnimationSettings { ClipId = "run", EndFrame = 0, TotalDurationSeconds = 0.7 });
            second[2].EndFrame = 2;
            m_Config.Set(original, new CharacterAnimationEntry { CharacterId = "second_character", AtlasCatalog = secondCatalog, Animations = second },
                new CharacterAnimationEntry { CharacterId = "unconfigured_other" });
            m_Producer = SpriteRunTestProducer.Install(DefaultWorld);
            yield return RestartBattle(BattleSetup.Default);
            m_Renderer.Refresh();
            var firstAtlas = m_Renderer.Atlas;
            Assert.That(firstAtlas.FrameCount, Is.EqualTo(3));
            Assert.That(m_Renderer.PixelWorldSize, Is.EqualTo(2f / 96f));
            m_Renderer.ConfigureAnimation(Shader.Find(ShaderName), m_Camera, m_Config, "second_character");
            m_Renderer.Refresh();
            var secondAtlas = m_Renderer.Atlas;
            Assert.That(firstAtlas.IsDisposed, Is.True);
            Assert.That(secondAtlas.Definition.CharacterId, Is.EqualTo("second_character"));
            Assert.That(secondAtlas.FrameCount, Is.EqualTo(2));
            Assert.That(secondAtlas.GetPage(0), Is.SameAs(page0));
            Assert.That(secondAtlas.PageCount, Is.EqualTo(1), "A two-page character can switch to one page without retaining old bindings.");
            Assert.That(secondAtlas.Definition.Pivot.x, Is.EqualTo(128));
            Assert.That(secondAtlas.Definition.Pivot.y, Is.EqualTo(200));
            Assert.That(secondAtlas.Definition.Frames[1].TrimOffset.x, Is.EqualTo(42));
            Assert.That(m_Renderer.PixelWorldSize, Is.EqualTo(2f / 160f));
            Assert.That(m_Renderer.AnimationTiming.DurationSeconds, Is.EqualTo(2.5));
            Assert.That(m_Renderer.RunAnimationTiming.DurationSeconds, Is.EqualTo(0.7));
            Assert.That(m_Renderer.Animations.Jump.FrameCount, Is.EqualTo(3), "Its own clip table, not the first character's.");
            for (var i = 0; i < m_Renderer.Extraction.Count; i++)
                Assert.That(m_Renderer.Extraction.Instances[i].Frame,
                    Is.EqualTo((m_Renderer.Extraction.InstanceIds[i] & 1) == 0 ? 0u : 1u));
            m_Renderer.ConfigureAnimation(Shader.Find(ShaderName), m_Camera, m_Config, TechnicalAtlasFixture.CharacterId);
            m_Renderer.Refresh();
            Assert.That(secondAtlas.IsDisposed, Is.True);
            Assert.That(m_Renderer.Atlas.Definition.CharacterId, Is.EqualTo(TechnicalAtlasFixture.CharacterId));
            Assert.That(m_Renderer.Atlas.GetPage(0), Is.SameAs(m_Catalog.Pages[0].Texture));
            Assert.That(m_Renderer.PixelWorldSize, Is.EqualTo(2f / 96f));
            Assert.That(m_Renderer.AnimationTiming.DurationSeconds, Is.EqualTo(1.2));
            Assert.That(m_Renderer.RunAnimationTiming.DurationSeconds, Is.EqualTo(0.3));
            AssertMixed(2);
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
            LogAssert.Expect(LogType.Error, new Regex("(?s)animation rejected.*idle_combat.EndFrame"));
            m_Renderer.Refresh();
            m_Renderer.Refresh();
            Assert.That(m_Renderer.Stats.Instances, Is.Zero);
            SetTiming(2, 1.2);
            AssertCurrentFrame(2);
            var manifest = m_Catalog.Manifest;
            var bindings = m_Catalog.Pages.ToArray();
            void Rejects(string text, string error)
            {
                var broken = new TextAsset(text);
                m_Owned.Add(broken);
                m_Catalog.Set(broken, bindings);
                LogAssert.Expect(LogType.Error, new Regex("(?s)animation rejected.*" + error));
                m_Renderer.Refresh();
                m_Renderer.Refresh();
                Assert.That(m_Renderer.Animations, Is.Null, "An invalid clip cannot retain an old table or substitute another clip.");
                Assert.That(m_Renderer.Stats.Instances, Is.Zero, "No first-clip fallback.");
                m_Catalog.Set(manifest, bindings);
                AssertCurrentFrame(2);
            }

            Rejects(manifest.text.Replace("\"idle_combat\":", "\"unavailable_idle\":"), "idle_combat: clip missing");
            Rejects(manifest.text.Replace("\"run\":", "\"unavailable_run\":"), "run: clip missing");
            Rejects(manifest.text.Replace("\"kick_spin\":", "\"unavailable_spin\":"), "kick_spin: clip missing");
            Rejects(manifest.text.Replace("\"land\": [4, 5]", "\"landing\": [4, 5]"), "jump.markers.land: missing");
            Rejects(manifest.text.Replace("\"strike_pose\": [1] } }, \"punch_right\"", "\"pose\": [1] } }, \"punch_right\""), "punch_left.markers.strike_pose: missing");
            m_Config.Set(new CharacterAnimationEntry
            {
                CharacterId = TechnicalAtlasFixture.CharacterId, AtlasCatalog = m_Catalog,
                Animations = Clips(2, 1.2).Where(c => c.ClipId != "kick_left").ToArray(),
            });
            LogAssert.Expect(LogType.Error, new Regex("(?s)animation rejected.*kick_left: animation settings missing"));
            m_Renderer.Refresh();
            m_Renderer.Refresh();
            Assert.That(m_Renderer.AnimationTiming, Is.Null);
            Assert.That(m_Renderer.Stats.Instances, Is.Zero);
            SetTiming(2, 1.2);
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
