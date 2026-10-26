#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using static MLFLegends.Tests.PlayMode.SpritePlayModeSupport;
using Object = UnityEngine.Object;

namespace MLFLegends.Tests.PlayMode
{
    // Stage 03 (#38) graphics-device checks: the real URP shader renders into a RenderTexture that is read back (tool:
    // Camera.Render + Texture2D.ReadPixels; PNGs under TestResults/graphics). With -nographics every test is Ignored as
    // NOT RUN. The expected image is computed independently of the renderer: camera projection from the test's own
    // constants (pitch 40°, 2 target pixels per source pixel), colours from the fixture definition, front/back from Z and
    // CombatantId (lower ID in front on equal depth).
    [Category("Graphics")]
    public class CombatSpriteGraphicsTests
    {
        const int Size = 256;
        const float Pitch = 40f;
        const float PixelWorld = 2f / 96f;

        static readonly float CosPitch = math.cos(math.radians(Pitch));
        static readonly float SinPitch = math.sin(math.radians(Pitch));

        // Card rects (left, bottom, right, top) in source pixels relative to the pivot, from the fixture's manifest numbers.
        static readonly Dictionary<string, int4> s_CardRects = new Dictionary<string, int4>
        {
            ["tech_a"] = new int4(-26, 0, 14, 80),
            ["tech_b"] = new int4(0, -10, 60, 30),
            ["tech_c"] = new int4(-66, 20, -16, 120),
        };

        // A support point at the camera's look-at base appears at (128, 40); see Camera().
        const float BaseScreenY = 40f;

        RenderTexture m_Target;
        Texture2D m_Readback;
        Camera m_Camera;
        CombatSpriteRenderer m_Renderer;
        float m_PixelsPerUnit;
        float3 m_LookAt;
        readonly List<Object> m_Owned = new List<Object>();

        struct Fighter
        {
            public float3 Support;
            public FacingDirection Facing;
            public string Frame;
        }

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            RequireGraphicsDevice();
            m_Target = CreateTarget(Size);
            m_Readback = new Texture2D(Size, Size, TextureFormat.RGBA32, false, false);
            SetCamera(float3.zero, 2f);
            m_Renderer = CreateRenderer(m_Camera);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            DestroyAll(m_Renderer, m_Camera, m_Target, m_Readback);
            DestroyAll(m_Owned.ToArray());
            m_Owned.Clear();
            yield return EndBattle();
        }

        /// <summary>
        /// Camera whose target shows a support at <paramref name="support"/> at pixel (128, 40), with
        /// <paramref name="targetPixelsPerSourcePixel"/> target pixels per source pixel (orthographic size from it).
        /// </summary>
        void SetCamera(float3 support, float targetPixelsPerSourcePixel, Vector3 pan = default)
        {
            m_PixelsPerUnit = targetPixelsPerSourcePixel / PixelWorld;
            var up = new float3(0f, CosPitch, SinPitch);
            m_LookAt = support + up * ((Size * 0.5f - BaseScreenY) / m_PixelsPerUnit) + (float3)pan;
            var orthographicSize = Size * 0.5f / m_PixelsPerUnit;
            if (m_Camera == null)
                m_Camera = CreateCamera("Sprite graphics camera", m_LookAt, orthographicSize, m_Target, Pitch);
            else
            {
                var rotation = Quaternion.Euler(Pitch, 0f, 0f);
                m_Camera.transform.SetPositionAndRotation((Vector3)m_LookAt - rotation * Vector3.forward * 30f, rotation);
                m_Camera.orthographicSize = orthographicSize;
            }
        }

        /// <summary>Screen position of a support point (independent projection: camera right = +X, up = (0, cos, sin)).</summary>
        Vector2 ScreenOf(float3 support)
        {
            var d = support - m_LookAt;
            return new Vector2(Size * 0.5f + d.x * m_PixelsPerUnit, Size * 0.5f + math.dot(d, new float3(0f, CosPitch, SinPitch)) * m_PixelsPerUnit);
        }

