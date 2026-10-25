using System.Collections.Generic;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace MLFLegends.Tests.EditMode
{
    // Isolated test worlds with the real clock, processor and lifecycle system. The lifecycle system is updated
    // between fixed steps (Boundary), as SimulationSystemGroup does before FixedStepSimulationSystemGroup.
    public class BattleLifecycleTests
    {
        World m_World;
        FixedStepSimulationSystemGroup m_FixedStep;
        SystemHandle m_Lifecycle;
        uint m_Frame;

        [SetUp]
        public void SetUp()
        {
            m_World = CreateBattleWorld("MLF Legends lifecycle test", out m_FixedStep, out m_Lifecycle);
            m_Frame = 0;
        }

        [TearDown]
        public void TearDown()
        {
            if (m_World != null && m_World.IsCreated)
                m_World.Dispose();
        }

        // ---- helpers ----

        static World CreateBattleWorld(string name, out FixedStepSimulationSystemGroup fixedStep, out SystemHandle lifecycle)
        {
            var world = new World(name);
            fixedStep = world.CreateSystemManaged<FixedStepSimulationSystemGroup>();
            fixedStep.AddSystemToUpdateList(world.CreateSystemManaged<BeginFixedStepSimulationEntityCommandBufferSystem>());
            fixedStep.AddSystemToUpdateList(world.CreateSystem<CombatClockSystem>());
            var collection = world.CreateSystemManaged<CombatCommandCollectionSystemGroup>();
            collection.AddSystemToUpdateList(world.CreateSystem<CombatCommandProcessorSystem>());
            fixedStep.AddSystemToUpdateList(collection);
            fixedStep.SortSystems();
            lifecycle = world.CreateSystem<BattleLifecycleSystem>();
            return world;
        }

        EntityManager Manager => m_World.EntityManager;

        uint Tick => Manager.CreateEntityQuery(typeof(SimulationClock)).GetSingleton<SimulationClock>().Tick;

        BattleSession Session => Manager.CreateEntityQuery(typeof(BattleSession)).GetSingleton<BattleSession>();

        CombatCommandQueue Queue => Manager.CreateEntityQuery(typeof(CombatCommandQueue)).GetSingleton<CombatCommandQueue>();

        Entity QueueEntity => Manager.CreateEntityQuery(typeof(CombatCommandQueue)).GetSingletonEntity();

        CommandOutcome[] Outcomes => Manager.GetBuffer<CombatCommandResult>(QueueEntity).AsNativeArray().ToArray().Select(r => r.Outcome).ToArray();

        int PendingCount => Manager.GetBuffer<PendingCombatCommand>(QueueEntity).Length;

        // All combatants, defeated ones included.
        EntityQuery CombatantQuery => Manager.CreateEntityQuery(new EntityQueryDesc
        {
            All = new ComponentType[] { typeof(CombatantId) },
            Options = EntityQueryOptions.IgnoreComponentEnabledState,
        });

        uint[] LiveIds
        {
            get
            {
                using var ids = CombatantQuery.ToComponentDataArray<CombatantId>(Allocator.Temp);
                return ids.ToArray().Select(id => id.Value).OrderBy(v => v).ToArray();
            }
        }

        void Step(int ticks = 1)
        {
            var dt = (double)SimulationTiming.TickDuration;
            for (var i = 0; i < ticks; i++)
            {
                m_World.SetTime(new TimeData((m_Frame + 0.5) * dt, (float)dt));
                m_FixedStep.Update();
                m_Frame++;
            }
        }

        // The frame boundary where the lifecycle applies requests.
        void Boundary() => m_Lifecycle.Update(m_World.Unmanaged);

        BattleRequestOutcome Request(BattleRequest request)
        {
            BattleRequest.Submit(Manager, request);
            Boundary();
            return Session.LastOutcome;
        }

        Entity Find(CombatantId id)
        {
            using var entities = CombatantQuery.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                if (Manager.GetComponentData<CombatantId>(entity).Value == id.Value)
                    return entity;
            }

            return Entity.Null;
        }

        void Submit(params CombatCommand[] commands)
        {
            var buffer = Manager.GetBuffer<CombatCommandSubmission>(QueueEntity);
            foreach (var command in commands)
                buffer.Add(new CombatCommandSubmission(command));
        }

        static CombatCommand Cmd(CombatantId id, uint tick, sbyte moveX = 1, CommandSource source = CommandSource.Player) =>
            new CombatCommand { Combatant = id, Tick = tick, MoveX = moveX, Source = source };

        void AssertCombatant(CombatantId id, in CombatantSetup setup)
        {
            var entity = Find(id);
            Assert.That(entity, Is.Not.EqualTo(Entity.Null), $"Combatant {id.Value} exists.");
            Assert.That(Manager.GetComponentData<CombatTeam>(entity).Value, Is.EqualTo(setup.Team));
            Assert.That(Manager.GetComponentData<CombatPosition>(entity).Value, Is.EqualTo(setup.Position));
            Assert.That(Manager.GetComponentData<CombatFacing>(entity).Value, Is.EqualTo(setup.Facing));
            Assert.That(Manager.HasComponent<InCombat>(entity), Is.True);
            Assert.That(Manager.IsComponentEnabled<InCombat>(entity), Is.True);
        }

        static BattleSetup OtherSetup => new BattleSetup
        {
            Human = new CombatantSetup { Team = 3, Position = new float3(-5f, 0f, 1.5f), Facing = FacingDirection.Right },
            Ai = new CombatantSetup { Team = 4, Position = new float3(5f, 1f, -1.5f), Facing = FacingDirection.Left },
        };

        // ---- creation ----

        [Test]
        public void LifecycleOwnsOneIdleSessionAndASecondInstanceIsRejected()
        {
            Assert.That(Manager.CreateEntityQuery(typeof(BattleSession)).CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(Session.State, Is.EqualTo(BattleState.Idle));
            Assert.That(Session.BattleNumber, Is.Zero);
            Assert.That(Session.HumanCombatant.IsValid, Is.False);
            Assert.That(CombatantQuery.CalculateEntityCount(), Is.Zero, "No combatant exists before a battle is requested.");

            Assert.That(() => m_World.CreateSystem<BattleLifecycleSystem>(), Throws.Exception);
            Assert.That(Manager.CreateEntityQuery(typeof(BattleSession)).CalculateEntityCount(), Is.EqualTo(1));
        }

        [Test]
        public void StartSpawnsExactlyTwoFullyInitialisedCombatantsOnDifferentTeams()
        {
            Step(5);
            var setup = BattleSetup.Default;

            Assert.That(Request(BattleRequest.Start(setup)), Is.EqualTo(BattleRequestOutcome.Started));

            var session = Session;
            Assert.That(session.State, Is.EqualTo(BattleState.Active));
            Assert.That(session.BattleNumber, Is.EqualTo(1u));
            Assert.That(session.BattleStartTick, Is.EqualTo(5u), "BattleStartTick is the last completed tick.");
            Assert.That(Queue.BattleStartTick, Is.EqualTo(session.BattleStartTick), "The command boundary is the session's value.");
            Assert.That(session.HumanCombatant.Value, Is.EqualTo(1u));
            Assert.That(session.AiCombatant.Value, Is.EqualTo(2u));
            Assert.That(LiveIds, Is.EqualTo(new[] { 1u, 2u }));
            AssertCombatant(session.HumanCombatant, setup.Human);
            AssertCombatant(session.AiCombatant, setup.Ai);
            Assert.That(Manager.GetChunk(Find(session.HumanCombatant)).Archetype,
                Is.EqualTo(Manager.GetChunk(Find(session.AiCombatant)).Archetype), "Human and AI combatants carry identical components.");
            Assert.That(Manager.GetName(Find(session.HumanCombatant)), Is.EqualTo("Combatant 1"));
        }

        [Test]
        public void BattleStartedBeforeTheFirstTickStartsAtTickZero()
        {
            Assert.That(Request(BattleRequest.Start(BattleSetup.Default)), Is.EqualTo(BattleRequestOutcome.Started));
            Assert.That(Session.BattleStartTick, Is.EqualTo(SimulationClock.NoTick));

            Step();
            Assert.That(Session.RelativeTick(Tick), Is.EqualTo(1u));
        }

        [Test]
        public void FirstSimulatedTickOfTheBattleIsRelativeTickOne()
        {
            Step(3);
            Request(BattleRequest.Start(BattleSetup.Default));
            var session = Session;
            var start = session.BattleStartTick;
            Submit(Cmd(session.HumanCombatant, start), Cmd(session.HumanCombatant, start + 1),
                Cmd(session.AiCombatant, start + 1, source: CommandSource.AI));

            Step();

            Assert.That(Tick, Is.EqualTo(start + 1));
            Assert.That(session.RelativeTick(Tick), Is.EqualTo(1u));
            Assert.That(Outcomes, Is.EqualTo(new[] { CommandOutcome.BeforeBattleStart, CommandOutcome.Accepted, CommandOutcome.Accepted }));
            var applied = Manager.GetComponentData<AppliedCombatCommand>(Find(session.HumanCombatant));
            Assert.That(applied.Command.Tick, Is.EqualTo(start + 1));
            Assert.That(applied.WasSubmitted, Is.True);
            Assert.That(Manager.GetComponentData<AppliedCombatCommand>(Find(session.AiCombatant)).Command.Source, Is.EqualTo(CommandSource.AI));
        }

        // ---- two sequential battles (end-to-end) ----

        [Test]
        public void TwoSequentialBattlesInOneWorldShareTheClockButNoCombatantsIdsOrCommands()
        {
            var ticks = new List<uint>();

            // Battle 1.
            Step(2);
            Request(BattleRequest.Start(BattleSetup.Default));
            var first = Session;
            Step(4);
            ticks.Add(Tick);
            Assert.That(first.RelativeTick(Tick), Is.EqualTo(4u));
            // Late, current and future commands of battle 1, left in the queue when it ends.
            Submit(Cmd(first.HumanCombatant, Tick + 1), Cmd(first.AiCombatant, Tick + 3, source: CommandSource.AI));
            Step();
            Assert.That(PendingCount, Is.EqualTo(1), "A future command of battle 1 is pending.");
            Submit(Cmd(first.HumanCombatant, Tick + 2));

            Assert.That(Request(BattleRequest.End()), Is.EqualTo(BattleRequestOutcome.Ended));
            Assert.That(Session.State, Is.EqualTo(BattleState.Idle));
            Assert.That(CombatantQuery.CalculateEntityCount(), Is.Zero, "No combatant of battle 1 survives.");
            Assert.That(PendingCount, Is.Zero);
            Assert.That(Manager.GetBuffer<CombatCommandSubmission>(QueueEntity).Length, Is.Zero);
            Assert.That(Manager.GetBuffer<CombatCommandResult>(QueueEntity).Length, Is.Zero);

            // Idle time between battles: the clock keeps running.
            Step(3);
            ticks.Add(Tick);

            // Battle 2 in the same World, different setup.
            var setup = OtherSetup;
            Assert.That(Request(BattleRequest.Start(setup)), Is.EqualTo(BattleRequestOutcome.Started));
            var second = Session;
            Assert.That(second.BattleNumber, Is.EqualTo(2u));
            Assert.That(second.BattleStartTick, Is.EqualTo(Tick));
            Assert.That(second.BattleStartTick, Is.GreaterThan(first.BattleStartTick));
            Assert.That(LiveIds, Is.EqualTo(new[] { 3u, 4u }), "Fresh IDs, never those of battle 1.");
            Assert.That(Find(first.HumanCombatant), Is.EqualTo(Entity.Null));
            Assert.That(Find(first.AiCombatant), Is.EqualTo(Entity.Null));
            AssertCombatant(second.HumanCombatant, setup.Human);
            AssertCombatant(second.AiCombatant, setup.Ai);

            // A producer that still targets battle 1 never reaches battle 2.
            Submit(Cmd(first.HumanCombatant, Tick + 1), Cmd(second.HumanCombatant, Tick + 1));
            Step();
            ticks.Add(Tick);
            Assert.That(second.RelativeTick(Tick), Is.EqualTo(1u));
            Assert.That(Outcomes, Is.EqualTo(new[] { CommandOutcome.UnknownCombatant, CommandOutcome.Accepted }));
            Step(3);
            ticks.Add(Tick);
            Assert.That(Outcomes, Is.Empty, "Commands of battle 1 that were pending at its end never come back.");
            Assert.That(Manager.GetComponentData<AppliedCombatCommand>(Find(second.AiCombatant)).WasSubmitted, Is.False);

            Assert.That(ticks, Is.Ordered.Ascending, "The World clock never goes back.");
            Assert.That(Manager.CreateEntityQuery(typeof(SimulationClock)).CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(Manager.CreateEntityQuery(typeof(CombatCommandQueue)).CalculateEntityCount(), Is.EqualTo(1));
        }

        [Test]
        public void RestartReplacesTheFightersInOneStep()
        {
            Request(BattleRequest.Start(BattleSetup.Default));
            Step(2);
            var first = Session;
            Submit(Cmd(first.HumanCombatant, Tick + 1));

            Assert.That(Request(BattleRequest.Restart(BattleSetup.Default)), Is.EqualTo(BattleRequestOutcome.Started));

            var second = Session;
            Assert.That(second.BattleNumber, Is.EqualTo(2u));
            Assert.That(second.BattleStartTick, Is.EqualTo(Tick));
            Assert.That(LiveIds, Is.EqualTo(new[] { 3u, 4u }));
            Assert.That(Manager.GetBuffer<CombatCommandSubmission>(QueueEntity).Length, Is.Zero);
            Step();
            Assert.That(Manager.GetComponentData<AppliedCombatCommand>(Find(second.HumanCombatant)).WasSubmitted, Is.False);
        }

        [Test]
        public void RestartWhileIdleStartsABattle()
        {
            Assert.That(Request(BattleRequest.Restart(BattleSetup.Default)), Is.EqualTo(BattleRequestOutcome.Started));
            Assert.That(LiveIds, Is.EqualTo(new[] { 1u, 2u }));
        }

        [Test]
        public void ManyBattlesInOneWorldNeverReuseAnId()
        {
            var seen = new HashSet<uint>();
            // Restart alternates between starting from idle (after End) and replacing a running battle.
            for (var battle = 0; battle < 5; battle++)
            {
                Assert.That(Request(BattleRequest.Restart(BattleSetup.Default)), Is.EqualTo(BattleRequestOutcome.Started));
                Step(2);
                foreach (var id in LiveIds)
                    Assert.That(seen.Add(id), Is.True, $"ID {id} reused in battle {battle + 1}.");
                Assert.That(LiveIds.Length, Is.EqualTo(2));
                if (battle % 2 == 0)
                    Request(BattleRequest.End());
            }

            Assert.That(seen.Min(), Is.EqualTo(1u));
        }

        // ---- repeated and invalid requests ----

        [Test]
        public void RepeatedStartIsRejectedWithoutSideEffects()
        {
            Request(BattleRequest.Start(BattleSetup.Default));
            Step(2);
            var before = Session;
            var entities = LiveIds.Select(id => Find(new CombatantId { Value = id })).ToArray();
            Submit(Cmd(before.HumanCombatant, Tick + 1));

            Assert.That(Request(BattleRequest.Start(OtherSetup)), Is.EqualTo(BattleRequestOutcome.AlreadyActive));

            var after = Session;
            Assert.That(after.BattleNumber, Is.EqualTo(before.BattleNumber));
            Assert.That(after.BattleStartTick, Is.EqualTo(before.BattleStartTick));
            Assert.That(after.HumanCombatant, Is.EqualTo(before.HumanCombatant));
            Assert.That(LiveIds, Is.EqualTo(new[] { 1u, 2u }));
            Assert.That(LiveIds.Select(id => Find(new CombatantId { Value = id })), Is.EqualTo(entities), "The same entities, not respawned.");
            AssertCombatant(after.HumanCombatant, BattleSetup.Default.Human);
            Assert.That(Manager.GetBuffer<CombatCommandSubmission>(QueueEntity).Length, Is.EqualTo(1), "The queue is not reset.");

            Request(BattleRequest.Restart(BattleSetup.Default));
            Assert.That(LiveIds, Is.EqualTo(new[] { 3u, 4u }), "The rejected start consumed no IDs.");
        }

        [Test]
        public void EndWhileIdleOrRepeatedEndIsANoOp()
        {
            Assert.That(Request(BattleRequest.End()), Is.EqualTo(BattleRequestOutcome.NotActive));
            Request(BattleRequest.Start(BattleSetup.Default));
            Step();
            Assert.That(Request(BattleRequest.End()), Is.EqualTo(BattleRequestOutcome.Ended));
            Step();
            var boundary = Queue.BattleStartTick;

            Assert.That(Request(BattleRequest.End()), Is.EqualTo(BattleRequestOutcome.NotActive));
            Assert.That(Queue.BattleStartTick, Is.EqualTo(boundary), "A rejected end does not reset the queue again.");
            Assert.That(CombatantQuery.CalculateEntityCount(), Is.Zero);
            Assert.That(Session.BattleNumber, Is.EqualTo(1u));
        }

        [Test]
        public void InvalidSetupIsRejectedAndKeepsTheRunningBattle()
        {
            var sameTeam = BattleSetup.Default;
            sameTeam.Ai.Team = sameTeam.Human.Team;
            var noFacing = BattleSetup.Default;
            noFacing.Human.Facing = 0;
            var badPosition = BattleSetup.Default;
            badPosition.Ai.Position = new float3(float.NaN, 0f, 0f);

            foreach (var setup in new[] { sameTeam, noFacing, badPosition })
            {
                Assert.That(setup.IsValid, Is.False);
                Assert.That(Request(BattleRequest.Start(setup)), Is.EqualTo(BattleRequestOutcome.InvalidSetup));
                Assert.That(CombatantQuery.CalculateEntityCount(), Is.Zero);
                Assert.That(Session.State, Is.EqualTo(BattleState.Idle));
            }

            Request(BattleRequest.Start(BattleSetup.Default));
            Assert.That(Request(BattleRequest.Restart(sameTeam)), Is.EqualTo(BattleRequestOutcome.InvalidSetup));
            Assert.That(LiveIds, Is.EqualTo(new[] { 1u, 2u }), "A rejected restart leaves the running battle and consumes no IDs.");
            Assert.That(Session.BattleNumber, Is.EqualTo(1u));
        }

        [Test]
        public void RequestsOfOneFrameAreAppliedInOrder()
        {
            BattleRequest.Submit(Manager, BattleRequest.Start(BattleSetup.Default));
            BattleRequest.Submit(Manager, BattleRequest.Start(BattleSetup.Default));
            BattleRequest.Submit(Manager, BattleRequest.End());
            BattleRequest.Submit(Manager, BattleRequest.Start(OtherSetup));
            Boundary();

            Assert.That(Session.LastOutcome, Is.EqualTo(BattleRequestOutcome.Started));
            Assert.That(Session.BattleNumber, Is.EqualTo(2u));
            Assert.That(LiveIds, Is.EqualTo(new[] { 3u, 4u }));
            AssertCombatant(Session.HumanCombatant, OtherSetup.Human);

            BattleRequest.Submit(Manager, BattleRequest.Restart(BattleSetup.Default));
            BattleRequest.Submit(Manager, BattleRequest.End());
            Boundary();
            Assert.That(Session.State, Is.EqualTo(BattleState.Idle));
            Assert.That(CombatantQuery.CalculateEntityCount(), Is.Zero);
        }

        // ---- despawn ----

        [Test]
        public void DespawningOneCombatantLeavesTheOtherUntouched()
        {
            Request(BattleRequest.Start(BattleSetup.Default));
            Step(2);
            var session = Session;
            var ai = Find(session.AiCombatant);

            Assert.That(Request(BattleRequest.Despawn(session.HumanCombatant)), Is.EqualTo(BattleRequestOutcome.Despawned));

            Assert.That(LiveIds, Is.EqualTo(new[] { session.AiCombatant.Value }));
            Assert.That(Find(session.AiCombatant), Is.EqualTo(ai));
            AssertCombatant(session.AiCombatant, BattleSetup.Default.Ai);
            Assert.That(Session.State, Is.EqualTo(BattleState.Active));
            Submit(Cmd(session.HumanCombatant, Tick + 1), Cmd(session.AiCombatant, Tick + 1, source: CommandSource.AI));
            Step();
            Assert.That(Outcomes, Is.EqualTo(new[] { CommandOutcome.UnknownCombatant, CommandOutcome.Accepted }));

            Assert.That(Request(BattleRequest.Despawn(session.HumanCombatant)), Is.EqualTo(BattleRequestOutcome.UnknownCombatant));
            Assert.That(Request(BattleRequest.Despawn(CombatantId.Invalid)), Is.EqualTo(BattleRequestOutcome.UnknownCombatant));
            Assert.That(LiveIds.Length, Is.EqualTo(1));

            Request(BattleRequest.End());
            Assert.That(Request(BattleRequest.Despawn(session.AiCombatant)), Is.EqualTo(BattleRequestOutcome.NotActive));
            Request(BattleRequest.Start(BattleSetup.Default));
            Assert.That(LiveIds, Is.EqualTo(new[] { 3u, 4u }), "A despawned ID is not reused either.");
        }

        [Test]
        public void EndAlsoRemovesDefeatedCombatants()
        {
            Request(BattleRequest.Start(BattleSetup.Default));
            Step();
            Manager.SetComponentEnabled<InCombat>(Find(Session.AiCombatant), false);

            Request(BattleRequest.End());
            Assert.That(CombatantQuery.CalculateEntityCount(), Is.Zero);
        }

        // ---- scope of cleanup ----

        [Test]
        public void BattleCleanupNeverTouchesTheClockTheQueueOrUnrelatedEntities()
        {
            var unrelated = Manager.CreateEntity(typeof(CombatPosition));
            var clock = Manager.CreateEntityQuery(typeof(SimulationClock)).GetSingletonEntity();
            var queue = QueueEntity;

            Request(BattleRequest.Start(BattleSetup.Default));
            Step(2);
            Request(BattleRequest.Restart(BattleSetup.Default));
            Step();
            Request(BattleRequest.End());

            Assert.That(Manager.Exists(unrelated), Is.True);
            Assert.That(Manager.Exists(clock), Is.True);
            Assert.That(Manager.Exists(queue), Is.True);
            Assert.That(Manager.HasComponent<SimulationClockRunning>(clock), Is.True);
            Assert.That(Tick, Is.EqualTo(3u));
        }

        [Test]
        public void DestroyingTheLifecycleSystemLeavesNoSessionOrCombatant()
        {
            Request(BattleRequest.Start(BattleSetup.Default));
            m_World.DestroySystem(m_Lifecycle);

            Assert.That(Manager.CreateEntityQuery(typeof(BattleSession)).CalculateEntityCount(), Is.Zero);
            Assert.That(CombatantQuery.CalculateEntityCount(), Is.Zero);
            Assert.That(() => BattleRequest.Submit(Manager, BattleRequest.End()), Throws.InvalidOperationException);
        }

        // ---- limits ----

        [Test]
        public void IdAllocatorRejectsExhaustionInsteadOfWrappingToZero()
        {
            var session = Manager.CreateEntityQuery(typeof(BattleSession)).GetSingletonEntity();
            Manager.SetComponentData(session, CombatantIdAllocator.AfterIssued(uint.MaxValue - 3));

            Assert.That(Request(BattleRequest.Start(BattleSetup.Default)), Is.EqualTo(BattleRequestOutcome.Started));
            Assert.That(LiveIds, Is.EqualTo(new[] { uint.MaxValue - 2, uint.MaxValue - 1 }));

            Assert.That(Request(BattleRequest.Restart(BattleSetup.Default)), Is.EqualTo(BattleRequestOutcome.IdsExhausted));
            Assert.That(LiveIds, Is.EqualTo(new[] { uint.MaxValue - 2, uint.MaxValue - 1 }), "The running battle is kept.");
            Assert.That(Manager.GetComponentData<CombatantIdAllocator>(session).LastIssued, Is.EqualTo(uint.MaxValue - 1));

            var allocator = CombatantIdAllocator.AfterIssued(uint.MaxValue);
            Assert.That(allocator.TryReserve(1, out var none), Is.False);
            Assert.That(none, Is.Zero);
            Assert.That(allocator.LastIssued, Is.EqualTo(uint.MaxValue));
        }

        [Test]
        public void StoppedClockRejectsANewBattleButStillAllowsEnd()
        {
            Request(BattleRequest.Start(BattleSetup.Default));
            var clock = Manager.CreateEntityQuery(typeof(SimulationClock)).GetSingletonEntity();
            Manager.SetComponentData(clock, SimulationClock.AtTick(SimulationClock.LastTick));
            LogAssert.Expect(LogType.Error, "SimulationClock reached its last tick; the clock and combat phases stopped.");
            Step();
            Assert.That(Manager.HasComponent<SimulationClockRunning>(clock), Is.False);

            Assert.That(Request(BattleRequest.Restart(BattleSetup.Default)), Is.EqualTo(BattleRequestOutcome.ClockStopped));
            Assert.That(LiveIds, Is.EqualTo(new[] { 1u, 2u }));
            Assert.That(Request(BattleRequest.End()), Is.EqualTo(BattleRequestOutcome.Ended));
            Assert.That(Request(BattleRequest.Start(BattleSetup.Default)), Is.EqualTo(BattleRequestOutcome.ClockStopped));
            Assert.That(CombatantQuery.CalculateEntityCount(), Is.Zero);
            Assert.That(Tick, Is.EqualTo(SimulationClock.LastTick), "The clock is never reset to start a battle.");
        }

        // ---- worlds ----

        [Test]
        public void SeparateWorldsHaveIndependentBattlesAndIds()
        {
            using var other = CreateBattleWorld("MLF Legends lifecycle test 2", out _, out var otherLifecycle);
            Request(BattleRequest.Start(BattleSetup.Default));
            Request(BattleRequest.Restart(BattleSetup.Default));

            BattleRequest.Submit(other.EntityManager, BattleRequest.Start(BattleSetup.Default));
            otherLifecycle.Update(other.Unmanaged);

            var otherIds = other.EntityManager.CreateEntityQuery(typeof(CombatantId)).ToComponentDataArray<CombatantId>(Allocator.Temp);
            Assert.That(otherIds.ToArray().Select(id => id.Value).OrderBy(v => v), Is.EqualTo(new[] { 1u, 2u }));
            otherIds.Dispose();
            Assert.That(LiveIds, Is.EqualTo(new[] { 3u, 4u }));

            BattleRequest.Submit(other.EntityManager, BattleRequest.End());
            otherLifecycle.Update(other.Unmanaged);
            Assert.That(LiveIds, Is.EqualTo(new[] { 3u, 4u }), "Ending a battle in another world leaves this one alone.");
        }

        [Test]
        public void LifecycleIdleUpdateDoesNotAllocateManagedMemory()
        {
            Boundary();
            Assert.That(() => Boundary(), Is.Not.AllocatingGCMemory());
        }
    }
}
