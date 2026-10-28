using System;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace MLFLegends.Tests.EditMode
{
    public class CombatSpatialIndexTests
    {
        static CombatSpatialBounds Box(float3 min, float3 max) => new CombatSpatialBounds { Min = min, Max = max };

        internal static readonly CombatSpatialBounds Everywhere = Box(new float3(-float.MaxValue), new float3(float.MaxValue));

        // The geometry fixture uses the real index but independent, explicitly-created data. Lifecycle tests below use
        // only BattleRequest. One table of spawn defaults; special bodies here are geometry test inputs, not spawn tuning.
        internal static World DataWorld(out CombatSpatialIndexSystem index)
        {
            var world = new World("spatial data fixture");
            var session = new BattleSession();
            session.Begin(0, 0, new CombatantId { Value = 1 }, new CombatantId { Value = 2 });
            world.EntityManager.CreateSingleton(session);
            world.EntityManager.CreateSingleton(SimulationClock.AtTick(1));
            index = world.CreateSystemManaged<CombatSpatialIndexSystem>();
            return world;
        }

        internal static Entity Add(EntityManager manager, uint id, float3 position, CombatBody? custom = null)
        {
            var entity = manager.CreateEntity(typeof(CombatantId), typeof(CombatTeam), typeof(CombatPosition), typeof(CombatBody), typeof(InCombat));
            manager.SetComponentData(entity, new CombatantId { Value = id });
            manager.SetComponentData(entity, new CombatTeam { Value = (byte)(1 + id % 2) });
            manager.SetComponentData(entity, new CombatPosition { Value = position });
            manager.SetComponentData(entity, custom ?? CombatBodyTuning.Default);
            return entity;
        }

        static uint[] Ids(NativeList<CombatSpatialBody> results) => results.AsArray().ToArray().Select(b => b.Id.Value).ToArray();

        // Independent reference: compare six scalar body-bounds inequalities, with no grid, production bounds helper,
        // cell calculation, hash map or production overlap method.
        static bool Reference(float3 p, CombatBody b, CombatSpatialBounds q) =>
            p.x - b.Radius <= q.Max.x && p.x + b.Radius >= q.Min.x
            && p.z - b.Radius <= q.Max.z && p.z + b.Radius >= q.Min.z
            && p.y <= q.Max.y && p.y + b.Height >= q.Min.y;

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void EachBodyFieldRejectsInvalidValues(float value)
        {
            for (var field = 0; field < 3; field++)
            {
                var body = CombatBodyTuning.Default;
                if (field == 0) body.Radius = value;
                if (field == 1) body.Height = value;
                if (field == 2) body.Mass = value;
                Assert.That(CombatSpatialBounds.TryFromBody(float3.zero, body, out _), Is.False);
            }
        }

        [Test]
        public void ValidSmallBodiesAndOverflowedComputedBounds()
        {
            Assert.That(CombatSpatialBounds.TryFromBody(float3.zero, CombatBodyTuning.Default, out _), Is.True);
            var body = new CombatBody { Radius = 0.00001f, Height = 0.00001f, Mass = float.Epsilon };
            Assert.That(CombatSpatialBounds.TryFromBody(new float3(-1, 2, -3), body, out _), Is.True);
            body.Radius = float.MaxValue;
            Assert.That(CombatSpatialBounds.TryFromBody(new float3(float.MaxValue, 0, 0), body, out _), Is.False);
            body = CombatBodyTuning.Default;
            body.Height = float.MaxValue;
            Assert.That(CombatSpatialBounds.TryFromBody(new float3(0, float.MaxValue, 0), body, out _), Is.False);
            Assert.That(CombatSpatialBounds.TryFromBody(new float3(float.NaN, 0, 0), body, out _), Is.False);
        }

        [Test]
        public void ClosedCellAndBodyBoundariesIncludeTouchingAndRespectHeight()
        {
            using var world = DataWorld(out var index);
            Add(world.EntityManager, 8, float3.zero, new CombatBody { Radius = 1, Height = 2, Mass = 3 });
            Add(world.EntityManager, 2, new float3(-2, 4, -2));
            Add(world.EntityManager, 5, new float3(-2, 0, -2));
            index.Update();
            using var results = new NativeList<CombatSpatialBody>(Allocator.Temp);
            foreach (var point in new[] { new float3(-1, 0, -1), new float3(1, 2, 1), new float3(0, 1, 0) })
            {
                Assert.That(index.TryQueryAabb(Box(point, point), results), Is.True);
                Assert.That(Ids(results), Is.EqualTo(new uint[] { 8 }));
            }
            Assert.That(index.TryQueryAabb(Box(new float3(-2, 2, -2), new float3(-2, 4, -2)), results), Is.True);
            Assert.That(Ids(results), Is.EqualTo(new uint[] { 2, 5 }), "Touch each Y interval at one end.");
            Assert.That(index.TryQueryAabb(Box(new float3(-1, 2.001f, -1), new float3(1, 3.999f, 1)), results), Is.True);
            Assert.That(results.Length, Is.Zero);
            Assert.That(index.TryGetSnapshotInfo(out var info), Is.True);
            Assert.That(info.Entries, Is.GreaterThan(info.BodyCount), "A large footprint occupies every intersecting cell.");
        }

        [Test]
        public void GridMatchesIndependentBoundsScanAcrossInsertionOrdersAndUniquePairs()
        {
            const int count = 128;
            var positions = new float3[count];
            var bodies = new CombatBody[count];
            var random = new Unity.Mathematics.Random(581u);
            for (var i = 0; i < count; i++)
            {
                positions[i] = i < 4 ? float3.zero : random.NextFloat3(new float3(-12, -3, -12), new float3(12, 8, 12));
                bodies[i] = new CombatBody
                {
                    Radius = i % 9 == 0 ? 4.25f : i % 7 == 0 ? 0.0001f : random.NextFloat(0.05f, 1.5f),
                    Height = random.NextFloat(0.01f, 8f), Mass = random.NextFloat(0.01f, 100f),
                };
            }
            var queries = new CombatSpatialBounds[260];
            queries[0] = Everywhere;
            queries[1] = Box(float3.zero, float3.zero);
            for (var i = 2; i < queries.Length; i++)
            {
                var p = random.NextFloat3(new float3(-16, -5, -16), new float3(16, 12, 16));
                queries[i] = Box(p, p + random.NextFloat3(0f, 6f));
            }
            foreach (var reverse in new[] { false, true })
            {
                using var world = DataWorld(out var index);
                for (var n = 0; n < count; n++)
                {
                    var i = reverse ? count - n - 1 : n;
                    var entity = Add(world.EntityManager, (uint)i + 1, positions[i], bodies[i]);
                    // Several archetypes as well as reversed creation order: never trust a chunk traversal order.
                    if (i % 2 == 0)
                        world.EntityManager.AddComponent<CombatHealth>(entity);
                }
                index.Update();
                using var results = new NativeList<CombatSpatialBody>(Allocator.Temp);
                foreach (var q in queries)
                {
                    Assert.That(index.TryQueryAabb(q, results), Is.True);
                    var expected = Enumerable.Range(0, count).Where(i => Reference(positions[i], bodies[i], q)).Select(i => (uint)i + 1).ToArray();
                    Assert.That(Ids(results), Is.EqualTo(expected), "Exact AABB broadphase, sorted, without losses or duplicates.");
                }
                var pairs = new System.Collections.Generic.HashSet<(uint, uint)>();
                for (var i = 0; i < count; i++)
                {
                    var q = Box(positions[i] - new float3(bodies[i].Radius, 0, bodies[i].Radius),
                        positions[i] + new float3(bodies[i].Radius, bodies[i].Height, bodies[i].Radius));
                    Assert.That(index.TryQueryAabb(q, results), Is.True);
                    foreach (var b in results.AsArray())
                        if (b.Id.Value > i + 1)
                            Assert.That(pairs.Add(((uint)i + 1, b.Id.Value)), Is.True);
                    for (var j = i + 1; j < count; j++)
                        if (Reference(positions[j], bodies[j], q))
                            Assert.That(pairs.Contains(((uint)i + 1, (uint)j + 1)), Is.True);
                }
                Assert.That(index.TryQueryAabb(Everywhere, results), Is.True);
                Assert.That(index.LastQueryUsedFullScan, Is.True);
                Assert.That(results.Length, Is.EqualTo(count));
                Assert.That(index.TryQueryAabb(Box(new float3(-16), new float3(16)), results), Is.True);
                Assert.That(index.LastQueryDuplicatesEliminated, Is.GreaterThan(0));
            }
        }

        [Test]
        public void InvalidBuildAndUnrepresentableFootprintAreExplicitAndNeverPublishPartialData()
        {
            foreach (var body in new[] { default(CombatBody), new CombatBody { Radius = 100000, Height = 1, Mass = 1 } })
            {
                using var world = DataWorld(out var index);
                Add(world.EntityManager, 1, float3.zero, body);
                Assert.Throws<InvalidOperationException>(() => index.Update());
                Assert.That(index.TryGetSnapshotInfo(out _), Is.False);
            }
            using var edgeWorld = DataWorld(out var edgeIndex);
            Add(edgeWorld.EntityManager, 1, new float3(float.MaxValue, 0, 0));
            Assert.Throws<InvalidOperationException>(() => edgeIndex.Update());
            Assert.That(edgeIndex.TryGetSnapshotInfo(out _), Is.False);
        }

        [Test]
        public void QueriesRejectInvalidBoundsAndDoNotKeepOldResults()
        {
            using var world = DataWorld(out var index);
            Add(world.EntityManager, 1, float3.zero);
            index.Update();
            using var results = new NativeList<CombatSpatialBody>(Allocator.Temp);
            Assert.That(index.TryQueryAabb(Everywhere, results), Is.True);
            Assert.Throws<ArgumentException>(() => index.TryQueryAabb(Box(new float3(1), float3.zero), results));
            Assert.That(results.Length, Is.Zero);
            Assert.Throws<ArgumentException>(() => index.TryQueryAabb(Box(new float3(float.NaN), new float3(1)), results));
        }

        [Test]
        public void AtomicBatchMatchesSingleQueriesAndPreservesEachRangeAcrossBufferGrowth()
        {
            using var world = DataWorld(out var index);
            for (uint id = 12; id > 0; id--)
                Add(world.EntityManager, id, new float3((int)id - 6, id % 3, -(int)id % 4));
            index.Update();
            using var queryStorage = new NativeArray<CombatSpatialBounds>(5, Allocator.Temp);
            var queries = queryStorage; // Writable alias; the using local owns disposal.
            queries[0] = Everywhere; // Huge full scan followed by grid queries; never sort an earlier range again.
            queries[1] = Box(new float3(-3, 0, -5), new float3(2, 4, 2));
            queries[2] = Box(new float3(100), new float3(101));
            queries[3] = Everywhere;
            queries[4] = queries[1]; // Stamps are query-local, not batch-local.
            using var batch = new NativeList<CombatSpatialBody>(1, Allocator.Temp);
            using var ranges = new NativeList<int2>(1, Allocator.Temp);
            using var single = new NativeList<CombatSpatialBody>(Allocator.Temp);
            Assert.That(index.TryQueryAabbBatch(queries, batch, ranges), Is.True);
            Assert.That(index.LastQueryCandidates, Is.EqualTo(batch.Length));
            Assert.That(index.LastQueryUsedFullScan, Is.True);
            Assert.That(ranges.Length, Is.EqualTo(queries.Length));
            var offset = 0;
            for (var q = 0; q < queries.Length; q++)
            {
                Assert.That(ranges[q].x, Is.EqualTo(offset));
                Assert.That(index.TryQueryAabb(queries[q], single), Is.True);
                var actual = batch.AsArray().GetSubArray(ranges[q].x, ranges[q].y).ToArray().Select(b => b.Id.Value).ToArray();
                Assert.That(actual, Is.EqualTo(Ids(single)));
                offset += ranges[q].y;
            }
            Assert.That(offset, Is.EqualTo(batch.Length));
            queries[1] = Box(new float3(float.NaN), new float3(1));
            Assert.Throws<ArgumentException>(() => index.TryQueryAabbBatch(queries, batch, ranges));
            Assert.That(batch.Length, Is.Zero, "Validate every query before producing any partial result.");
            Assert.That(ranges.Length, Is.Zero);
            queries[1] = queries[0];
            using var clock = world.EntityManager.CreateEntityQuery(typeof(SimulationClock));
            world.EntityManager.SetComponentData(clock.GetSingletonEntity(), SimulationClock.AtTick(2));
            Assert.That(index.TryQueryAabbBatch(queries, batch, ranges), Is.False);
            Assert.That(batch.Length, Is.Zero);
            Assert.That(ranges.Length, Is.Zero);
        }

        [Test]
        public void QueryStampWrapDoesNotReuseOldSeenMarks()
        {
            using var world = DataWorld(out var index);
            Add(world.EntityManager, 1, float3.zero);
            index.Update();
            using var results = new NativeList<CombatSpatialBody>(Allocator.Temp);
            var q = Box(new float3(-1), new float3(3));
            Assert.That(index.TryQueryAabb(q, results), Is.True);
            Assert.That(results.Length, Is.EqualTo(1));
            // Force the otherwise four-billion-query boundary without adding a production mutation API.
            var stamp = typeof(CombatSpatialIndexSystem).GetField("m_QueryStamp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.That(stamp, Is.Not.Null);
            stamp.SetValue(index, uint.MaxValue);
            Assert.That(index.TryQueryAabb(q, results), Is.True);
            Assert.That(results.Length, Is.EqualTo(1), "Old stamp 1 must be cleared before wrap returns to stamp 1.");
        }

        [Test]
        public void FailedRebuildHidesOldSnapshotAndNextRealTickCanRecover()
        {
            using var world = DataWorld(out var index);
            var first = Add(world.EntityManager, 1, float3.zero);
            var duplicate = Add(world.EntityManager, 1, new float3(2, 0, 0));
            Assert.Throws<InvalidOperationException>(() => index.Update());
            Assert.That(index.TryGetSnapshotInfo(out _), Is.False);
            world.EntityManager.DestroyEntity(duplicate);
            using var clock = world.EntityManager.CreateEntityQuery(typeof(SimulationClock));
            world.EntityManager.SetComponentData(clock.GetSingletonEntity(), SimulationClock.AtTick(2));
            index.Update();
            Assert.That(index.TryGetSnapshotInfo(out _), Is.True);
            world.EntityManager.SetComponentData(first, default(CombatBody));
            world.EntityManager.SetComponentData(clock.GetSingletonEntity(), SimulationClock.AtTick(3));
            Assert.Throws<InvalidOperationException>(() => index.Update());
            Assert.That(index.TryGetSnapshotInfo(out _), Is.False, "A good old snapshot must not survive a failed rebuild.");
            world.EntityManager.SetComponentData(first, CombatBodyTuning.Default);
            world.EntityManager.SetComponentData(clock.GetSingletonEntity(), SimulationClock.AtTick(4));
            index.Update();
            Assert.That(index.TryGetSnapshotInfo(out var info), Is.True);
            Assert.That(info.Tick, Is.EqualTo(4));
        }

        [Test]
        public void RepresentableExtremeCellsAndCellCountOverflowRemainConservative()
        {
            using var world = DataWorld(out var index);
            Add(world.EntityManager, 1, new float3(int.MinValue, 0, int.MinValue));
            Add(world.EntityManager, 2, new float3(2147483520f, 0, 2147483520f)); // Largest float cell below int.MaxValue.
            Add(world.EntityManager, 3, float3.zero);
            index.Update();
            using var results = new NativeList<CombatSpatialBody>(Allocator.Temp);
            var point = new float3(int.MinValue, 0, int.MinValue);
            Assert.That(index.TryQueryAabb(Box(point, point), results), Is.True);
            Assert.That(Ids(results), Is.EqualTo(new uint[] { 1 }));
            point = new float3(2147483520f, 0, 2147483520f);
            Assert.That(index.TryQueryAabb(Box(point, point), results), Is.True);
            Assert.That(Ids(results), Is.EqualTo(new uint[] { 2 }));
            Assert.That(index.TryQueryAabb(Box(new float3(-2_000_000_000f), new float3(2_000_000_000f)), results), Is.True);
            Assert.That(index.LastQueryUsedFullScan, Is.True, "Representable keys but overflowing cell-count product uses complete scan.");
            Assert.That(Ids(results), Is.EqualTo(new uint[] { 3 }));
        }
    }
}
