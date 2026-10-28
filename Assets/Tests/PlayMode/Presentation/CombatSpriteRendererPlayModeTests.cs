#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using static MLFLegends.Tests.PlayMode.SpritePlayModeSupport;
using Object = UnityEngine.Object;

namespace MLFLegends.Tests.PlayMode
{
    // Stage 03 (#38): CombatSpriteRenderer in the real player loop and default World (no graphics device needed: with the
    // Null device extraction runs and GPU submission is skipped). GPU output is covered by CombatSpriteGraphicsTests.
    public class CombatSpriteRendererPlayModeTests
    {
        const string SmokeScenePath = "Assets/Scenes/CombatSmoke.unity";
        float m_TimeScale;

        [SetUp]
        public void SetUp() => m_TimeScale = Time.timeScale;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Time.timeScale = m_TimeScale;
            foreach (var renderer in Object.FindObjectsByType<CombatSpriteRenderer>(FindObjectsInactive.Include))
                Object.Destroy(renderer.gameObject.scene.path == SmokeScenePath ? renderer : (Object)renderer.gameObject);
            foreach (var scene in SmokeScenes())
                SceneManager.UnloadSceneAsync(scene);
            yield return Until(() => SmokeScenes().Length == 0);
            yield return EndBattle();
            if (DefaultWorld != null && DefaultWorld.IsCreated && Session.IsActive)
            {
                BattleRequest.Submit(Manager, BattleRequest.End());
                yield return Until(() => !Session.IsActive);
            }
        }

        /// <summary>
        /// Two full frames, so the renderer's LateUpdate ran after the last state change (WaitForEndOfFrame is not evoked
        /// in batchmode). No producer moves anyone in these tests, so positions are static between ticks.
        /// </summary>
        static IEnumerator Frames()
        {
            yield return null;
            yield return null;
        }

        static Scene[] SmokeScenes() => Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
            .Where(scene => scene.path == SmokeScenePath && scene.isLoaded).ToArray();

        static void AssertMatchesEcs(CombatSpriteRenderer renderer)
        {
            var combatants = Combatants();
            Assert.That(renderer.Extraction.Count, Is.EqualTo(combatants.Length));
            foreach (var (id, position, facing) in combatants)
            {
                Assert.That(renderer.TryGetInstance(new CombatantId { Value = id }, out var instance), Is.True, $"combatant {id}");
                Assert.That(instance.Position, Is.EqualTo(position), "The position after the frame's last tick.");
                Assert.That(instance.Facing, Is.EqualTo(facing == FacingDirection.Left ? -1f : 1f));
            }
        }

        [UnityTest]
        public IEnumerator ReadsTheDefaultWorldAfterTheFramesTicksFor2_100_400([Values(0, 49, 199)] int allies)
        {
            var camera = CreateCamera("Sprite camera", Vector3.zero, 6f);
            var renderer = CreateRenderer(camera);
            yield return RestartBattle(Duel(new float3(-2f, 0f, 0f), FacingDirection.Right, new float3(2f, 0f, 0f), FacingDirection.Left, allies));
            yield return Frames();
            Assert.That(renderer.Stats.Instances, Is.EqualTo(2 + 2 * allies));
            AssertMatchesEcs(renderer);
            Assert.That(renderer.Atlas, Is.Not.Null);
            Assert.That(renderer.BoundWorld, Is.SameAs(DefaultWorld));
            Assert.That(math.dot(renderer.Basis.ViewDirection, (float3)camera.transform.forward), Is.EqualTo(1f).Within(1e-5f),
                "The card basis uses the camera placed in this frame.");
            if (!HasGraphicsDevice)
                Assert.That(renderer.Stats.Submissions, Is.EqualTo(0), "Null device: no GPU resources or draws.");
            else
                Assert.That(renderer.Stats.Submissions, Is.EqualTo(1), "All fighters share page 0: one draw, not one per fighter.");
            DestroyAll(camera, renderer);
        }