        float TargetPixelsPerSourcePixel => m_PixelsPerUnit * PixelWorld;

        /// <summary>Fixture colour of fighter <paramref name="f"/> at target pixel (px, py); null where transparent or outside.
        /// <paramref name="ambiguous"/> is set when the sample lies within 0.15 source pixels of a texel/rect edge.</summary>
        Color32? FighterColor(in Fighter f, int px, int py, ref bool ambiguous)
        {
            var screen = ScreenOf(f.Support);
            var k = TargetPixelsPerSourcePixel;
            var cardX = (px + 0.5f - screen.x) / k;
            var cardY = (py + 0.5f - screen.y) / k;
            var authoredX = f.Facing == FacingDirection.Left ? -cardX : cardX; // mirror around the pivot
            var rect = s_CardRects[f.Frame];
            if (authoredX < rect.x - 0.15f || authoredX > rect.z + 0.15f || cardY < rect.y - 0.15f || cardY > rect.w + 0.15f)
                return null;
            var fx = authoredX - math.floor(authoredX);
            var fy = cardY - math.floor(cardY);
            if (fx < 0.15f || fx > 0.85f || fy < 0.15f || fy > 0.85f)
            {
                ambiguous = true;
                return null;
            }

            var lx = (int)math.floor(authoredX) - rect.x;
            var row = rect.w - 1 - (int)math.floor(cardY); // rows counted from the rect's top edge
            if (lx < 0 || row < 0 || lx >= rect.z - rect.x || row >= rect.w - rect.y)
                return null;
            var color = TechnicalAtlasFixture.FramePixel(f.Frame, lx, row);
            return color.a == 0 ? (Color32?)null : color;
        }

        /// <summary>Compares the whole target against fighters composited front to back over the clear colour.</summary>
        void AssertImage(Color32[] pixels, string evidence, params Fighter[] frontToBack)
        {
            SaveEvidence(m_Readback, evidence);
            var compared = 0;
            var mismatches = new List<string>();
            for (var py = 0; py < Size; py++)
            for (var px = 0; px < Size; px++)
            {
                var ambiguous = false;
                Color32? expected = null;
                foreach (var fighter in frontToBack)
                {
                    var color = FighterColor(fighter, px, py, ref ambiguous);
                    if (ambiguous)
                        break;
                    if (color.HasValue)
                    {
                        expected = color;
                        break;
                    }
                }

                var actual = pixels[py * Size + px];
                Assert.That(IsSentinel(actual), Is.False, $"Magenta page padding sampled at ({px},{py}) [{evidence}]");
                if (ambiguous)
                    continue;
                compared++;
                var want = expected ?? new Color32(0, 0, 0, 255);
                if (!Near(actual, want) && mismatches.Count < 10)
                    mismatches.Add($"({px},{py}) expected {want} got {actual}");
            }

            Assert.That(mismatches, Is.Empty, $"[{evidence}] {string.Join("; ", mismatches)}");
            Assert.That(compared, Is.GreaterThan(Size * Size / 2), "Most pixels are unambiguous.");
        }

        static bool Near(Color32 a, Color32 b) => math.abs(a.r - b.r) <= 2 && math.abs(a.g - b.g) <= 2 && math.abs(a.b - b.b) <= 2;

        static bool IsSentinel(Color32 c) => c.r > 240 && c.g < 15 && c.b > 240;

        Color32[] Render() => RenderAndRead(m_Renderer, m_Camera, m_Readback);

        Color32 PixelAt(Color32[] pixels, in Fighter f, float cardX, float cardY)
        {
            var screen = ScreenOf(f.Support);
            var k = TargetPixelsPerSourcePixel;
            var x = (int)math.floor(screen.x + (f.Facing == FacingDirection.Left ? -cardX : cardX) * k);
            var y = (int)math.floor(screen.y + cardY * k);
            return pixels[y * Size + x];
        }

        CombatantId Human => Session.HumanCombatant;

        CombatantId Ai => Session.AiCombatant;

        // ---------------------------------------------------------------- shader, UV, trim, flip, frames, pages

