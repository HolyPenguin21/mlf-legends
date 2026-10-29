using System;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace MLFLegends.Tests.EditMode
{
    public class CombatSpatialLifecycleTests
    {
        static CombatSpatialIndexSystem Index(ReplayWorld replay) => replay.World.GetExistingSystemManaged<CombatSpatialIndexSystem>();
        static void Initialization(ReplayWorld replay) => replay.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
        static Entity Find(ReplayWorld replay, CombatantId id)
        {
            using var query = replay.Manager.CreateEntityQuery(typeof(CombatantId));
            using var entities = query.ToEntityArray(Allocator.Temp);
            return entities.ToArray().Single(e => replay.Manager.GetComponentData<CombatantId>(e).Equals(id));
        }

        [TestCase(2)]
        [TestCase(100)]
        [TestCase(400)]
        public void EveryRosterUsesOneBodyDefaultAndPausedRestartCannotReadOldEntities(int count)
        {
            using var replay = new ReplayWorld("spatial lifecycle roster");
            var index = Index(replay);
            using var results = new NativeList<CombatSpatialBody>(Allocator.Temp);
            Assert.That(index.TryQueryAabb(CombatSpatialIndexTests.Everywhere, results), Is.False);
            var setup = BattleSetup.Default;
            setup.HumanAllies = setup.AiAllies = (ushort)(count / 2 - 1);
            replay.Request(BattleRequest.Start(setup));
            Initialization(replay);
            Assert.That(index.TryGetSnapshotInfo(out _), Is.False, "No artificial first tick.");
            replay.Step();
            Assert.That(index.TryQueryAabb(CombatSpatialIndexTests.Everywhere, results), Is.True);
            Assert.That(results.Length, Is.EqualTo(count));
            var old = results.AsArray().ToArray();
            foreach (var row in old)
                Assert.That(row.Body, Is.EqualTo(CombatBodyTuning.Default));
            var tick = replay.Tick;
            var battle = replay.Session.BattleNumber;
            var capacity = results.Capacity;
            for (var restart = 0; restart < 3; restart++)
            {
                replay.Request(BattleRequest.Restart(setup));
                Initialization(replay); // Exactly the paused-frame boundary; no FixedStep call.
                Assert.That(replay.Tick, Is.EqualTo(tick + (uint)restart));
                Assert.That(index.TryQueryAabb(CombatSpatialIndexTests.Everywhere, results), Is.False);
                Assert.That(results.Length, Is.Zero);
                if (restart == 0)
                    foreach (var row in old)
                        Assert.That(replay.Manager.Exists(row.Entity), Is.False);
                replay.Step();
                Assert.That(index.TryQueryAabb(CombatSpatialIndexTests.Everywhere, results), Is.True);
                Assert.That(results.Length, Is.EqualTo(count));
                Assert.That(results.Capacity, Is.EqualTo(capacity), "Caller reuses its capacity too.");
                Assert.That(index.TryGetSnapshotInfo(out var info), Is.True);
                Assert.That(info.Tick, Is.EqualTo(replay.Tick));
                Assert.That(info.BattleNumber, Is.EqualTo(battle + restart + 1));
            }
            replay.Request(BattleRequest.End());
            Initialization(replay);
            Assert.That(index.TryQueryAabb(CombatSpatialIndexTests.Everywhere, results), Is.False);
            Assert.That(results.Length, Is.Zero);
            Assert.That(replay.CountWith<CombatBody>(), Is.Zero);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(10)]
        public void OneBuildPerRealCatchupTickAfterMovementAndMutableBodyReadOnNextBuild(int pacing)
        {
            using var replay = new ReplayWorld("spatial catchup");
            replay.Request(BattleRequest.Start(BattleSetup.Default));
            var script = new ReplayScript();
            for (uint t = 1; t <= 30; t++)
                script.Player(t, t, ReplayRole.Human, moveX: 1);
            replay.Script = script;
            var index = Index(replay);
            using var results = new NativeList<CombatSpatialBody>(Allocator.Temp);
            for (var frame = 0; frame < 30 / pacing; frame++)
            {
                replay.Frame(pacing);
                Assert.That(index.BuildCount, Is.EqualTo(replay.Tick));
                Assert.That(index.TryGetSnapshotInfo(out var info), Is.True);
                Assert.That(info.Tick, Is.EqualTo(replay.Tick));
                Assert.That(index.TryQueryAabb(CombatSpatialIndexTests.Everywhere, results), Is.True);
                foreach (var row in results.AsArray())
                    Assert.That(row.Position, Is.EqualTo(replay.Manager.GetComponentData<CombatPosition>(row.Entity).Value));
                var builds = index.BuildCount;
                index.Update();
                Assert.That(index.BuildCount, Is.EqualTo(builds), "A manual duplicate update cannot rebuild this tick.");
            }
            var human = Find(replay, replay.Session.HumanCombatant);
            var custom = new CombatBody { Radius = 1.75f, Height = 5f, Mass = 13f };
            replay.Manager.SetComponentData(human, custom);
            replay.Step();
            Assert.That(index.TryQueryAabb(CombatSpatialIndexTests.Everywhere, results), Is.True);
            Assert.That(results.AsArray().ToArray().Single(b => b.Entity == human).Body, Is.EqualTo(custom));
            Assert.That(replay.Records.Last().Combatant(replay.Session.HumanCombatant.Value).Body, Is.EqualTo(custom));
        }

        [Test]
        public void DespawnAndEnableChangesInvalidateAtOnceThenNextTickContainsOnlyLiveMembers()
        {
            using var replay = new ReplayWorld("spatial despawn");
            replay.Request(BattleRequest.Start(BattleSetup.Default));
            replay.Step();
            var index = Index(replay);
            using var results = new NativeList<CombatSpatialBody>(Allocator.Temp);
            var human = Find(replay, replay.Session.HumanCombatant);
            replay.Manager.SetComponentEnabled<InCombat>(human, false);
            Assert.That(index.TryQueryAabb(CombatSpatialIndexTests.Everywhere, results), Is.False);
            replay.Step();
            Assert.That(index.TryQueryAabb(CombatSpatialIndexTests.Everywhere, results), Is.True);
            Assert.That(results.Length, Is.EqualTo(1));
            Assert.That(results[0].Id, Is.EqualTo(replay.Session.AiCombatant));
            replay.Request(BattleRequest.Despawn(replay.Session.AiCombatant));
            Initialization(replay);
            Assert.That(index.TryQueryAabb(CombatSpatialIndexTests.Everywhere, results), Is.False, "Despawn is rejected before another fixed tick.");
            replay.Step();
            Assert.That(index.TryQueryAabb(CombatSpatialIndexTests.Everywhere, results), Is.True);
            Assert.That(results.Length, Is.Zero);
        }

        [Test]
        public void EqualCountEnableSwapDoesNotFoolSnapshotMembershipValidation()
        {
            using var world = CombatSpatialIndexTests.DataWorld(out var index);
            var first = CombatSpatialIndexTests.Add(world.EntityManager, 1, float3.zero);
            var second = CombatSpatialIndexTests.Add(world.EntityManager, 2, float3.zero);
            world.EntityManager.SetComponentEnabled<InCombat>(second, false);
            index.Update();
            Assert.That(index.TryGetSnapshotInfo(out _), Is.True);
            world.EntityManager.SetComponentEnabled<InCombat>(first, false);
            world.EntityManager.SetComponentEnabled<InCombat>(second, true);
            Assert.That(index.TryGetSnapshotInfo(out _), Is.False);
        }

        [Test]
        public void TickMismatchTwoWorldsDisposalAndPartialWorldsHaveNoSharedOrHiddenState()
        {
            using var first = new ReplayWorld("spatial world one");
            using var second = new ReplayWorld("spatial world two");
            first.Request(BattleRequest.Start(BattleSetup.Default));
            second.Request(BattleRequest.Start(BattleSetup.Default));
            first.Step(2);
            second.Step();
            var firstIndex = Index(first);
            var secondIndex = Index(second);
            Assert.That(firstIndex.TryGetSnapshotInfo(out var a), Is.True);
            Assert.That(secondIndex.TryGetSnapshotInfo(out var b), Is.True);
            Assert.That(a.Tick, Is.EqualTo(2));
            Assert.That(b.Tick, Is.EqualTo(1));
            first.Request(BattleRequest.End());
            Initialization(first);
            Assert.That(firstIndex.TryGetSnapshotInfo(out _), Is.False);
            Assert.That(secondIndex.TryGetSnapshotInfo(out _), Is.True);
            second.Dispose();
            using var results = new NativeList<CombatSpatialBody>(Allocator.Temp);
            Assert.That(secondIndex.TryQueryAabb(CombatSpatialIndexTests.Everywhere, results), Is.False, "Retained managed API cannot access disposed native memory.");

            using var data = CombatSpatialIndexTests.DataWorld(out var dataIndex);
            var actor = CombatSpatialIndexTests.Add(data.EntityManager, 1, float3.zero);
            dataIndex.Update();
            using (var query = data.EntityManager.CreateEntityQuery(typeof(SimulationClock)))
                data.EntityManager.SetComponentData(query.GetSingletonEntity(), SimulationClock.AtTick(2));
            Assert.That(dataIndex.TryGetSnapshotInfo(out _), Is.False);
            dataIndex.Update();
            Assert.That(dataIndex.TryGetSnapshotInfo(out _), Is.True);
            data.EntityManager.RemoveComponent<CombatBody>(actor);
            dataIndex.Update();
            Assert.That(dataIndex.TryGetSnapshotInfo(out _), Is.False, "No hidden body defaults in partial Worlds.");

            using var partial = new World("no session or clock");
            var partialIndex = partial.CreateSystemManaged<CombatSpatialIndexSystem>();
            CombatSpatialIndexTests.Add(partial.EntityManager, 1, float3.zero);
            partialIndex.Update();
            Assert.That(partialIndex.TryGetSnapshotInfo(out _), Is.False);
            using var sessions = partial.EntityManager.CreateEntityQuery(typeof(BattleSession));
            using var clocks = partial.EntityManager.CreateEntityQuery(typeof(SimulationClock));
            Assert.That(sessions.CalculateEntityCount(), Is.Zero);
            Assert.That(clocks.CalculateEntityCount(), Is.Zero);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(10)]
        public void IndexPreservesAllPreviousCombatStateForBothSourcesAndPacing(int pacing)
        {
            using var indexed = new ReplayWorld("indexed replay");
            using var control = new ReplayWorld("single-step indexed control");
            // Since 04.5 Hit consumes the snapshot, so a disabled index is no longer a valid control. The pre-index
            // behaviour is instead checked per tick by the independent brute-force oracle on the same final positions.
            var oracle = HitOracle.Attach(indexed);
            var script = new ReplayScript();
            for (uint t = 1; t <= 120; t++)
            {
                var buttons = t % 23 == 0 ? CommandButtons.Jump : t % 7 == 0 ? CommandButtons.HandSeries : CommandButtons.None;
                script.Player(t, t, ReplayRole.Human, moveX: (sbyte)(t % 3 - 1), pressed: buttons);
                script.Ai(t, t, ReplayRole.Ai, moveX: (sbyte)(1 - t % 3), pressed: buttons);
            }
            indexed.Script = control.Script = script;
            var setup = BattleSetup.Default;
            setup.Human.Position = float3.zero;
            setup.Ai.Position = new float3(0.6f, 0, 0);
            indexed.Request(BattleRequest.Start(setup));
            control.Request(BattleRequest.Start(setup));
            for (var i = 0; i < 120 / pacing; i++)
                indexed.Frame(pacing);
            control.Step(120);
            Assert.That(indexed.RawLog, Is.EqualTo(control.RawLog), "Position, motion, HP, strike, facing, commands and body replay are identical.");
            Assert.That(oracle.Mismatches, Is.Empty, "Indexed Hit equals the brute-force pre-index result on every tick.");
            Assert.That(oracle.CheckedTicks, Is.GreaterThan(100)); Assert.That(oracle.CheckedHits, Is.GreaterThan(0));
            Assert.That(indexed.Records.SelectMany(r => r.Combatants).All(c => c.Body.Equals(CombatBodyTuning.Default)), Is.True);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void PartialFighterMissingAnySpatialComponentCannotPublishACompleteSnapshot(int missing)
        {
            using var world = CombatSpatialIndexTests.DataWorld(out var index);
            var actor = CombatSpatialIndexTests.Add(world.EntityManager, 1, float3.zero);
            if (missing == 0) world.EntityManager.RemoveComponent<CombatBody>(actor);
            if (missing == 1) world.EntityManager.RemoveComponent<CombatPosition>(actor);
            if (missing == 2) world.EntityManager.RemoveComponent<CombatTeam>(actor);
            index.Update();
            Assert.That(index.TryGetSnapshotInfo(out _), Is.False);
            using var results = new NativeList<CombatSpatialBody>(Allocator.Temp);
            using var ranges = new NativeList<int2>(Allocator.Temp);
            using var queryStorage = new NativeArray<CombatSpatialBounds>(1, Allocator.Temp);
            var queries = queryStorage; // Writable alias; the using local owns disposal.
            queries[0] = CombatSpatialIndexTests.Everywhere;
            Assert.That(index.TryQueryAabbBatch(queries, results, ranges), Is.False);
        }

        [TestCase(BattleRequestKind.End)]
        [TestCase(BattleRequestKind.Restart)]
        [TestCase(BattleRequestKind.Despawn)]
        public void LifecycleDestroysWholeEntityDisabledFightersAtPausedBoundary(BattleRequestKind operation)
        {
            using var replay = new ReplayWorld("whole-entity disabled lifecycle");
            var setup = BattleSetup.Default;
            setup.HumanAllies = setup.AiAllies = 1;
            replay.Request(BattleRequest.Start(setup));
            replay.Step();
            using var results = new NativeList<CombatSpatialBody>(Allocator.Temp);
            Assert.That(Index(replay).TryQueryAabb(CombatSpatialIndexTests.Everywhere, results), Is.True);
            var old = results.AsArray().ToArray();
            var human = Find(replay, replay.Session.HumanCombatant);
            replay.Manager.SetEnabled(human, false); // Unity Disabled tag, distinct from an InCombat enable bit.
            Assert.That(Index(replay).TryGetSnapshotInfo(out _), Is.False);
            var tick = replay.Tick;
            var request = operation == BattleRequestKind.End ? BattleRequest.End()
                : operation == BattleRequestKind.Restart ? BattleRequest.Restart(setup)
                : BattleRequest.Despawn(replay.Session.HumanCombatant);
            replay.Request(request);
            Initialization(replay);
            Assert.That(replay.Tick, Is.EqualTo(tick));
            Assert.That(replay.Manager.Exists(human), Is.False, "Lifecycle must find disabled entities, not only enabled-query members.");
            Assert.That(Index(replay).TryGetSnapshotInfo(out _), Is.False);
            if (operation != BattleRequestKind.Despawn)
                foreach (var row in old)
                    Assert.That(replay.Manager.Exists(row.Entity), Is.False);
            Assert.That(replay.CountWith<CombatBody>(), Is.EqualTo(operation == BattleRequestKind.End ? 0
                : operation == BattleRequestKind.Restart ? setup.CombatantCount : setup.CombatantCount - 1));
        }

        [Test]
        public void ASecondSpatialOwnerIsRejectedWithoutDamagingTheFirst()
        {
            using var world = CombatSpatialIndexTests.DataWorld(out var index);
            CombatSpatialIndexTests.Add(world.EntityManager, 1, float3.zero);
            index.Update();
            Assert.Throws<InvalidOperationException>(() => world.CreateSystemManaged<CombatSpatialIndexSystem>());
            // Creating/destroying a failed system can conservatively invalidate EntityOrderVersion; rebuild next tick.
            using var clock = world.EntityManager.CreateEntityQuery(typeof(SimulationClock));
            world.EntityManager.SetComponentData(clock.GetSingletonEntity(), SimulationClock.AtTick(2));
            index.Update();
            Assert.That(index.TryGetSnapshotInfo(out _), Is.True);
            Assert.That(world.GetExistingSystemManaged<CombatSpatialIndexSystem>(), Is.SameAs(index));
        }

        [Test]
        public void EnablingAPartialFighterInvalidatesReadEvenWithoutAnotherIndexUpdate()
        {
            using var world = CombatSpatialIndexTests.DataWorld(out var index);
            CombatSpatialIndexTests.Add(world.EntityManager, 1, float3.zero);
            var partial = CombatSpatialIndexTests.Add(world.EntityManager, 2, float3.zero);
            world.EntityManager.SetComponentEnabled<InCombat>(partial, false);
            world.EntityManager.RemoveComponent<CombatBody>(partial);
            index.Update();
            Assert.That(index.TryGetSnapshotInfo(out _), Is.True);
            world.EntityManager.SetComponentEnabled<InCombat>(partial, true); // No structural version change here.
            Assert.That(index.TryGetSnapshotInfo(out _), Is.False, "Expected membership and complete-body membership must both match.");
            using var results = new NativeList<CombatSpatialBody>(Allocator.Temp);
            Assert.That(index.TryQueryAabb(CombatSpatialIndexTests.Everywhere, results), Is.False);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void SessionAndClockAreRequiredIndependentlyAndAreNeverCreatedByTheIndex(bool sessionPresent, bool clockPresent)
        {
            using var world = new World("independent partial prerequisites");
            if (sessionPresent)
            {
                var session = new BattleSession();
                session.Begin(0, 0, new CombatantId { Value = 1 }, new CombatantId { Value = 2 });
                world.EntityManager.CreateSingleton(session);
            }
            if (clockPresent)
                world.EntityManager.CreateSingleton(SimulationClock.AtTick(1));
            CombatSpatialIndexTests.Add(world.EntityManager, 1, float3.zero);
            var index = world.CreateSystemManaged<CombatSpatialIndexSystem>();
            index.Update();
            Assert.That(index.TryGetSnapshotInfo(out _), Is.EqualTo(sessionPresent && clockPresent));
            using var sessions = world.EntityManager.CreateEntityQuery(typeof(BattleSession));
            using var clocks = world.EntityManager.CreateEntityQuery(typeof(SimulationClock));
            Assert.That(sessions.CalculateEntityCount(), Is.EqualTo(sessionPresent ? 1 : 0));
            Assert.That(clocks.CalculateEntityCount(), Is.EqualTo(clockPresent ? 1 : 0));
        }
    }
}
