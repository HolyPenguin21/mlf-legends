using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace MLFLegends.Tests.EditMode
{
    // Stage 01 harness (#7), now measuring Stage 02.2 runtime (#31 movement, #33 facing), with 2, 100 and 400 combatants that exist at the same time: battle start/end through
    // the real lifecycle, 1000 fixed ticks with and without commands at 1, 4 and 10 ticks per render frame, managed and
    // native memory. One warm-up pass, then Repetitions measured passes, all in one World per roster size. Every simulated
    // render frame is followed by a real editor frame (yield, untimed): main-thread Temp memory is only released at the end
    // of a frame, as in the game, which a synchronous loop never reaches. Results go to TestResults/performance/*.tsv (see
    // Docs/PERFORMANCE_BASELINE.md); the tests assert correctness only, never timings.
    [Category("Performance")]
    public class CombatBaselineTests
    {
        const int MeasuredTicks = 1000;
        const int Repetitions = 3;
        const int RequestCycles = 10;
        const int WarmInTicks = 40;
        static readonly int[] s_Pacings = { 1, 4, 10 };

        CombatBenchmarkWorld m_Bench;

        [TearDown]
        public void TearDown()
        {
            // A failing coroutine is not disposed by the runner: release its World here.
            m_Bench?.Dispose();
            m_Bench = null;
        }

        /// <summary>Two equal teams through the lifecycle's own roster contract: 2 = the default 1v1 duel.</summary>
        static BattleSetup Roster(int combatants)
        {
            var setup = BattleSetup.Default;
            setup.HumanAllies = (ushort)(combatants / 2 - 1);
            setup.AiAllies = (ushort)(combatants / 2 - 1);
            Assert.That(setup.CombatantCount, Is.EqualTo(combatants));
            return setup;
        }

        // ---- correctness of what is measured ----

        sealed class Checks
        {
            readonly CombatBenchmarkWorld m_Bench;
            readonly int m_Combatants;
            readonly HashSet<uint> m_Issued = new HashSet<uint>();
            uint m_LastTick;

            public Checks(CombatBenchmarkWorld bench, int combatants)
            {
                m_Bench = bench;
                m_Combatants = combatants;
            }

            public int Battles { get; private set; }

            /// <summary>A started roster: exactly N fresh, consecutive IDs (never issued before in this World), N/2 per team.</summary>
            public void Started()
            {
                var session = m_Bench.Session;
                Assert.That(session.LastOutcome, Is.EqualTo(BattleRequestOutcome.Started));
                Assert.That(session.State, Is.EqualTo(BattleState.Active));
                using var ids = m_Bench.Teams.ToComponentDataArray<CombatantId>(Allocator.Temp);
                using var teams = m_Bench.Teams.ToComponentDataArray<CombatTeam>(Allocator.Temp);
                using var entities = m_Bench.Teams.ToEntityArray(Allocator.Temp);
                Assert.That(ids.Length, Is.EqualTo(m_Combatants));
                var first = session.HumanCombatant.Value;
                var onHumanTeam = 0;
                for (var i = 0; i < ids.Length; i++)
                {
                    var id = ids[i].Value;
                    Assert.That(id >= first && id < first + m_Combatants, Is.True, $"ID {id} outside the reserved range.");
                    Assert.That(m_Issued.Add(id), Is.True, $"ID {id} reused.");
                    Assert.That(m_Bench.Manager.HasComponent<CombatMotion>(entities[i]), Is.True, $"ID {id} has no motion state.");
                    var motion = m_Bench.Manager.GetComponentData<CombatMotion>(entities[i]);
                    Assert.That((motion.Grounded, motion.VerticalVelocity, motion.GroundHeight),
                        Is.EqualTo((true, 0f, m_Bench.Manager.GetComponentData<CombatPosition>(entities[i]).Value.y)));
                    if (teams[i].Value == 1)
                        onHumanTeam++;
                }

                Assert.That(onHumanTeam, Is.EqualTo(m_Combatants / 2));
                Battles++;
            }

            public void Ended()
            {
                Assert.That(m_Bench.Session.State, Is.EqualTo(BattleState.Idle));
                Assert.That(m_Bench.CombatantCount, Is.Zero, "No combatant survives the end.");
            }

            public void TicksAdvanced(int ticks)
            {
                var tick = m_Bench.Tick;
                Assert.That(tick, Is.EqualTo(m_LastTick + (uint)ticks), "Every requested tick ran exactly once.");
                m_LastTick = tick;
            }

            /// <summary>The last tick gave every combatant its command (or the implicit None) for that tick.</summary>
            public void Applied(ProducerLoad load)
            {
                var tick = m_Bench.Tick;
                using var applied = m_Bench.Applied.ToComponentDataArray<AppliedCombatCommand>(Allocator.Temp);
                Assert.That(applied.Length, Is.EqualTo(m_Combatants));
                for (var i = 0; i < applied.Length; i++)
                {
                    Assert.That(applied[i].Command.Tick, Is.EqualTo(tick));
                    Assert.That(applied[i].WasSubmitted, Is.EqualTo(load != ProducerLoad.None));
                    if (load != ProducerLoad.None)
                        Assert.That(applied[i].Command.MoveX, Is.EqualTo(BenchmarkCommandProducer.Move(applied[i].Command.Combatant.Value, tick)));
                }

                m_Bench.CountOutcomes(out var accepted, out var duplicate, out var deferred, out var other);
                var n = load == ProducerLoad.None ? 0 : m_Combatants;
                Assert.That(accepted, Is.EqualTo(n));
                Assert.That(duplicate, Is.EqualTo(load == ProducerLoad.CurrentTickAndLead ? n : 0));
                Assert.That(deferred, Is.EqualTo(load == ProducerLoad.CurrentTickAndLead ? n : 0));
                Assert.That(other, Is.Zero, "No stale, unknown or rejected command.");
                Assert.That(m_Bench.PendingCount,
                    Is.EqualTo(load == ProducerLoad.CurrentTickAndLead ? n * (int)CombatCommandQueue.MaxLeadTicks : 0));
            }
        }

        static readonly ComponentType[] s_CombatantComponents =
        {
            typeof(CombatantId), typeof(CombatTeam), typeof(CombatPosition), typeof(CombatFacing), typeof(InCombat),
            typeof(AppliedCombatCommand), typeof(CombatMotion),
        };

        /// <summary>A combatant is the lifecycle's unmanaged archetype only: no GameObject, MonoBehaviour or physics/animation object.</summary>
        static void AssertLightCombatants(CombatBenchmarkWorld bench, int[] sceneObjectsBefore)
        {
            using var entities = bench.Combatants.ToEntityArray(Allocator.Temp);
            var archetype = bench.Manager.GetChunk(entities[0]).Archetype;
            foreach (var entity in entities)
                Assert.That(bench.Manager.GetChunk(entity).Archetype, Is.EqualTo(archetype), "All combatants share one archetype.");

            using var types = archetype.GetComponentTypes(Allocator.Temp);
            var allowed = new HashSet<TypeIndex> { ComponentType.ReadWrite<Simulate>().TypeIndex };
            foreach (var type in s_CombatantComponents)
            {
                allowed.Add(type.TypeIndex);
                var found = false;
                foreach (var actual in types)
                    found |= actual.TypeIndex == type.TypeIndex;
                Assert.That(found, Is.True, $"Required combatant component missing: {type}.");
            }
            var unexpected = new List<string>();
            foreach (var type in types)
            {
                if (type.IsManagedComponent || !allowed.Contains(type.TypeIndex))
                    unexpected.Add(type.ToString());
            }

            Assert.That(unexpected, Is.Empty, "Combatant components besides the lifecycle archetype (+ Entities' Simulate).");
            Assert.That(SceneObjectCounts(), Is.EqualTo(sceneObjectsBefore), "No GameObject, Rigidbody, Animator or NavMeshAgent was created.");
        }

        // Untimed: a future harness edit must not silently measure Stage 02 without its facing and motion owners.
        static void AssertStateOwners(CombatBenchmarkWorld bench)
        {
            Assert.That(bench.World.GetExistingSystem<CombatFacingSystem>(), Is.Not.EqualTo(SystemHandle.Null));
            Assert.That(bench.World.GetExistingSystem<CombatMovementSystem>(), Is.Not.EqualTo(SystemHandle.Null));
            ReplayWorld.AssertStateOwners(bench.World);
            using var systems = bench.World.GetExistingSystemManaged<CombatStateUpdateSystemGroup>().GetAllSystems(Allocator.Temp);
            Assert.That(systems.Length, Is.EqualTo(2), "Stage 02.2 State contains exactly the facing and movement owners.");
        }

        static int[] SceneObjectCounts() => new[]
        {
            Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
            Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
            Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
            Object.FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
            Object.FindObjectsByType<NavMeshAgent>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
        };

        // ---- measurement helpers (everything outside the Stopwatch pair is untimed) ----

        /// <summary>Managed allocations and native growth of a series, summed over its samples.</summary>
        struct Accumulated
        {
            public int GcAllocations;
            public long NativeDelta;
        }

        static BenchmarkRow Row(string scenario, int combatants, int rep, int ticksPerFrame, BenchmarkSamples samples,
            in Accumulated memory, string note)
        {
            var stats = samples.Stats();
            return new BenchmarkRow
            {
                Scenario = scenario,
                Combatants = combatants,
                Repetition = rep,
                TicksPerFrame = ticksPerFrame,
                PerSample = stats,
                MeanPerTick = stats.Mean / Math.Max(1, ticksPerFrame),
                GcAllocations = memory.GcAllocations,
                NativeDeltaBytes = memory.NativeDelta,
                Note = note,
            };
        }

        /// <summary>A row whose samples are byte counts, not Stopwatch ticks (mean/median/p95/min/max in bytes).</summary>
        static BenchmarkRow BytesRow(string scenario, int combatants, int rep, int ticksPerFrame, BenchmarkSamples bytes,
            long drift, string note)
        {
            var stats = bytes.RawStats();
            return new BenchmarkRow
            {
                Scenario = scenario,
                Combatants = combatants,
                Repetition = rep,
                TicksPerFrame = ticksPerFrame,
                PerSample = stats,
                MeanPerTick = stats.Mean,
                NativeDeltaBytes = drift,
                Note = note,
            };
        }

        /// <summary>
        /// Times one lifecycle update that applies the already submitted request(s) (<paramref name="frameTicks"/> = 0), or
        /// one render frame of the real fixed-step group with <paramref name="frameTicks"/> ticks.
        /// </summary>
        static void Time(CombatBenchmarkWorld bench, int frameTicks, BenchmarkSamples samples, ref Accumulated memory)
        {
            if (frameTicks > 0)
                bench.PrepareFrame(frameTicks);
            var gc = GcAllocationScope.Begin();
            var native = Profiler.GetTotalAllocatedMemoryLong();
            var start = Stopwatch.GetTimestamp();
            if (frameTicks > 0)
                bench.RunFrame();
            else
                bench.ApplyRequests();
            var end = Stopwatch.GetTimestamp();
            memory.NativeDelta += Profiler.GetTotalAllocatedMemoryLong() - native;
            memory.GcAllocations += gc.End();
            samples.Add(end - start);
        }

        static string LoadName(ProducerLoad load) => load switch
        {
            ProducerLoad.None => "idle",
            ProducerLoad.CurrentTick => "cmd",
            _ => "cmd-lead32",
        };

        /// <summary>A fresh battle with the producer at <paramref name="load"/>; warm-in ticks fill the lead load's pending store (N x 32).</summary>
        static void BeginLoad(CombatBenchmarkWorld bench, BattleSetup setup, Checks checks, ProducerLoad load)
        {
            bench.Submit(BattleRequest.Restart(setup));
            bench.ApplyRequests();
            checks.Started();
            bench.Producer.Load = load;
            bench.Frames(WarmInTicks, 1);
            checks.TicksAdvanced(WarmInTicks);
            checks.Applied(load);
        }

        // ---- one pass: request cycles, then every tick load and pacing ----

        IEnumerator RunPass(BattleSetup setup, Checks checks, int combatants, int rep, BenchmarkReport report, int[] sceneObjectsBefore)
        {
            var bench = m_Bench;
            var start = new BenchmarkSamples(RequestCycles);
            var firstTick = new BenchmarkSamples(RequestCycles);
            var end = new BenchmarkSamples(RequestCycles);
            var restart = new BenchmarkSamples(RequestCycles);
            Accumulated startMemory = default, firstTickMemory = default, endMemory = default, restartMemory = default;

            // Battle start (queue reset + spawn of N), its first tick (adds AppliedCombatCommand to N), battle end (destroy N).
            GC.Collect();
            for (var cycle = 0; cycle < RequestCycles; cycle++)
            {
                bench.Submit(BattleRequest.Start(setup));
                Time(bench, 0, start, ref startMemory);
                checks.Started();
                Time(bench, 1, firstTick, ref firstTickMemory);
                checks.TicksAdvanced(1);
                checks.Applied(ProducerLoad.None);
                if (cycle == 0)
                    AssertLightCombatants(bench, sceneObjectsBefore);
                bench.Submit(BattleRequest.End());
                Time(bench, 0, end, ref endMemory);
                checks.Ended();
                yield return null;
            }

            // Restart of a running battle: destroy N + spawn N in one request.
            bench.Submit(BattleRequest.Start(setup));
            bench.ApplyRequests();
            checks.Started();
            for (var cycle = 0; cycle < RequestCycles; cycle++)
            {
                bench.Frames(1, 1);
                checks.TicksAdvanced(1);
                bench.Submit(BattleRequest.Restart(setup));
                Time(bench, 0, restart, ref restartMemory);
                checks.Started();
                yield return null;
            }

            if (report != null)
            {
                report.Rows.Add(Row("start", combatants, rep, 0, start, startMemory, "lifecycle update applying Start: queue reset + spawn"));
                report.Rows.Add(Row("first-tick", combatants, rep, 1, firstTick, firstTickMemory, "first fixed step: adds AppliedCombatCommand to every combatant"));
                report.Rows.Add(Row("end", combatants, rep, 0, end, endMemory, "lifecycle update applying End: destroy + queue reset"));
                report.Rows.Add(Row("restart", combatants, rep, 0, restart, restartMemory, "lifecycle update applying Restart: destroy + reset + spawn"));
            }

            foreach (var load in new[] { ProducerLoad.None, ProducerLoad.CurrentTick, ProducerLoad.CurrentTickAndLead })
            {
                foreach (var ticksPerFrame in s_Pacings)
                {
                    if (load == ProducerLoad.CurrentTickAndLead && ticksPerFrame == 4)
                        continue;
                    var loop = RunTickLoad(setup, checks, combatants, rep, report, load, ticksPerFrame);
                    while (loop.MoveNext())
                        yield return loop.Current;
                }
            }

            bench.Submit(BattleRequest.End());
            bench.ApplyRequests();
            checks.Ended();
        }

        IEnumerator RunTickLoad(BattleSetup setup, Checks checks, int combatants, int rep, BenchmarkReport report,
            ProducerLoad load, int ticksPerFrame)
        {
            var bench = m_Bench;
            var frames = MeasuredTicks / ticksPerFrame;
            Assert.That(frames * ticksPerFrame, Is.EqualTo(MeasuredTicks));
            var step = new BenchmarkSamples(frames);
            var producer = new BenchmarkSamples(frames);
            var runtime = new BenchmarkSamples(frames);
            var producerSystem = bench.Producer;
            var gcAllocations = 0;

            BeginLoad(bench, setup, checks, load);
            GC.Collect();
            yield return null;
            var nativeBefore = Profiler.GetTotalAllocatedMemoryLong();
            for (var frame = 0; frame < frames; frame++)
            {
                bench.PrepareFrame(ticksPerFrame);
                var producerBefore = producerSystem.ElapsedTicks;
                var gc = GcAllocationScope.Begin();
                var t0 = Stopwatch.GetTimestamp();
                bench.RunFrame();
                var t1 = Stopwatch.GetTimestamp();
                gcAllocations += gc.End();
                var produced = producerSystem.ElapsedTicks - producerBefore;
                step.Add(t1 - t0);
                producer.Add(produced);
                runtime.Add(t1 - t0 - produced);
                yield return null;
            }

            var memory = new Accumulated { NativeDelta = Profiler.GetTotalAllocatedMemoryLong() - nativeBefore, GcAllocations = gcAllocations };
            producerSystem.Load = ProducerLoad.None;
            checks.TicksAdvanced(MeasuredTicks);
            checks.Applied(load);

            if (report == null)
                yield break;
            var chunks = bench.CombatantChunkCount;
            var name = $"{LoadName(load)}-k{ticksPerFrame}";
            var note = $"{frames} frames x {ticksPerFrame} ticks, editor frame between frames; chunks={chunks} ({chunks * CombatBenchmarkWorld.ChunkBytes} B); "
                + $"queue buffers capacity={bench.QueueBufferBytes} B";
            report.Rows.Add(Row(name, combatants, rep, ticksPerFrame, step, memory, "full fixed-step group per frame (incl. test producer); " + note));
            if (load != ProducerLoad.None)
            {
                report.Rows.Add(Row(name + ":runtime", combatants, rep, ticksPerFrame, runtime, memory, "frame minus the test producer's self-time"));
                report.Rows.Add(Row(name + ":producer", combatants, rep, ticksPerFrame, producer, default, "test producer self-time (submission), managed SystemBase"));
            }
        }

        // ---- tests ----

        [UnityTest]
        [Timeout(1800000)]
        public IEnumerator Baseline([Values(2, 100, 400)] int combatants)
        {
            var environment = BenchmarkEnvironment.Capture();
            var sceneObjectsBefore = SceneObjectCounts();
            var setup = Roster(combatants);
            var report = new BenchmarkReport();
            m_Bench = new CombatBenchmarkWorld($"MLF Legends baseline {combatants}");
            AssertStateOwners(m_Bench);
            var checks = new Checks(m_Bench, combatants);

            for (var rep = 0; rep <= Repetitions; rep++)
            {
                // Repetition 0 is the warm-up (JIT, Burst, chunk and buffer growth): not recorded.
                var pass = RunPass(setup, checks, combatants, rep, rep == 0 ? null : report, sceneObjectsBefore);
                while (pass.MoveNext())
                    yield return pass.Current;
                // Session, clock, queue and the WorldTime singleton that the first World.SetTime creates.
                Assert.That(m_Bench.Manager.UniversalQuery.CalculateEntityCount(), Is.EqualTo(4),
                    $"After the End of repetition {rep} only the World's own entities remain.");
            }

            Assert.That(m_Bench.Manager.CreateEntityQuery(typeof(SimulationClock)).CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(m_Bench.Manager.CreateEntityQuery(typeof(CombatCommandQueue)).CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(m_Bench.Manager.CreateEntityQuery(typeof(BattleSession)).CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(checks.Battles, Is.EqualTo((Repetitions + 1) * (2 * RequestCycles + 1 + 8)));

            var path = report.Write($"stage02-33-{BenchmarkEnvironment.Label}-n{combatants}", environment);
            Debug.Log($"[Stage 02.2 #33 baseline] {combatants} combatants: {report.Rows.Count} rows written to {path}");
        }

        /// <summary>
        /// Native memory per render frame: growth of Unity's total allocated native memory (process-wide) at its peak after any
        /// tick of the frame (publication-phase probe, enabled only here) and at the end of the frame, with an editor frame
        /// between frames as in the game. The last series repeats the full pending store without frame ends (a synchronous loop)
        /// to show that its per-tick Temp scratch is only returned at the end of a frame.
        /// </summary>
        [UnityTest]
        [Timeout(1800000)]
        public IEnumerator FrameNativeMemory([Values(2, 100, 400)] int combatants)
        {
            const int frames = 100;
            var environment = BenchmarkEnvironment.Capture();
            var setup = Roster(combatants);
            var report = new BenchmarkReport();
            m_Bench = new CombatBenchmarkWorld($"MLF Legends frame memory {combatants}");
            AssertStateOwners(m_Bench);
            var checks = new Checks(m_Bench, combatants);
            var series = new[]
            {
                (ProducerLoad.CurrentTick, 1), (ProducerLoad.CurrentTick, 10),
                (ProducerLoad.CurrentTickAndLead, 1), (ProducerLoad.CurrentTickAndLead, 18), // 18: the most catch-up ticks below MaximumDeltaTime
            };

            for (var rep = 0; rep <= Repetitions; rep++)
            {
                foreach (var (load, ticksPerFrame) in series)
                {
                    BeginLoad(m_Bench, setup, checks, load);
                    yield return null;
                    var peak = new BenchmarkSamples(frames);
                    var held = new BenchmarkSamples(frames);
                    var startOfRun = Profiler.GetTotalAllocatedMemoryLong();
                    m_Bench.Probe.Enabled = true;
                    for (var frame = 0; frame < frames; frame++)
                    {
                        m_Bench.PrepareFrame(ticksPerFrame);
                        m_Bench.Probe.ResetFrame();
                        var before = m_Bench.Probe.Peak;
                        m_Bench.RunFrame();
                        Assert.That(m_Bench.Probe.Ticks, Is.EqualTo(ticksPerFrame));
                        peak.Add(m_Bench.Probe.Peak - before);
                        held.Add(Profiler.GetTotalAllocatedMemoryLong() - before);
                        yield return null;
                    }

                    m_Bench.Probe.Enabled = false;
                    var drift = Profiler.GetTotalAllocatedMemoryLong() - startOfRun;
                    checks.TicksAdvanced(frames * ticksPerFrame);
                    checks.Applied(load);
                    if (rep == 0)
                        continue; // warm-up
                    var name = $"{LoadName(load)}-k{ticksPerFrame}";
                    report.Rows.Add(BytesRow($"frame-native-peak:{name}", combatants, rep, ticksPerFrame, peak, drift,
                        "bytes: max over the ticks of a frame of (total allocated - before the frame); nativeDelta = drift over the series"));
                    report.Rows.Add(BytesRow($"frame-native-end:{name}", combatants, rep, ticksPerFrame, held, drift,
                        "bytes: total allocated after the frame - before it"));
                }

                // The same full pending store, 100 ticks without an editor frame in between.
                BeginLoad(m_Bench, setup, checks, ProducerLoad.CurrentTickAndLead);
                yield return null;
                var withoutFrameEnd = Profiler.GetTotalAllocatedMemoryLong();
                m_Bench.Frames(frames, 1);
                var growth = Profiler.GetTotalAllocatedMemoryLong() - withoutFrameEnd;
                yield return null;
                var afterFrameEnd = Profiler.GetTotalAllocatedMemoryLong() - withoutFrameEnd;
                checks.TicksAdvanced(frames);
                checks.Applied(ProducerLoad.CurrentTickAndLead);
                if (rep > 0)
                {
                    var single = new BenchmarkSamples(1);
                    single.Add(growth);
                    report.Rows.Add(BytesRow("no-frame-end:cmd-lead32-k1x100", combatants, rep, 1, single, afterFrameEnd,
                        "bytes: growth over 100 ticks run in one editor frame; nativeDelta = the same after the next editor frame"));
                }
            }

            m_Bench.Producer.Load = ProducerLoad.None;
            m_Bench.Submit(BattleRequest.End());
            m_Bench.ApplyRequests();
            checks.Ended();

            var path = report.Write($"stage02-33-{BenchmarkEnvironment.Label}-memory-n{combatants}", environment);
            Debug.Log($"[Stage 02.2 #33 baseline] frame memory, {combatants} combatants: written to {path}");
        }
    }
}