        [UnityTest]
        public IEnumerator TrimFlipColoursHolesAndFramesLandOnTheExpectedPixels()
        {
            var human = new Fighter { Support = float3.zero, Facing = FacingDirection.Right, Frame = "tech_a" };
            var ai = new Fighter { Support = new float3(0.75f, 0f, 0f), Facing = FacingDirection.Left, Frame = "tech_a" }; // 72 px right, fully inside the target
            yield return RestartBattle(Duel(human.Support, human.Facing, ai.Support, ai.Facing));
            var pixels = Render();
            Assert.That(m_Renderer.Stats.Submissions, Is.EqualTo(1), "Same page: one draw for both.");
            AssertImage(pixels, "01-tech_a-right-and-left", human, ai);
            AssertColor(PixelAt(pixels, human, -20.5f, 40.5f), TechnicalAtlasFixture.Red, "Authored left half is red…");
            AssertColor(PixelAt(pixels, ai, -20.5f, 40.5f), TechnicalAtlasFixture.Red, "…and mirrored for the left-facing fighter.");
            AssertColor(PixelAt(pixels, human, -5.5f, 35.5f), new Color32(0, 0, 0, 255), "The transparent hole shows the background.");

            // Different frames per instance, two pages: tech_b hangs below the pivot, tech_c lives on page 1.
            Assert.That(m_Renderer.SetFrame(Human, "tech_b"), Is.True);
            Assert.That(m_Renderer.SetFrame(Ai, "tech_c"), Is.True);
            human.Frame = "tech_b";
            ai.Frame = "tech_c";
            pixels = Render();
            Assert.That(m_Renderer.Stats.Submissions, Is.EqualTo(2), "One draw per page, not per fighter.");
            AssertImage(pixels, "02-tech_b-and-tech_c-two-pages", human, ai);
            AssertColor(PixelAt(pixels, human, 30.5f, -5.5f), TechnicalAtlasFixture.Cyan, "Below the pivot is drawn too.");

            // Same page, different frames in one batch.
            Assert.That(m_Renderer.SetFrame(Ai, "tech_a"), Is.True);
            ai.Frame = "tech_a";
            pixels = Render();
            Assert.That(m_Renderer.Stats.Submissions, Is.EqualTo(1));
            AssertImage(pixels, "03-tech_b-and-tech_a-one-batch", human, ai);
        }

        // ---------------------------------------------------------------- depth between sprites

        static float ShiftZ(int pixels, float targetPixelsPerSourcePixel) => pixels * PixelWorld / targetPixelsPerSourcePixel / SinPitch;

