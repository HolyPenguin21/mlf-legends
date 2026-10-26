using System.Collections.Generic;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace MLFLegends.Tests.EditMode
{
    // Stage 03 (#38): rect/UV/trim/pivot/flip/anchor math with concrete numbers from the technical fixture (not a
    // re-statement of the implementation), card basis/projection, depth tie-break and page binding of SpriteAtlas.
    public class SpriteCardMathTests
    {
        const float Eps = 1e-5f;
        static readonly float2 Pivot = new float2(96f, 160f);
        const float S = 2f / 96f; // start calibration: 96 px = 2 world units

        static void Near(float3 actual, float3 expected, string message = null) =>
            Assert.That(math.distance(actual, expected), Is.LessThan(1e-4f), $"{message} {actual} vs {expected}");

        [Test]
        public void TrimmedFramesKeepTheirPlaceRelativeToThePivot()
        {
            // tech_a: 40×80 at canvas (70,80) → x −26…14 (asymmetric), y 0…80 above the pivot.
            Assert.That(SpriteCardMath.FrameCardRect(new int4(4, 4, 40, 80), new int2(70, 80), Pivot), Is.EqualTo(new float4(-26f, 0f, 14f, 80f)));
            // tech_b: 60×40 at (96,130) → hangs 10 px below the pivot.
            Assert.That(SpriteCardMath.FrameCardRect(new int4(52, 4, 60, 40), new int2(96, 130), Pivot), Is.EqualTo(new float4(0f, -10f, 60f, 30f)));
            // tech_c: 50×100 at (30,40) → entirely left of and above the pivot.
            Assert.That(SpriteCardMath.FrameCardRect(new int4(8, 8, 50, 100), new int2(30, 40), Pivot), Is.EqualTo(new float4(-66f, 20f, -16f, 120f)));
            // Where the rect sits on its page never moves the support point.
            Assert.That(SpriteCardMath.FrameCardRect(new int4(300, 900, 40, 80), new int2(70, 80), Pivot), Is.EqualTo(new float4(-26f, 0f, 14f, 80f)));
            // Fractional pivots are continuous coordinates.
            Assert.That(SpriteCardMath.FrameCardRect(new int4(0, 0, 10, 10), new int2(0, 0), new float2(2.5f, 7.5f)), Is.EqualTo(new float4(-2.5f, -2.5f, 7.5f, 7.5f)));
        }

        [Test]
        public void UvFlipsTheTopLeftOriginExactlyOnce()
        {
            var uv = SpriteCardMath.FrameUv(new int4(4, 4, 40, 80), new int2(128, 128));
            Assert.That(uv.x, Is.EqualTo(4f / 128f).Within(Eps));
            Assert.That(uv.y, Is.EqualTo(44f / 128f).Within(Eps), "v0 = bottom edge = 1 − (y + h) / H");
            Assert.That(uv.z, Is.EqualTo(44f / 128f).Within(Eps));
            Assert.That(uv.w, Is.EqualTo(124f / 128f).Within(Eps), "v1 = top edge = 1 − y / H");
            // A non-square page uses its own size per axis.
            var c = SpriteCardMath.FrameUv(new int4(8, 8, 50, 100), new int2(96, 160));
            Assert.That(c, Is.EqualTo(new float4(8f / 96f, 52f / 160f, 58f / 96f, 152f / 160f)));
        }

        [Test]
        public void FlipMirrorsAroundThePivotAndTwiceIsIdentity()
        {
            var corner = new float2(70f, 80f); // tech_a top-left on the canvas
            Assert.That(SpriteCardMath.CanvasToCard(corner, Pivot, FacingDirection.Right), Is.EqualTo(new float2(-26f, 80f)));
            Assert.That(SpriteCardMath.CanvasToCard(corner, Pivot, FacingDirection.Left), Is.EqualTo(new float2(26f, 80f)),
                "x' = W − x, pivot mirrored too: the offset changes sign, y stays.");
            // Contract formula on a non-centred pivot: mirrored canvas point and mirrored pivot.
            var pivot = new float2(80f, 150f);
            var p = new float2(120f, 40f);
            var mirrored = SpriteCardMath.CanvasToCard(new float2(192f - p.x, p.y), new float2(192f - pivot.x, pivot.y), FacingDirection.Right);
            Assert.That(SpriteCardMath.CanvasToCard(p, pivot, FacingDirection.Left), Is.EqualTo(mirrored));
            var twice = SpriteCardMath.CanvasToCard(p, pivot, FacingDirection.Left) * new float2(-1f, 1f);
            Assert.That(twice, Is.EqualTo(SpriteCardMath.CanvasToCard(p, pivot, FacingDirection.Right)));
            Assert.That(SpriteCardMath.CanvasToCard(Pivot, Pivot, FacingDirection.Left), Is.EqualTo(float2.zero), "The pivot is the support for both facings.");
        }

        [Test]
        public void AnchorsUseTheBodyTransformAndMirrorTheirDirection()
        {
            // tech_a hand_right: (108,118), direction (1, 0.5) in canvas axes.
            Assert.That(SpriteCardMath.CanvasToCard(new float2(108f, 118f), Pivot, FacingDirection.Right), Is.EqualTo(new float2(12f, 42f)));
            Assert.That(SpriteCardMath.CanvasToCard(new float2(108f, 118f), Pivot, FacingDirection.Left), Is.EqualTo(new float2(-12f, 42f)));
            Assert.That(SpriteCardMath.CanvasDirectionToCard(new float2(1f, 0.5f), FacingDirection.Right), Is.EqualTo(new float2(1f, -0.5f)));
            Assert.That(SpriteCardMath.CanvasDirectionToCard(new float2(1f, 0.5f), FacingDirection.Left), Is.EqualTo(new float2(-1f, -0.5f)));

            var basis = Basis(40f, 0f);
            var support = new float3(1f, 0f, 2f);
            var right = SpriteCardMath.CanvasToWorld(basis, support, new float2(108f, 118f), Pivot, FacingDirection.Right, S);
            Near(right, new float3(1f + 12f * S, 42f * S / math.cos(math.radians(40f)), 2f), "Above the pivot: on the vertical card.");
            var left = SpriteCardMath.CanvasToWorld(basis, support, new float2(108f, 118f), Pivot, FacingDirection.Left, S);
            Near(left, new float3(1f - 12f * S, right.y, 2f));
            var below = SpriteCardMath.CanvasToWorld(basis, support, new float2(96f, 170f), Pivot, FacingDirection.Right, S);
            Near(below, new float3(1f, 0f, 2f - 10f * S / math.sin(math.radians(40f))), "Below the pivot: on the ground, towards the camera.");
            var direction = SpriteCardMath.CanvasDirectionToWorld(basis, new float2(1f, 0f), FacingDirection.Left);
            Near(direction, new float3(-1f, 0f, 0f), "A forward hand points −X when facing left.");
        }

        static SpriteCardBasis Basis(float pitch, float yaw)
        {
            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            return SpriteCardBasis.FromCamera(rotation * Vector3.forward, rotation * Vector3.up);
        }

        [TestCase(40f, 0f)]
        [TestCase(40f, 90f)]
        [TestCase(25f, -30f)]
        [TestCase(70f, 180f)]
        public void TheCardProjectsEachSourcePixelToAScreenSquare(float pitch, float yaw)
        {
            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            var camRight = (float3)(rotation * Vector3.right);
            var camUp = (float3)(rotation * Vector3.up);
            var camForward = (float3)(rotation * Vector3.forward);
            var basis = Basis(pitch, yaw);
            var support = new float3(3f, 1.5f, -4f);
            foreach (var card in new[] { new float2(-26f, 80f), new float2(14f, 0f), new float2(60f, -10f), new float2(-66f, 120f), new float2(0f, -32f) })
            {
                var offset = SpriteCardMath.CardToWorld(basis, support, card, S) - support;
                Assert.That(math.dot(offset, camRight), Is.EqualTo(card.x * S).Within(1e-4f), $"screen x of {card}");
                Assert.That(math.dot(offset, camUp), Is.EqualTo(card.y * S).Within(1e-4f), $"screen y of {card}: the art is not tilted twice");
                if (card.y >= 0f)
                    Assert.That(math.length(offset - basis.Right * card.x * S - new float3(0f, offset.y, 0f)), Is.LessThan(1e-4f), "Upright above the support.");
                else
                    Assert.That(offset.y, Is.EqualTo(0f).Within(1e-5f), "Below the support it lies at support height.");

                var pushed = SpriteCardMath.CardToWorld(basis, support, card, S, depthOffset: 0.3f) - support;
                Assert.That(math.dot(pushed, camRight), Is.EqualTo(card.x * S).Within(1e-4f), "A depth push never moves the image.");
                Assert.That(math.dot(pushed, camUp), Is.EqualTo(card.y * S).Within(1e-4f));
                Assert.That(math.dot(pushed - offset, camForward), Is.EqualTo(-0.3f).Within(1e-4f), "…only brings it closer.");
            }
        }

        [Test]
        public void DepthKeyIsSharedByTheWholeCardAndIgnoresHeight()
        {
            var basis = Basis(40f, 0f);
            Assert.That(basis.DepthKey(new float3(5f, 3f, 2f)), Is.EqualTo(basis.DepthKey(new float3(-5f, 0f, 2f))).Within(Eps),
                "Jumping (Y) or walking along X does not change the depth of an upright card.");
            Assert.That(basis.DepthKey(new float3(0f, 0f, 2f)), Is.GreaterThan(basis.DepthKey(new float3(0f, 0f, 1f))), "Larger Z is farther (yaw 0).");
        }

        [Test]
        public void EqualDepthIsOrderedByCombatantIdWhateverTheInputOrder()
        {
            const float sep = 0.002f;
            var cases = new (float key, uint id)[] { (10f, 5), (10f, 3), (10f, 9), (12f, 1), (4f, 7), (10.0005f, 8) };
            var expected = Offsets(cases, sep);
            // 12 is alone; 10.0005 (id 8) is the farthest of the cluster, then equal 10s by id 9, 5, 3 (lowest in front).
            Assert.That(expected[1], Is.EqualTo(0f), "Separated cards are not pushed.");
            Assert.That(expected[7], Is.EqualTo(0f));
            Assert.That(expected[8], Is.EqualTo(0f));
            Assert.That(expected[9], Is.EqualTo(sep - 0.0005f).Within(1e-5f));
            Assert.That(expected[5], Is.EqualTo(2f * sep - 0.0005f).Within(1e-5f));
            Assert.That(expected[3], Is.EqualTo(3f * sep - 0.0005f).Within(1e-5f));

            var random = new System.Random(38);
            for (var round = 0; round < 20; round++)
            {
                var shuffled = cases.OrderBy(_ => random.Next()).ToArray();
                var offsets = Offsets(shuffled, sep);
                foreach (var id in expected.Keys)
                    Assert.That(offsets[id], Is.EqualTo(expected[id]), "Independent of entity/chunk order.");
            }
        }

        [Test]
        public void TheTieBreakPushIsBoundedByCountTimesSeparation()
        {
            var cases = Enumerable.Range(1, 400).Select(i => (0f, (uint)i)).ToArray();
            var offsets = Offsets(cases, 0.002f, out var max);
            Assert.That(max, Is.EqualTo(399 * 0.002f).Within(1e-4f), "Worst case: all 400 at one depth.");
            Assert.That(offsets[1], Is.EqualTo(max), "Lowest ID in front.");
            Assert.That(offsets[400], Is.EqualTo(0f));
        }

        static Dictionary<uint, float> Offsets((float key, uint id)[] cases, float separation) => Offsets(cases, separation, out _);

        static Dictionary<uint, float> Offsets((float key, uint id)[] cases, float separation, out float max)
        {
            using var keys = new NativeArray<float>(cases.Select(c => c.key).ToArray(), Allocator.Temp);
            using var ids = new NativeArray<uint>(cases.Select(c => c.id).ToArray(), Allocator.Temp);
            using var offsets = new NativeArray<float>(cases.Length, Allocator.Temp);
            max = CombatSpriteExtraction.ComputeDepthOffsets(keys, ids, separation, offsets);
            var result = new Dictionary<uint, float>();
            for (var i = 0; i < cases.Length; i++)
                result[cases[i].id] = offsets[i];
            return result;
        }

        // ---------------------------------------------------------------- SpriteAtlas page binding

        [Test]
        public void TheFixtureLoadsWithSharedTablesAndMeasuredMemory()
        {
            var pages = TechnicalAtlasFixture.CreatePages();
            try
            {
                var errors = new List<string>();
                var warnings = new List<string>();
                using var atlas = SpriteAtlas.Load(TechnicalAtlasFixture.ManifestJson, TechnicalAtlasFixture.Bind(pages), errors, warnings);
                Assert.That(errors, Is.Empty, string.Join("\n", errors));
                Assert.That(warnings, Is.Empty, string.Join("\n", warnings));
                Assert.That(atlas.FrameCount, Is.EqualTo(3));
                Assert.That(atlas.PageCount, Is.EqualTo(2));
                Assert.That(atlas.PageBytes, Is.EqualTo(128 * 128 * 4 + 96 * 160 * 4), "W × H × 4 per page, no mipmaps.");
                Assert.That(atlas.FrameTableBytes, Is.EqualTo(3 * 32));
                Assert.That(SpriteAtlas.FrameStride, Is.EqualTo(32));
                Assert.That(CombatSpriteExtraction.InstanceStride, Is.EqualTo(24));
                Assert.That(atlas.Frames[0].CardRect, Is.EqualTo(new float4(-26f, 0f, 14f, 80f)));
                Assert.That(atlas.Frames[2].Uv, Is.EqualTo(SpriteCardMath.FrameUv(new int4(8, 8, 50, 100), new int2(96, 160))));
                Assert.That(atlas.FramePages.ToArray(), Is.EqualTo(new[] { 0, 0, 1 }));
                Assert.That(atlas.MaxHalfWidthPx, Is.EqualTo(66f), "tech_c reaches 66 px left; flip needs it on both sides.");
                Assert.That(atlas.MaxTopPx, Is.EqualTo(120f), "Bounds use the real trimmed extents, not nominal_height_px.");
                Assert.That(atlas.MaxBelowPx, Is.EqualTo(10f));
                Assert.That(atlas.GetPage(1), Is.SameAs(pages[1]));
                Assert.That(atlas.GpuBuffersCreated, Is.EqualTo(0), "No GPU resource until a renderer draws.");
                atlas.Dispose();
                atlas.Dispose();
                Assert.That(atlas.IsDisposed, Is.True);
                Assert.That(pages[0] != null, Is.True, "Disposing the atlas never destroys page textures it does not own.");
            }
            finally
            {
                foreach (var page in pages)
                    Object.DestroyImmediate(page);
            }
        }

        [Test]
        public void PageBindingErrorsRejectTheWholeAtlas()
        {
            var pages = TechnicalAtlasFixture.CreatePages();
            Texture2D extra = null;
            try
            {
                extra = new Texture2D(128, 128, TextureFormat.DXT5, false);
            }
            catch (System.Exception)
            {
                // The device cannot create a compressed texture: that case is skipped, the others still run.
            }

            var mipped = new Texture2D(128, 128, TextureFormat.RGBA32, mipChain: true, linear: true);
            try
            {
                void Rejects(SpriteAtlasPageBinding[] bindings, string expected)
                {
                    var errors = new List<string>();
                    Assert.That(SpriteAtlas.Load(TechnicalAtlasFixture.ManifestJson, bindings, errors, null), Is.Null, expected);
                    Assert.That(errors, Has.Some.Contains(expected), string.Join("\n", errors));
                }

                var p0 = TechnicalAtlasFixture.Page0File;
                var p1 = TechnicalAtlasFixture.Page1File;
                Rejects(new[] { new SpriteAtlasPageBinding(p0, pages[0]) }, "pages[1] \"technical_page1.png\": no binding");
                Rejects(new[] { new SpriteAtlasPageBinding(p0, pages[0]), new SpriteAtlasPageBinding("other.png", pages[1]) }, "file \"other.png\" is not a page");
                Rejects(new[] { new SpriteAtlasPageBinding(p0, pages[0]), new SpriteAtlasPageBinding(p0, pages[0]), new SpriteAtlasPageBinding(p1, pages[1]) }, "is bound twice");
                Rejects(new[] { new SpriteAtlasPageBinding(p0, pages[1]), new SpriteAtlasPageBinding(p1, pages[0]) }, "is 96x160, manifest says 128x128");
                Rejects(new[] { new SpriteAtlasPageBinding(p0, null), new SpriteAtlasPageBinding(p1, pages[1]) }, "no texture assigned");
                if (extra != null)
                    Rejects(new[] { new SpriteAtlasPageBinding(p0, extra), new SpriteAtlasPageBinding(p1, pages[1]) }, "compressed");
                Rejects(null, "no binding");
                var opaque = new Texture2D(128, 128, TextureFormat.RGB24, false);
                try
                {
                    Rejects(new[] { new SpriteAtlasPageBinding(p0, opaque), new SpriteAtlasPageBinding(p1, pages[1]) }, "has no alpha channel");
                }
                finally
                {
                    Object.DestroyImmediate(opaque);
                }

                var errors = new List<string>();
                Assert.That(SpriteAtlas.Load("{}", TechnicalAtlasFixture.Bind(pages), errors, null), Is.Null, "A broken manifest rejects before binding.");

                var warnings = new List<string>();
                errors.Clear();
                using var warned = SpriteAtlas.Load(TechnicalAtlasFixture.ManifestJson,
                    new[] { new SpriteAtlasPageBinding(p0, mipped), new SpriteAtlasPageBinding(p1, pages[1]) }, errors, warnings);
                Assert.That(warned, Is.Not.Null, "Mipmaps and linear data only warn.");
                Assert.That(warnings, Has.Some.Contains("mipmaps"));
                Assert.That(warnings, Has.Some.Contains("not sRGB"));
            }
            finally
            {
                foreach (var page in pages)
                    Object.DestroyImmediate(page);
                if (extra != null)
                    Object.DestroyImmediate(extra);
                Object.DestroyImmediate(mipped);
            }
        }
    }
}