        [UnityTest]
        public IEnumerator AnchorsFollowTheBodyTransformFacingAndFrame()
        {
            var camera = CreateCamera("Sprite camera", Vector3.zero, 6f);
            var renderer = CreateRenderer(camera);
            yield return RestartBattle(Duel(new float3(-2f, 0f, 0f), FacingDirection.Right, new float3(2f, 0f, 0f), FacingDirection.Left));
            yield return Frames();
            var s = 2f / 96f;
            var cos = math.cos(math.radians(40f));
            var view = new float3(0f, -math.sin(math.radians(40f)), cos);
            var human = Session.HumanCombatant;
            var ai = Session.AiCombatant;

            // tech_a hand_right (108,118) → card (12, 42) px: right of the support when facing right, left when facing left.
            Assert.That(renderer.TryGetInstance(human, out var h), Is.True);
            Assert.That(renderer.TryGetAnchor(human, CombatSpriteRenderer.AnchorKind.HandRight, out var position, out var direction, out var anchor), Is.True);
            var push = view * (h.DepthOffset + renderer.DepthBias);
            Assert.That(math.distance(position, new float3(-2f + 12f * s, 42f * s / cos, 0f) - push), Is.LessThan(1e-4f), $"{position}");
            Assert.That(anchor.Layer, Is.EqualTo(SpriteAnchorLayer.Front));
            Assert.That(direction.x, Is.GreaterThan(0f), "(1, 0.5) points right and down on screen when facing right.");
            Assert.That(direction.y, Is.LessThan(0f));

            Assert.That(renderer.TryGetInstance(ai, out var a), Is.True);
            Assert.That(renderer.TryGetAnchor(ai, CombatSpriteRenderer.AnchorKind.HandRight, out position, out direction, out _), Is.True);
            push = view * (a.DepthOffset + renderer.DepthBias);
            Assert.That(math.distance(position, new float3(2f - 12f * s, 42f * s / cos, 0f) - push), Is.LessThan(1e-4f), "Mirrored, name not swapped.");
            Assert.That(direction.x, Is.LessThan(0f));
            Assert.That(renderer.TryGetAnchor(ai, CombatSpriteRenderer.AnchorKind.CastOrigin, out _, out _, out _), Is.False, "cast_origin is null in tech_a.");

            // tech_b: the hidden back hand keeps its virtual position; cast_origin (156,131) → card (60, 29).
            Assert.That(renderer.SetFrame(ai, "tech_b"), Is.True);
            yield return Frames();
            renderer.TryGetInstance(ai, out a);
            push = view * (a.DepthOffset + renderer.DepthBias);
            Assert.That(renderer.TryGetAnchor(ai, CombatSpriteRenderer.AnchorKind.CastOrigin, out position, out _, out _), Is.True);
            Assert.That(math.distance(position, new float3(2f - 60f * s, 29f * s / cos, 0f) - push), Is.LessThan(1e-4f));
            Assert.That(renderer.TryGetAnchor(ai, CombatSpriteRenderer.AnchorKind.HandLeft, out position, out _, out anchor), Is.True);
            Assert.That(anchor.Visible, Is.False);
            Assert.That(anchor.Layer, Is.EqualTo(SpriteAnchorLayer.Back));
            Assert.That(math.distance(position, new float3(2f - 4f * s, 20f * s / cos, 0f) - push), Is.LessThan(1e-4f), "(100,140) → card (−4, 20) mirrored.");
            DestroyAll(camera, renderer);
        }

        [UnityTest]
        public IEnumerator PauseKeepsTheFrameAndPositionsWhileEndAndRestartReplaceEverything()
        {
            var camera = CreateCamera("Sprite camera", Vector3.zero, 6f);
            var renderer = CreateRenderer(camera);
            yield return RestartBattle(Duel(new float3(-2f, 0f, 0f), FacingDirection.Right, new float3(2f, 0f, 0f), FacingDirection.Left, 3));
            var first = Session;
            Assert.That(renderer.SetFrame(first.HumanCombatant, "tech_c"), Is.True);
            Assert.That(renderer.SetFrame(first.AiCombatant, "missing"), Is.False, "Only existing frame ids are accepted.");
            Assert.That(renderer.SetClipFrame(first.AiCombatant, "tech_idle", 1), Is.True, "clip + local index selects tech_b.");
            yield return Frames();
            var snapshot = renderer.Extraction.Instances.ToArray();

            Time.timeScale = 0f;
            var tick = Tick;
            for (var i = 0; i < 5; i++)
                yield return null;
            yield return Frames();
            Assert.That(Tick, Is.EqualTo(tick), "Paused.");
            Assert.That(renderer.Extraction.Instances.ToArray(), Is.EqualTo(snapshot), "A static frame and position survive pause.");
            renderer.TryGetInstance(first.HumanCombatant, out var human);
            Assert.That(human.Frame, Is.EqualTo(2u));
            renderer.TryGetInstance(first.AiCombatant, out var ai);
            Assert.That(ai.Frame, Is.EqualTo(1u));

            // End while paused (Initialization boundary): the next refresh draws nothing.
            BattleRequest.Submit(Manager, BattleRequest.End(Owner));
            yield return Until(() => !Session.IsActive);
            yield return Frames();
            Assert.That(renderer.Stats.Instances, Is.EqualTo(0), "End leaves no geometry.");
            Assert.That(renderer.Stats.Submissions, Is.EqualTo(0));

            Time.timeScale = m_TimeScale;
            yield return RestartBattle(Duel(new float3(-2f, 0f, 0f), FacingDirection.Right, new float3(2f, 0f, 0f), FacingDirection.Left));
            yield return Frames();
            AssertMatchesEcs(renderer);
            Assert.That(renderer.Extraction.InstanceIds.ToArray().Min(), Is.GreaterThan(first.AiCombatant.Value + 6), "New IDs, no stale handles.");
            Assert.That(renderer.Extraction.Instances.ToArray().All(i => i.Frame == renderer.DefaultFrame), Is.True, "Selections do not carry over.");
            DestroyAll(camera, renderer);
        }