        [UnityTest]
        public IEnumerator NearerFightersCoverFartherOnesAcrossPagesAndTiesFollowTheId()
        {
            var behind = ShiftZ(20, 2f); // 20 target pixels higher on screen, farther away
            var near = new Fighter { Support = float3.zero, Facing = FacingDirection.Right, Frame = "tech_c" };
            var far = new Fighter { Support = new float3(0f, 0f, behind), Facing = FacingDirection.Left, Frame = "tech_a" };

            // Human near on page 1, AI far on page 0.
            yield return RestartBattle(Duel(near.Support, near.Facing, far.Support, far.Facing));
            m_Renderer.SetFrame(Human, "tech_c");
            AssertImage(Render(), "10-near-page1-over-far-page0", near, far);

            // Swap depth: the AI (page 0) is now near; draw/page order does not matter.
            yield return RestartBattle(Duel(far.Support, far.Facing, near.Support, near.Facing));
            m_Renderer.SetFrame(Ai, "tech_c");
            AssertImage(Render(), "11-near-page1-over-far-page0-swapped-ids", near, far);
            m_Renderer.SetFrame(Ai, "tech_a");
            m_Renderer.SetFrame(Human, "tech_c");
            var nearA = new Fighter { Support = near.Support, Facing = near.Facing, Frame = "tech_a" };
            var farC = new Fighter { Support = far.Support, Facing = far.Facing, Frame = "tech_c" };
            AssertImage(Render(), "12-near-page0-over-far-page1", nearA, farC);

            // Equal depth (same Z, overlapping): the lower CombatantId (human) is in front, whatever the pages; stable over frames.
            var left = new Fighter { Support = float3.zero, Facing = FacingDirection.Right, Frame = "tech_c" };
            var right = new Fighter { Support = new float3(-0.25f, 0f, 0f), Facing = FacingDirection.Right, Frame = "tech_a" };
            yield return RestartBattle(Duel(left.Support, left.Facing, right.Support, right.Facing));
            m_Renderer.SetFrame(Human, "tech_c");
            for (var frame = 0; frame < 3; frame++)
            {
                AssertImage(Render(), $"13-equal-depth-human-front-{frame}", left, right);
                yield return null;
            }

            m_Renderer.SetFrame(Human, "tech_a");
            m_Renderer.SetFrame(Ai, "tech_c");
            left.Frame = "tech_a";
            right.Frame = "tech_c";
            AssertImage(Render(), "14-equal-depth-pages-swapped-human-still-front", left, right);

            // Identical position.
            var same = new Fighter { Support = new float3(0.125f, 0f, 0f), Facing = FacingDirection.Left, Frame = "tech_a" };
            yield return RestartBattle(Duel(same.Support, FacingDirection.Left, same.Support, FacingDirection.Right));
            m_Renderer.SetFrame(Ai, "tech_c");
            var sameAi = new Fighter { Support = same.Support, Facing = FacingDirection.Right, Frame = "tech_c" };
            AssertImage(Render(), "15-identical-position-human-front", same, sameAi);
        }

        [UnityTest]
        public IEnumerator AFighterPassingThroughAnotherInDepthSwapsFrontExactlyAtEqualDepth()
        {
            // The AI steps from 16 target pixels behind the human to 16 in front (2 px per step), across pages; at equal depth
            // the human (lower ID) stays in front, so the front changes once, between equal and nearer.
            var human = new Fighter { Support = float3.zero, Facing = FacingDirection.Right, Frame = "tech_c" };
            for (var step = 8; step >= -8; step -= 2)
            {
                var ai = new Fighter { Support = new float3(-0.25f, 0f, ShiftZ(step * 2, 2f)), Facing = FacingDirection.Right, Frame = "tech_a" };
                yield return RestartBattle(Duel(human.Support, human.Facing, ai.Support, ai.Facing));
                m_Renderer.SetFrame(Human, "tech_c");
                var pixels = Render();
                if (step >= 0)
                    AssertImage(pixels, $"16-crossing-ai-behind-{step}", human, ai);
                else
                    AssertImage(pixels, $"16-crossing-ai-front-{-step}", ai, human);
            }
        }

        [UnityTest]
        public IEnumerator JumpPanAndZoomKeepTheDepthOrder()
        {
            // The AI is airborne (Y from the ECS position only) and nearer; the human stands farther away.
            var lift = 74f / (CosPitch * 96f); // 74 target pixels up at 2 px per source pixel
            var airborne = new Fighter { Support = new float3(0f, lift, 0f), Facing = FacingDirection.Left, Frame = "tech_a" };
            var standing = new Fighter { Support = new float3(-0.125f, 0f, ShiftZ(60, 2f)), Facing = FacingDirection.Right, Frame = "tech_c" };
            yield return RestartBattle(Duel(standing.Support, standing.Facing, airborne.Support, airborne.Facing));
            m_Renderer.SetFrame(Human, "tech_c");
            AssertImage(Render(), "20-airborne-near-over-standing-far", airborne, standing);

            SetCamera(float3.zero, 2f, pan: new Vector3(10f * PixelWorld / 2f * 2f, 0f, 0f));
            AssertImage(Render(), "21-panned", airborne, standing);
            SetCamera(float3.zero, 3f);
            AssertImage(Render(), "22-zoomed-3px", airborne, standing);
            SetCamera(float3.zero, 1f);
            AssertImage(Render(), "23-zoomed-1px", airborne, standing);
        }

        // ---------------------------------------------------------------- 3D geometry

