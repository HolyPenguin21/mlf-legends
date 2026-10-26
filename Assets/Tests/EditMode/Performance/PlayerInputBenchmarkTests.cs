using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEditor;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace MLFLegends.Tests.EditMode
{
    // Stage 02.3 (#22) cost of the real player input producer, in the existing benchmark World (ReplayWorld.RuntimeSystems,
    // BenchmarkCommandProducer at None) plus the real PlayerInputCommandSystem: per render frame with 1 and 10 ticks, at
    // 2 and 400 combatants, the producer detached (inert, = the existing idle load) vs attached to a capture whose virtual
    // keyboard holds D (one Player command per tick; the hold is re-reported untimed before each frame). One warm-up pass, then Repetitions measured passes; every frame is
    // followed by a real editor frame (untimed). Results: TestResults/performance/player-input-<burst>.tsv; asserts
    // correctness only, never timings.
    [Category("Performance")]
    public class PlayerInputBenchmarkTests : InputTestFixture
    {
        const int MeasuredTicks = 1000;
        const int Repetitions = 3;
        const int WarmInTicks = 40;
        static readonly int[] s_Pacings = { 1, 10 };

        CombatBenchmarkWorld m_Bench;
        PlayerInputCapture m_Capture;

        public override void TearDown()
        {
            m_Capture?.Dispose();
            m_Capture = null;
            m_Bench?.Dispose();
            m_Bench = null;
            base.TearDown();
        }

        [UnityTest]
        public IEnumerator PlayerInputProducerCostPerTick()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(PlayerInputProducerTests.ActionsPath);
            Assert.That(asset, Is.Not.Null);
            var keyboard = InputSystem.AddDevice<Keyboard>();
            var report = new BenchmarkReport();

            foreach (var combatants in new[] { 2, 400 })
            {
                m_Bench = new CombatBenchmarkWorld($"Player input benchmark {combatants}");
                var producer = m_Bench.World.CreateSystemManaged<PlayerInputCommandSystem>();
                var collection = m_Bench.World.GetExistingSystemManaged<CombatCommandCollectionSystemGroup>();
                collection.AddSystemToUpdateList(producer);
                collection.SortSystems();
                m_Capture = new PlayerInputCapture(asset);
                // Pressed after the capture is enabled: its Move action sees the change in this update.
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.D));
                InputSystem.Update();

                var setup = BattleSetup.Default;
                setup.HumanAllies = setup.AiAllies = (ushort)(combatants / 2 - 1);
                m_Bench.Submit(BattleRequest.Start(setup));
                m_Bench.ApplyRequests();
                Assert.That(m_Bench.CombatantCount, Is.EqualTo(combatants));
                m_Bench.Frames(WarmInTicks, 1);

                for (var rep = 0; rep <= Repetitions; rep++)
                {
                    foreach (var attached in new[] { false, true })
                    {
                        if (attached)
                            producer.Attach(m_Capture);
                        else
                            producer.Detach(m_Capture);

                        foreach (var ticksPerFrame in s_Pacings)
                        {
                            var frames = MeasuredTicks / ticksPerFrame;
                            var samples = new BenchmarkSamples(frames);
                            var gcAllocations = 0;
                            long native = 0;
                            System.GC.Collect();
                            for (var frame = 0; frame < frames; frame++)
                            {
                                // Untimed, like PreUpdate in the game: the fixture's virtual device does not keep its state
                                // across EditMode editor frames, so the hold is re-reported before every measured frame.
                                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.D));
                                InputSystem.Update();
                                m_Bench.PrepareFrame(ticksPerFrame);
                                var gc = GcAllocationScope.Begin();
                                var nativeBefore = Profiler.GetTotalAllocatedMemoryLong();
                                var start = Stopwatch.GetTimestamp();
                                m_Bench.RunFrame();
                                var end = Stopwatch.GetTimestamp();
                                native += Profiler.GetTotalAllocatedMemoryLong() - nativeBefore;
                                gcAllocations += gc.End();
                                samples.Add(end - start);
                                yield return null; // a real frame end: Temp memory reclaimed as in the game
                            }

                            AssertLastTick(combatants, attached);
                            if (rep == 0)
                                continue;
                            var stats = samples.Stats();
                            report.Rows.Add(new BenchmarkRow
                            {
                                Scenario = attached ? "player-input-held" : "player-input-detached",
                                Combatants = combatants,
                                Repetition = rep,
                                TicksPerFrame = ticksPerFrame,
                                PerSample = stats,
                                MeanPerTick = stats.Mean / ticksPerFrame,
                                GcAllocations = gcAllocations,
                                NativeDeltaBytes = native,
                                Note = attached
                                    ? "real PlayerInputCommandSystem + capture, virtual keyboard holds D: 1 Player command/tick"
                                    : "same World, producer detached (inert)",
                            });
                        }
                    }
                }

                producer.Detach(m_Capture);
                m_Capture.Dispose();
                m_Capture = null;
                m_Bench.Dispose();
                m_Bench = null;
                yield return null;
            }

            var environment = BenchmarkEnvironment.Capture();
            environment.Add(new KeyValuePair<string, string>("Scenario scope",
                "Stage 02.3 (#22): ReplayWorld.RuntimeSystems + real PlayerInputCommandSystem; InputTestFixture virtual keyboard"));
            var path = report.Write($"player-input-{BenchmarkEnvironment.Label}", environment);
            Debug.Log($"Player input benchmark written to {path}");
        }

        void AssertLastTick(int combatants, bool attached)
        {
            var human = m_Bench.Session.HumanCombatant.Value;
            using var ids = m_Bench.Applied.ToComponentDataArray<CombatantId>(Allocator.Temp);
            using var applied = m_Bench.Applied.ToComponentDataArray<AppliedCombatCommand>(Allocator.Temp);
            Assert.That(applied.Length, Is.EqualTo(combatants));
            for (var i = 0; i < applied.Length; i++)
            {
                Assert.That(applied[i].Command.Tick, Is.EqualTo(m_Bench.Tick), "Every combatant is simulated every tick.");
                var driven = attached && ids[i].Value == human;
                Assert.That(applied[i].WasSubmitted, Is.EqualTo(driven));
                if (driven)
                    Assert.That((applied[i].Command.Source, applied[i].Command.MoveX), Is.EqualTo((CommandSource.Player, (sbyte)1)));
            }

            m_Bench.CountOutcomes(out var accepted, out _, out _, out var other);
            Assert.That((accepted, other), Is.EqualTo((attached ? 1 : 0, 0)));
        }
    }
}