        [UnityTest]
        public IEnumerator EnableDisableDestroyAndWorldDisposalReleaseEverything()
        {
            var camera = CreateCamera("Sprite camera", Vector3.zero, 6f);
            var pagesBefore = Resources.FindObjectsOfTypeAll<Texture2D>().Count(t => t.name.StartsWith("technical_page"));
            var renderer = CreateRenderer(camera);
            yield return RestartBattle(Duel(new float3(-2f, 0f, 0f), FacingDirection.Right, new float3(2f, 0f, 0f), FacingDirection.Left));
            for (var i = 0; i < 3; i++)
            {
                yield return Frames();
                Assert.That(renderer.Stats.Instances, Is.EqualTo(2));
                renderer.enabled = false;
                Assert.That(renderer.Extraction, Is.Null, "Extraction (queries, native lists) released on disable.");
                Assert.That(renderer.Atlas, Is.Null, "Atlas tables and GPU frame buffer released on disable.");
                Assert.That(renderer.LiveGpuResources, Is.EqualTo(0));
                yield return null;
                renderer.enabled = true;
            }

            // A World the renderer is bound to disappears: nothing drawn, no exception; it rebinds when told to follow the default again.
            var temporary = new World("Sprite temporary world");
            renderer.BindWorld(temporary);
            yield return Frames();
            Assert.That(renderer.Stats.Instances, Is.EqualTo(0), "A World without a battle draws nothing.");
            Assert.That(renderer.BoundWorld, Is.SameAs(temporary));
            temporary.Dispose();
            yield return Frames();
            Assert.That(renderer.BoundWorld, Is.Null);
            Assert.That(renderer.Stats.Instances, Is.EqualTo(0));
            renderer.BindDefaultWorld();
            yield return Frames();
            Assert.That(renderer.Stats.Instances, Is.EqualTo(2));

            Object.Destroy(renderer.gameObject);
            yield return null;
            yield return null;
            Assert.That(Resources.FindObjectsOfTypeAll<Texture2D>().Count(t => t != null && t.name.StartsWith("technical_page")), Is.EqualTo(pagesBefore),
                "Generated technical pages are destroyed by their owner.");
            DestroyAll(camera);
        }

        [UnityTest]
        public IEnumerator TwoRenderersOnOneWorldAndCameraDrawOnce()
        {
            var camera = CreateCamera("Sprite camera", Vector3.zero, 6f);
            var other = CreateCamera("Other camera", Vector3.zero, 6f);
            var first = CreateRenderer(camera, "Sprites A");
            var second = CreateRenderer(camera, "Sprites B");
            var elsewhere = CreateRenderer(other, "Sprites C");
            LogAssert.Expect(LogType.Warning, new Regex("Sprites A.*superseded"));
            yield return RestartBattle(Duel(new float3(-2f, 0f, 0f), FacingDirection.Right, new float3(2f, 0f, 0f), FacingDirection.Left));
            yield return Frames();
            Assert.That(first.IsActiveOwner, Is.False, "The earlier renderer of the same World + Camera is superseded…");
            Assert.That(second.IsActiveOwner, Is.True, "…by the later enabled one.");
            Assert.That(elsewhere.IsActiveOwner, Is.True, "Another camera is a separate owner.");
            Assert.That(first.Stats.Submissions, Is.EqualTo(0), "No double draw.");

            second.enabled = false;
            yield return Frames();
            Assert.That(first.IsActiveOwner, Is.True, "Disabling the later one hands drawing back.");
            Assert.That(first.Stats.Instances, Is.EqualTo(2));

            // Re-enabling makes B the latest again (and A logs once more).
            LogAssert.Expect(LogType.Warning, new Regex("Sprites A.*superseded"));
            second.enabled = true;
            yield return null;
            yield return Frames();
            Assert.That(second.IsActiveOwner, Is.True);
            Assert.That(first.IsActiveOwner, Is.False);
            DestroyAll(camera, other, first, second, elsewhere);
        }