        GameObject Box(string name, Vector3 center, Vector3 size, Color32 color)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetPositionAndRotation(center, Quaternion.identity);
            box.transform.localScale = size;
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            material.SetColor("_BaseColor", color);
            box.GetComponent<MeshRenderer>().sharedMaterial = material;
            m_Owned.Add(box);
            m_Owned.Add(material);
            return box;
        }

        static readonly Color32 Floor = new Color32(0, 80, 0, 255);
        static readonly Color32 Wall = new Color32(90, 90, 90, 255);
        static readonly Color32 Front = new Color32(160, 160, 160, 255);

        static bool Close(Color32 a, Color32 b) => Near(a, b);

        static void AssertColor(Color32 actual, Color32 expected, string message) =>
            Assert.That(Near(actual, expected), Is.True, $"{message} expected {expected}, got {actual}");

        [UnityTest]
        public IEnumerator OpaqueGeometryOccludesInFrontAndNotBehindAndFeetStayOnTheFloor()
        {
            var fighter = new Fighter { Support = float3.zero, Facing = FacingDirection.Right, Frame = "tech_a" };
            yield return RestartBattle(Duel(fighter.Support, fighter.Facing, new float3(5f, 0f, 0f), FacingDirection.Left));
            Box("Floor", new Vector3(0f, -0.05f, 0f), new Vector3(20f, 0.1f, 20f), Floor);
            Box("Wall behind", new Vector3(0f, 1.5f, 0.9f), new Vector3(3f, 3f, 0.2f), Wall);
            var pixels = Render();
            SaveEvidence(m_Readback, "30-floor-and-wall-behind");
            AssertColor(PixelAt(pixels, fighter, -20.5f, 40.5f), TechnicalAtlasFixture.Red, "Body over the wall behind it.");
            AssertColor(PixelAt(pixels, fighter, -20.5f, 75.5f), TechnicalAtlasFixture.Blue, "Even the head (an upright card, not a tilted one).");
            AssertColor(PixelAt(pixels, fighter, 10.5f, 0.5f), TechnicalAtlasFixture.Green, "The lowest row at the pivot is not swallowed by the floor.");
            Assert.That(Close(PixelAt(pixels, fighter, -40.5f, 40.5f), Wall), Is.True, "Beside the sprite the wall is visible.");
            Assert.That(Close(PixelAt(pixels, fighter, -5.5f, 35.5f), Wall) || Close(PixelAt(pixels, fighter, -5.5f, 35.5f), Floor), Is.True,
                "Transparent pixels do not occlude what is behind them (no rectangular occluder).");
            Assert.That(Close(PixelAt(pixels, fighter, 20.5f, -15.5f), Floor), Is.True, "Floor in front of the feet.");

            // Below the pivot (tech_b's bottom 10 rows) lies on the floor and stays visible over it.
            m_Renderer.SetFrame(Human, "tech_b");
            var low = new Fighter { Support = fighter.Support, Facing = fighter.Facing, Frame = "tech_b" };
            pixels = Render();
            SaveEvidence(m_Readback, "31-below-pivot-on-floor");
            AssertColor(PixelAt(pixels, low, 30.5f, -5.5f), TechnicalAtlasFixture.Cyan, "No sinking below the floor.");
            AssertColor(PixelAt(pixels, low, 30.5f, -9.5f), TechnicalAtlasFixture.Cyan, "The lowest row below the pivot.");

            // A box in front covers the lower body only.
            m_Renderer.SetFrame(Human, "tech_a");
            Box("Box in front", new Vector3(0f, 0.75f, -1f), new Vector3(3f, 1.5f, 0.2f), Front);
            pixels = Render();
            SaveEvidence(m_Readback, "32-box-in-front");
            Assert.That(Close(PixelAt(pixels, fighter, -20.5f, 10.5f), Front), Is.True, "Lower body hidden behind the box in front.");
            AssertColor(PixelAt(pixels, fighter, -20.5f, 60.5f), TechnicalAtlasFixture.Red, "Upper body above the box stays visible.");
        }
    }
}
#endif
