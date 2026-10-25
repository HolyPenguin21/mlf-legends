using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Entities;

namespace MLFLegends.Tests.EditMode
{
    public partial class BattleLifecycleTests
    {
        // Initialization is a real frame boundary, not a fixed step or a second lifecycle instance.
        void InitializeFrame() => m_World.GetExistingSystemManaged<InitializationSystemGroup>().Update();

        int RequestCount => Manager.GetBuffer<BattleRequest>(
            Manager.CreateEntityQuery(typeof(BattleSession)).GetSingletonEntity()).Length;

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

    }
}