        [UnityTest]
        public IEnumerator ARejectedCatalogDrawsNothingAndTheBattleKeepsRunning()
        {
            var camera = CreateCamera("Sprite camera", Vector3.zero, 6f);
            var catalog = ScriptableObject.CreateInstance<SpriteAtlasCatalog>();
            var manifest = new TextAsset(TechnicalAtlasFixture.ManifestJson.Replace("\"schema_version\": 1", "\"schema_version\": 7"));
            catalog.Set(manifest, new SpriteAtlasPageBinding[0]);
            var gameObject = new GameObject("Bad catalog sprites");
            gameObject.SetActive(false);
            var renderer = gameObject.AddComponent<CombatSpriteRenderer>();
            renderer.Configure(Shader.Find(ShaderName), camera, CombatSpriteRenderer.AtlasSource.Catalog, catalog);
            LogAssert.Expect(LogType.Error, new Regex("atlas rejected.*\\n.*unsupported schema_version 7"));
            gameObject.SetActive(true);

            yield return RestartBattle(Duel(new float3(-2f, 0f, 0f), FacingDirection.Right, new float3(2f, 0f, 0f), FacingDirection.Left));
            var tick = Tick;
            yield return Until(() => Tick >= tick + 3); // ECS keeps simulating with a rejected atlas
            yield return Frames();
            Assert.That(renderer.Atlas, Is.Null);
            Assert.That(renderer.Stats.Instances, Is.EqualTo(0));
            Assert.That(renderer.LiveGpuResources, Is.EqualTo(0), "No half-built catalog or GPU buffers.");

            // Fixing the catalog reloads it (once) without re-enabling.
            var pages = TechnicalAtlasFixture.CreatePages();
            catalog.Set(new TextAsset(TechnicalAtlasFixture.ManifestJson), TechnicalAtlasFixture.Bind(pages));
            yield return Frames();
            Assert.That(renderer.Atlas, Is.Not.Null);
            Assert.That(renderer.Stats.Instances, Is.EqualTo(2));
            DestroyAll(camera, renderer, catalog, manifest, pages[0], pages[1]);
        }

        [UnityTest]
        public IEnumerator TheSmokeSceneDrawsItsBattleWithTheRealCombatCatalog()
        {
            if (UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/MLFLegends/Art/Characters/MageFemale01/Atlas/mage_female_01_00.png") == null)
                Assert.Ignore("NOT RUN: install the external mage PNG with its tracked .meta before real-art smoke.");
            var load = EditorSceneManager.LoadSceneAsyncInPlayMode(SmokeScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            yield return Until(() => load.isDone);
            yield return Until(() => Session.IsActive && Session.RelativeTick(Tick) >= 1);
            var renderer = SmokeScenes().Single().GetRootGameObjects().SelectMany(o => o.GetComponentsInChildren<CombatSpriteRenderer>(true)).Single();
            Assert.That(renderer.GetComponent<CombatCameraController>(), Is.Not.Null, "On the scene's existing camera, after its controller.");
            yield return Frames();
            Assert.That(renderer.Atlas.Definition.CharacterId, Is.EqualTo("mage_female_01"));
            Assert.That(renderer.UsesCharacterAnimation, Is.True);
            Assert.That(renderer.AnimationTiming.FrameCount, Is.EqualTo(6));
            var config = UnityEditor.AssetDatabase.LoadAssetAtPath<CharacterAnimationConfig>("Assets/MLFLegends/Combat/PresentationData/CharacterAnimationConfig.asset");
            Assert.That(config.TryResolve("mage_female_01", "idle_combat", out _, out var idle, out _), Is.True);
            Assert.That(renderer.AnimationTiming.DurationSeconds, Is.EqualTo(idle.TotalDurationSeconds));
            Assert.That(renderer.RunAnimationTiming.FrameCount, Is.EqualTo(5));
            Assert.That(renderer.RunAnimationTiming.DurationSeconds, Is.EqualTo(0.5));
            Assert.That(renderer.Animations.Jump.FrameCount, Is.EqualTo(5), "Project jump range is 0..4; all eight clips remain bound.");
            Assert.That(renderer.Animations.LandingTicks, Is.EqualTo(12u));
            Assert.That(renderer.Animations.Attack(CombatAttackKind.KickSpin).FrameCount, Is.EqualTo(6));
            Assert.That(renderer.Animations.Attack(CombatAttackKind.PunchLeft).DurationSeconds, Is.EqualTo(29 / 60.0).Within(1e-9));
            Assert.That(renderer.PixelWorldSize, Is.EqualTo(2f / 128f));
            Assert.That(renderer.IsActiveOwner, Is.True);
            AssertMatchesEcs(renderer);
            Assert.That(renderer.Stats.Instances, Is.EqualTo(3), "CombatSmoke retains its authored roster: one human and two AI fighters.");
        }
    }
}
#endif
