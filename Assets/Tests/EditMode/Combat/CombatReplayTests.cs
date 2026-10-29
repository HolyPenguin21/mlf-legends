using System;
using System.Collections.Generic;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace MLFLegends.Tests.EditMode
{
    // Stage 01.5 integration and replay tests (Issue #6): the real clock, lifecycle and command processor together in
    // isolated Worlds built by ReplayWorld (see CombatReplayDriver.cs). Never touches the default world.
    //
    // What "reproducible" means here: the same BattleSetup, the same script of commands (by role and relative tick) and
    // the same between-frame requests give the same canonical record of every completed tick: combatant ID/team/
    // position/facing/InCombat, AppliedCombatCommand, CombatCommandResult and pending commands. The record also includes
    // Stage 02.1 motion state. No seed is involved: nothing consumes one yet. These tests cover the current
    // tick/command/lifecycle/movement pipeline on one machine and Unity version, not cross-platform float determinism.
    public class CombatReplayTests
    {
        const uint IdleTicksBeforeFirstBattle = 3;
        const int MatrixLength = 8;
        const int DespawnAfter = 5;

        static readonly int[] OneTickPerFrame = { 1, 1, 1, 1, 1, 1, 1, 1 };

        readonly List<ReplayWorld> m_Worlds = new List<ReplayWorld>();

        [TearDown]
        public void TearDown()
        {
            foreach (var world in m_Worlds)
                world.Dispose();
            m_Worlds.Clear();
        }

        ReplayWorld NewWorld(string name)
        {
            var world = new ReplayWorld("MLF Legends replay " + name);
            m_Worlds.Add(world);
            return world;
        }

        // ---- scripts ----

        // Every command case of the processor, produced in the fixed step by the player and AI producers. The AI
        // combatant is despawned between relative ticks 5 and 6 (RunMatrix). Expected outcomes: CommandMatrixGives...
        static ReplayScript CommandMatrix => new ReplayScript()
            // r1: one accepted command per producer.
            .Player(1, 1, ReplayRole.Human, moveX: 1)
            .Ai(1, 1, ReplayRole.Ai, moveX: -1)
            // r2: nothing for the human (implicit None); the AI repeats an identical command.
            .Ai(2, 2, ReplayRole.Ai, held: CommandButtons.Guard)
            .Ai(2, 2, ReplayRole.Ai, held: CommandButtons.Guard)
            // r3: conflicting values, a late command, a conflicting source, a previous-battle tick and a future command.
            .Player(3, 3, ReplayRole.Human, moveX: 1)
            .Player(3, 3, ReplayRole.Human, moveX: -1)
            .Player(3, 2, ReplayRole.Human, moveX: 1)
            .Ai(3, 3, ReplayRole.Human, moveX: 1)
            .Ai(3, 0, ReplayRole.Ai)
            .Ai(3, 5, ReplayRole.Ai, pressed: CommandButtons.HandSeries)
            // r4: future command and its repeat, too far ahead, current command, conflict with a pending command.
            .Player(4, 5, ReplayRole.Human, moveX: 1)
            .Player(4, 5, ReplayRole.Human, moveX: 1)
            .Player(4, 4 + CombatCommandQueue.MaxLeadTicks + 1, ReplayRole.Human)
            .Player(4, 4, ReplayRole.Human, moveZ: 1)
            .Ai(4, 5, ReplayRole.Ai, moveX: 1, pressed: CommandButtons.HandSeries)
            // r5: pending commands are due; a same-tick submission loses to them; another future AI command.
            .Player(5, 5, ReplayRole.Human, moveX: -1)
            .Ai(5, 7, ReplayRole.Ai, held: CommandButtons.Guard)
            // r6 (AI despawned): a submitted None for the human, the AI producer still targets its fighter.
            .Player(6, 6, ReplayRole.Human)
            .Ai(6, 6, ReplayRole.Ai)
            // r7: the AI's pending command is due for a despawned fighter; a command for the previous battle's human.
            .Player(7, 7, ReplayRole.PreviousHuman, moveX: 1)
            // r8: nothing due; two commands for ticks after the battle ends (must never reach the next battle).
            .Player(8, 9, ReplayRole.Human, moveX: 1)
            .Player(8, 11, ReplayRole.Human);

        // One command per producer and tick, plus future commands from r1 for r4 (dropped if the battle ends first).
        static ReplayScript PerTickScript
        {
            get
            {
                var script = new ReplayScript()
                    .Player(1, 4, ReplayRole.Human, pressed: CommandButtons.HandSeries)
                    .Ai(1, 4, ReplayRole.Ai, pressed: CommandButtons.HandSeries);
                for (var r = 1u; r <= 12; r++)
                    script.Player(r, r, ReplayRole.Human, moveX: 1).Ai(r, r, ReplayRole.Ai, moveX: -1);
                return script;
            }
        }

        static BattleSetup OtherSetup => new BattleSetup
        {
            Human = new CombatantSetup { Team = 3, Position = new float3(-5f, 0f, 1.5f), Facing = FacingDirection.Right },
            Ai = new CombatantSetup { Team = 4, Position = new float3(5f, 1f, -1.5f), Facing = FacingDirection.Left },
        };

        // Requests the battle, runs the command matrix in the given render frames (a frame boundary must follow relative
        // tick 5, where the AI combatant is despawned) and calls beforeTick(r) before the frame that starts with tick r.
        static void RunMatrix(ReplayWorld world, BattleRequest start, int[] frames = null, Action<uint> beforeTick = null)
        {
            world.Script = CommandMatrix;
            world.Request(start);
            var done = 0;
            var despawned = false;
            foreach (var ticks in frames ?? OneTickPerFrame)
            {
                if (done == DespawnAfter)
                {
                    world.Request(BattleRequest.Despawn(world.Session.AiCombatant));
                    despawned = true;
                }

                beforeTick?.Invoke((uint)done + 1);
                world.Frame(ticks);
                done += ticks;
            }

            Assert.That(done, Is.EqualTo(MatrixLength), "The frames cover the whole matrix.");
            Assert.That(despawned, Is.True, "The frames have a boundary after relative tick 5.");
        }

        // The invariants of the tick timeline, checked on every recorded tick of a World.
        static void AssertTimeline(ReplayWorld world)
        {
            var records = world.Records;
            Assert.That(records, Is.Not.Empty);
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                Assert.That(record.Tick, Is.EqualTo((uint)i + 1), "Every tick of the World runs once: none skipped, none repeated.");
                Assert.That(record.LastProcessedTick, Is.EqualTo(record.Tick), $"The processor ran on tick {record.Tick}.");
                foreach (var combatant in record.Combatants.Where(c => c.InCombat))
                {
                    Assert.That(combatant.HasApplied, Is.True);
                    Assert.That(combatant.Applied.Command.Tick, Is.EqualTo(record.Tick),
                        $"Combatant {combatant.Id} received exactly the command of tick {record.Tick}.");
                }

                if (record.Active)
                    Assert.That(record.QueueBattleStartTick, Is.EqualTo(record.BattleStartTick), "One battle boundary for session and queue.");
            }

            var battles = records.Where(r => r.Active).Select(r => r.BattleNumber).Distinct().ToArray();
            Assert.That(battles, Is.Ordered.Ascending);
            var previousStart = -1L;
            foreach (var battle in battles)
            {
                var ticks = world.BattleRecords(battle);
                Assert.That(ticks.Select(r => r.RelativeTick), Is.EqualTo(Enumerable.Range(1, ticks.Length).Select(i => (uint)i)),
                    $"Battle {battle} counts its own ticks from relative tick 1.");
                Assert.That(ticks[0].Tick, Is.EqualTo(ticks[0].BattleStartTick + 1), "BattleStartTick is set before the next tick advances.");
                Assert.That((long)ticks[0].BattleStartTick, Is.GreaterThan(previousStart), "Battle boundaries only move forward.");
                previousStart = ticks[0].BattleStartTick;
            }
        }

        static string Applied(CombatantRecord c)
        {
            var command = c.Applied.Command;
            return $"x{command.MoveX} z{command.MoveZ} held:{command.Held} pressed:{command.Pressed} {command.Source}"
                + (c.Applied.WasSubmitted ? "" : " implicit");
        }

        static string[] PendingSummary(TickRecord record) => record.Pending
            .Select(p => $"{(p.Combatant.Value == record.HumanId ? "H" : p.Combatant.Value == record.AiId ? "A" : "foreign")} r{p.Tick - record.BattleStartTick}")
            .ToArray();

        // ---- 1. one World, one pipeline, player and AI through one mechanism ----

        [Test]
        public void RealSystemsRunInTheirDeclaredOrderInsideOneFixedStep()
        {
            var w = NewWorld("order");
            using var steps = w.FixedStep.GetAllSystems(Allocator.Temp);
            var begin = steps.IndexOf(w.World.GetExistingSystem<BeginFixedStepSimulationEntityCommandBufferSystem>());
            var lifecycle = steps.IndexOf(w.World.GetExistingSystem<BattleLifecycleSystem>());
            var clock = steps.IndexOf(w.World.GetExistingSystem<CombatClockSystem>());
            var collection = steps.IndexOf(w.World.GetExistingSystem<CombatCommandCollectionSystemGroup>());
            var state = steps.IndexOf(w.World.GetExistingSystem<CombatStateUpdateSystemGroup>());
            var publication = steps.IndexOf(w.World.GetExistingSystem<CombatPublicationSystemGroup>());
            var end = steps.IndexOf(w.World.GetExistingSystem<EndFixedStepSimulationEntityCommandBufferSystem>());
            Assert.That(new[] { begin, lifecycle, clock, collection, state, publication, end }, Is.All.GreaterThanOrEqualTo(0));
            Assert.That(new[] { begin, lifecycle, clock, collection, state, publication, end }, Is.Ordered.Ascending.And.Unique,
                "Begin ECB -> lifecycle -> clock -> collection -> state update -> publication -> End ECB, from the systems' own attributes.");

            using (var initialization = w.World.GetExistingSystemManaged<InitializationSystemGroup>().GetAllSystems(Allocator.Temp))
            {
                Assert.That(initialization.IndexOf(w.World.GetExistingSystem<BattleLifecycleInitializationProxy>()), Is.GreaterThanOrEqualTo(0),
                    "Attribute-driven bootstrap puts the lifecycle's Initialization proxy into Initialization.");
                Assert.That(initialization.IndexOf(w.World.GetExistingSystem<BattleLifecycleSystem>()), Is.EqualTo(-1),
                    "The lifecycle itself has one group, FixedStep; the proxy updates that same instance.");
                Assert.That(initialization.IndexOf(w.World.GetExistingSystem<CombatClockSystem>()), Is.EqualTo(-1));
                Assert.That(steps.IndexOf(w.World.GetExistingSystem<BattleLifecycleInitializationProxy>()), Is.EqualTo(-1));
            }

            using var producers = w.World.GetExistingSystemManaged<CombatCommandCollectionSystemGroup>().GetAllSystems(Allocator.Temp);
            Assert.That(producers.IndexOf(w.World.GetExistingSystem<PlayerCommandProducer>()),
                Is.LessThan(producers.IndexOf(w.World.GetExistingSystem<AiCommandProducer>())));
            Assert.That(producers[producers.Length - 1], Is.EqualTo(w.World.GetExistingSystem<CombatCommandProcessorSystem>()),
                "The single processor runs after every producer of the tick.");
            Assert.That(w.FixedStep.Timestep, Is.EqualTo(SimulationTiming.TickDuration));
            ReplayWorld.AssertStateOwners(w.World);
            using var states = w.World.GetExistingSystemManaged<CombatStateUpdateSystemGroup>().GetAllSystems(Allocator.Temp);
            Assert.That(states.AsArray().ToArray(), Is.EqualTo(ReplayWorld.StateOwners(w.World)),
                "Without test requesters, State holds exactly the runtime strike, facing, movement, spatial and hit owners in order.");
            Assert.That(w.CountWith<SimulationClock>(), Is.EqualTo(1));
            Assert.That(w.CountWith<CombatCommandQueue>(), Is.EqualTo(1));
            Assert.That(w.CountWith<BattleSession>(), Is.EqualTo(1));
        }

        [Test]
        public void PlayerAndAiCommandsFollowIdenticalRulesThroughOneQueue()
        {
            var w = NewWorld("mirror");
            var script = new ReplayScript();
            void Both(uint on, uint @for, sbyte moveX = 0, CommandButtons pressed = CommandButtons.None)
            {
                script.Player(on, @for, ReplayRole.Human, moveX: moveX, pressed: pressed);
                script.Ai(on, @for, ReplayRole.Ai, moveX: moveX, pressed: pressed);
            }

            Both(1, 1, moveX: 1);
            Both(2, 2, moveX: 1);
            Both(2, 2, moveX: 1);
            Both(3, 3, moveX: 1);
            Both(3, 3, moveX: -1);
            Both(3, 6, pressed: CommandButtons.HandSeries);
            Both(4, 6, pressed: CommandButtons.HandSeries);
            Both(4, 6, moveX: 1);
            Both(4, 4 + CombatCommandQueue.MaxLeadTicks + 1);
            // r5: nothing from either producer.
            Both(6, 6, moveX: -1);
            Both(7, 6, moveX: 1);
            Both(7, 0);
            w.Script = script;
            w.Step((int)IdleTicksBeforeFirstBattle);
            w.Request(BattleRequest.Start(BattleSetup.Default));

            for (var r = 1; r <= 7; r++)
            {
                w.Step();
                Assert.That(w.SubmissionCount, Is.Zero, "The one submission buffer is drained on every tick.");
            }

            var outcomes = new HashSet<CommandOutcome>();
            foreach (var record in w.BattleRecords(1))
            {
                string Rule(ResultRecord r) =>
                    $"r{r.RelativeTick} x{r.Command.MoveX} pressed:{r.Command.Pressed} -> {r.Outcome}";
                var human = record.Results.Where(r => r.Label == "H").ToArray();
                var ai = record.Results.Where(r => r.Label == "A").ToArray();
                Assert.That(human.All(r => r.Command.Source == CommandSource.Player) && ai.All(r => r.Command.Source == CommandSource.AI));
                Assert.That(ai.Select(Rule), Is.EqualTo(human.Select(Rule)), $"Same outcomes for player and AI on relative tick {record.RelativeTick}.");

                var humanApplied = Applied(record.Combatant(record.HumanId));
                var aiApplied = Applied(record.Combatant(record.AiId));
                Assert.That(aiApplied.Replace(" AI", " Player"), Is.EqualTo(humanApplied), $"Same applied command on relative tick {record.RelativeTick}.");
                // The AI spawns Left, the human Right: the accepted x+1 of r1 turns the AI on that same tick, and no
                // rejected x-1 (r3 conflict, r6 conflict, r7 stale) turns either of them back.
                Assert.That((record.Combatant(record.HumanId).Facing, record.Combatant(record.AiId).Facing),
                    Is.EqualTo((FacingDirection.Right, FacingDirection.Right)), $"Same facing on relative tick {record.RelativeTick}.");
                outcomes.UnionWith(human.Select(r => r.Outcome));
            }

            Assert.That(outcomes, Is.SupersetOf(new[]
            {
                CommandOutcome.Accepted, CommandOutcome.Duplicate, CommandOutcome.Conflict, CommandOutcome.Deferred,
                CommandOutcome.Stale, CommandOutcome.TooFarAhead, CommandOutcome.BeforeBattleStart,
            }), "The mirror exercised every rule.");
            Assert.That(Applied(w.BattleRecords(1)[4].Combatant(1)), Does.EndWith("implicit"), "Both get an implicit None on r5.");
            Assert.That(w.CountWith<CombatCommandQueue>(), Is.EqualTo(1));
            AssertTimeline(w);
        }

        // ---- 2. fixed-tick sequence with every command case ----

        [Test]
        public void CommandMatrixGivesTheExpectedOutcomesOnEveryFixedTick()
        {
            var w = NewWorld("matrix");
            w.Step((int)IdleTicksBeforeFirstBattle);
            RunMatrix(w, BattleRequest.Start(BattleSetup.Default));

            var b = w.BattleRecords(1);
            Assert.That(b.Length, Is.EqualTo(MatrixLength));
            Assert.That(b[0].BattleStartTick, Is.EqualTo(IdleTicksBeforeFirstBattle));
            var expectedResults = new[]
            {
                new[] { "H r1 Accepted", "A r1 Accepted" },
                new[] { "A r2 Accepted", "A r2 Duplicate" },
                new[] { "H r2 Stale", "H r3 Accepted", "H r3 Conflict", "H r3 Conflict", "A r0 BeforeBattleStart", "A r5 Deferred" },
                new[] { "H r4 Accepted", "H r5 Deferred", "H r5 Duplicate", "H r37 TooFarAhead", "A r5 Conflict" },
                new[] { "H r5 Accepted", "H r5 Conflict", "A r5 Accepted", "A r7 Deferred" },
                new[] { "H r6 Accepted", "A r6 UnknownCombatant" },
                new[] { "A r7 UnknownCombatant", "foreign r7 UnknownCombatant" },
                new[] { "H r9 Deferred", "H r11 Deferred" },
            };
            var expectedHuman = new[]
            {
                "x1 z0 held:None pressed:None Player",
                "x0 z0 held:None pressed:None None implicit",
                "x1 z0 held:None pressed:None Player",
                "x0 z1 held:None pressed:None Player",
                "x1 z0 held:None pressed:None Player",
                "x0 z0 held:None pressed:None Player",
                "x0 z0 held:None pressed:None None implicit",
                "x0 z0 held:None pressed:None None implicit",
            };
            var expectedAi = new[]
            {
                "x-1 z0 held:None pressed:None AI",
                "x0 z0 held:Guard pressed:None AI",
                "x0 z0 held:None pressed:None None implicit",
                "x0 z0 held:None pressed:None None implicit",
                "x0 z0 held:None pressed:HandSeries AI",
            };
            var expectedPending = new[]
            {
                new string[0],
                new string[0],
                new[] { "A r5" },
                new[] { "H r5", "A r5" },
                new[] { "A r7" },
                new[] { "A r7" },
                new string[0],
                new[] { "H r9", "H r11" },
            };

            var expectedHumanXSteps = new[] { 1, 1, 2, 2, 3, 3, 3, 3 };
            // Facing follows the sign of the applied MoveX, else keeps the previous value. Human: x+1 on r1, r3, r5, X-less
            // otherwise; the rejected x-1 of r3 (conflict) and r5 (loses to the due pending x+1) must not turn it Left.
            // AI: spawned Left, x-1 on r1, then only Guard/None/Attack until its despawn.
            var R = FacingDirection.Right;
            var L = FacingDirection.Left;
            var expectedHumanFacing = new[] { R, R, R, R, R, R, R, R };
            var expectedAiFacing = new[] { L, L, L, L, L };
            for (var i = 0; i < MatrixLength; i++)
            {
                var record = b[i];
                var r = i + 1;
                Assert.That(record.ResultSummary, Is.EqualTo(expectedResults[i]), $"Results of relative tick {r}.");
                Assert.That(PendingSummary(record), Is.EqualTo(expectedPending[i]), $"Pending after relative tick {r}.");
                Assert.That(Applied(record.Combatant(record.HumanId)), Is.EqualTo(expectedHuman[i]), $"Human command on relative tick {r}.");

                var human = record.Combatant(record.HumanId);
                Assert.That((human.Id, human.Team, human.Facing, human.InCombat),
                    Is.EqualTo((1u, (byte)1, expectedHumanFacing[i], true)), $"Human identity and facing on relative tick {r}.");
                Assert.That(human.Position.x, Is.EqualTo(-2f + expectedHumanXSteps[i] * CombatMovementTuning.MoveSpeed * SimulationTiming.TickDuration).Within(1e-5f));
                Assert.That(human.Position.z, Is.EqualTo(r >= 4 ? CombatMovementTuning.MoveSpeed * SimulationTiming.TickDuration : 0f).Within(1e-5f));
                Assert.That(human.Position.y, Is.Zero);
                if (r <= DespawnAfter)
                {
                    Assert.That(record.Combatants.Select(c => c.Id), Is.EqualTo(new[] { 1u, 2u }));
                    var ai = record.Combatant(record.AiId);
                    Assert.That((ai.Team, ai.Facing, ai.InCombat), Is.EqualTo(((byte)2, expectedAiFacing[i], true)),
                        $"AI facing on relative tick {r}.");
                    Assert.That(ai.Position.x, Is.EqualTo(2f - CombatMovementTuning.MoveSpeed * SimulationTiming.TickDuration).Within(1e-5f));
                    Assert.That(ai.Position.yz, Is.EqualTo(new float2(0f)));
                    Assert.That(Applied(ai), Is.EqualTo(expectedAi[i]), $"AI command on relative tick {r}.");
                }
                else
                {
                    Assert.That(record.Combatants.Select(c => c.Id), Is.EqualTo(new[] { 1u }), "The despawned AI combatant is gone.");
                }
            }

            Assert.That(w.Session.AiCombatant.Value, Is.EqualTo(2u), "Despawn keeps the session's roles.");
            Assert.That(w.BattleRecords(1)[6].Results[1].Command.Combatant.Value, Is.EqualTo(ReplayWorld.NeverIssuedId));
            AssertTimeline(w);
        }

        // ---- 3. End/Restart inside a fixed tick of a catch-up frame ----

        [TestCase(typeof(CombatCommandCollectionSystemGroup))]
        [TestCase(typeof(CombatStateUpdateSystemGroup))]
        [TestCase(typeof(CombatPublicationSystemGroup))]
        public void TwoRestartsInsideOneCatchUpFrameNeverLeakTheEndedBattles(Type phase)
        {
            var w = NewWorld("catch-up restart");
            w.Script = PerTickScript;
            w.Step((int)IdleTicksBeforeFirstBattle);
            w.Request(BattleRequest.Start(BattleSetup.Default, owner: 1));
            w.Step(2);
            var first = w.Session;
            // A frame-rate producer still targets battle 1's fighters on ticks that will belong to battles 2 and 3.
            w.SubmitBetweenFrames(
                new CombatCommand { Combatant = first.HumanCombatant, Tick = w.Tick + 2, MoveX = 1, Source = CommandSource.Player },
                new CombatCommand { Combatant = first.AiCombatant, Tick = w.Tick + 3, MoveX = 1, Source = CommandSource.AI });
            w.RequestInStep(phase, 1, 3, BattleRequest.Restart(OtherSetup, owner: 2));
            w.RequestInStep(phase, 2, 1, BattleRequest.Restart(BattleSetup.Default, owner: 3));

            w.Frame(5);

            var frame = w.Records.Skip(w.Records.Count - 5).ToArray();
            var lastTickOfFirst = IdleTicksBeforeFirstBattle + 3;
            Assert.That(frame.Select(r => r.Tick), Is.EqualTo(Enumerable.Range((int)lastTickOfFirst, 5).Select(t => (uint)t)));
            Assert.That(frame.Select(r => r.BattleNumber), Is.EqualTo(new[] { 1u, 2u, 3u, 3u, 3u }));
            Assert.That(frame.Select(r => r.RelativeTick), Is.EqualTo(new[] { 3u, 1u, 1u, 2u, 3u }));
            Assert.That(frame[1].BattleStartTick, Is.EqualTo(lastTickOfFirst), "Battle 2 starts on the tick that requested it, before the next tick advances.");
            Assert.That(frame[2].BattleStartTick, Is.EqualTo(lastTickOfFirst + 1));

            Assert.That(frame[0].Combatants.Select(c => c.Id), Is.EqualTo(new[] { 1u, 2u }), "The requesting tick completes with its own battle.");
            Assert.That(frame[1].Combatants.Select(c => c.Id), Is.EqualTo(new[] { 3u, 4u }));
            Assert.That(frame.Skip(2).Select(r => r.Combatants.Select(c => c.Id).ToArray()), Is.All.EqualTo(new[] { 5u, 6u }));
            foreach (var record in frame.Skip(1))
            {
                Assert.That(record.Results.Select(r => r.Label).Where(l => l != "H" && l != "A"), Is.Empty,
                    $"Only the running battle's commands are processed on tick {record.Tick}.");
                Assert.That(PendingSummary(record).Where(p => p.StartsWith("foreign")), Is.Empty);
                Assert.That(record.Combatants.Select(Applied).Where(a => a.EndsWith("implicit")), Is.Empty,
                    $"The new battle's own producers are applied on its relative tick {record.RelativeTick}.");
            }

            Assert.That(frame[1].ResultSummary, Is.EqualTo(new[] { "H r1 Accepted", "H r4 Deferred", "A r1 Accepted", "A r4 Deferred" }));
            var session = w.Session;
            Assert.That((session.BattleNumber, session.Owner, session.BattleStartTick), Is.EqualTo((3u, 3u, lastTickOfFirst + 1)));
            Assert.That(w.LiveIds, Is.EqualTo(new[] { 5u, 6u }));

            // Battle 3 replays exactly like a battle started between frames in a fresh World.
            var reference = NewWorld("catch-up restart reference");
            reference.Script = PerTickScript;
            reference.Step((int)IdleTicksBeforeFirstBattle);
            reference.Request(BattleRequest.Start(BattleSetup.Default));
            reference.Step(3);
            Assert.That(w.NormalizedLog(3), Is.EqualTo(reference.NormalizedLog(1)));
            AssertTimeline(w);
        }

        // ---- 4 + 5. two sequential battles in one World; global and relative ticks ----

        [Test]
        public void TwoSequentialBattlesInOneWorldReplayIdenticallyWithoutLeaks()
        {
            var w = NewWorld("two battles");
            w.Step((int)IdleTicksBeforeFirstBattle);
            var baseline = w.EntityCount;

            RunMatrix(w, BattleRequest.Start(BattleSetup.Default));
            var first = w.Session;
            Assert.That(w.PendingCount, Is.EqualTo(2), "Battle 1 ends with commands pending for its later ticks.");
            w.Request(BattleRequest.End());
            w.Step(2);

            Assert.That(w.Session.State, Is.EqualTo(BattleState.Idle));
            Assert.That(w.EntityCount, Is.EqualTo(baseline), "No entity of battle 1 survives its end.");
            Assert.That(w.CountWith<CombatantId>(), Is.Zero);
            Assert.That(w.CountWith<AppliedCombatCommand>(), Is.Zero);
            Assert.That((w.SubmissionCount, w.PendingCount, w.ResultCount), Is.EqualTo((0, 0, 0)), "No command of battle 1 is left in the queue.");

            RunMatrix(w, BattleRequest.Start(BattleSetup.Default));
            var second = w.Session;
            w.Request(BattleRequest.End());
            w.Step();

            Assert.That(w.NormalizedLog(2), Is.EqualTo(w.NormalizedLog(1)), "Same setup and script: the same battle, tick by tick.");
            Assert.That(w.NormalizedLog(1).Length, Is.EqualTo(MatrixLength));
            Assert.That(second.BattleStartTick, Is.EqualTo(first.BattleStartTick + MatrixLength + 2));
            Assert.That(new[] { first.HumanCombatant.Value, first.AiCombatant.Value }, Is.EqualTo(new[] { 1u, 2u }));
            Assert.That(new[] { second.HumanCombatant.Value, second.AiCombatant.Value }, Is.EqualTo(new[] { 3u, 4u }), "IDs are never reused.");
            // Battle 1's pending command for its relative tick 11 has the absolute tick of battle 2's relative tick 1.
            Assert.That(first.BattleStartTick + 11, Is.EqualTo(second.BattleStartTick + 1));
            var foreign = w.BattleRecords(2).SelectMany(r => r.Results).Where(r => r.Label == "foreign").ToArray();
            Assert.That(foreign.Select(r => (r.Command.Combatant.Value, r.RelativeTick, r.Outcome)),
                Is.EqualTo(new[] { (first.HumanCombatant.Value, 7L, CommandOutcome.UnknownCombatant) }),
                "The only command for a battle-1 fighter in battle 2 is the scripted late one, and it is rejected.");
            Assert.That(w.EntityCount, Is.EqualTo(baseline));
            AssertTimeline(w);
        }

        [Test]
        public void GlobalTickIsMonotonicWhileEveryBattleCountsFromRelativeTickOne()
        {
            var w = NewWorld("timeline");
            w.Script = PerTickScript;
            w.Request(BattleRequest.Start(BattleSetup.Default));     // before the first tick: BattleStartTick 0
            w.Frame(4);
            w.Request(BattleRequest.Restart(OtherSetup));            // replaces a running battle between frames
            w.Step(2);
            w.RequestInStep(typeof(CombatStateUpdateSystemGroup), 2, 3, BattleRequest.End());
            w.Frame(6);                                              // ends inside the frame, then idle catch-up ticks
            w.Request(BattleRequest.Restart(BattleSetup.Default));   // from idle
            w.Frame(3);
            w.RequestInStep(typeof(CombatPublicationSystemGroup), 3, 4, BattleRequest.Restart(BattleSetup.Default));
            w.Frame(7);

            Assert.That(w.Records.Count, Is.EqualTo(22));
            Assert.That(w.Records.Where(r => r.Active).Select(r => r.BattleNumber).Distinct(), Is.EqualTo(new[] { 1u, 2u, 3u, 4u }));
            Assert.That(new[] { 1u, 2u, 3u, 4u }.Select(b => w.BattleRecords(b).Length), Is.EqualTo(new[] { 4, 3, 4, 6 }));
            Assert.That(new[] { 1u, 2u, 3u, 4u }.Select(b => w.BattleRecords(b)[0].BattleStartTick), Is.EqualTo(new[] { 0u, 4u, 12u, 16u }));
            Assert.That(w.Records.Where(r => !r.Active).Select(r => r.Tick), Is.EqualTo(new[] { 8u, 9u, 10u, 11u, 12u }),
                "The clock keeps running while no battle is active.");
            Assert.That(w.Records.Where(r => r.Active).SelectMany(r => r.Combatants.Select(c => c.Id)).Distinct(),
                Is.EqualTo(new[] { 1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u }), "Every battle has fresh IDs.");
            // Battle 3 started from idle between frames, battle 4 by a restart inside a catch-up frame: same relative timeline.
            Assert.That(w.NormalizedLog(4).Take(4), Is.EqualTo(w.NormalizedLog(3)));
            AssertTimeline(w);
        }

        // ---- 6. reproducibility ----

        static void RunTwoBattles(ReplayWorld w, int[] firstFrames, int[] secondFrames)
        {
            w.Frame((int)IdleTicksBeforeFirstBattle);
            RunMatrix(w, BattleRequest.Start(BattleSetup.Default, owner: 1), firstFrames);
            // Restart directly: battle 1's pending commands (relative ticks 9, 11) fall on battle 2's ticks.
            RunMatrix(w, BattleRequest.Restart(OtherSetup, owner: 2), secondFrames);
            w.Request(BattleRequest.End());
            w.Step();
        }

        [Test]
        public void SameInputsInFreshWorldsGiveIdenticalRawRecords()
        {
            var a = NewWorld("fresh A");
            var b = NewWorld("fresh B");
            RunTwoBattles(a, OneTickPerFrame, OneTickPerFrame);
            RunTwoBattles(b, OneTickPerFrame, OneTickPerFrame);

            Assert.That(b.RawLog, Is.EqualTo(a.RawLog), "Absolute ticks and IDs match too: both Worlds start from tick 0 and ID 1.");
            Assert.That(a.RawLog.Length, Is.EqualTo(IdleTicksBeforeFirstBattle + 2 * MatrixLength + 1));
            Assert.That(a.BattleRecords(2)[0].Combatant(3).Position.x,
                Is.EqualTo(OtherSetup.Human.Position.x + CombatMovementTuning.MoveSpeed * SimulationTiming.TickDuration).Within(1e-5f));
            Assert.That(a.BattleRecords(2)[0].Combatant(3).Position.yz, Is.EqualTo(OtherSetup.Human.Position.yz));
            AssertTimeline(a);
        }

        static IEnumerable<int[]> Pacings()
        {
            yield return new[] { 5, 3 };
            yield return new[] { 2, 3, 3 };
            yield return new[] { 1, 4, 1, 2 };
        }

        [TestCaseSource(nameof(Pacings))]
        public void RenderFramePacingDoesNotChangeCompletedTickResults(int[] frames)
        {
            var reference = NewWorld("pacing reference");
            var paced = NewWorld("pacing " + string.Join("-", frames));
            RunTwoBattles(reference, OneTickPerFrame, OneTickPerFrame);
            RunTwoBattles(paced, frames, frames);

            Assert.That(paced.RawLog, Is.EqualTo(reference.RawLog),
                "Producers inside the fixed step and requests between frames: the frame rate changes nothing.");
            AssertTimeline(paced);
        }

        // Documented exception (not hidden): a producer that runs once per render frame and targets "the next tick" sees
        // only frame boundaries. With catch-up frames it covers fewer ticks; the simulation is still the same function of
        // the commands it received. Producers that target explicit ticks ahead of time are pacing-independent.
        [Test]
        public void FrameRateProducerTargetingTheNextTickDependsOnFramePacing()
        {
            ReplayWorld Battle(string name)
            {
                var world = NewWorld(name);
                world.Request(BattleRequest.Start(BattleSetup.Default));
                world.Step();
                return world;
            }

            void NextTickCommand(ReplayWorld world) => world.SubmitBetweenFrames(new CombatCommand
            {
                Combatant = world.Session.HumanCombatant, Tick = world.Tick + 1, MoveX = 1, Source = CommandSource.Player,
            });

            int Submitted(ReplayWorld world) => world.BattleRecords(1).Skip(1)
                .Count(r => r.Combatant(r.HumanId).Applied.WasSubmitted);

            var everyFrame = Battle("next-tick producer, 1 tick per frame");
            var catchUp = Battle("next-tick producer, 3 ticks per frame");
            for (var i = 0; i < 6; i++)
            {
                NextTickCommand(everyFrame);
                everyFrame.Frame(1);
            }

            for (var i = 0; i < 2; i++)
            {
                NextTickCommand(catchUp);
                catchUp.Frame(3);
            }

            Assert.That(everyFrame.Tick, Is.EqualTo(catchUp.Tick));
            Assert.That(Submitted(everyFrame), Is.EqualTo(6));
            Assert.That(Submitted(catchUp), Is.EqualTo(2), "Only the first tick of each catch-up frame receives the producer's command.");

            // The same producer, targeting explicit ticks ahead: identical results for both pacings.
            var aheadEveryFrame = Battle("ahead producer, 1 tick per frame");
            var aheadCatchUp = Battle("ahead producer, 3 ticks per frame");
            foreach (var world in new[] { aheadEveryFrame, aheadCatchUp })
            {
                var start = world.Session.BattleStartTick;
                for (var r = 2u; r <= 7; r++)
                    world.SubmitBetweenFrames(new CombatCommand { Combatant = world.Session.HumanCombatant, Tick = start + r, MoveX = 1, Source = CommandSource.Player });
            }

            aheadEveryFrame.Step(6);
            aheadCatchUp.Frame(3);
            aheadCatchUp.Frame(3);
            Assert.That(aheadCatchUp.RawLog, Is.EqualTo(aheadEveryFrame.RawLog));
            Assert.That(Submitted(aheadCatchUp), Is.EqualTo(6));
        }

        // ---- requests with no effect, scene switch ----

        [Test]
        public void RejectedLifecycleRequestsLeaveTheReplayUnchanged()
        {
            var clean = NewWorld("no requests");
            var noisy = NewWorld("rejected requests");
            clean.Step((int)IdleTicksBeforeFirstBattle);
            noisy.Step((int)IdleTicksBeforeFirstBattle);
            RunMatrix(clean, BattleRequest.Start(BattleSetup.Default, owner: 1));

            var invalid = BattleSetup.Default;
            invalid.Ai.Team = invalid.Human.Team;
            var seen = new List<BattleRequestOutcome>();
            RunMatrix(noisy, BattleRequest.Start(BattleSetup.Default, owner: 1), beforeTick: r =>
            {
                seen.Add(noisy.Session.LastOutcome);
                if (r == 2)
                    noisy.Request(BattleRequest.Start(OtherSetup, owner: 2));
                else if (r == 3)
                    noisy.Request(BattleRequest.End(owner: 99));
                else if (r == 4)
                    noisy.Request(BattleRequest.Restart(invalid, owner: 3));
                else if (r == 7)
                    noisy.Request(BattleRequest.Despawn(new CombatantId { Value = ReplayWorld.NeverIssuedId }));
            });

            Assert.That(seen, Is.EqualTo(new[]
            {
                BattleRequestOutcome.None, BattleRequestOutcome.Started, BattleRequestOutcome.AlreadyActive, BattleRequestOutcome.NotOwner,
                BattleRequestOutcome.InvalidSetup, BattleRequestOutcome.InvalidSetup, BattleRequestOutcome.Despawned,
                BattleRequestOutcome.UnknownCombatant,
            }));
            Assert.That(noisy.RawLog, Is.EqualTo(clean.RawLog), "Rejections touch no combatant, ID, queue or tick.");
        }

        [Test]
        public void LateEndOfThePreviousSceneDoesNotDisturbTheReplayOfTheNextBattle()
        {
            var w = NewWorld("scene switch");
            w.Step((int)IdleTicksBeforeFirstBattle);
            RunMatrix(w, BattleRequest.Restart(BattleSetup.Default, owner: 1));
            var first = w.Session;

            // Async scene switch: the next scene's bootstrap restarts before the previous one sends its owned End.
            RunMatrix(w, BattleRequest.Restart(BattleSetup.Default, owner: 2), beforeTick: r =>
            {
                if (r == 1)
                    w.Request(BattleRequest.End(owner: 1));
                if (r == 2)
                    Assert.That(w.Session.LastOutcome, Is.EqualTo(BattleRequestOutcome.NotOwner));
            });

            var second = w.Session;
            Assert.That((second.State, second.Owner, second.BattleNumber), Is.EqualTo((BattleState.Active, 2u, 2u)));
            Assert.That(second.BattleStartTick, Is.EqualTo(first.BattleStartTick + MatrixLength));
            Assert.That(w.NormalizedLog(2), Is.EqualTo(w.NormalizedLog(1)));
            Assert.That(w.BattleRecords(2)[0].Results.Select(r => r.Label).Where(l => l != "H" && l != "A"), Is.Empty,
                "Battle 1's pending command for relative tick 9 (= battle 2's tick 1) was dropped by the restart.");
            AssertTimeline(w);
        }

        // ---- 7. isolated Worlds ----

        [Test]
        public void IsolatedWorldsRunTheSameBattleIndependently()
        {
            var defaultWorld = World.DefaultGameObjectInjectionWorld;
            var defaultEntities = defaultWorld != null && defaultWorld.IsCreated ? defaultWorld.EntityManager.UniversalQuery.CalculateEntityCount() : -1;
            var worldsBefore = World.All.Count;

            var a = NewWorld("isolation A");
            var b = NewWorld("isolation B");
            var solo = NewWorld("isolation solo");
            a.Step((int)IdleTicksBeforeFirstBattle);
            b.Step(10);
            solo.Step(10);
            a.Script = CommandMatrix;
            b.Script = CommandMatrix;
            a.Request(BattleRequest.Start(BattleSetup.Default));
            b.Request(BattleRequest.Start(BattleSetup.Default, owner: 7));
            for (var r = 1; r <= MatrixLength; r++)
            {
                if (r == DespawnAfter + 1)
                {
                    a.Request(BattleRequest.Despawn(a.Session.AiCombatant));
                    b.Request(BattleRequest.Despawn(b.Session.AiCombatant));
                }

                a.Step();
                b.Step();
            }

            a.Request(BattleRequest.End());
            a.Step();
            b.Step();
            RunMatrix(solo, BattleRequest.Start(BattleSetup.Default, owner: 7));
            solo.Step();

            Assert.That(a.Session.State, Is.EqualTo(BattleState.Idle));
            Assert.That(b.Session.State, Is.EqualTo(BattleState.Active), "Ending the battle in World A leaves World B's battle running.");
            Assert.That(a.LiveIds, Is.Empty);
            Assert.That(b.LiveIds, Is.EqualTo(new[] { 1u }), "Each World allocates its own IDs from 1.");
            Assert.That((a.Tick, b.Tick), Is.EqualTo((IdleTicksBeforeFirstBattle + MatrixLength + 1, 10u + MatrixLength + 1)));
            Assert.That(b.NormalizedLog(1).Take(MatrixLength), Is.EqualTo(a.NormalizedLog(1)), "Same battle at different absolute ticks.");
            Assert.That(b.RawLog, Is.EqualTo(solo.RawLog), "Interleaving with another World changes nothing in B.");

            a.Dispose();
            b.Step(2);
            Assert.That(b.Records.Count, Is.EqualTo(10 + MatrixLength + 3));
            Assert.That(b.Session.RelativeTick(b.Tick), Is.EqualTo((uint)MatrixLength + 3), "World B keeps running after World A is disposed.");
            AssertTimeline(b);

            Assert.That(World.DefaultGameObjectInjectionWorld, Is.SameAs(defaultWorld));
            if (defaultWorld != null && defaultWorld.IsCreated)
            {
                Assert.That(defaultWorld.EntityManager.UniversalQuery.CalculateEntityCount(), Is.EqualTo(defaultEntities), "The default world is untouched.");
                Assert.That(m_Worlds.Select(w => w.World), Has.None.SameAs(defaultWorld));
            }

            Assert.That(World.All.Count, Is.EqualTo(worldsBefore + 2), "Disposing World A removed only World A.");
        }
    }
}
