using System;
using System.Collections.Generic;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Core;
using Unity.Entities;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace MLFLegends.Tests.EditMode
{
    // Isolated test worlds with the real clock, phase group and processor; producers are test systems
    // or direct buffer writes between fixed steps. Never touches the default world.
    public partial class CombatCommandProcessorTests
    {
        World m_World;
        FixedStepSimulationSystemGroup m_FixedStep;
        SystemHandle m_Processor;
        FirstProducer m_First;
        SecondProducer m_Second;
        uint m_Frame;

        [SetUp]
        public void SetUp()
        {
            m_World = new World("MLF Legends command test");
            m_FixedStep = m_World.CreateSystemManaged<FixedStepSimulationSystemGroup>();
            m_FixedStep.AddSystemToUpdateList(m_World.CreateSystemManaged<BeginFixedStepSimulationEntityCommandBufferSystem>());
            m_FixedStep.AddSystemToUpdateList(m_World.CreateSystem<CombatClockSystem>());

            var collection = m_World.CreateSystemManaged<CombatCommandCollectionSystemGroup>();
            m_Processor = m_World.CreateSystem<CombatCommandProcessorSystem>();
            m_First = m_World.CreateSystemManaged<FirstProducer>();
            m_Second = m_World.CreateSystemManaged<SecondProducer>();
            // Processor added first: its position comes from OrderLast, not from insertion order.
            collection.AddSystemToUpdateList(m_Processor);
            collection.AddSystemToUpdateList(m_Second);
            collection.AddSystemToUpdateList(m_First);
            m_FixedStep.AddSystemToUpdateList(collection);
            m_FixedStep.SortSystems();
            m_Frame = 0;
        }

        [TearDown]
        public void TearDown()
        {
            if (m_World != null && m_World.IsCreated)
                m_World.Dispose();
        }

        // ---- helpers ----

        EntityManager Manager => m_World.EntityManager;

        uint Tick => Manager.CreateEntityQuery(typeof(SimulationClock)).GetSingleton<SimulationClock>().Tick;

        CombatCommandQueue Queue => Manager.CreateEntityQuery(typeof(CombatCommandQueue)).GetSingleton<CombatCommandQueue>();

        Entity QueueEntity => Manager.CreateEntityQuery(typeof(CombatCommandQueue)).GetSingletonEntity();

        CombatCommandResult[] Results => Manager.GetBuffer<CombatCommandResult>(QueueEntity).AsNativeArray().ToArray();

        int PendingCount => Manager.GetBuffer<PendingCombatCommand>(QueueEntity).Length;

        int SubmissionCount => Manager.GetBuffer<CombatCommandSubmission>(QueueEntity).Length;

        // One fixed step per call (frame half a tick past the tick time, as in CombatClockTests).
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

        // Several fixed steps in one render frame (catch-up), half a tick past the last one; stays below
        // World.MaximumDeltaTime (19 ticks at 60 Hz) so no tick is dropped.
        void StepInOneFrame(int ticks)
        {
            var dt = (double)SimulationTiming.TickDuration;
            m_World.SetTime(new TimeData((m_Frame + ticks - 0.5) * dt, (float)(ticks * dt)));
            m_FixedStep.Update();
            m_Frame += (uint)ticks;
        }

        // Frame-rate producer: writes between fixed steps.
        void Submit(params CombatCommand[] commands)
        {
            var buffer = Manager.GetBuffer<CombatCommandSubmission>(QueueEntity);
            foreach (var command in commands)
                buffer.Add(new CombatCommandSubmission(command));
        }

        Entity Spawn(uint id)
        {
            var entity = Manager.CreateEntity(typeof(CombatantId), typeof(InCombat));
            Manager.SetComponentData(entity, new CombatantId { Value = id });
            return entity;
        }

        AppliedCombatCommand Applied(Entity combatant) => Manager.GetComponentData<AppliedCombatCommand>(combatant);

        static CombatCommand Cmd(uint id, uint tick, sbyte moveX = 0, CommandSource source = CommandSource.Player,
            CommandButtons pressed = CommandButtons.None) =>
            new CombatCommand { Combatant = new CombatantId { Value = id }, Tick = tick, MoveX = moveX, Pressed = pressed, Source = source };

        static CommandOutcome[] Outcomes(IEnumerable<CombatCommandResult> results) => results.Select(r => r.Outcome).ToArray();

        static void AssertSame(CombatCommand actual, CombatCommand expected)
        {
            Assert.That(actual.Combatant.Value, Is.EqualTo(expected.Combatant.Value));
            Assert.That(actual.Tick, Is.EqualTo(expected.Tick));
            Assert.That(actual.MoveX, Is.EqualTo(expected.MoveX));
            Assert.That(actual.MoveZ, Is.EqualTo(expected.MoveZ));
            Assert.That(actual.Held, Is.EqualTo(expected.Held));
            Assert.That(actual.Pressed, Is.EqualTo(expected.Pressed));
            Assert.That(actual.Source, Is.EqualTo(expected.Source));
        }

        // ---- creation ----

        [Test]
        public void ProcessorOwnsOneQueueAndASecondInstanceIsRejected()
        {
            Assert.That(Manager.CreateEntityQuery(typeof(CombatCommandQueue)).CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(() => m_World.CreateSystem<CombatCommandProcessorSystem>(), Throws.Exception);
            Assert.That(Manager.CreateEntityQuery(typeof(CombatCommandQueue)).CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(Queue.BattleStartTick, Is.EqualTo(SimulationClock.NoTick));

            m_World.DestroySystem(m_Processor);
            Assert.That(Manager.CreateEntityQuery(typeof(CombatCommandQueue)).CalculateEntityCount(), Is.Zero);
        }

        [Test]
        public void ProcessorRunsLastInTheCollectionPhase()
        {
            var order = new List<string>();
            m_Second.Produce = t => { order.Add("second"); return Array.Empty<CombatCommand>(); };
            var combatant = Spawn(1);
            m_First.Produce = t => { order.Add("first"); return new[] { Cmd(1, t, 1) }; };

            Step();

            Assert.That(order, Is.EqualTo(new[] { "first", "second" }));
            Assert.That(Applied(combatant).Command.MoveX, Is.EqualTo(1), "Commands produced in the same tick reach the processor.");
        }

        // ---- accept / none / missing ----

        [Test]
        public void CommandForTheCurrentTickIsAcceptedAndAppliedOnThatTick()
        {
            var combatant = Spawn(7);
            var command = Cmd(7, 1, moveX: -1, pressed: CommandButtons.Attack);
            command.MoveZ = 1;
            command.Held = CommandButtons.Guard;
            Submit(command);

            Step();

            Assert.That(Queue.LastProcessedTick, Is.EqualTo(1u));
            Assert.That(Results.Length, Is.EqualTo(1));
            Assert.That(Results[0].Outcome, Is.EqualTo(CommandOutcome.Accepted));
            AssertSame(Results[0].Command, command);
            Assert.That(Applied(combatant).WasSubmitted, Is.True);
            AssertSame(Applied(combatant).Command, command);
            Assert.That(SubmissionCount, Is.Zero, "The inbox is drained every tick.");
        }

        [Test]
        public void MissingCommandIsAppliedAsNoneForTheTick()
        {
            var combatant = Spawn(3);
            Submit(Cmd(3, 1, moveX: 1));
            Step(2);

            var applied = Applied(combatant);
            Assert.That(applied.Command.Tick, Is.EqualTo(2u));
            Assert.That(applied.Command.Combatant.Value, Is.EqualTo(3u));
            Assert.That(applied.Command.IsNone, Is.True);
            Assert.That(applied.WasSubmitted, Is.False);
            Assert.That(Results, Is.Empty, "A missing command produces no result entry.");
        }

        [Test]
        public void SubmittedNoneCommandIsAcceptedLikeAnyOther()
        {
            var combatant = Spawn(3);
            Submit(Cmd(3, 1, source: CommandSource.AI));
            Step();

            Assert.That(Outcomes(Results), Is.EqualTo(new[] { CommandOutcome.Accepted }));
            Assert.That(Applied(combatant).Command.IsNone, Is.True);
            Assert.That(Applied(combatant).WasSubmitted, Is.True);
        }

        // ---- duplicates and conflicts ----

        [Test]
        public void SecondCommandForTheSameKeyIsRejectedAndNeverOverwrites()
        {
            var combatant = Spawn(1);
            var first = Cmd(1, 1, moveX: 1);
            Submit(first, first, Cmd(1, 1, moveX: -1), Cmd(1, 1, moveX: 1, source: CommandSource.AI));
            Step();

            Assert.That(Outcomes(Results), Is.EqualTo(new[]
            {
                CommandOutcome.Accepted, CommandOutcome.Duplicate, CommandOutcome.Conflict, CommandOutcome.Conflict,
            }));
            AssertSame(Applied(combatant).Command, first);
        }

        [TestCase(CommandSource.Player, CommandSource.AI)]
        [TestCase(CommandSource.AI, CommandSource.Player)]
        public void PlayerAndAiFollowTheSameFirstProducerWinsRule(CommandSource firstSource, CommandSource secondSource)
        {
            var combatant = Spawn(1);
            m_First.Produce = t => new[] { Cmd(1, t, moveX: 1, source: firstSource) };
            m_Second.Produce = t => new[] { Cmd(1, t, moveX: -1, source: secondSource) };
            Step();

            Assert.That(Outcomes(Results), Is.EqualTo(new[] { CommandOutcome.Accepted, CommandOutcome.Conflict }));
            Assert.That(Results[0].Command.Source, Is.EqualTo(firstSource));
            Assert.That(Applied(combatant).Command.Source, Is.EqualTo(firstSource));
            Assert.That(Applied(combatant).Command.MoveX, Is.EqualTo(1));
        }

        [Test]
        public void DeferredCommandWasSubmittedBeforeTheSameTickSubmissionAndWins()
        {
            var combatant = Spawn(1);
            Submit(Cmd(1, 3, moveX: 1)); // submitted during tick 1 for tick 3
            Step();
            m_First.Produce = t => t == 3 ? new[] { Cmd(1, 3, moveX: -1) } : Array.Empty<CombatCommand>();
            Step(2);

            Assert.That(Outcomes(Results), Is.EqualTo(new[] { CommandOutcome.Accepted, CommandOutcome.Conflict }));
            Assert.That(Applied(combatant).Command.MoveX, Is.EqualTo(1));
        }

        // ---- stale / future ----

        [Test]
        public void StaleAndZeroTickCommandsAreRejected()
        {
            var combatant = Spawn(1);
            Step(3);
            Submit(Cmd(1, 2), Cmd(1, 3), Cmd(1, 0));
            Step(); // tick 4

            Assert.That(Outcomes(Results), Is.EqualTo(new[] { CommandOutcome.InvalidTick, CommandOutcome.Stale, CommandOutcome.Stale }));
            Assert.That(Applied(combatant).WasSubmitted, Is.False);
            Assert.That(PendingCount, Is.Zero);
        }

        [Test]
        public void CommandSubmittedAfterItsTickWasProcessedIsStale()
        {
            Spawn(1);
            // A state-phase writer is not a producer; emulate a late write after the tick's processing.
            Step();
            Submit(Cmd(1, 1, moveX: 1));
            Step();
            Assert.That(Outcomes(Results), Is.EqualTo(new[] { CommandOutcome.Stale }));
        }

        [Test]
        public void FutureCommandIsAppliedExactlyOnItsTickAndNeverBefore()
        {
            var combatant = Spawn(1);
            Submit(Cmd(1, 4, moveX: 1));

            Step();
            Assert.That(Outcomes(Results), Is.EqualTo(new[] { CommandOutcome.Deferred }));
            Assert.That(PendingCount, Is.EqualTo(1));

            for (uint tick = 2; tick <= 3; tick++)
            {
                Step();
                Assert.That(Applied(combatant).Command.Tick, Is.EqualTo(tick));
                Assert.That(Applied(combatant).WasSubmitted, Is.False);
            }

            Step();
            Assert.That(Outcomes(Results), Is.EqualTo(new[] { CommandOutcome.Accepted }));
            Assert.That(Applied(combatant).Command.Tick, Is.EqualTo(4u));
            Assert.That(Applied(combatant).Command.MoveX, Is.EqualTo(1));
            Assert.That(PendingCount, Is.Zero);

            Step();
            Assert.That(Applied(combatant).WasSubmitted, Is.False, "Applied only on its tick, never later.");
        }

        [Test]
        public void CommandsBeyondTheLeadLimitAreRejected()
        {
            Spawn(1);
            Submit(Cmd(1, 1 + CombatCommandQueue.MaxLeadTicks), Cmd(1, 2 + CombatCommandQueue.MaxLeadTicks));
            Step();

            Assert.That(Outcomes(Results), Is.EqualTo(new[] { CommandOutcome.Deferred, CommandOutcome.TooFarAhead }));
            Assert.That(PendingCount, Is.EqualTo(1));
        }

        [Test]
        public void PendingCommandWhoseTickWasNotProcessedExpiresAsStale()
        {
            var combatant = Spawn(1);
            Submit(Cmd(1, 2, moveX: 1));
            Step();

            ref var processorState = ref m_World.Unmanaged.ResolveSystemStateRef(m_Processor);
            processorState.Enabled = false;
            Step(); // tick 2 without the processor
            processorState.Enabled = true;
            Step(); // tick 3

            Assert.That(Outcomes(Results), Is.EqualTo(new[] { CommandOutcome.Stale }));
            Assert.That(Applied(combatant).Command.Tick, Is.EqualTo(3u));
            Assert.That(Applied(combatant).WasSubmitted, Is.False);
            Assert.That(PendingCount, Is.Zero);
        }

        [Test]
        public void RepeatedFutureKeyIsRejectedAtSubmissionAndKeepsOnePendingCommand()
        {
            var combatant = Spawn(1);
            var first = Cmd(1, 3, moveX: 1);
            Submit(first, first, Cmd(1, 3, moveX: -1, source: CommandSource.AI));
            Step();
            Assert.That(Outcomes(Results), Is.EqualTo(new[] { CommandOutcome.Deferred, CommandOutcome.Duplicate, CommandOutcome.Conflict }));
            Assert.That(PendingCount, Is.EqualTo(1));

            Submit(Cmd(1, 3, moveX: -1)); // next tick, still in the future
            Step();
            Assert.That(Outcomes(Results), Is.EqualTo(new[] { CommandOutcome.Conflict }));

            Step();
            Assert.That(Outcomes(Results), Is.EqualTo(new[] { CommandOutcome.Accepted }));
            AssertSame(Applied(combatant).Command, first);
        }

        [Test]
        public void FutureCommandForACombatantNotInCombatIsRejectedAtSubmission()
        {
            var defeated = Spawn(2);
            Manager.SetComponentEnabled<InCombat>(defeated, false);
            Submit(Cmd(2, 5), Cmd(99, 5));
            Step();

            Assert.That(Outcomes(Results), Is.EqualTo(new[] { CommandOutcome.UnknownCombatant, CommandOutcome.UnknownCombatant }));
            Assert.That(PendingCount, Is.Zero, "Garbage IDs never occupy the pending store.");
        }

        [Test]
        public void PendingStoreIsBoundedAndOverflowIsReported()
        {
            // 401 combatants x 32 future ticks = 12 832 distinct keys > MaxPendingCommands (400 x 32).
            const int combatants = 401;
            for (uint id = 1; id <= combatants; id++)
                Spawn(id);
            var commands = new List<CombatCommand>();
            for (uint tick = 2; tick <= 1 + CombatCommandQueue.MaxLeadTicks; tick++)
                for (uint id = 1; id <= combatants; id++)
                    commands.Add(Cmd(id, tick));
            Submit(commands.ToArray());
            Step();

            var outcomes = Outcomes(Results);
            Assert.That(outcomes.Count(o => o == CommandOutcome.Deferred), Is.EqualTo(CombatCommandQueue.MaxPendingCommands));
            Assert.That(outcomes.Count(o => o == CommandOutcome.QueueFull), Is.EqualTo(commands.Count - CombatCommandQueue.MaxPendingCommands));
            Assert.That(PendingCount, Is.EqualTo(CombatCommandQueue.MaxPendingCommands));

            Step();
            Assert.That(Outcomes(Results).Count(o => o == CommandOutcome.Accepted), Is.EqualTo(combatants));
            Assert.That(PendingCount, Is.EqualTo(CombatCommandQueue.MaxPendingCommands - combatants));
        }

        // ---- invalid values and combatants ----

        [Test]
        public void InvalidValuesAreRejectedForEverySource()
        {
            var combatant = Spawn(1);
            var moveX = Cmd(1, 1, moveX: 2);
            var moveZ = Cmd(1, 1, source: CommandSource.AI);
            moveZ.MoveZ = -2;
            var buttons = Cmd(1, 1, source: CommandSource.AI);
            buttons.Held = (CommandButtons)0x80;
            var noSource = Cmd(1, 1, source: CommandSource.None);
            var badSource = Cmd(1, 1, source: (CommandSource)9);
            Submit(moveX, moveZ, buttons, noSource, badSource, Cmd(0, 1));
            Step();

            Assert.That(Outcomes(Results), Is.EqualTo(new[]
            {
                CommandOutcome.InvalidCombatant,
                CommandOutcome.InvalidValue, CommandOutcome.InvalidValue, CommandOutcome.InvalidValue,
                CommandOutcome.InvalidValue, CommandOutcome.InvalidValue,
            }));
            Assert.That(Applied(combatant).WasSubmitted, Is.False);
        }

        [Test]
        public void CommandsForUnknownDefeatedOrDespawnedCombatantsAreRejected()
        {
            var defeated = Spawn(2);
            var despawned = Spawn(3);
            Step();
            Manager.SetComponentEnabled<InCombat>(defeated, false);
            var lastDefeatedCommand = Applied(defeated).Command.Tick;
            Manager.DestroyEntity(despawned);
            Submit(Cmd(2, 2), Cmd(3, 2), Cmd(99, 2));
            Step();

            Assert.That(Outcomes(Results), Is.EqualTo(new[]
            {
                CommandOutcome.UnknownCombatant, CommandOutcome.UnknownCombatant, CommandOutcome.UnknownCombatant,
            }));
            Assert.That(Applied(defeated).Command.Tick, Is.EqualTo(lastDefeatedCommand), "Disabled combatants are not written.");
        }

        [Test]
        public void DeferredCommandForACombatantDespawnedBeforeItsTickIsRejected()
        {
            var combatant = Spawn(5);
            Submit(Cmd(5, 3, moveX: 1));
            Step();
            Manager.DestroyEntity(combatant);
            Step(2);

            Assert.That(Outcomes(Results), Is.EqualTo(new[] { CommandOutcome.UnknownCombatant }));
            Assert.That(PendingCount, Is.Zero);
        }

        [Test]
        public void CombatantSpawnedMidBattleReceivesCommandsFromItsFirstTick()
        {
            Step(2);
            var combatant = Spawn(4);
            Submit(Cmd(4, 3, moveX: -1));
            Step();

            Assert.That(Manager.HasComponent<AppliedCombatCommand>(combatant), Is.True);
            Assert.That(Applied(combatant).Command.MoveX, Is.EqualTo(-1));
        }

        // ---- order ----

        [Test]
        public void ResultsAndAppliedCommandsDoNotDependOnSubmissionOrderOfDistinctKeys()
        {
            var forward = RunWithSubmissionOrder(reverse: false);
            TearDown();
            SetUp();
            var backward = RunWithSubmissionOrder(reverse: true);

            Assert.That(backward.results, Is.EqualTo(forward.results));
            Assert.That(backward.applied, Is.EqualTo(forward.applied));
            Assert.That(forward.results.Select(r => r.id), Is.Ordered);
        }

        (List<(uint id, uint tick, sbyte move, CommandOutcome outcome)> results, List<(uint id, sbyte move)> applied) RunWithSubmissionOrder(bool reverse)
        {
            // Spawn in a scrambled order so chunk order differs from id order.
            var ids = new uint[] { 17, 3, 250, 42, 8, 1 };
            var entities = ids.ToDictionary(id => id, Spawn);
            var commands = ids.Select(id => Cmd(id, 1, moveX: (sbyte)(id % 3 - 1))).Concat(new[] { Cmd(500, 1), Cmd(3, 0) }).ToList();
            if (reverse)
                commands.Reverse();
            Submit(commands.ToArray());
            Step();

            var results = Results.Select(r => (r.Command.Combatant.Value, r.Command.Tick, r.Command.MoveX, r.Outcome)).ToList();
            var applied = ids.OrderBy(id => id).Select(id => (id, Applied(entities[id]).Command.MoveX)).ToList();
            return (results, applied);
        }

        // ---- scale ----

        [Test]
        public void FourHundredCombatantsWithCurrentAndFutureCommandsAreAllAppliedOnTheirTicks()
        {
            const int count = 400;
            const int ticks = 120;
            var entities = new Entity[count];
            for (var i = 0; i < count; i++)
                entities[i] = Spawn((uint)(i + 1));

            // Player-like producer for even ids (current tick), AI-like for odd ids (two ticks ahead).
            m_First.Produce = t => Enumerable.Range(0, count).Where(i => i % 2 == 0)
                .Select(i => Cmd((uint)(i + 1), t, moveX: (sbyte)((t + i) % 3 - 1))).ToArray();
            m_Second.Produce = t => Enumerable.Range(0, count).Where(i => i % 2 == 1)
                .Select(i => Cmd((uint)(i + 1), t + 2, moveX: (sbyte)((t + 2 + i) % 3 - 1), source: CommandSource.AI)).ToArray();

            for (uint tick = 1; tick <= ticks; tick++)
            {
                Step();
                var results = Results;
                var accepted = results.Where(r => r.Outcome == CommandOutcome.Accepted).ToArray();
                Assert.That(accepted.Length, Is.EqualTo(tick <= 2 ? count / 2 : count), $"tick {tick}");
                Assert.That(results.Count(r => r.Outcome == CommandOutcome.Deferred), Is.EqualTo(count / 2));
                Assert.That(PendingCount, Is.EqualTo(tick == 1 ? count / 2 : count), "Pending holds at most two ticks of the AI half.");

                for (var i = 0; i < count; i++)
                {
                    var applied = Applied(entities[i]).Command;
                    Assert.That(applied.Tick, Is.EqualTo(tick));
                    if (i % 2 == 0 || tick > 2)
                        Assert.That(applied.MoveX, Is.EqualTo((sbyte)((tick + i) % 3 - 1)), $"id {i + 1} tick {tick}");
                }
            }
        }

        [Test]
        public void FullPendingStoreWithFourHundredCombatantsSurvivesCatchUpFramesAndABattleReset()
        {
            const int count = 400;
            const uint lead = CombatCommandQueue.MaxLeadTicks;
            const int ticksPerFrame = 16;
            var entities = new Entity[count];
            for (var i = 0; i < count; i++)
                entities[i] = Spawn((uint)(i + 1));

            // Every tick each combatant gets a command for the tick and an equal one a full lead ahead, so the
            // pending store reaches MaxPendingCommands and, once due, the earlier one wins over a Duplicate.
            static sbyte Move(int id, uint tick) => (sbyte)((id + tick) % 3 - 1);
            m_First.Produce = t => Enumerable.Range(1, count).Select(id => Cmd((uint)id, t, moveX: Move(id, t))).ToArray();
            m_Second.Produce = t => Enumerable.Range(1, count).Select(id => Cmd((uint)id, t + lead, moveX: Move(id, t + lead))).ToArray();

            Step();
            for (var frame = 0; frame < 4; frame++)
            {
                StepInOneFrame(ticksPerFrame);
                var tick = Tick;
                Assert.That(tick, Is.EqualTo(1u + (uint)((frame + 1) * ticksPerFrame)), "One render frame runs every catch-up tick.");
                Assert.That(Queue.LastProcessedTick, Is.EqualTo(tick));

                var results = Results;
                var duplicates = tick > lead ? count : 0;
                Assert.That(results.Length, Is.EqualTo(2 * count + duplicates), $"tick {tick}");
                Assert.That(results.Count(r => r.Outcome == CommandOutcome.Accepted), Is.EqualTo(count));
                Assert.That(results.Count(r => r.Outcome == CommandOutcome.Deferred), Is.EqualTo(count));
                Assert.That(results.Count(r => r.Outcome == CommandOutcome.Duplicate), Is.EqualTo(duplicates));
                Assert.That(PendingCount, Is.EqualTo(count * (int)Math.Min(tick, lead)));
                for (var i = 0; i < count; i++)
                {
                    var applied = Applied(entities[i]).Command;
                    Assert.That(applied.Tick, Is.EqualTo(tick));
                    Assert.That(applied.MoveX, Is.EqualTo(Move(i + 1, tick)), $"id {i + 1} tick {tick}");
                }
            }
            Assert.That(PendingCount, Is.EqualTo(CombatCommandQueue.MaxPendingCommands));

            // New battle in the same World: none of the 12 800 kept commands may survive the reset.
            m_First.Produce = null;
            m_Second.Produce = null;
            CombatCommandQueue.ResetForBattle(Manager, Tick);
            Assert.That(PendingCount, Is.Zero);
            Assert.That(SubmissionCount, Is.Zero);
            Assert.That(Results, Is.Empty);
            Assert.That(entities.All(e => Applied(e).Command.Tick == SimulationClock.NoTick));

            StepInOneFrame(ticksPerFrame);
            Assert.That(Results, Is.Empty);
            Assert.That(PendingCount, Is.Zero);
            Assert.That(entities.All(e => Applied(e).Command.Tick == Tick && !Applied(e).WasSubmitted));
        }

        [Test]
        public void ProcessorUpdateDoesNotAllocateManagedMemoryWithFourHundredCombatants()
        {
            for (uint i = 1; i <= 400; i++)
                Spawn(i);
            Step();

            var commands = Enumerable.Range(1, 400).Select(i => Cmd((uint)i, Tick)).Concat(
                Enumerable.Range(1, 400).Select(i => Cmd((uint)i, Tick + 3))).ToArray();
            Submit(commands);
            m_Processor.Update(m_World.Unmanaged); // warm-up
            Submit(commands);
            Assert.That(() => m_Processor.Update(m_World.Unmanaged), Is.Not.AllocatingGCMemory());
        }

        // ---- battle cleanup ----

        [Test]
        public void ResetForBattleClearsEverythingAndOldCommandsNeverReachTheNextBattle()
        {
            // Battle 1: ids 1, 2 with current, future and unprocessed commands.
            var old1 = Spawn(1);
            Spawn(2);
            Submit(Cmd(1, 1, moveX: 1), Cmd(1, 5, moveX: 1), Cmd(2, 30, moveX: -1));
            Step(2);
            Submit(Cmd(1, 3, moveX: 1)); // still in the inbox at the boundary
            Assert.That(PendingCount, Is.EqualTo(2));

            // #5 lifecycle: end battle 1 at the tick boundary, despawn, start battle 2 in the same World.
            var tickBefore = Tick;
            CombatCommandQueue.ResetForBattle(Manager, tickBefore);
            Assert.That(Tick, Is.EqualTo(tickBefore), "The clock is never touched.");
            Assert.That(Queue.BattleStartTick, Is.EqualTo(tickBefore));
            Assert.That(SubmissionCount, Is.Zero);
            Assert.That(PendingCount, Is.Zero);
            Assert.That(Results, Is.Empty);
            Assert.That(Applied(old1).Command.Tick, Is.EqualTo(SimulationClock.NoTick));

            Manager.DestroyEntity(Manager.CreateEntityQuery(typeof(CombatantId)));
            var new3 = Spawn(3);
            var new4 = Spawn(4);

            // Late producers of battle 1 and normal producers of battle 2.
            Submit(Cmd(1, tickBefore + 3, moveX: 1), Cmd(3, tickBefore, moveX: 1), Cmd(4, tickBefore + 1, moveX: -1));
            Step(); // first battle tick = BattleStartTick + 1
            Assert.That(Tick - Queue.BattleStartTick, Is.EqualTo(1u));
            Assert.That(Outcomes(Results), Is.EqualTo(new[]
            {
                CommandOutcome.UnknownCombatant, CommandOutcome.BeforeBattleStart, CommandOutcome.Accepted,
            }));
            Assert.That(Applied(new3).WasSubmitted, Is.False);
            Assert.That(Applied(new4).Command.MoveX, Is.EqualTo(-1));

            for (var i = 0; i < 40; i++)
            {
                Step();
                Assert.That(Results.All(r => r.Command.Combatant.Value != 2), "Pending command of battle 1 leaked.");
                Assert.That(Results.Where(r => r.Command.Combatant.Value == 1).All(r => r.Outcome == CommandOutcome.UnknownCombatant));
            }
            Assert.That(PendingCount, Is.Zero);
        }

        [Test]
        public void ResetForBattleRejectsBoundariesInThePast()
        {
            Step(5);
            Assert.That(() => CombatCommandQueue.ResetForBattle(Manager, 4), Throws.TypeOf<ArgumentOutOfRangeException>());
            CombatCommandQueue.ResetForBattle(Manager, 7);
            Step(); // tick 6
            Assert.That(() => CombatCommandQueue.ResetForBattle(Manager, 7), Throws.Nothing, "Same boundary again is allowed.");
            Assert.That(Queue.BattleStartTick, Is.EqualTo(7u));
            Assert.That(() => CombatCommandQueue.ResetForBattle(Manager, 6), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void CanResetForBattleAnswersExactlyLikeResetForBattleWithoutSideEffects()
        {
            Step(5);
            Assert.That(CombatCommandQueue.CanResetForBattle(Manager, 4), Is.False, "Tick in the past.");
            Assert.That(CombatCommandQueue.CanResetForBattle(Manager, 5), Is.True);
            CombatCommandQueue.ResetForBattle(Manager, 7);
            Assert.That(CombatCommandQueue.CanResetForBattle(Manager, 6), Is.False, "Boundary would move back.");
            Assert.That(CombatCommandQueue.CanResetForBattle(Manager, 7), Is.True);

            // A rejected reset, checked or thrown, leaves the queue as it was.
            var combatant = Spawn(1);
            Step(); // tick 6, before the boundary
            Submit(Cmd(1, 9, moveX: 1));
            Assert.That(CombatCommandQueue.CanResetForBattle(Manager, 6), Is.False);
            Assert.That(() => CombatCommandQueue.ResetForBattle(Manager, 6), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(SubmissionCount, Is.EqualTo(1));
            Assert.That(Queue.BattleStartTick, Is.EqualTo(7u));
            Assert.That(Manager.Exists(combatant), Is.True);

            using (var world = new World("MLF Legends command test without processor"))
                Assert.That(CombatCommandQueue.CanResetForBattle(world.EntityManager, 0), Is.False);
        }

        [Test]
        public void ResetForBattleRequiresTheProcessor()
        {
            using (var world = new World("MLF Legends command test without processor"))
                Assert.That(() => CombatCommandQueue.ResetForBattle(world.EntityManager, 0), Throws.InvalidOperationException);
        }

        [Test]
        public void ResetFromABurstSystemInTheCollectionPhaseRejectsThatTicksCommands()
        {
            var collection = m_World.GetExistingSystemManaged<CombatCommandCollectionSystemGroup>();
            collection.AddSystemToUpdateList(m_World.CreateSystem<BurstBattleResetProbe>());
            collection.SortSystems();

            var combatant = Spawn(1);
            Submit(Cmd(1, 1, moveX: 1), Cmd(1, 4, moveX: 1));
            Step();
            Assert.That(PendingCount, Is.EqualTo(1));

            // Tick 2: the first producer submits, then the probe resets with BattleStartTick = 2, then the processor runs.
            Manager.CreateSingleton(new ResetRequest { OnTick = 2 });
            m_First.Produce = t => t == 2 ? new[] { Cmd(1, 2, moveX: -1) } : Array.Empty<CombatCommand>();
            m_Second.Produce = t => t == 2 ? new[] { Cmd(1, 2, moveX: -1), Cmd(1, 3, moveX: 1) } : Array.Empty<CombatCommand>();
            Step();

            var request = Manager.CreateEntityQuery(typeof(ResetRequest)).GetSingleton<ResetRequest>();
            Assert.That(request.Done, Is.EqualTo(1));
            if (Unity.Burst.BurstCompiler.IsEnabled)
                Assert.That(request.RanManaged, Is.Zero, "ResetForBattle must compile and run under Burst.");
            Assert.That(Queue.BattleStartTick, Is.EqualTo(2u));
            Assert.That(Results, Is.Empty, "Everything submitted before the reset is dropped.");
            Assert.That(PendingCount, Is.Zero);
            Assert.That(Applied(combatant).Command.Tick, Is.EqualTo(2u));
            Assert.That(Applied(combatant).WasSubmitted, Is.False);

            Step(2);
            Assert.That(Applied(combatant).WasSubmitted, Is.False, "Battle-1 command for tick 4 never applies.");
        }

        [Test]
        public void RepeatedBattlesInOneWorldStartFromAnEmptyQueue()
        {
            uint nextId = 1;
            for (var battle = 0; battle < 3; battle++)
            {
                CombatCommandQueue.ResetForBattle(Manager, Tick);
                Manager.DestroyEntity(Manager.CreateEntityQuery(typeof(CombatantId)));
                var a = Spawn(nextId++);
                var b = Spawn(nextId++);
                var start = Tick;
                var idA = Manager.GetComponentData<CombatantId>(a).Value;
                var idB = Manager.GetComponentData<CombatantId>(b).Value;
                Submit(Cmd(idA, start + 1, moveX: 1), Cmd(idB, start + 4, moveX: -1, source: CommandSource.AI));

                Step();
                Assert.That(Applied(a).Command.MoveX, Is.EqualTo(1));
                Assert.That(Applied(b).WasSubmitted, Is.False);
                Assert.That(PendingCount, Is.EqualTo(1));
                Step(2); // battle ends before b's command is due
            }
            Assert.That(Tick, Is.EqualTo(9u), "One monotonic World clock across battles.");
        }

        // ---- test systems ----

        internal struct ResetRequest : IComponentData
        {
            public uint OnTick;
            public byte Done;
            public byte RanManaged;
        }

        // Plays the battle lifecycle owner (#5) calling the cleanup contract from Burst, after the
        // producers and before the processor.
        [DisableAutoCreation]
        [Unity.Burst.BurstCompile]
        [UpdateInGroup(typeof(CombatCommandCollectionSystemGroup))]
        [UpdateAfter(typeof(SecondProducer))]
        internal partial struct BurstBattleResetProbe : ISystem
        {
            [Unity.Burst.BurstCompile(CompileSynchronously = true)]
            public void OnUpdate(ref SystemState state)
            {
                var tick = SystemAPI.GetSingleton<SimulationClock>().Tick;
                if (!SystemAPI.TryGetSingleton<ResetRequest>(out var request) || request.OnTick != tick)
                    return;

                CombatCommandQueue.ResetForBattle(state.EntityManager, tick);
                request.Done = 1;
                MarkManaged(ref request.RanManaged);
                SystemAPI.SetSingleton(request);
            }

            [Unity.Burst.BurstDiscard]
            static void MarkManaged(ref byte ranManaged) => ranManaged = 1;
        }

        abstract partial class ScriptedProducer : SystemBase
        {
            public Func<uint, IEnumerable<CombatCommand>> Produce;

            protected override void OnUpdate()
            {
                if (Produce == null)
                    return;
                var tick = SystemAPI.GetSingleton<SimulationClock>().Tick;
                var buffer = SystemAPI.GetSingletonBuffer<CombatCommandSubmission>();
                foreach (var command in Produce(tick))
                    buffer.Add(new CombatCommandSubmission(command));
            }
        }

        [DisableAutoCreation]
        [UpdateInGroup(typeof(CombatCommandCollectionSystemGroup))]
        [UpdateBefore(typeof(SecondProducer))]
        partial class FirstProducer : ScriptedProducer
        {
        }

        [DisableAutoCreation]
        [UpdateInGroup(typeof(CombatCommandCollectionSystemGroup))]
        partial class SecondProducer : ScriptedProducer
        {
        }
    }
}
