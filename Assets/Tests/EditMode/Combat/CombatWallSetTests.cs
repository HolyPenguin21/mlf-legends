using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Mathematics;

namespace MLFLegends.Tests.EditMode
{
    // #70: the final velocity must satisfy every active wall constraint at once (exact projection onto their cone), keep the
    // tangent along a single wall and never depend on normal order; the set itself stays minimal instead of dropping normals.
    public class CombatWallSetTests
    {
        static readonly float S = math.sqrt(.5f);

        static CombatWallSet Set(params float2[] normals)
        {
            var set = new CombatWallSet();
            foreach (var n in normals) set.Add(n);
            return set;
        }

        static void Satisfies(CombatWallSet set, float2[] normals, float2 v)
        {
            foreach (var n in normals)
                Assert.That(math.dot(v, math.normalize(n)), Is.GreaterThanOrEqualTo(-1e-5f * math.max(1, math.length(v))), $"{v} into {n}");
        }

        // Manager's numeric counterexample: two sequential passes of the old ClipWalls returned (-3,-3), and after ground drag
        // (-2.717,-2.717) still pointed into the first wall.
        [Test]
        public void AcuteCornerLeavesNoVelocityIntoEitherWall()
        {
            var normals = new[] { new float2(1, 0), new float2(-S, S) };
            var v = Set(normals).Clip(new float2(-12, -12));
            Satisfies(Set(normals), normals, v);
            Assert.That(v, Is.EqualTo(float2.zero), "In an acute corner the nearest allowed velocity is the apex.");
            var w = Set(normals).Clip(new float2(-12, 20));
            Satisfies(Set(normals), normals, w);
            Assert.That(w.x, Is.EqualTo(0).Within(1e-5)); Assert.That(w.y, Is.EqualTo(20).Within(1e-5), "Tangent along the first wall stays.");
        }

        [Test]
        public void ObtuseAndRightCornersKeepTheAllowedTangent()
        {
            Assert.That(Set(new float2(1, 0), new float2(0, 1)).Clip(new float2(-12, 5)), Is.EqualTo(new float2(0, 5)));
            var obtuse = Set(new float2(1, 0), new float2(S, S)).Clip(new float2(-12, 3));
            Assert.That(obtuse.x, Is.EqualTo(0).Within(1e-6)); Assert.That(obtuse.y, Is.EqualTo(3).Within(1e-6));
            var along = Set(new float2(1, 0), new float2(S, S)).Clip(new float2(-1, -9));
            Satisfies(default, new[] { new float2(1, 0), new float2(S, S) }, along);
            Assert.That(along.x, Is.EqualTo(4).Within(1e-5)); Assert.That(along.y, Is.EqualTo(-4).Within(1e-5), "Slides along the second wall, not stopped.");
        }

        [Test]
        public void SingleWallKeepsTangentAndNeverStopsTheBody()
        {
            var wall = Set(new float2(1, 0));
            Assert.That(wall.Clip(new float2(-3, 4)), Is.EqualTo(new float2(0, 4)));
            Assert.That(wall.Clip(new float2(3, 4)), Is.EqualTo(new float2(3, 4)), "Moving away is untouched.");
            Assert.That(new CombatWallSet().Clip(new float2(-3, 4)), Is.EqualTo(new float2(-3, 4)));
        }

        [Test]
        public void ResultDoesNotDependOnNormalOrder()
        {
            var normals = new[] { new float2(1, 0), new float2(-S, S), new float2(.2f, 1), new float2(-1, .3f) };
            var velocities = new[] { new float2(-12, -12), new float2(5, -7), new float2(-3, 9), new float2(.5f, -.1f) };
            var orders = new[] { new[] { 0, 1, 2, 3 }, new[] { 3, 2, 1, 0 }, new[] { 1, 3, 0, 2 }, new[] { 2, 0, 3, 1 } };
            foreach (var v in velocities)
            {
                var expected = Set(orders[0].Select(i => normals[i]).ToArray()).Clip(v);
                foreach (var order in orders)
                {
                    var actual = Set(order.Select(i => normals[i]).ToArray()).Clip(v);
                    Assert.That(math.distance(actual, expected), Is.LessThan(1e-5f), $"{v} order {string.Join(",", order)}");
                }
            }
        }

