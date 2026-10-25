using System.Collections.Generic;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace MLFLegends.Tests.EditMode
{
    // Rosters larger than 1v1 (Issue #7): allies spawned by the same Start/Spawn and the same ID allocator.
    public partial class BattleLifecycleTests
    {
        static BattleSetup Roster(ushort humanAllies, ushort aiAllies)
        {
            var setup = BattleSetup.Default;
            setup.HumanAllies = humanAllies;
            setup.AiAllies = aiAllies;
            return setup;
        }

        int CountOnTeam(byte team) =>
            LiveIds.Count(id => Manager.GetComponentData<CombatTeam>(Find(new CombatantId { Value = id })).Value == team);

        [Test]
        public void DefaultSetupIsTheOneVsOneDuel()
        {
            Assert.That(BattleSetup.Default.HumanAllies, Is.Zero);
            Assert.That(BattleSetup.Default.AiAllies, Is.Zero);
            Assert.That(BattleSetup.Default.CombatantCount, Is.EqualTo(2));
            Assert.That(default(BattleSetup).CombatantCount, Is.EqualTo(2), "A setup written before Issue #7 still spawns two.");
        }

        [Test]
        public void StartWithAlliesSpawnsTheWholeRosterWithConsecutiveIdsAndAFormationBehindEachLeader()
        {
            Step(4);
            var setup = Roster(3, 12);

            Assert.That(Request(BattleRequest.Start(setup)), Is.EqualTo(BattleRequestOutcome.Started));

            var session = Session;
            Assert.That(session.HumanCombatant.Value, Is.EqualTo(1u), "The leaders keep the 1v1 IDs.");
            Assert.That(session.AiCombatant.Value, Is.EqualTo(2u));
            Assert.That(session.BattleStartTick, Is.EqualTo(4u));
            Assert.That(LiveIds, Is.EqualTo(Enumerable.Range(1, 17).Select(i => (uint)i)));
            Assert.That(CountOnTeam(1), Is.EqualTo(4));
            Assert.That(CountOnTeam(2), Is.EqualTo(13));
            AssertCombatant(session.HumanCombatant, setup.Human);
            AssertCombatant(session.AiCombatant, setup.Ai);

            // Human (X = -2, facing right) allies 3..5 one row behind it; AI (X = +2, facing left) allies 6..17 in two rows.
            var right = FacingDirection.Right;
            var left = FacingDirection.Left;
            AssertCombatant(new CombatantId { Value = 3 }, new CombatantSetup { Team = 1, Position = new float3(-3f, 0f, -4.5f), Facing = right });
            AssertCombatant(new CombatantId { Value = 5 }, new CombatantSetup { Team = 1, Position = new float3(-3f, 0f, -2.5f), Facing = right });
            AssertCombatant(new CombatantId { Value = 6 }, new CombatantSetup { Team = 2, Position = new float3(3f, 0f, -4.5f), Facing = left });
            AssertCombatant(new CombatantId { Value = 15 }, new CombatantSetup { Team = 2, Position = new float3(3f, 0f, 4.5f), Facing = left });
            AssertCombatant(new CombatantId { Value = 16 }, new CombatantSetup { Team = 2, Position = new float3(4f, 0f, -4.5f), Facing = left });
            AssertCombatant(new CombatantId { Value = 17 }, new CombatantSetup { Team = 2, Position = new float3(4f, 0f, -3.5f), Facing = left });

            var archetype = Manager.GetChunk(Find(session.HumanCombatant)).Archetype;
            foreach (var id in LiveIds)
                Assert.That(Manager.GetChunk(Find(new CombatantId { Value = id })).Archetype, Is.EqualTo(archetype), $"Combatant {id}");
            Assert.That(Manager.GetName(Find(session.HumanCombatant)), Is.EqualTo("Combatant 1"));
            Assert.That(Manager.GetName(Find(session.AiCombatant)), Is.EqualTo("Combatant 2"));
            Assert.That(Manager.GetName(Find(new CombatantId { Value = 17 })), Is.Empty, "Allies are not named.");
        }

        [Test]
        public void RepeatedLargeRostersKeepNamingTheLeaders()
        {
            // 45 x 400 never-reused IDs would be 18 000 unique names, more than Entities' process-wide name store holds
            // (16 384, emptied only by a domain reload); once it is full every later SetName is silently dropped.
            var setup = Roster(BattleSetup.MaxCombatantsPerTeam - 1, BattleSetup.MaxCombatantsPerTeam - 1);
            for (var battle = 0; battle < 45; battle++)
                Assert.That(Request(BattleRequest.Restart(setup)), Is.EqualTo(BattleRequestOutcome.Started));

            var session = Session;
            Assert.That(session.HumanCombatant.Value, Is.EqualTo(44u * 400 + 1));
            Assert.That(Manager.GetName(Find(session.HumanCombatant)), Is.EqualTo($"Combatant {session.HumanCombatant.Value}"));
            Assert.That(Manager.GetName(Find(session.AiCombatant)), Is.EqualTo($"Combatant {session.AiCombatant.Value}"));
        }

        [Test]
        public void TwoHundredVersusTwoHundredTakesPartInTheSameTick()
        {
            var setup = Roster(BattleSetup.MaxCombatantsPerTeam - 1, BattleSetup.MaxCombatantsPerTeam - 1);
            Assert.That(setup.CombatantCount, Is.EqualTo(400));

            Assert.That(Request(BattleRequest.Start(setup)), Is.EqualTo(BattleRequestOutcome.Started));
            Assert.That(LiveIds, Is.EqualTo(Enumerable.Range(1, 400).Select(i => (uint)i)));
            Assert.That(CountOnTeam(1), Is.EqualTo(200));
            Assert.That(CountOnTeam(2), Is.EqualTo(200));

            var last = new CombatantId { Value = 400 };
            Submit(Cmd(Session.HumanCombatant, Tick + 1), Cmd(last, Tick + 1, source: CommandSource.AI));
            Step();

            Assert.That(Outcomes, Is.EqualTo(new[] { CommandOutcome.Accepted, CommandOutcome.Accepted }));
            Assert.That(Manager.GetComponentData<AppliedCombatCommand>(Find(last)).WasSubmitted, Is.True);
            foreach (var id in LiveIds)
            {
                var applied = Manager.GetComponentData<AppliedCombatCommand>(Find(new CombatantId { Value = id }));
                Assert.That(applied.Command.Tick, Is.EqualTo(Tick), $"Combatant {id} got its command of the tick.");
            }

            Assert.That(CommandOutcomeAfterDespawn(last), Is.EqualTo(CommandOutcome.UnknownCombatant), "Despawn works for an ally too.");
            Assert.That(LiveIds.Length, Is.EqualTo(399));
        }

        CommandOutcome CommandOutcomeAfterDespawn(CombatantId id)
        {
            Assert.That(Request(BattleRequest.Despawn(id)), Is.EqualTo(BattleRequestOutcome.Despawned));
            Submit(Cmd(id, Tick + 1, source: CommandSource.AI));
            Step();
            return Outcomes.Single();
        }

        [TestCase(BattleSetup.MaxCombatantsPerTeam, 0)]
        [TestCase(0, BattleSetup.MaxCombatantsPerTeam)]
        [TestCase(65535, 65535)]
        public void OversizedTeamIsRejectedWithoutSideEffects(int humanAllies, int aiAllies)
        {
            var oversized = Roster((ushort)humanAllies, (ushort)aiAllies);
            Assert.That(oversized.IsValid, Is.False);
            Assert.That(Request(BattleRequest.Start(oversized)), Is.EqualTo(BattleRequestOutcome.InvalidSetup));
            Assert.That(CombatantQuery.CalculateEntityCount(), Is.Zero);

            Request(BattleRequest.Start(Roster(5, 5)));
            var sessionEntity = Manager.CreateEntityQuery(typeof(BattleSession)).GetSingletonEntity();
            Assert.That(Request(BattleRequest.Restart(oversized)), Is.EqualTo(BattleRequestOutcome.InvalidSetup));
            Assert.That(LiveIds, Is.EqualTo(Enumerable.Range(1, 12).Select(i => (uint)i)), "The running battle is kept.");
            Assert.That(Manager.GetComponentData<CombatantIdAllocator>(sessionEntity).LastIssued, Is.EqualTo(12u), "No ID consumed.");
        }

        [Test]
        public void RestartsWithDifferentRosterSizesInOneWorldNeverReuseAnId()
        {
            var seen = new HashSet<uint>();
            var ticks = new List<uint>();
            var expectedFirst = 1u;
            foreach (var (humanAllies, aiAllies) in new (ushort, ushort)[] { (0, 0), (199, 199), (0, 0), (49, 49), (199, 199) })
            {
                var setup = Roster(humanAllies, aiAllies);
                Assert.That(Request(BattleRequest.Restart(setup)), Is.EqualTo(BattleRequestOutcome.Started));
                Step(3);
                ticks.Add(Tick);

                var ids = LiveIds;
                Assert.That(ids.Length, Is.EqualTo(setup.CombatantCount));
                Assert.That(ids, Is.EqualTo(Enumerable.Range((int)expectedFirst, setup.CombatantCount).Select(i => (uint)i)));
                Assert.That(Session.HumanCombatant.Value, Is.EqualTo(expectedFirst));
                Assert.That(Session.AiCombatant.Value, Is.EqualTo(expectedFirst + 1));
                foreach (var id in ids)
                    Assert.That(seen.Add(id), Is.True, $"ID {id} reused.");
                expectedFirst += (uint)setup.CombatantCount;
            }

            Assert.That(Request(BattleRequest.End()), Is.EqualTo(BattleRequestOutcome.Ended));
            Assert.That(CombatantQuery.CalculateEntityCount(), Is.Zero, "No combatant of the 400 survives the end.");
            Assert.That(PendingCount, Is.Zero);
            Assert.That(ticks, Is.Ordered.Ascending);
            Assert.That(Manager.CreateEntityQuery(typeof(SimulationClock)).CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(Manager.CreateEntityQuery(typeof(CombatCommandQueue)).CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(Manager.CreateEntityQuery(typeof(BattleSession)).CalculateEntityCount(), Is.EqualTo(1));
        }

        [Test]
        public void IdReservationCoversTheWholeRosterOrNothing()
        {
            var session = Manager.CreateEntityQuery(typeof(BattleSession)).GetSingletonEntity();
            Manager.SetComponentData(session, CombatantIdAllocator.AfterIssued(uint.MaxValue - 10));

            Assert.That(Request(BattleRequest.Start(Roster(5, 5))), Is.EqualTo(BattleRequestOutcome.IdsExhausted), "12 IDs needed, 10 left.");
            Assert.That(CombatantQuery.CalculateEntityCount(), Is.Zero);
            Assert.That(Manager.GetComponentData<CombatantIdAllocator>(session).LastIssued, Is.EqualTo(uint.MaxValue - 10));

            Assert.That(Request(BattleRequest.Start(Roster(4, 4))), Is.EqualTo(BattleRequestOutcome.Started));
            Assert.That(LiveIds, Is.EqualTo(Enumerable.Range(0, 10).Select(i => uint.MaxValue - 9 + (uint)i)));
            Assert.That(LiveIds, Has.No.Member(CombatantId.Invalid.Value), "The allocator never wraps to 0.");
        }

        [Test]
        public void LargestRosterFitsTheBoundOfThePendingCommandStore()
        {
            Assert.That(CombatCommandQueue.MaxPendingCommands,
                Is.EqualTo(2 * BattleSetup.MaxCombatantsPerTeam * (int)CombatCommandQueue.MaxLeadTicks));
        }
    }
}
