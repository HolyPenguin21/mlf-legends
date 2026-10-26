using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace MLFLegends.Tests.EditMode
{
    public partial class BattleLifecycleTests
    {
        // Initialization is a real frame boundary, not a fixed step or a second lifecycle instance.
        void InitializeFrame() => m_World.GetExistingSystemManaged<InitializationSystemGroup>().Update();

        int RequestCount
        {
            get
            {
                using var session = Manager.CreateEntityQuery(typeof(BattleSession));
                return Manager.GetBuffer<BattleRequest>(session.GetSingletonEntity()).Length;
            }
        }

        static int CountOf<T>(World world, NativeArray<SystemHandle> systems) where T : unmanaged, ISystem
        {
            var type = TypeManager.GetSystemTypeIndex<T>();
            var count = 0;
            foreach (var system in systems)
                count += world.Unmanaged.GetSystemTypeIndex(system) == type ? 1 : 0;
            return count;
        }

        [Test]
        public void InitializationProxyUpdatesTheOneFixedStepLifecycleOnAPausedFrame()
        {
            using (var all = m_World.Unmanaged.GetAllSystems(Allocator.Temp))
                Assert.That(CountOf<BattleLifecycleSystem>(m_World, all), Is.EqualTo(1), "Exactly one lifecycle instance in the World.");

            using (var initialization = m_World.GetExistingSystemManaged<InitializationSystemGroup>().GetAllSystems(Allocator.Temp))
            {
                Assert.That(CountOf<BattleLifecycleInitializationProxy>(m_World, initialization.AsArray()), Is.EqualTo(1));
                Assert.That(initialization.IndexOf(m_Lifecycle), Is.EqualTo(-1), "Initialization holds the proxy, not the lifecycle.");
            }

            using (var fixedStep = m_FixedStep.GetAllSystems(Allocator.Temp))
            {
                Assert.That(fixedStep.IndexOf(m_Lifecycle), Is.GreaterThanOrEqualTo(0));
                Assert.That(CountOf<BattleLifecycleInitializationProxy>(m_World, fixedStep.AsArray()), Is.Zero);
            }

            Step(2);
            var tick = Tick;
            BattleRequest.Submit(Manager, BattleRequest.Start(BattleSetup.Default, owner: 4));
            var before = m_World.Unmanaged.ResolveSystemStateRef(m_Lifecycle).LastSystemVersion;
            InitializeFrame();
            Assert.That(m_World.Unmanaged.ResolveSystemStateRef(m_Lifecycle).LastSystemVersion, Is.Not.EqualTo(before),
                "The proxy ran the existing lifecycle instance.");
            Assert.That(RequestCount, Is.Zero, "The one request buffer was drained once.");
            Assert.That((Session.IsActive, Session.Owner, Session.BattleStartTick), Is.EqualTo((true, 4u, tick)));
            Assert.That(Tick, Is.EqualTo(tick), "A paused frame applies requests without a fixed tick.");
            Assert.That(Queue.LastProcessedTick, Is.EqualTo(tick), "No command phase ran.");

            BattleRequest.Submit(Manager, BattleRequest.End(owner: 4));
            InitializeFrame();
            Assert.That((Session.IsActive, Session.LastOutcome, Tick), Is.EqualTo((false, BattleRequestOutcome.Ended, tick)));
            Assert.That(LiveIds, Is.Empty);
        }

        [Test]
        public void InitializationProxyIsInertWithoutOrAfterTheLifecycle()
        {
            using (var bare = new World("MLF Legends proxy without lifecycle"))
            {
                var initialization = bare.CreateSystemManaged<InitializationSystemGroup>();
                initialization.AddSystemToUpdateList(bare.CreateSystem<BattleLifecycleInitializationProxy>());
                initialization.SortSystems();
                Assert.That(() => initialization.Update(), Throws.Nothing);
                Assert.That(bare.EntityManager.CreateEntityQuery(typeof(BattleSession)).CalculateEntityCount(), Is.Zero,
                    "The proxy creates no session of its own.");
            }

            Request(BattleRequest.Start(BattleSetup.Default));
            m_World.DestroySystem(m_Lifecycle);
            Assert.That(Manager.CreateEntityQuery(typeof(BattleSession)).CalculateEntityCount(), Is.Zero);
            Assert.That(() => InitializeFrame(), Throws.Nothing, "A destroyed lifecycle is not updated by the proxy.");
        }

        [TestCase((ushort)0)]
        [TestCase((ushort)199)]
        public void FrameBoundaryStartsAndRestartsWithoutSimulatingOrDrainingNewCommandsTwice(ushort allies)
        {
            Step(3);
            var tick = Tick;
            var setup = Roster(allies, allies);
            BattleRequest.Submit(Manager, BattleRequest.Start(setup, owner: 1));
            InitializeFrame();
            var first = Session;
            Assert.That(first.IsActive, Is.True);
            Assert.That(first.BattleStartTick, Is.EqualTo(tick));
            Assert.That(first.RelativeTick(Tick), Is.Zero);
            Assert.That(LiveIds.Length, Is.EqualTo(setup.CombatantCount));
            Assert.That(Manager.HasComponent<AppliedCombatCommand>(Find(first.HumanCombatant)), Is.False,
                "Initialization did not run the command processor.");

            // A second Start is a rejection, including between frames.
            BattleRequest.Submit(Manager, BattleRequest.Start(setup));
            InitializeFrame();
            Assert.That(Session.LastOutcome, Is.EqualTo(BattleRequestOutcome.AlreadyActive));
            Assert.That(Session.HumanCombatant, Is.EqualTo(first.HumanCombatant));

            // Populate pending and result stores in a real tick, then freeze world time.
            Submit(Cmd(first.HumanCombatant, tick + 5));
            Step();
            tick = Tick;
            var processed = Queue.LastProcessedTick;
            Assert.That(PendingCount, Is.EqualTo(1));
            Assert.That(Outcomes, Is.EqualTo(new[] { CommandOutcome.Deferred }));
            Submit(Cmd(first.HumanCombatant, tick + 1));
            BattleRequest.Submit(Manager, BattleRequest.Restart(setup, owner: 2));
            InitializeFrame();
            var second = Session;
            Assert.That(second.BattleNumber, Is.EqualTo(first.BattleNumber + 1));
            Assert.That(second.Owner, Is.EqualTo(2u));
            Assert.That(second.HumanCombatant.Value, Is.GreaterThan(LiveIds.Length));
            Assert.That(Find(first.HumanCombatant), Is.EqualTo(Entity.Null));
            Assert.That(PendingCount, Is.Zero);
            Assert.That(Outcomes, Is.Empty);
            Assert.That(Manager.GetBuffer<CombatCommandSubmission>(QueueEntity).Length, Is.Zero);
            Assert.That(Queue.BattleStartTick, Is.EqualTo(tick));
            Assert.That(Queue.LastProcessedTick, Is.EqualTo(processed));

            // If the following lifecycle update processed Restart again it would clear this new command.
            Submit(Cmd(second.HumanCombatant, tick + 1));
            InitializeFrame();
            Assert.That(Tick, Is.EqualTo(tick));
            Assert.That(Session.BattleNumber, Is.EqualTo(second.BattleNumber));
            Assert.That(RequestCount, Is.Zero);
            Assert.That(Manager.GetBuffer<CombatCommandSubmission>(QueueEntity).Length, Is.EqualTo(1));
            Step();
            Assert.That(Session.RelativeTick(Tick), Is.EqualTo(1u));
            Assert.That(Outcomes, Is.EqualTo(new[] { CommandOutcome.Accepted }));
        }

        [TestCase((ushort)0)]
        [TestCase((ushort)199)]
        public void FrameBoundaryEndClearsDefeatedActorsAndAllCommandStoresWithoutAdvancingTime(ushort allies)
        {
            Request(BattleRequest.Start(Roster(allies, allies), owner: 7));
            Submit(Cmd(Session.HumanCombatant, Tick + 5));
            Step();
            Assert.That(PendingCount, Is.EqualTo(1));
            var old = Session;
            Manager.SetComponentEnabled<InCombat>(Find(old.AiCombatant), false);
            Submit(Cmd(old.HumanCombatant, Tick + 1));
            var tick = Tick;
            var processed = Queue.LastProcessedTick;
            BattleRequest.Submit(Manager, BattleRequest.End(owner: 7));
            InitializeFrame();

            Assert.That(Tick, Is.EqualTo(tick));
            Assert.That(Queue.LastProcessedTick, Is.EqualTo(processed));
            Assert.That(Session.LastOutcome, Is.EqualTo(BattleRequestOutcome.Ended));
            Assert.That(Session.IsActive, Is.False);
            Assert.That(Session.Owner, Is.Zero);
            Assert.That(LiveIds, Is.Empty);
            Assert.That(PendingCount, Is.Zero);
            Assert.That(Outcomes, Is.Empty);
            Assert.That(Manager.GetBuffer<CombatCommandSubmission>(QueueEntity).Length, Is.Zero);
            Assert.That(RequestCount, Is.Zero);
            InitializeFrame();
            Assert.That(Session.LastOutcome, Is.EqualTo(BattleRequestOutcome.Ended), "No repeated End processing.");
            StepInOneFrame(3);
            Assert.That(LiveIds, Is.Empty);
            Assert.That(PendingCount, Is.Zero);
            Assert.That(Outcomes, Is.Empty, "Old future commands cannot execute after unpause.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FrameBoundaryKeepsAsyncOwnerProtectionWhetherRequestsShareAFrame(bool separateFrames)
        {
            BattleRequest.Submit(Manager, BattleRequest.Start(BattleSetup.Default, owner: 11));
            InitializeFrame();
            var old = Session;
            BattleRequest.Submit(Manager, BattleRequest.Restart(BattleSetup.Default, owner: 12));
            if (separateFrames)
                InitializeFrame();
            BattleRequest.Submit(Manager, BattleRequest.End(owner: 11));
            InitializeFrame();
            Assert.That(Session.LastOutcome, Is.EqualTo(BattleRequestOutcome.NotOwner));
            Assert.That(Session.IsActive, Is.True);
            Assert.That(Session.Owner, Is.EqualTo(12u));
            Assert.That(Session.BattleNumber, Is.EqualTo(old.BattleNumber + 1));
            Assert.That(LiveIds.Length, Is.EqualTo(2));
            Assert.That(Tick, Is.Zero);
            BattleRequest.Submit(Manager, BattleRequest.End(owner: 12));
            InitializeFrame();
            Assert.That(LiveIds, Is.Empty);
        }

        [Test]
        public void FrameBoundaryInvalidRestartIsAtomicAndFollowingEndStillApplies()
        {
            Request(BattleRequest.Start(BattleSetup.Default, owner: 1));
            var before = Session;
            var ids = LiveIds;
            var invalid = BattleSetup.Default;
            invalid.Ai.Team = invalid.Human.Team;
            BattleRequest.Submit(Manager, BattleRequest.Restart(invalid, owner: 2));
            InitializeFrame();
            Assert.That(Session.LastOutcome, Is.EqualTo(BattleRequestOutcome.InvalidSetup));
            Assert.That(Session.Owner, Is.EqualTo(before.Owner));
            Assert.That(LiveIds, Is.EqualTo(ids));
            BattleRequest.Submit(Manager, BattleRequest.Restart(invalid, owner: 2));
            BattleRequest.Submit(Manager, BattleRequest.End(owner: 1));
            InitializeFrame();
            Assert.That(Session.LastOutcome, Is.EqualTo(BattleRequestOutcome.Ended));
            Assert.That(LiveIds, Is.Empty);
            Assert.That(RequestCount, Is.Zero);
        }

        [TestCase((ushort)0)]
        [TestCase((ushort)199)]
        public void ExtraPausedFrameBoundariesPreserveCanonicalReplayAcrossRestart(ushort allies)
        {
            using var frames = new ReplayWorld("Lifecycle with paused frames");
            using var fixedOnly = new ReplayWorld("Lifecycle fixed boundaries");
            var setup = Roster(allies, allies);
            var script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: 1)
                .Player(1, 5, ReplayRole.Human).Ai(2, 2, ReplayRole.Ai, moveX: -1);
            frames.Script = fixedOnly.Script = script;
            for (var battle = 0; battle < 3; battle++)
            {
                frames.Request(BattleRequest.Restart(setup));
                fixedOnly.Request(BattleRequest.Restart(setup));
                for (var pauseFrame = 0; pauseFrame < 4; pauseFrame++)
                    frames.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
                frames.Frame(2);
                fixedOnly.Frame(2);
                Assert.That(frames.RawLog, Is.EqualTo(fixedOnly.RawLog),
                    "Extra lifecycle-only frames do not change any simulated snapshot, ID, command or tick.");
                Assert.That(frames.PendingCount, Is.EqualTo(1), "A future command exercises Restart cleanup.");
            }
            frames.Request(BattleRequest.End());
            fixedOnly.Request(BattleRequest.End());
            frames.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            Assert.That(frames.LiveIds, Is.Empty);
            frames.Frame(3);
            fixedOnly.Frame(3);
            Assert.That(frames.RawLog, Is.EqualTo(fixedOnly.RawLog));
            Assert.That(frames.PendingCount, Is.Zero);
        }

        [TestCase(BattleRequestKind.End, (ushort)0)]
        [TestCase(BattleRequestKind.End, (ushort)199)]
        [TestCase(BattleRequestKind.Restart, (ushort)0)]
        [TestCase(BattleRequestKind.Restart, (ushort)199)]
        public void RequestFromLastCatchUpTickIsAppliedByTheNextFrameWithoutAnotherTick(BattleRequestKind kind, ushort allies)
        {
            var setup = Roster(allies, allies);
            var recorder = AddToPhase<TickRecorder>(typeof(CombatPublicationSystemGroup));
            var requester = AddToPhase<InStepRequester>(typeof(CombatStateUpdateSystemGroup));
            Request(BattleRequest.Start(setup, owner: 5));
            Step(2);
            var previous = Session;
            var ids = LiveIds;
            Submit(Cmd(previous.HumanCombatant, Tick + 6));
            requester.OnTick = Tick + 3; // final catch-up step, with no subsequent fixed boundary in this frame
            requester.Request = kind == BattleRequestKind.End
                ? BattleRequest.End(owner: 5) : BattleRequest.Restart(setup, owner: 6);
            StepInOneFrame(3);
            var tick = Tick;
            var records = recorder.Ticks.Count;
            Assert.That(Session.Owner, Is.EqualTo(5u), "The last requesting tick finishes before cleanup.");
            Assert.That(LiveIds, Is.EqualTo(ids));
            Assert.That(RequestCount, Is.EqualTo(1));
            Assert.That(PendingCount, Is.EqualTo(1));
            Submit(Cmd(previous.HumanCombatant, tick + 1));

            InitializeFrame();

            Assert.That(Tick, Is.EqualTo(tick));
            Assert.That(recorder.Ticks.Count, Is.EqualTo(records), "No phase or fake tick ran in Initialization.");
            Assert.That(RequestCount, Is.Zero);
            Assert.That(PendingCount, Is.Zero);
            Assert.That(Outcomes, Is.Empty);
            Assert.That(Manager.GetBuffer<CombatCommandSubmission>(QueueEntity).Length, Is.Zero);
            Assert.That(Find(previous.HumanCombatant), Is.EqualTo(Entity.Null));
            if (kind == BattleRequestKind.End)
            {
                Assert.That(Session.LastOutcome, Is.EqualTo(BattleRequestOutcome.Ended));
                Assert.That(LiveIds, Is.Empty);
            }
            else
            {
                Assert.That(Session.LastOutcome, Is.EqualTo(BattleRequestOutcome.Started));
                Assert.That(Session.Owner, Is.EqualTo(6u));
                Assert.That(Session.BattleNumber, Is.EqualTo(previous.BattleNumber + 1));
                Assert.That(Session.BattleStartTick, Is.EqualTo(tick));
                Assert.That(LiveIds.Length, Is.EqualTo(setup.CombatantCount));
                Assert.That(Session.HumanCombatant.Value, Is.GreaterThan(ids[ids.Length - 1]));
            }
            recorder.Ticks.Clear();
            StepInOneFrame(3);
            if (kind == BattleRequestKind.Restart)
                Assert.That(Session.RelativeTick(recorder.Ticks[0].Tick), Is.EqualTo(1u));
            else
                Assert.That(LiveIds, Is.Empty);
            Assert.That(PendingCount, Is.Zero);
        }

        [Test]
        public void EnableThenDisableBeforeAnyTickLeavesNoBattleOrQueuedRequest()
        {
            BattleRequest.Submit(Manager, BattleRequest.Restart(BattleSetup.Default, owner: 7));
            BattleRequest.Submit(Manager, BattleRequest.End(owner: 7));
            InitializeFrame();
            Assert.That(Tick, Is.Zero);
            Assert.That(Session.BattleNumber, Is.EqualTo(1u));
            Assert.That(Session.LastOutcome, Is.EqualTo(BattleRequestOutcome.Ended));
            Assert.That(Session.IsActive, Is.False);
            Assert.That(LiveIds, Is.Empty);
            Assert.That(RequestCount, Is.Zero);
            InitializeFrame();
            Step();
            Assert.That(Session.BattleNumber, Is.EqualTo(1u));
            Assert.That(LiveIds, Is.Empty);
        }

        [TestCase((ushort)0)]
        [TestCase((ushort)199)]
        public void EndFixedEcbPlaysBackBeforeFrameBoundaryCleanup(ushort allies)
        {
            // Exercise the documented ECB lifetime contract with the real ECB system, not a direct write.
            var end = m_World.CreateSystemManaged<EndFixedStepSimulationEntityCommandBufferSystem>();
            m_FixedStep.AddSystemToUpdateList(end);
            m_FixedStep.SortSystems();
            Request(BattleRequest.Start(Roster(allies, allies), owner: 8));
            var actor = Find(Session.HumanCombatant);
            var probe = AddToPhase<EndFixedExitProbe>(typeof(CombatStateUpdateSystemGroup));
            probe.Actor = actor;

            Step();

            Assert.That(Manager.GetComponentData<CombatPosition>(actor).Value, Is.EqualTo(new float3(12f, 0f, 0f)),
                "The producing tick's EndFixedStep buffer has played back before paused-frame cleanup.");
            Assert.That(Session.IsActive, Is.True);
            Assert.That(RequestCount, Is.EqualTo(1));
            var tick = Tick;
            InitializeFrame();
            Assert.That(Tick, Is.EqualTo(tick));
            Assert.That(Manager.Exists(actor), Is.False);
            Assert.That(LiveIds, Is.Empty);
            Step();
            Assert.That(LiveIds, Is.Empty, "There is no deferred ECB left targeting the destroyed actor.");
        }

        [DisableAutoCreation]
        partial class EndFixedExitProbe : SystemBase
        {
            public Entity Actor;

            protected override void OnUpdate()
            {
                if (!EntityManager.Exists(Actor))
                    return;
                var ecb = World.GetExistingSystemManaged<EndFixedStepSimulationEntityCommandBufferSystem>().CreateCommandBuffer();
                ecb.SetComponent(Actor, new CombatPosition { Value = new float3(12f, 0f, 0f) });
                BattleRequest.Submit(EntityManager, BattleRequest.End(owner: 8));
            }
        }
    }
}
