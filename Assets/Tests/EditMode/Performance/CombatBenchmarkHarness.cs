using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using MLFLegends.Combat;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Core;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.Profiling;

namespace MLFLegends.Tests.EditMode
{
    // Stage 01 profiling harness (#7), extended to Stage 02.1 movement (#31) and Stage 02.2 facing (#33). Test-only: it adds no runtime system and copies no runtime
    // rule. The World is built from the same runtime system list as the replay tests (ReplayWorld.RuntimeSystems), ordered
    // by the systems' own attributes, so the clock, lifecycle, processor, facing, movement and fixed-step order
    // are those of the default world. Combatants exist only through BattleRequest and the real BattleLifecycleSystem.
    // A timed region contains only runtime update calls and Stopwatch reads: no strings, LINQ, Debug.Log, replay
    // recording or allocations of the harness. Everything else (requests, SetTime, checks, reports) happens outside it.

    /// <summary>What the benchmark producer submits on every tick, from the command-collection phase.</summary>
    public enum ProducerLoad : byte
    {
        /// <summary>No command: every combatant gets the implicit None.</summary>
        None,

        /// <summary>One command per in-combat combatant for the current tick.</summary>
        CurrentTick,

        /// <summary>As <see cref="CurrentTick"/>, plus an equal one <see cref="CombatCommandQueue.MaxLeadTicks"/> ahead (full pending store).</summary>
        CurrentTickAndLead,
    }

    /// <summary>An isolated World with the real runtime systems and two test systems, driven frame by frame.</summary>
    public sealed class CombatBenchmarkWorld : IDisposable
    {
        /// <summary>Entities chunk size (Unity.Entities.Chunk.kChunkSize, internal): 16 KiB.</summary>
        public const int ChunkBytes = 16 * 1024;

        public readonly World World;
        public readonly FixedStepSimulationSystemGroup FixedStep;
        public readonly SystemHandle Lifecycle;
        public readonly BenchmarkCommandProducer Producer;
        public readonly NativeMemoryProbe Probe;

        // Built once, released with the World: no query is created inside a measured loop.
        readonly EntityQuery m_Combatants;
        readonly EntityQuery m_Teams;
        readonly EntityQuery m_Applied;
        readonly EntityQuery m_Session;
        readonly EntityQuery m_Clock;
        readonly EntityQuery m_Queue;
        uint m_Frame;