        // A third distinct constraint is kept (corridor + end wall = ray), never silently dropped.
        [Test]
        public void ThirdConstraintIsKeptForCorridorEnd()
        {
            var corridor = Set(new float2(1, 0), new float2(-1, 0));
            Assert.That(corridor.Clip(new float2(5, 7)), Is.EqualTo(new float2(0, 7)), "A corridor keeps motion along it.");
            var end = Set(new float2(1, 0), new float2(-1, 0), new float2(0, 1));
            Assert.That(end.Count, Is.EqualTo(3)); Assert.That(end.Overflow, Is.False);
            Assert.That(end.Clip(new float2(3, -4)), Is.EqualTo(float2.zero));
            Assert.That(end.Clip(new float2(3, 4)), Is.EqualTo(new float2(0, 4)));
        }

        // Sliding round a rounded box corner produces many normals; only the two extremes bound the cone, so no overflow and
        // no loss: every intermediate normal is still satisfied.
        [Test]
        public void RoundedCornerArcKeepsOnlyItsExtremes()
        {
            var normals = Enumerable.Range(0, 81).Select(d => new float2(math.cos(math.radians(d)), math.sin(math.radians(d)))).ToArray();
            var set = Set(normals);
            Assert.That(set.Count, Is.EqualTo(2)); Assert.That(set.Overflow, Is.False);
            foreach (var v in new[] { new float2(-12, -12), new float2(-1, 9), new float2(9, -1), new float2(-5, 2) })
                Satisfies(set, normals, set.Clip(v));
        }

        [Test]
        public void PointConeStopsAndIgnoresFurtherNormalsWithoutOverflow()
        {
            var set = Set(new float2(1, 0), new float2(-.5f, math.sqrt(.75f)), new float2(-.5f, -math.sqrt(.75f)), new float2(0, 1),
                new float2(S, -S), new float2(-1, .1f));
            Assert.That(set.Overflow, Is.False); Assert.That(set.Count, Is.EqualTo(3));
            Assert.That(set.Clip(new float2(4, -2)), Is.EqualTo(float2.zero));
        }

        [Test]
        public void InvalidNormalsAreIgnoredAndOverflowStopsExplicitly()
        {
            var set = Set(new float2(float.NaN, 0), float2.zero, new float2(1e-12f, 0));
            Assert.That(set.Count, Is.Zero);
            var overflow = new CombatWallSet { Overflow = true };
            Assert.That(overflow.Clip(new float2(3, 4)), Is.EqualTo(float2.zero), "Unsupported input stops the body, diagnosed by the owner.");
        }

        // Exact projection: feasible, and no sampled feasible velocity is closer to the input.
        [Test]
        public void ClipIsTheNearestAllowedVelocityForRandomConstraints()
        {
            var random = new Random(70);
            for (var trial = 0; trial < 300; trial++)
            {
                var normals = Enumerable.Range(0, random.NextInt(1, 5)).Select(_ =>
                { var a = random.NextFloat(0, 2 * math.PI); return new float2(math.cos(a), math.sin(a)); }).ToArray();
                var set = Set(normals);
                var v = random.NextFloat2(-20, 20);
                var clipped = set.Clip(v);
                Satisfies(set, normals, clipped);
                var best = math.distance(v, clipped);
                for (var k = 0; k < 720; k++)
                {
                    var dir = new float2(math.cos(k * math.PI / 360), math.sin(k * math.PI / 360));
                    if (normals.Any(n => math.dot(dir, n) < 0)) continue;
                    var q = dir * math.max(0, math.dot(v, dir)); // nearest point of that feasible ray
                    Assert.That(best, Is.LessThanOrEqualTo(math.distance(v, q) + 1e-4f), $"trial {trial}");
                }
            }
        }
    }
}
