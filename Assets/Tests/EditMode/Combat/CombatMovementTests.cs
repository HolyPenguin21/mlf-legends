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
    // Integration tests: real lifecycle, queue, processor, facing, movement and publication recorder (plus isolated
    // single-system checks of writer separation and the clock guard).
    public class CombatMovementTests
    {
        static Entity Actor(ReplayWorld w, CombatantId id)
        {
            using var query = w.Manager.CreateEntityQuery(typeof(CombatantId));
            using var entities = query.ToEntityArray(Allocator.Temp);
            return entities.ToArray().Single(e => w.Manager.GetComponentData<CombatantId>(e).Equals(id));
        }

        static CombatantRecord Human(ReplayWorld w) => w.Records.Last().Combatant(w.Session.HumanCombatant.Value);

        static void Near(float3 actual, float3 expected, float tolerance = 1e-4f) =>
            Assert.That(math.distance(actual, expected), Is.LessThan(tolerance));

        static void SameMotion(CombatantRecord a, CombatantRecord b)
        {
            Assert.That(a.Position, Is.EqualTo(b.Position));
            Assert.That(a.Motion, Is.EqualTo(b.Motion));
        }

        static void Initialize(ReplayWorld w, BattleSetup setup)
        {
            w.Request(BattleRequest.Start(setup));
            w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
        }

        [TestCase(-1, -1)] [TestCase(-1, 0)] [TestCase(-1, 1)]
        [TestCase(0, -1)] [TestCase(0, 0)] [TestCase(0, 1)]
        [TestCase(1, -1)] [TestCase(1, 0)] [TestCase(1, 1)]
        public void EightDirectionsAndIdleHaveEqualSpeedAndNoInertia(int x, int z)
        {
            using var w = new ReplayWorld("movement direction");
            var script = new ReplayScript();
            for (uint tick = 1; tick <= 60; tick++)
                script.Player(tick, tick, ReplayRole.Human, (sbyte)x, (sbyte)z);
            w.Script = script;
            w.Request(BattleRequest.Start(BattleSetup.Default));
            w.Step(60);
            var p = Human(w).Position;
            var direction = math.normalizesafe(new float3(x, 0, z));
            Near(p, BattleSetup.Default.Human.Position + direction * CombatMovementTuning.MoveSpeed);
            Assert.That(math.distance(p, BattleSetup.Default.Human.Position),
                Is.EqualTo(x == 0 && z == 0 ? 0f : CombatMovementTuning.MoveSpeed).Within(1e-4f));
            w.Step(3); // implicit None must stop horizontal movement immediately
            Assert.That(Human(w).Position, Is.EqualTo(p));
            Assert.That(Human(w).Facing, Is.EqualTo(x < 0 ? FacingDirection.Left : FacingDirection.Right),
                "Facing follows the sign of X; X = 0 (idle, Z-only) keeps the spawned Right, also after the None ticks.");
        }

        // Writer separation, each system alone in a World with a running-clock tag: movement never turns, facing never moves.
        static Entity IsolatedActor(World world, FacingDirection facing, sbyte moveX)
        {
            world.EntityManager.CreateEntity(typeof(SimulationClockRunning));
            var entity = world.EntityManager.CreateEntity(typeof(CombatPosition), typeof(CombatMotion), typeof(CombatFacing),
                typeof(InCombat), typeof(AppliedCombatCommand));
            world.EntityManager.SetComponentData(entity, new CombatMotion { Grounded = true });
            world.EntityManager.SetComponentData(entity, new CombatFacing { Value = facing });
            world.EntityManager.SetComponentData(entity, new AppliedCombatCommand(new CombatCommand
            {
                Combatant = new CombatantId { Value = 1 }, Tick = 1, MoveX = moveX, Pressed = CommandButtons.Jump, Source = CommandSource.Player,
            }));
            return entity;
        }

        [Test]
        public void MovementAloneNeverWritesFacingAndFacingAloneNeverWritesMotion()
        {
            using (var w = new World("movement only"))
            {
                var entity = IsolatedActor(w, FacingDirection.Left, 1);
                w.CreateSystem<CombatMovementSystem>().Update(w.Unmanaged);
                Assert.That(w.EntityManager.GetComponentData<CombatPosition>(entity).Value.x, Is.GreaterThan(0f));
                Assert.That(w.EntityManager.GetComponentData<CombatFacing>(entity).Value, Is.EqualTo(FacingDirection.Left),
                    "Movement must not write facing.");
            }

            using (var w = new World("facing only"))
            {
                var entity = IsolatedActor(w, FacingDirection.Left, 1);
                w.CreateSystem<CombatFacingSystem>().Update(w.Unmanaged);
                Assert.That(w.EntityManager.GetComponentData<CombatFacing>(entity).Value, Is.EqualTo(FacingDirection.Right));
                Assert.That(w.EntityManager.GetComponentData<CombatPosition>(entity).Value, Is.EqualTo(float3.zero),
                    "Facing must not write position.");
                var motion = w.EntityManager.GetComponentData<CombatMotion>(entity);
                Assert.That((motion.VerticalVelocity, motion.Grounded), Is.EqualTo((0f, true)), "Facing must not start the jump.");
            }
        }

        [TestCase(2)] [TestCase(100)] [TestCase(400)]
        public void EveryRosterActorUsesTheSamePlayerAndAiMotionPath(int count)
        {
            using var w = new ReplayWorld("movement roster");
            using var single = new ReplayWorld("movement roster single-tick reference");
            var setup = BattleSetup.Default;
            setup.HumanAllies = setup.AiAllies = (ushort)(count / 2 - 1);
            setup.Human.Position.y = -3f;
            setup.Ai.Position.y = 7f;
            Initialize(w, setup);
            Initialize(single, setup);
            using var query = w.Manager.CreateEntityQuery(typeof(CombatantId), typeof(CombatMotion));
            using var entities = query.ToEntityArray(Allocator.Temp);
            Assert.That(entities.Length, Is.EqualTo(count));
            var starts = entities.ToArray().ToDictionary(e => w.Manager.GetComponentData<CombatantId>(e).Value,
                e => w.Manager.GetComponentData<CombatPosition>(e).Value);
            foreach (var entity in entities)
            {
                var id = w.Manager.GetComponentData<CombatantId>(entity);
                var motion = w.Manager.GetComponentData<CombatMotion>(entity);
                Assert.That((motion.GroundHeight, motion.VerticalVelocity, motion.Grounded), Is.EqualTo((starts[id.Value].y, 0f, true)));
                for (uint tick = 1; tick <= 10; tick++)
                {
                    var command = new CombatCommand
                    {
                        Combatant = id, Tick = tick, MoveX = 1,
                        Pressed = tick == 1 ? CommandButtons.Jump : CommandButtons.None,
                        Held = CommandButtons.Jump,
                        // Each team includes both sources; identity/team must not select a different path.
                        Source = id.Value % 2 == 0 ? CommandSource.Player : CommandSource.AI,
                    };
                    w.SubmitBetweenFrames(command);
                    single.SubmitBetweenFrames(command); // fresh Worlds allocate the same IDs
                }
            }
            w.Frame(10);
            single.Step(10);
            Assert.That(w.RawLog, Is.EqualTo(single.RawLog),
                "Every actor's position, kinetics and processor results match on every tick, not just the final state.");
            var reference = Human(w);
            foreach (var c in w.Records.Last().Combatants)
            {
                var delta = c.Position - starts[c.Id];
                Near(delta, reference.Position - starts[reference.Id]);
                Assert.That(delta.x, Is.EqualTo(10 * CombatMovementTuning.MoveSpeed * SimulationTiming.TickDuration).Within(1e-4f));
                Assert.That(delta.y, Is.GreaterThan(0));
                Assert.That(c.Motion.Grounded, Is.False);
                Assert.That(c.Motion.VerticalVelocity, Is.EqualTo(reference.Motion.VerticalVelocity));
                Assert.That(c.Motion.GroundHeight, Is.EqualTo(starts[c.Id].y));
            }
            Assert.That(w.Records.Last().Results.Length, Is.EqualTo(count));
            Assert.That(w.Records.Last().Results.All(r => r.Outcome == CommandOutcome.Accepted), Is.True);
        }

        [TestCase(2)] [TestCase(3)] [TestCase(10)]
        public void CatchUpIntegratesExactlyKTimesAndReplayMatchesSingleTickFrames(int k)
        {
            using var single = new ReplayWorld("movement single steps");
            using var grouped = new ReplayWorld("movement catch-up");
            var script = new ReplayScript();
            for (uint r = 1; r <= 60; r++)
            {
                script.Player(r, r, ReplayRole.Human, moveX: 1, moveZ: -1,
                    pressed: r == 1 ? CommandButtons.Jump : CommandButtons.None, held: CommandButtons.Jump);
                script.Ai(r, r, ReplayRole.Ai, moveX: -1,
                    pressed: r == 5 ? CommandButtons.Jump : CommandButtons.None);
            }
            single.Script = grouped.Script = script;
            single.Request(BattleRequest.Start(BattleSetup.Default));
            grouped.Request(BattleRequest.Start(BattleSetup.Default));
            single.Step(60);
            for (var i = 0; i < 60 / k; i++)
                grouped.Frame(k);
            Assert.That(grouped.Tick, Is.EqualTo(60));
            Assert.That(grouped.Records.Count, Is.EqualTo(60));
            Assert.That(grouped.RawLog, Is.EqualTo(single.RawLog), "Includes position, vertical velocity, support and grounded each tick.");
            Near(Human(grouped).Position, BattleSetup.Default.Human.Position + math.normalize(new float3(1, 0, -1)) * CombatMovementTuning.MoveSpeed);
        }

        [TestCase(false, 0f)] [TestCase(false, 3.5f)] [TestCase(true, 3.5f)] [TestCase(false, -5f)]
        public void PressedJumpsOnceAndLandsWithoutCommandsOrWhileHeld(bool held, float groundHeight)
        {
            using var w = new ReplayWorld("jump edge");
            var setup = BattleSetup.Default;
            setup.Human.Position.y = groundHeight;
            var script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: CommandButtons.Jump);
            if (held)
                for (uint r = 2; r <= 90; r++)
                    script.Player(r, r, ReplayRole.Human, held: CommandButtons.Jump);
            w.Script = script;
            w.Request(BattleRequest.Start(setup));
            w.Step(90);
            var records = w.BattleRecords(1).Select(r => r.Combatant(r.HumanId)).ToArray();
            Assert.That(records[0].Motion.Grounded, Is.False, "A released tap in Pressed still jumps.");
            Assert.That(records[0].Motion.VerticalVelocity,
                Is.EqualTo(CombatMovementTuning.JumpImpulse - CombatMovementTuning.Gravity * SimulationTiming.TickDuration).Within(1e-6f));
            Assert.That(records.Max(c => c.Position.y), Is.GreaterThan(setup.Human.Position.y));
            Assert.That(records.Any(c => c.Motion.VerticalVelocity < 0f), Is.True, "Gravity produces descent.");
            var landing = System.Array.FindIndex(records, 1, c => c.Motion.Grounded);
            Assert.That(landing, Is.GreaterThan(1).And.LessThan(89));
            foreach (var c in records.Skip(landing))
            {
                Assert.That(c.Position, Is.EqualTo(setup.Human.Position));
                Assert.That((c.Motion.VerticalVelocity, c.Motion.Grounded), Is.EqualTo((0f, true)));
            }
            w.SubmitBetweenFrames(new CombatCommand { Combatant = w.Session.HumanCombatant, Tick = w.Tick + 1,
                Pressed = CommandButtons.Jump, Source = CommandSource.Player });
            w.Step();
            Assert.That(Human(w).Motion.Grounded, Is.False, "A fresh Pressed after landing can jump again.");
        }

        [Test]
        public void HeldAloneNeverStartsAJumpAndAirbornePressedDoesNotDoubleJump()
        {
            using var clean = new ReplayWorld("one jump");
            using var extra = new ReplayWorld("airborne extra presses");
            clean.Script = new ReplayScript().Player(2, 2, ReplayRole.Human, pressed: CommandButtons.Jump);
            extra.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, held: CommandButtons.Jump)
                .Player(2, 2, ReplayRole.Human, pressed: CommandButtons.Jump)
                .Player(4, 4, ReplayRole.Human, pressed: CommandButtons.Jump)
                .Player(6, 6, ReplayRole.Human) // explicit None also continues gravity
                .Player(8, 8, ReplayRole.Human, held: CommandButtons.Guard | CommandButtons.Attack, pressed: CommandButtons.Attack)
                .Player(10, 10, ReplayRole.Human, pressed: CommandButtons.Jump);
            clean.Request(BattleRequest.Start(BattleSetup.Default));
            extra.Request(BattleRequest.Start(BattleSetup.Default));
            clean.Step(70);
            extra.Step(70);
            for (var i = 0; i < clean.Records.Count; i++)
                SameMotion(clean.Records[i].Combatant(1), extra.Records[i].Combatant(1));
        }

        [Test]
        public void RejectedCommandsCannotChangeMotionAndFutureCommandWaitsForItsTick()
        {
            using var clean = new ReplayWorld("motion accepted only");
            using var noisy = new ReplayWorld("motion rejected commands");
            clean.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: 1, pressed: CommandButtons.Jump)
                .Player(8, 8, ReplayRole.Human, moveZ: 1);
            noisy.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: 1, pressed: CommandButtons.Jump)
                .Player(1, 1, ReplayRole.Human, moveX: 1, pressed: CommandButtons.Jump) // duplicate
                .Player(1, 1, ReplayRole.Human, moveX: -1) // conflict
                .Player(2, 1, ReplayRole.Human, moveX: 1) // stale
                .Player(2, 2, ReplayRole.Human, moveX: 2, pressed: CommandButtons.Jump) // invalid
                .Player(2, 8, ReplayRole.Human, moveZ: 1) // deferred, due only on tick 8
                .Player(2, 50, ReplayRole.Human, moveX: 1) // too far ahead
                .Player(2, 2, ReplayRole.PreviousHuman, moveX: 1, pressed: CommandButtons.Jump) // unknown
                .Player(2, 2, ReplayRole.Invalid, moveX: 1)
                .Player(2, 0, ReplayRole.Human, moveX: 1);
            clean.Request(BattleRequest.Start(BattleSetup.Default));
            noisy.Request(BattleRequest.Start(BattleSetup.Default));
            clean.Step(60);
            noisy.Step(60);
            for (var i = 0; i < clean.Records.Count; i++)
                SameMotion(clean.Records[i].Combatant(1), noisy.Records[i].Combatant(1));
            Assert.That(noisy.Records.SelectMany(r => r.Results).Select(r => r.Outcome), Is.SupersetOf(new[]
            {
                CommandOutcome.Duplicate, CommandOutcome.Conflict, CommandOutcome.Stale, CommandOutcome.InvalidValue,
                CommandOutcome.Deferred, CommandOutcome.TooFarAhead, CommandOutcome.UnknownCombatant,
                CommandOutcome.InvalidCombatant, CommandOutcome.InvalidTick,
            }));
            Assert.That(Human(noisy).Position.z, Is.EqualTo(CombatMovementTuning.MoveSpeed * SimulationTiming.TickDuration));
        }

        [Test]
        public void DisabledInCombatFreezesBothPositionAndAirborneMotion()
        {
            using var w = new ReplayWorld("disabled movement");
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: 1, pressed: CommandButtons.Jump);
            w.Request(BattleRequest.Start(BattleSetup.Default));
            w.Step();
            var before = Human(w);
            w.Manager.SetComponentEnabled<InCombat>(Actor(w, w.Session.HumanCombatant), false);
            w.SubmitBetweenFrames(new CombatCommand { Combatant = w.Session.HumanCombatant, Tick = w.Tick + 1,
                MoveX = 1, Pressed = CommandButtons.Jump, Source = CommandSource.Player });
            w.Frame(10);
            SameMotion(before, Human(w));
            Assert.That(w.Records[1].Results.Single().Outcome, Is.EqualTo(CommandOutcome.UnknownCombatant));
        }

        [TestCase(2)] [TestCase(400)]
        public void PausedFramesDoNotIntegrateAndRestartEndDespawnClearKinetics(int count)
        {
            using var w = new ReplayWorld("motion lifecycle");
            var setup = BattleSetup.Default;
            setup.HumanAllies = setup.AiAllies = (ushort)(count / 2 - 1);
            Initialize(w, setup);
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: 1, pressed: CommandButtons.Jump);
            w.Step();
            var old = Actor(w, w.Session.HumanCombatant);
            var before = Human(w);
            var tick = w.Tick;
            var records = w.Records.Count;
            // A real fixed group with unchanged elapsed time runs zero steps after its first step.
            for (var i = 0; i < 5; i++)
            {
                w.World.SetTime(new TimeData(w.World.Time.ElapsedTime, 0f));
                w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
                w.FixedStep.Update();
                Assert.That(w.Manager.GetComponentData<CombatPosition>(old).Value, Is.EqualTo(before.Position));
                Assert.That(w.Manager.GetComponentData<CombatMotion>(old), Is.EqualTo(before.Motion));
            }
            Assert.That(w.Tick, Is.EqualTo(tick));
            Assert.That(w.Records.Count, Is.EqualTo(records));
            w.SubmitBetweenFrames(new CombatCommand { Combatant = w.Session.HumanCombatant, Tick = tick + 5,
                MoveX = 1, Pressed = CommandButtons.Jump, Source = CommandSource.Player });
            w.Step(); // defer the old-battle command, then also leave a submission outstanding
            w.SubmitBetweenFrames(new CombatCommand { Combatant = w.Session.HumanCombatant, Tick = w.Tick + 1,
                MoveX = 1, Source = CommandSource.Player });
            var oldMax = w.LiveIds.Max();
            setup.Human.Position.y = -5f;
            setup.Ai.Position.y = 9f;
            w.Request(BattleRequest.Restart(setup));
            w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            Assert.That(w.Manager.Exists(old), Is.False);
            Assert.That(w.LiveIds.Min(), Is.GreaterThan(oldMax));
            Assert.That(w.Session.BattleStartTick, Is.EqualTo(w.Tick));
            Assert.That((w.PendingCount, w.SubmissionCount, w.ResultCount), Is.EqualTo((0, 0, 0)));
            Assert.That(w.CountWith<CombatMotion>(), Is.EqualTo(count));
            var fresh = Actor(w, w.Session.HumanCombatant);
            Assert.That(w.Manager.GetComponentData<CombatPosition>(fresh).Value, Is.EqualTo(setup.Human.Position));
            var motion = w.Manager.GetComponentData<CombatMotion>(fresh);
            Assert.That((motion.GroundHeight, motion.VerticalVelocity, motion.Grounded), Is.EqualTo((-5f, 0f, true)));
            w.Script = new ReplayScript();
            w.Step(6);
            Assert.That(Human(w).Position, Is.EqualTo(setup.Human.Position), "No old velocity or command leaks after restart.");
            Assert.That(w.Session.RelativeTick(w.Tick), Is.EqualTo(6));
            w.Request(BattleRequest.Despawn(w.Session.AiCombatant));
            w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            Assert.That(w.CountWith<CombatMotion>(), Is.EqualTo(count - 1));
            w.Request(BattleRequest.End());
            w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            Assert.That(w.CountWith<CombatMotion>(), Is.Zero);
            Assert.That(w.CountWith<CombatPosition>(), Is.Zero);
        }

        [TestCase(typeof(CombatCommandCollectionSystemGroup), BattleRequestKind.Restart, 2)]
        [TestCase(typeof(CombatCommandCollectionSystemGroup), BattleRequestKind.Restart, 400)]
        [TestCase(typeof(CombatStateUpdateSystemGroup), BattleRequestKind.Restart, 2)]
        [TestCase(typeof(CombatStateUpdateSystemGroup), BattleRequestKind.Restart, 400)]
        [TestCase(typeof(CombatPublicationSystemGroup), BattleRequestKind.Restart, 2)]
        [TestCase(typeof(CombatPublicationSystemGroup), BattleRequestKind.Restart, 400)]
        [TestCase(typeof(CombatCommandCollectionSystemGroup), BattleRequestKind.End, 2)]
        [TestCase(typeof(CombatCommandCollectionSystemGroup), BattleRequestKind.End, 400)]
        [TestCase(typeof(CombatStateUpdateSystemGroup), BattleRequestKind.End, 2)]
        [TestCase(typeof(CombatStateUpdateSystemGroup), BattleRequestKind.End, 400)]
        [TestCase(typeof(CombatPublicationSystemGroup), BattleRequestKind.End, 2)]
        [TestCase(typeof(CombatPublicationSystemGroup), BattleRequestKind.End, 400)]
        public void AirborneEndAndRestartInsideCatchUpCleanBeforeTheNextTick(System.Type phase, BattleRequestKind kind, int count)
        {
            using var w = new ReplayWorld("airborne catch-up lifecycle");
            var setup = BattleSetup.Default;
            setup.HumanAllies = setup.AiAllies = (ushort)(count / 2 - 1);
            Initialize(w, setup);
            var previous = w.Session;
            using var query = w.Manager.CreateEntityQuery(typeof(CombatantId));
            using var oldEntities = query.ToEntityArray(Allocator.Temp);
            var oldMax = w.LiveIds.Max();
            w.Script = new ReplayScript()
                .Player(1, 1, ReplayRole.Human, moveX: 1, pressed: CommandButtons.Jump)
                .Ai(1, 1, ReplayRole.Ai, moveX: -1, pressed: CommandButtons.Jump)
                .Player(1, 5, ReplayRole.Human, moveX: 1, pressed: CommandButtons.Jump);
            w.Step(2);
            Assert.That(Human(w).Motion.Grounded, Is.False);
            Assert.That(w.PendingCount, Is.EqualTo(1));
            w.Manager.SetComponentEnabled<InCombat>(Actor(w, previous.AiCombatant), false);
            w.SubmitBetweenFrames(new CombatCommand { Combatant = previous.HumanCombatant, Tick = w.Tick + 2,
                MoveX = 1, Pressed = CommandButtons.Jump, Source = CommandSource.Player });
            // New battle gets no scripted commands: a jump there can only be leaked old motion/input.
            w.Script = new ReplayScript();
            setup.Human.Position.y = -5f;
            setup.Ai.Position.y = 7f;
            w.RequestInStep(phase, 1, 3, kind == BattleRequestKind.Restart
                ? BattleRequest.Restart(setup) : BattleRequest.End());

            w.Frame(3); // old tick 3 completes; cleanup precedes ticks 4 and 5 in this same frame

            Assert.That(w.Tick, Is.EqualTo(5));
            Assert.That(w.Records.Count, Is.EqualTo(5));
            Assert.That(w.Records[2].BattleNumber, Is.EqualTo(previous.BattleNumber));
            Assert.That(w.Records[2].Combatant(previous.HumanCombatant.Value).Motion.Grounded, Is.False);
            foreach (var entity in oldEntities)
                Assert.That(w.Manager.Exists(entity), Is.False, "Cleanup includes the disabled airborne actor and every ally.");
            Assert.That((w.PendingCount, w.SubmissionCount, w.ResultCount), Is.EqualTo((0, 0, 0)));
            if (kind == BattleRequestKind.End)
            {
                Assert.That(w.Session.IsActive, Is.False);
                Assert.That(w.CountWith<CombatMotion>(), Is.Zero);
                Assert.That(w.Records.Skip(3).All(r => !r.Active && r.Combatants.Length == 0), Is.True);
            }
            else
            {
                Assert.That(w.Session.BattleNumber, Is.EqualTo(previous.BattleNumber + 1));
                Assert.That(w.Session.BattleStartTick, Is.EqualTo(3));
                Assert.That(w.LiveIds.Min(), Is.GreaterThan(oldMax));
                Assert.That(w.CountWith<CombatMotion>(), Is.EqualTo(count));
                var restarted = w.BattleRecords(2);
                Assert.That(restarted.Select(r => r.RelativeTick), Is.EqualTo(new[] { 1u, 2u }));
                Near(restarted[0].Combatant(w.Session.HumanCombatant.Value).Position, setup.Human.Position);
                Near(restarted[0].Combatant(w.Session.AiCombatant.Value).Position, setup.Ai.Position);
                Assert.That(restarted[1].Combatants.Select(c => c.Position), Is.EqualTo(restarted[0].Combatants.Select(c => c.Position)));
                foreach (var actor in restarted.SelectMany(r => r.Combatants))
                {
                    Assert.That((actor.Motion.Grounded, actor.Motion.VerticalVelocity, actor.Motion.GroundHeight),
                        Is.EqualTo((true, 0f, actor.Position.y)));
                    Assert.That(actor.Applied.WasSubmitted, Is.False);
                }
            }
        }

        [Test]
        public void JumpReplayMatchesAcrossRestartsAndFreshWorldsIncludingKineticState()
        {
            using var repeated = new ReplayWorld("jump replay repeated");
            using var fresh = new ReplayWorld("jump replay fresh");
            var script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: 1, pressed: CommandButtons.Jump)
                .Player(10, 10, ReplayRole.Human, pressed: CommandButtons.Jump)
                .Ai(3, 3, ReplayRole.Ai, moveZ: -1, pressed: CommandButtons.Jump);
            repeated.Script = fresh.Script = script;
            repeated.Request(BattleRequest.Start(BattleSetup.Default));
            repeated.Step(70);
            repeated.Request(BattleRequest.Restart(BattleSetup.Default));
            for (var i = 0; i < 7; i++)
                repeated.Frame(10);
            fresh.Request(BattleRequest.Start(BattleSetup.Default));
            fresh.Step(70);
            Assert.That(repeated.NormalizedLog(2), Is.EqualTo(repeated.NormalizedLog(1)));
            Assert.That(repeated.NormalizedLog(2), Is.EqualTo(fresh.NormalizedLog(1)));
        }

        [Test]
        public void ClockExhaustionStopsAirborneIntegrationInTheSameStep()
        {
            using var w = new ReplayWorld("motion clock exhausted");
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: 1, pressed: CommandButtons.Jump);
            w.Request(BattleRequest.Start(BattleSetup.Default));
            w.Step();
            var actor = Actor(w, w.Session.HumanCombatant);
            var before = Human(w);
            using (var clock = w.Manager.CreateEntityQuery(typeof(SimulationClock)))
                w.Manager.SetComponentData(clock.GetSingletonEntity(), SimulationClock.AtTick(SimulationClock.LastTick));
            LogAssert.Expect(LogType.Error, "SimulationClock reached its last tick; the clock and combat phases stopped.");
            w.Step();
            Assert.That(w.Records.Count, Is.EqualTo(1), "Publication and State both stop with the clock.");
            Assert.That(w.Manager.GetComponentData<CombatPosition>(actor).Value, Is.EqualTo(before.Position));
            Assert.That(w.Manager.GetComponentData<CombatMotion>(actor), Is.EqualTo(before.Motion));
        }

        [Test]
        public void MovementCannotRunWithoutRunningClockEvenWhenUpdatedDirectly()
        {
            using var w = new World("movement clock guard");
            var system = w.CreateSystem<CombatMovementSystem>();
            var entity = w.EntityManager.CreateEntity(typeof(CombatPosition), typeof(CombatMotion), typeof(InCombat), typeof(AppliedCombatCommand));
            w.EntityManager.SetComponentData(entity, new CombatMotion { VerticalVelocity = 8f });
            system.Update(w.Unmanaged);
            Assert.That(w.EntityManager.GetComponentData<CombatPosition>(entity).Value, Is.EqualTo(float3.zero));
            Assert.That(w.EntityManager.GetComponentData<CombatMotion>(entity).VerticalVelocity, Is.EqualTo(8f));
        }
    }
}