        public CombatBenchmarkWorld(string name)
        {
            World = new World(name);
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(World, ReplayWorld.RuntimeSystems);
            FixedStep = World.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
            Lifecycle = World.GetExistingSystem<BattleLifecycleSystem>();
            Producer = AddToPhase<BenchmarkCommandProducer>(typeof(CombatCommandCollectionSystemGroup));
            Probe = AddToPhase<NativeMemoryProbe>(typeof(CombatPublicationSystemGroup));
            Probe.Enabled = false;

            var manager = World.EntityManager;
            m_Combatants = manager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { typeof(CombatantId) },
                Options = EntityQueryOptions.IgnoreComponentEnabledState,
            });
            m_Teams = manager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { typeof(CombatantId), typeof(CombatTeam) },
                Options = EntityQueryOptions.IgnoreComponentEnabledState,
            });
            m_Applied = manager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { typeof(CombatantId), typeof(AppliedCombatCommand) },
                Options = EntityQueryOptions.IgnoreComponentEnabledState,
            });
            m_Session = manager.CreateEntityQuery(typeof(BattleSession));
            m_Clock = manager.CreateEntityQuery(typeof(SimulationClock));
            m_Queue = manager.CreateEntityQuery(typeof(CombatCommandQueue));
        }

        public void Dispose()
        {
            if (World.IsCreated)
                World.Dispose();
        }

        public EntityManager Manager => World.EntityManager;

        public uint Tick => m_Clock.GetSingleton<SimulationClock>().Tick;

        public BattleSession Session => m_Session.GetSingleton<BattleSession>();

        public Entity QueueEntity => m_Queue.GetSingletonEntity();

        /// <summary>Every combatant, defeated ones included.</summary>
        public EntityQuery Combatants => m_Combatants;

        /// <summary>Combatants with <see cref="CombatTeam"/> (all of them; for reading IDs and teams in the same order).</summary>
        public EntityQuery Teams => m_Teams;

        /// <summary>Combatants that already have <see cref="AppliedCombatCommand"/> (after their first tick).</summary>
        public EntityQuery Applied => m_Applied;

        public int CombatantCount => m_Combatants.CalculateEntityCount();

        public int CombatantChunkCount => m_Combatants.CalculateChunkCount();

        T AddToPhase<T>(Type phase) where T : SystemBase, new()
        {
            var system = World.CreateSystemManaged<T>();
            var group = (ComponentSystemGroup)World.GetExistingSystemManaged(phase);
            group.AddSystemToUpdateList(system);
            group.SortSystems();
            return system;
        }

        // ---- frames: Prepare (harness, untimed) then Run (the real fixed-step group, timed by the caller) ----

        /// <summary>
        /// Sets the time of the next render frame so that it runs <paramref name="ticks"/> fixed steps, half a tick past the
        /// last one (as the clock and replay tests do). At most 18, so World.MaximumDeltaTime never drops a tick.
        /// </summary>
        public void PrepareFrame(int ticks)
        {
            if (ticks < 1 || ticks > 18)
                throw new ArgumentOutOfRangeException(nameof(ticks));
            var dt = (double)SimulationTiming.TickDuration;
            World.SetTime(new TimeData((m_Frame + ticks - 0.5) * dt, (float)(ticks * dt)));
            m_Frame += (uint)ticks;
        }

        public void RunFrame() => FixedStep.Update();

        public void Frames(int frames, int ticksPerFrame)
        {
            for (var i = 0; i < frames; i++)
            {
                PrepareFrame(ticksPerFrame);
                RunFrame();
            }
        }

        // ---- battle requests: Submit (harness, untimed) then ApplyRequests (the lifecycle's own update, timed) ----

        public void Submit(in BattleRequest request) => BattleRequest.Submit(Manager, request);

        /// <summary>The lifecycle update alone: what the start of the next fixed step does before the clock advances.</summary>
        public void ApplyRequests() => Lifecycle.Update(World.Unmanaged);

        /// <summary>Capacity in bytes of the three command queue buffers (heap memory, InternalBufferCapacity 0).</summary>
        public long QueueBufferBytes
        {
            get
            {
                var queue = QueueEntity;
                return (long)Manager.GetBuffer<CombatCommandSubmission>(queue).Capacity * UnsafeUtility.SizeOf<CombatCommandSubmission>()
                    + (long)Manager.GetBuffer<PendingCombatCommand>(queue).Capacity * UnsafeUtility.SizeOf<PendingCombatCommand>()
                    + (long)Manager.GetBuffer<CombatCommandResult>(queue).Capacity * UnsafeUtility.SizeOf<CombatCommandResult>();
            }
        }

        public int PendingCount => Manager.GetBuffer<PendingCombatCommand>(QueueEntity).Length;

        /// <summary>Outcome counts of the last processed tick (untimed check).</summary>
        public void CountOutcomes(out int accepted, out int duplicate, out int deferred, out int other)
        {
            accepted = duplicate = deferred = other = 0;
            var results = Manager.GetBuffer<CombatCommandResult>(QueueEntity);
            for (var i = 0; i < results.Length; i++)
            {
                switch (results[i].Outcome)
                {
                    case CommandOutcome.Accepted: accepted++; break;
                    case CommandOutcome.Duplicate: duplicate++; break;
                    case CommandOutcome.Deferred: deferred++; break;
                    default: other++; break;
                }
            }
        }
    }

    /// <summary>
    /// Test producer in the command-collection phase (before the processor): appends the configured load to the single
    /// <see cref="CombatCommandSubmission"/> buffer. Times itself, so its cost can be reported apart from the runtime.
    /// </summary>
    [DisableAutoCreation]
    public partial class BenchmarkCommandProducer : SystemBase
    {
        public ProducerLoad Load;

        /// <summary>Stopwatch ticks spent in this system's updates so far.</summary>
        public long ElapsedTicks;

        public long Submitted;

        EntityQuery m_InCombat;

        protected override void OnCreate() =>
            m_InCombat = GetEntityQuery(ComponentType.ReadOnly<CombatantId>(), ComponentType.ReadOnly<InCombat>());

        /// <summary>The move of a combatant on a tick: the same for its current and its lead command, so the later one is a Duplicate.</summary>
        public static sbyte Move(uint id, uint tick) => (sbyte)((id + tick) % 3 - 1);

        protected override void OnUpdate()
        {
            if (Load == ProducerLoad.None)
                return;

            var start = Stopwatch.GetTimestamp();
            var tick = SystemAPI.GetSingleton<SimulationClock>().Tick;
            var human = SystemAPI.GetSingleton<BattleSession>().HumanCombatant;
            var buffer = SystemAPI.GetSingletonBuffer<CombatCommandSubmission>();
            using (var ids = m_InCombat.ToComponentDataArray<CombatantId>(Allocator.Temp))
            {
                for (var i = 0; i < ids.Length; i++)
                    buffer.Add(new CombatCommandSubmission(Command(ids[i], tick, human)));
                if (Load == ProducerLoad.CurrentTickAndLead)
                {
                    for (var i = 0; i < ids.Length; i++)
                        buffer.Add(new CombatCommandSubmission(Command(ids[i], tick + CombatCommandQueue.MaxLeadTicks, human)));
                }

                Submitted += Load == ProducerLoad.CurrentTickAndLead ? 2 * ids.Length : ids.Length;
            }

            ElapsedTicks += Stopwatch.GetTimestamp() - start;
        }

        static CombatCommand Command(CombatantId id, uint tick, CombatantId human) => new CombatCommand
        {
            Combatant = id,
            Tick = tick,
            MoveX = Move(id.Value, tick),
            Source = id.Value == human.Value ? CommandSource.Player : CommandSource.AI,
        };
    }

    /// <summary>
    /// Publication-phase probe, enabled only in the memory pass (never while timing): after every tick it reads Unity's
    /// total allocated native memory and keeps the peak of the frame.
    /// </summary>
    [DisableAutoCreation]
    public partial class NativeMemoryProbe : SystemBase
    {
        public long Peak;
        public int Ticks;

        public void ResetFrame()
        {
            Peak = Profiler.GetTotalAllocatedMemoryLong();
            Ticks = 0;
        }

        protected override void OnUpdate()
        {
            var now = Profiler.GetTotalAllocatedMemoryLong();
            if (now > Peak)
                Peak = now;
            Ticks++;
        }
    }

    /// <summary>Preallocated Stopwatch samples; statistics are computed after the measurement.</summary>
    public sealed class BenchmarkSamples
    {
        readonly long[] m_Ticks;
        int m_Count;

        public BenchmarkSamples(int capacity) => m_Ticks = new long[capacity];

        public int Count => m_Count;

        public void Add(long stopwatchTicks) => m_Ticks[m_Count++] = stopwatchTicks;

        public void Clear() => m_Count = 0;

        public static double Microseconds(long stopwatchTicks) => stopwatchTicks * 1e6 / Stopwatch.Frequency;

        /// <summary>Mean, median, 95th percentile (nearest rank), min and max of Stopwatch samples, in microseconds.</summary>
        public SampleStats Stats() => Stats(Microseconds(1));

        /// <summary>The same statistics of samples that are plain values (e.g. bytes), unscaled.</summary>
        public SampleStats RawStats() => Stats(1);

        SampleStats Stats(double scale)
        {
            if (m_Count == 0)
                return default;
            var sorted = new long[m_Count];
            Array.Copy(m_Ticks, sorted, m_Count);
            Array.Sort(sorted);
            double sum = 0;
            for (var i = 0; i < m_Count; i++)
                sum += sorted[i];
            var p95 = sorted[Math.Min(m_Count - 1, (int)Math.Ceiling(0.95 * m_Count) - 1)];
            var median = m_Count % 2 == 1 ? sorted[m_Count / 2] : (sorted[m_Count / 2 - 1] + sorted[m_Count / 2]) / 2.0;
            return new SampleStats
            {
                Count = m_Count,
                Mean = scale * sum / m_Count,
                Median = scale * median,
                P95 = scale * p95,
                Min = scale * sorted[0],
                Max = scale * sorted[m_Count - 1],
            };
        }
    }

    public struct SampleStats
    {
        public int Count;
        public double Mean;
        public double Median;
        public double P95;
        public double Min;
        public double Max;
    }

    /// <summary>
    /// Managed allocations of the calling thread across a region: the number of GC.Alloc profiler samples, the method of
    /// Unity's <c>Is.Not.AllocatingGCMemory</c>. Bytes are not measured: <see cref="GC.GetAllocatedBytesForCurrentThread"/>
    /// reports 0 in the Editor's Mono (<see cref="BenchmarkEnvironment.GcByteCounterWorks"/>).
    /// </summary>
    public struct GcAllocationScope
    {
        Recorder m_Recorder;

        public static GcAllocationScope Begin()
        {
            var scope = new GcAllocationScope { m_Recorder = Recorder.Get("GC.Alloc") };
            scope.m_Recorder.enabled = false;
            scope.m_Recorder.FilterToCurrentThread();
            scope.m_Recorder.enabled = true;
            return scope;
        }

        /// <summary>GC.Alloc samples since <see cref="Begin"/>.</summary>
        public int End()
        {
            m_Recorder.enabled = false;
            m_Recorder.CollectFromAllThreads();
            return m_Recorder.sampleBlockCount;
        }
    }

    /// <summary>Tells whether Burst-compiled code actually executes: the [BurstDiscard] call exists only in managed code.</summary>
    [BurstCompile(CompileSynchronously = true)]
    struct BurstProbeJob : IJob
    {
        public NativeArray<int> RanManaged;

        public void Execute()
        {
            RanManaged[0] = 0;
            MarkManaged();
        }

        [BurstDiscard]
        void MarkManaged() => RanManaged[0] = 1;
    }

    /// <summary>Machine, Editor, package and compilation settings of a run, captured outside any measurement.</summary>
    public static class BenchmarkEnvironment
    {
        static object s_Sink;

        public static bool BurstExecutes()
        {
            using var ranManaged = new NativeArray<int>(1, Allocator.TempJob);
            new BurstProbeJob { RanManaged = ranManaged }.Run();
            return ranManaged[0] == 0;
        }

        /// <summary>Whether <see cref="GC.GetAllocatedBytesForCurrentThread"/> sees a known 4 KiB allocation.</summary>
        public static bool GcByteCounterWorks()
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            s_Sink = new byte[4096];
            var counted = GC.GetAllocatedBytesForCurrentThread() - before >= 4096;
            s_Sink = null;
            return counted;
        }

        static string PackageVersion(Type type) =>
            UnityEditor.PackageManager.PackageInfo.FindForAssembly(type.Assembly)?.version ?? "unknown";

        public static string Label => BurstExecutes() ? "burst-on" : "burst-off";

        public static List<KeyValuePair<string, string>> Capture()
        {
            var collectionsChecks = false;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            collectionsChecks = true;
#endif
            return new List<KeyValuePair<string, string>>
            {
                Pair("Date (UTC)", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                Pair("CPU", $"{SystemInfo.processorType.Trim()}, {SystemInfo.processorCount} logical, {SystemInfo.processorFrequency} MHz"),
                Pair("RAM", $"{SystemInfo.systemMemorySize} MB"),
                Pair("GPU (Unity)", $"{SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType})"),
                Pair("OS", SystemInfo.operatingSystem),
                Pair("Unity", Application.unityVersion),
                Pair("Mode", $"Editor EditMode test runner, batchmode={Application.isBatchMode}"),
                Pair("Entities", PackageVersion(typeof(World))),
                Pair("Collections", PackageVersion(typeof(NativeList<int>))),
                Pair("Burst", PackageVersion(typeof(BurstCompiler))),
                Pair("Burst compilation enabled", BurstCompiler.IsEnabled.ToString()),
                Pair("Burst code executes (probe job)", BurstExecutes().ToString()),
                Pair("Burst safety checks", BurstCompiler.Options.EnableBurstSafetyChecks.ToString()),
                Pair("Collections checks (ENABLE_UNITY_COLLECTIONS_CHECKS)", collectionsChecks.ToString()),
                Pair("Job debugger", JobsUtility.JobDebuggerEnabled.ToString()),
                Pair("Job worker count", JobsUtility.JobWorkerCount.ToString(CultureInfo.InvariantCulture)),
                Pair("Managed code optimization", CompilationPipeline.codeOptimization.ToString()),
                Pair("Stopwatch", $"{Stopwatch.Frequency} Hz, high resolution {Stopwatch.IsHighResolution}"),
                Pair("GC byte counter works", GcByteCounterWorks().ToString()),
                Pair("Simulation rate", $"{SimulationTiming.TickRateHz} Hz"),
                Pair("Runtime scope", "Stage 02.2 (#33): clock + lifecycle + commands + CombatFacingSystem + CombatMovementSystem; not the historical Stage 01 baseline"),
                Pair("CombatMotion bytes per actor", UnsafeUtility.SizeOf<CombatMotion>().ToString(CultureInfo.InvariantCulture)),
            };
        }

        static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);
    }

    /// <summary>One measured series of one repetition; written as a TSV row after all measurements.</summary>
    public struct BenchmarkRow
    {
        public string Scenario;
        public int Combatants;
        public int Repetition;
        public int TicksPerFrame;

        /// <summary>Statistics per sample (a frame, a request application, ...).</summary>
        public SampleStats PerSample;

        /// <summary>Mean per simulated tick (per sample / ticks per frame); equal to PerSample.Mean for single-tick samples.</summary>
        public double MeanPerTick;

        public int GcAllocations;

        /// <summary>Change of Unity's total allocated native memory (whole process, all threads) across the series.</summary>
        public long NativeDeltaBytes;
        public string Note;
    }

    /// <summary>Collects rows and the environment and writes them to TestResults/performance (ignored by Git).</summary>
    public sealed class BenchmarkReport
    {
        public readonly List<BenchmarkRow> Rows = new List<BenchmarkRow>();

        static string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

        public static string Directory =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "TestResults", "performance"));

        public string Write(string name, List<KeyValuePair<string, string>> environment)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var text = new StringBuilder();
            foreach (var pair in environment)
                text.Append("# ").Append(pair.Key).Append(": ").Append(pair.Value).Append('\n');
            text.Append("scenario\tcombatants\trep\tticksPerFrame\tsamples\tmean_us\tmedian_us\tp95_us\tmin_us\tmax_us\tmeanPerTick_us\tgcAllocs\tnativeDeltaBytes\tnote\n");
            foreach (var row in Rows)
            {
                var s = row.PerSample;
                text.Append(row.Scenario).Append('\t').Append(row.Combatants).Append('\t').Append(row.Repetition).Append('\t')
                    .Append(row.TicksPerFrame).Append('\t').Append(s.Count).Append('\t')
                    .Append(F(s.Mean)).Append('\t').Append(F(s.Median)).Append('\t').Append(F(s.P95)).Append('\t')
                    .Append(F(s.Min)).Append('\t').Append(F(s.Max)).Append('\t').Append(F(row.MeanPerTick)).Append('\t')
                    .Append(row.GcAllocations).Append('\t').Append(row.NativeDeltaBytes).Append('\t')
                    .Append(row.Note ?? string.Empty).Append('\n');
            }

            var path = Path.Combine(Directory, name + ".tsv");
            File.WriteAllText(path, text.ToString());
            return path;
        }
    }
}
