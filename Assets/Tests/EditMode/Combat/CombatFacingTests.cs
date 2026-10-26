using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace MLFLegends.Tests.EditMode
{
    // Stage 02.2 (#33) integration tests: real lifecycle, queue, processor, facing, movement and publication recorder.
    // Facing = sign of the accepted MoveX on the same fixed tick; MoveX 0 keeps the previous value.
    public class CombatFacingTests
    {
        const FacingDirection R = FacingDirection.Right;
        const FacingDirection L = FacingDirection.Left;

        static Entity Actor(ReplayWorld w, CombatantId id)
        {
            using var query = w.Manager.CreateEntityQuery(typeof(CombatantId));
            using var entities = query.ToEntityArray(Allocator.Temp);
            return entities.ToArray().Single(e => w.Manager.GetComponentData<CombatantId>(e).Equals(id));
        }

        static void Initialize(ReplayWorld w, BattleSetup setup)
        {
            w.Request(BattleRequest.Start(setup));
            w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
        }

        static FacingDirection[] Facings(ReplayWorld w, uint battle, ReplayRole role) => w.BattleRecords(battle)
            .Select(r => r.Combatant(role == ReplayRole.Human ? r.HumanId : r.AiId).Facing).ToArray();

        static BattleSetup Setup(FacingDirection human, FacingDirection ai)
        {
            var setup = BattleSetup.Default;
            setup.Human.Facing = human;
            setup.Ai.Facing = ai;
            return setup;
        }

        // One step of the turn script per relative tick; producer-agnostic so player and AI get the same input.
        delegate void Producer(uint on, uint @for, sbyte moveX, sbyte moveZ, CommandButtons held, CommandButtons pressed);

        const int TurnTicks = 14;

        static void TurnScript(Producer add)
        {
            // r1: no command (implicit None).
            add(2, 2, 0, 1, CommandButtons.None, CommandButtons.None);                       // Z only
            add(3, 3, 1, 0, CommandButtons.None, CommandButtons.None);                       // turn Right
            add(4, 4, 1, 0, CommandButtons.None, CommandButtons.None);                       // same direction again
            add(5, 5, 0, 0, CommandButtons.None, CommandButtons.None);                       // explicit None
            add(6, 6, -1, 0, CommandButtons.None, CommandButtons.None);                      // turn Left
            add(7, 7, -1, 1, CommandButtons.None, CommandButtons.None);                      // diagonal, X negative
            add(8, 8, 1, -1, CommandButtons.None, CommandButtons.None);                      // diagonal, X positive
            add(9, 9, 0, 0, CommandButtons.None, CommandButtons.Jump);                       // jump in place
            add(10, 10, -1, 0, CommandButtons.None, CommandButtons.None);                    // airborne turn
            add(11, 11, 0, -1, CommandButtons.None, CommandButtons.None);                    // airborne Z only
            add(12, 12, 1, 0, CommandButtons.Guard | CommandButtons.Attack, CommandButtons.Attack); // no action lock yet
            add(13, 13, -1, 0, CommandButtons.Guard, CommandButtons.None);
            // r14+: nothing.
        }

        static FacingDirection[] ExpectedTurns(FacingDirection initial) =>
            new[] { initial, initial, R, R, R, L, L, R, R, L, L, R, L, L };

        static ReplayScript MirroredTurnScript()
        {
            var script = new ReplayScript();
            TurnScript((on, @for, x, z, held, pressed) => script.Player(on, @for, ReplayRole.Human, x, z, held, pressed));
            TurnScript((on, @for, x, z, held, pressed) => script.Ai(on, @for, ReplayRole.Ai, x, z, held, pressed));
            return script;
        }

        [Test]
        public void SpawnFacingFromSetupIsKeptWithoutCommands()
        {
            foreach (var (human, ai) in new[] { (R, L), (L, R), (L, L), (R, R) })
            {
                using var w = new ReplayWorld("facing spawn");
                var setup = Setup(human, ai);
                setup.HumanAllies = 2;
                setup.AiAllies = 3;
                w.Request(BattleRequest.Start(setup));
                w.Step(20);
                foreach (var record in w.BattleRecords(1))
                foreach (var c in record.Combatants)
                    Assert.That(c.Facing, Is.EqualTo(c.Team == setup.Human.Team ? human : ai),
                        $"Actor {c.Id} on relative tick {record.RelativeTick} keeps its spawn facing ({human}/{ai}).");
            }
        }

        [TestCase(FacingDirection.Right)] [TestCase(FacingDirection.Left)]
        public void PlayerAndAiTurnBySignOfXOnTheSameTickThroughOnePath(FacingDirection initial)
        {
            using var w = new ReplayWorld("facing turns");
            w.Script = MirroredTurnScript();
            w.Request(BattleRequest.Start(Setup(initial, initial)));
            w.Step(TurnTicks);

            var expected = ExpectedTurns(initial);
            Assert.That(Facings(w, 1, ReplayRole.Human), Is.EqualTo(expected), "Human facing per relative tick.");
            Assert.That(Facings(w, 1, ReplayRole.Ai), Is.EqualTo(expected), "AI facing per relative tick: same rule, same path.");
            foreach (var record in w.BattleRecords(1))
            {
                var human = record.Combatant(record.HumanId);
                var ai = record.Combatant(record.AiId);
                Assert.That(ai.Applied.Command.Source, Is.EqualTo(ai.Applied.WasSubmitted ? CommandSource.AI : CommandSource.None));
                Assert.That(math.distance(ai.Position - BattleSetup.Default.Ai.Position, human.Position - BattleSetup.Default.Human.Position),
                    Is.LessThan(1e-4f), "Turning does not change movement: both actors move identically.");
                Assert.That(ai.Motion.VerticalVelocity, Is.EqualTo(human.Motion.VerticalVelocity));
                var x = human.Applied.Command.MoveX;
                if (x != 0)
                    Assert.That(human.Facing, Is.EqualTo(x > 0 ? R : L), $"Turn applies on the command's own tick r{record.RelativeTick}.");
            }

            var records = w.BattleRecords(1);
            Assert.That(records[9].Combatant(records[9].HumanId).Motion.Grounded, Is.False, "r10 turns while airborne.");
            Assert.That(records[10].Combatant(records[10].HumanId).Motion.Grounded, Is.False);
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(10)]
        public void CatchUpFramesGiveTheSameTickByTickFacingAsSingleTicks(int k)
        {
            using var single = new ReplayWorld("facing single steps");
            using var grouped = new ReplayWorld("facing catch-up");
            single.Script = grouped.Script = MirroredTurnScript();
            single.Request(BattleRequest.Start(BattleSetup.Default));
            grouped.Request(BattleRequest.Start(BattleSetup.Default));
            single.Step(30);
            for (var i = 0; i < 30 / k; i++)
                grouped.Frame(k);
            Assert.That(grouped.Records.Count, Is.EqualTo(30));
            Assert.That(grouped.RawLog, Is.EqualTo(single.RawLog), "Raw records include facing, position and kinetics each tick.");
            Assert.That(Facings(grouped, 1, ReplayRole.Human).Take(TurnTicks), Is.EqualTo(ExpectedTurns(R)));
        }

        [TestCase(2)] [TestCase(100)] [TestCase(400)]
        public void EveryRosterActorTurnsByTheSameRuleRegardlessOfSourceOrTeam(int count)
        {
            using var w = new ReplayWorld("facing roster");
            using var single = new ReplayWorld("facing roster single-tick reference");
            var setup = BattleSetup.Default;
            setup.HumanAllies = setup.AiAllies = (ushort)(count / 2 - 1);
            Initialize(w, setup);
            Initialize(single, setup);
            using var query = w.Manager.CreateEntityQuery(typeof(CombatantId), typeof(CombatFacing));
            using var entities = query.ToEntityArray(Allocator.Temp);
            Assert.That(entities.Length, Is.EqualTo(count));
            foreach (var entity in entities)
            {
                var id = w.Manager.GetComponentData<CombatantId>(entity);
                var team = w.Manager.GetComponentData<CombatTeam>(entity).Value;
                Assert.That(w.Manager.GetComponentData<CombatFacing>(entity).Value, Is.EqualTo(team == setup.Human.Team ? R : L));
                for (uint tick = 1; tick <= 9; tick++)
                {
                    // r1-3 Left, r4-6 Z only (keep), r7-9 Right; sources alternate inside each team.
                    var command = new CombatCommand
                    {
                        Combatant = id, Tick = tick,
                        MoveX = (sbyte)(tick <= 3 ? -1 : tick <= 6 ? 0 : 1), MoveZ = (sbyte)(tick is > 3 and <= 6 ? 1 : 0),
                        Source = id.Value % 2 == 0 ? CommandSource.Player : CommandSource.AI,
                    };
                    w.SubmitBetweenFrames(command);
                    single.SubmitBetweenFrames(command);
                }
            }

            w.Frame(9);
            single.Step(9);
            Assert.That(w.RawLog, Is.EqualTo(single.RawLog), "Every actor's facing matches on every tick, not just the final state.");
            for (var i = 0; i < 9; i++)
                Assert.That(w.Records[i].Combatants.Select(c => c.Facing), Is.All.EqualTo(i < 6 ? L : R),
                    $"All {count} actors on tick {i + 1}.");
            // Submitted before tick 1: ticks 2..9 are deferred on tick 1, then each is accepted on its own tick.
            var outcomes = w.Records.SelectMany(r => r.Results).Select(r => r.Outcome).ToArray();
            Assert.That(outcomes.Count(o => o == CommandOutcome.Accepted), Is.EqualTo(count * 9));
            Assert.That(outcomes.Count(o => o == CommandOutcome.Deferred), Is.EqualTo(count * 8));
            Assert.That(outcomes.Length, Is.EqualTo(count * 17), "No rejected command.");
        }

        [Test]
        public void RejectedStaleAndDeferredCommandsTurnNobodyBeforeTheirAcceptedTick()
        {
            using var clean = new ReplayWorld("facing accepted only");
            using var noisy = new ReplayWorld("facing rejected commands");
            clean.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: -1)
                .Player(8, 8, ReplayRole.Human, moveX: 1);
            noisy.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: -1)
                .Player(1, 1, ReplayRole.Human, moveX: -1) // duplicate
                .Player(1, 1, ReplayRole.Human, moveX: 1) // conflict
                .Ai(1, 1, ReplayRole.Human, moveX: 1) // conflicting source
                .Player(2, 1, ReplayRole.Human, moveX: 1) // stale
                .Player(2, 2, ReplayRole.Human, moveX: 2) // invalid value
                .Player(2, 8, ReplayRole.Human, moveX: 1) // deferred, due only on tick 8
                .Player(2, 50, ReplayRole.Human, moveX: 1) // too far ahead
                .Player(2, 2, ReplayRole.PreviousHuman, moveX: 1) // unknown
                .Player(2, 2, ReplayRole.Invalid, moveX: 1)
                .Player(2, 0, ReplayRole.Human, moveX: 1)
                .Ai(3, 3, ReplayRole.Ai, moveX: 2); // invalid for the AI
            clean.Request(BattleRequest.Start(BattleSetup.Default));
            noisy.Request(BattleRequest.Start(BattleSetup.Default));
            clean.Step(12);
            noisy.Step(12);

            var expected = new[] { L, L, L, L, L, L, L, R, R, R, R, R };
            Assert.That(Facings(clean, 1, ReplayRole.Human), Is.EqualTo(expected));
            Assert.That(Facings(noisy, 1, ReplayRole.Human), Is.EqualTo(expected), "Only accepted commands turn, the deferred one on tick 8.");
            Assert.That(Facings(noisy, 1, ReplayRole.Ai), Is.All.EqualTo(L), "The AI's rejected x2 and implicit None keep its spawn facing.");
            Assert.That(noisy.Records.SelectMany(r => r.Results).Select(r => r.Outcome), Is.SupersetOf(new[]
            {
                CommandOutcome.Duplicate, CommandOutcome.Conflict, CommandOutcome.Stale, CommandOutcome.InvalidValue,
                CommandOutcome.Deferred, CommandOutcome.TooFarAhead, CommandOutcome.UnknownCombatant,
                CommandOutcome.InvalidCombatant, CommandOutcome.InvalidTick,
            }));
        }

        [Test]
        public void DisabledInCombatKeepsItsFacing()
        {
            using var w = new ReplayWorld("facing disabled");
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: -1).Ai(1, 1, ReplayRole.Ai, moveX: 1);
            w.Request(BattleRequest.Start(BattleSetup.Default));
            w.Step();
            var human = Actor(w, w.Session.HumanCombatant);
            Assert.That(w.Manager.GetComponentData<CombatFacing>(human).Value, Is.EqualTo(L));
            w.Manager.SetComponentEnabled<InCombat>(human, false);
            w.Script = new ReplayScript();
            for (var i = 0; i < 5; i++)
                w.SubmitBetweenFrames(new CombatCommand { Combatant = w.Session.HumanCombatant, Tick = w.Tick + 1 + (uint)i,
                    MoveX = 1, Source = CommandSource.Player });
            w.Frame(5);
            Assert.That(w.Manager.GetComponentData<CombatFacing>(human).Value, Is.EqualTo(L));
            Assert.That(w.Records.Skip(1).SelectMany(r => r.Results).Select(r => r.Outcome), Is.All.EqualTo(CommandOutcome.UnknownCombatant));
            Assert.That(w.Records.Last().Combatant(w.Session.AiCombatant.Value).Facing, Is.EqualTo(R), "The enabled AI is unaffected.");
        }

        [TestCase(2)] [TestCase(400)]
        public void PausedFramesDoNotTurnAndRestartDespawnEndSpawnFreshFacing(int count)
        {
            using var w = new ReplayWorld("facing lifecycle");
            var setup = BattleSetup.Default;
            setup.HumanAllies = setup.AiAllies = (ushort)(count / 2 - 1);
            Initialize(w, setup);
            // Turn both leaders away from their spawn facing, then keep a turn back pending/outstanding at the restart.
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: -1).Ai(1, 1, ReplayRole.Ai, moveX: 1);
            w.Step();
            var oldHuman = Actor(w, w.Session.HumanCombatant);
            Assert.That(w.Manager.GetComponentData<CombatFacing>(oldHuman).Value, Is.EqualTo(L));
            w.SubmitBetweenFrames(new CombatCommand { Combatant = w.Session.HumanCombatant, Tick = w.Tick + 1,
                MoveX = 1, Source = CommandSource.Player });
            for (var i = 0; i < 5; i++)
            {
                // A real fixed group with unchanged elapsed time runs zero steps: no tick, no turn.
                w.World.SetTime(new TimeData(w.World.Time.ElapsedTime, 0f));
                w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
                w.FixedStep.Update();
                Assert.That(w.Manager.GetComponentData<CombatFacing>(oldHuman).Value, Is.EqualTo(L), "Paused frames do not turn.");
            }

            w.SubmitBetweenFrames(new CombatCommand { Combatant = w.Session.HumanCombatant, Tick = w.Tick + 3,
                MoveX = 1, Source = CommandSource.Player });
            w.Request(BattleRequest.Restart(setup));
            w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            Assert.That(w.Manager.Exists(oldHuman), Is.False);
            Assert.That(w.CountWith<CombatFacing>(), Is.EqualTo(count));
            w.Script = new ReplayScript();
            w.Step(5);
            foreach (var record in w.BattleRecords(2))
            foreach (var c in record.Combatants)
                Assert.That(c.Facing, Is.EqualTo(c.Team == setup.Human.Team ? R : L),
                    "Fresh actors use their setup facing; no turn or command of the ended battle leaks.");

            w.Request(BattleRequest.Despawn(w.Session.AiCombatant));
            w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            Assert.That(w.CountWith<CombatFacing>(), Is.EqualTo(count - 1));
            w.Request(BattleRequest.End());
            w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            Assert.That(w.CountWith<CombatFacing>(), Is.Zero);
        }

        [TestCase(typeof(CombatCommandCollectionSystemGroup))]
        [TestCase(typeof(CombatStateUpdateSystemGroup))]
        [TestCase(typeof(CombatPublicationSystemGroup))]
        public void RestartInsideCatchUpSpawnsFreshFacingBeforeTheNextTick(System.Type phase)
        {
            using var w = new ReplayWorld("facing catch-up restart");
            var first = Setup(R, L);
            var second = Setup(L, R);
            w.Script = MirroredTurnScript();
            w.Request(BattleRequest.Start(first));
            w.Step(6); // r6: both leaders turned Left
            w.RequestInStep(phase, 1, 7, BattleRequest.Restart(second));
            w.Frame(4); // old r7 completes, then the new battle's r1..r3 in the same frame

            Assert.That(Facings(w, 1, ReplayRole.Human), Is.EqualTo(ExpectedTurns(R).Take(7)));
            Assert.That(Facings(w, 2, ReplayRole.Human), Is.EqualTo(new[] { L, L, R }), "Battle 2 spawns from its setup, then turns by its own script.");
            Assert.That(Facings(w, 2, ReplayRole.Ai), Is.EqualTo(new[] { R, R, R }));

            using var reference = new ReplayWorld("facing restart reference");
            reference.Script = MirroredTurnScript();
            reference.Request(BattleRequest.Start(second));
            reference.Step(3);
            Assert.That(w.NormalizedLog(2), Is.EqualTo(reference.NormalizedLog(1)));
        }

        [Test]
        public void ClockGuardAndExhaustionStopFacing()
        {
            using (var w = new World("facing clock guard"))
            {
                var system = w.CreateSystem<CombatFacingSystem>();
                var entity = w.EntityManager.CreateEntity(typeof(CombatFacing), typeof(InCombat), typeof(AppliedCombatCommand));
                w.EntityManager.SetComponentData(entity, new CombatFacing { Value = L });
                w.EntityManager.SetComponentData(entity, new AppliedCombatCommand(new CombatCommand { MoveX = 1 }));
                system.Update(w.Unmanaged);
                Assert.That(w.EntityManager.GetComponentData<CombatFacing>(entity).Value, Is.EqualTo(L), "No running clock, no turn.");
            }

            using var replay = new ReplayWorld("facing clock exhausted");
            replay.Script = new ReplayScript().Player(2, 2, ReplayRole.Human, moveX: -1);
            replay.Request(BattleRequest.Start(BattleSetup.Default));
            replay.Step();
            using (var clock = replay.Manager.CreateEntityQuery(typeof(SimulationClock)))
                replay.Manager.SetComponentData(clock.GetSingletonEntity(), SimulationClock.AtTick(SimulationClock.LastTick));
            LogAssert.Expect(LogType.Error, "SimulationClock reached its last tick; the clock and combat phases stopped.");
            replay.Step();
            Assert.That(replay.Manager.GetComponentData<CombatFacing>(Actor(replay, replay.Session.HumanCombatant)).Value, Is.EqualTo(R));
        }
    }
}
