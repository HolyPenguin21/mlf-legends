#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Entities;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.Profiling;
using static MLFLegends.Tests.PlayMode.SpritePlayModeSupport;

namespace MLFLegends.Tests.PlayMode
{
    // Stage 03 (#38) measurements for 2/100/400 fighters through the real default World and lifecycle. Run explicitly:
    // -testCategory Performance (PlayMode, without -nographics for the GPU columns). Writes
    // TestResults/performance/stage03-sprites-n{N}.tsv. Not an FPS promise: batchmode Editor numbers on one machine.
    [Category("Performance")]
    public class CombatSpritePerformanceTests
    {
        const int WarmupFrames = 30;
        const int MeasuredFrames = 120;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var renderer in UnityEngine.Object.FindObjectsByType<CombatSpriteRenderer>(FindObjectsInactive.Include))
                UnityEngine.Object.Destroy(renderer.gameObject);
            yield return EndBattle();
        }

        struct Sample
        {
            public double RefreshUs;
            public double ExtractUs;
            public double SubmitUs;
            public double RenderUs;
            public double ShadowUs;
            public long GcBytes;
            public int GcSamples;
            public long DrawCalls;
            public long Batches;
            public long SetPass;
        }

        // Same current-thread Recorder pattern as EditMode GcAllocationScope. Probe and test plumbing stay outside
        // the Refresh region. Neither a valid recorder handle nor a returned zero proves that a counter works.
        sealed class AllocationMeter : IDisposable
        {
            readonly Recorder m_Recorder = Recorder.Get("GC.Alloc");
            readonly bool m_WasEnabled;
            static object s_LiveAllocation;
            public bool BytesWork { get; private set; }
            public bool SamplesWork { get; private set; }
            public long PositiveBytes { get; private set; }
            public int PositiveSamples { get; private set; }
            public long EmptyBytes { get; private set; }
            public int EmptySamples { get; private set; }

            public AllocationMeter() { m_WasEnabled = m_Recorder.enabled; }
            public void Begin()
            {
                m_Recorder.enabled = false;
                m_Recorder.FilterToCurrentThread();
                m_Recorder.enabled = true;
            }
            public int End()
            {
                m_Recorder.enabled = false;
                return m_Recorder.sampleBlockCount;
            }
            public void Calibrate()
            {
                // Warm both probe paths before saving their controls.
                Probe(true); Probe(false);
                Probe(true); Probe(false);
                BytesWork = PositiveBytes >= 4096;
                SamplesWork = PositiveSamples > 0;
            }
            void Probe(bool allocate)
            {
                Begin();
                var before = GC.GetAllocatedBytesForCurrentThread();
                if (allocate)
                    s_LiveAllocation = new byte[4096];
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                var samples = End();
                GC.KeepAlive(s_LiveAllocation);
                s_LiveAllocation = null;
                if (allocate) { PositiveBytes = bytes; PositiveSamples = samples; }
                else { EmptyBytes = bytes; EmptySamples = samples; }
            }
            public void AssertControls()
            {
                Assert.That(EmptyBytes, Is.EqualTo(0), "Empty current-thread byte-counter control.");
                Assert.That(EmptySamples, Is.EqualTo(0), "Empty current-thread GC.Alloc sample control.");
            }
            public void Dispose()
            {
                m_Recorder.enabled = false;
                m_Recorder.CollectFromAllThreads();
                m_Recorder.enabled = m_WasEnabled;
            }
        }

        [Test]
        public void AllocationMeasurementDetectsALiveAllocationAndRejectsAZeroAllocationClaim()
        {
            RequireGraphicsDevice();
            var profilerWasEnabled = Profiler.enabled;
            try
            {
                Profiler.enabled = true;
                using var meter = new AllocationMeter();
                meter.Calibrate();
                TestContext.WriteLine(Controls(meter));
                meter.AssertControls();
                if (!meter.BytesWork && !meter.SamplesWork)
                    Assert.Inconclusive("NOT RUN: neither allocation counter detected the live 4096-byte control.");
                // A deliberate allocating runtime region must fail the same zero predicate used by the benchmark.
                Assert.Throws<AssertionException>(() => AssertNoAllocation(
                    meter.BytesWork ? meter.PositiveBytes : -1, meter.SamplesWork ? meter.PositiveSamples : -1));
                AssertNoAllocation(meter.BytesWork ? meter.EmptyBytes : -1, meter.SamplesWork ? meter.EmptySamples : -1);
            }
            finally { Profiler.enabled = profilerWasEnabled; }
        }

        [UnityTest]
        public IEnumerator RendererCostAndAllocationsFor2_100_400([Values(0, 49, 199)] int allies, [Values(false, true)] bool realArt, [Values(false, true)] bool groundShadows)
        {
            RequireGraphicsDevice();
            var count = 2 + 2 * allies;
            var prefix = (realArt ? "stage05-combat-anim" : "stage03-sprites") + (groundShadows ? "-ground-shadows" : "");
            CharacterAnimationConfig config = null;
            if (realArt)
            {
                if (UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/MLFLegends/Art/Characters/MageFemale01/Atlas/mage_female_01_00.png") == null)
                    Assert.Ignore("NOT RUN: install the external mage PNG before real-art performance.");
                config = UnityEditor.AssetDatabase.LoadAssetAtPath<CharacterAnimationConfig>("Assets/MLFLegends/Combat/PresentationData/CharacterAnimationConfig.asset");
                Assert.That(config, Is.Not.Null);
            }
            RenderTexture target = null;
            Camera camera = null;
            CombatSpriteRenderer renderer = null;
            SpriteRunTestProducer movementProducer = null;
            var profilerWasEnabled = Profiler.enabled;
            CombatArenaSystem arena = null; ulong arenaLease = 0;
            try
            {
                if (groundShadows)
                {
                    arena = DefaultWorld.GetExistingSystemManaged<CombatArenaSystem>(); arenaLease = arena.Acquire();
                    arena.Stage(arenaLease, new[] { new CombatArenaShape { Kind = CombatArenaKind.Box, Center = float2.zero,
                        AxisX = new float2(1, 0), HalfSize = new float2(512), Bottom = -1, Top = 0 } });
                }
                target = CreateTarget(512);
                camera = CreateCamera("Sprite perf camera", new Vector3(0f, 1f, 0f), 14f, target);
                renderer = CreateRenderer(camera);
                if (realArt)
                {
                    renderer.ConfigureAnimation(Shader.Find(ShaderName), camera, config, "mage_female_01");
                    movementProducer = SpriteRunTestProducer.Install(DefaultWorld);
                    movementProducer.StrikeEvery = 40; // Stage 05.1: ID % 4 == 1 strike, other odd IDs idle, even IDs run
                }
                yield return RestartBattle(Duel(new float3(-2f, 0f, 0f), FacingDirection.Right, new float3(2f, 0f, 0f), FacingDirection.Left, allies));
                Profiler.enabled = true;
                using var meter = new AllocationMeter();
                meter.Calibrate();
                var control = Controls(meter);
                TestContext.WriteLine(control);
                File.WriteAllText(OutputPath($"{prefix}-n{count}-gc-controls.tsv"), control);
                meter.AssertControls();
                using var drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
                using var batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
                using var setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");

                var rows = new List<string>();
                var results = new Dictionary<bool, Sample[]>();
                foreach (var withRenderer in new[] { false, true })
                {
                    renderer.enabled = withRenderer;
                    for (var i = 0; i < WarmupFrames; i++)
                    {
                        if (withRenderer) renderer.Refresh();
                        camera.Render();
                        yield return null;
                    }
                    var buffersAfterWarmup = renderer.Stats.InstanceBuffersCreated;
                    var atlasBuffers = withRenderer ? renderer.Atlas.GpuBuffersCreated : 0;
                    var samples = new Sample[MeasuredFrames];
                    var render = new Stopwatch();
                    for (var i = 0; i < MeasuredFrames; i++)
                    {
                        // Recorder setup and timing machinery are outside the allocation region.
                        meter.Begin();
                        var gcBefore = GC.GetAllocatedBytesForCurrentThread();
                        var refreshStart = Stopwatch.GetTimestamp();
                        if (withRenderer) renderer.Refresh();
                        var refreshEnd = Stopwatch.GetTimestamp();
                        var gcAfter = GC.GetAllocatedBytesForCurrentThread();
                        var gcSamples = meter.End();
                        // Value-copy the exact measured Refresh BEFORE rendering or yielding to LateUpdate.
                        var refreshStats = renderer.Stats;
                        var refreshUs = (refreshEnd - refreshStart) * 1e6 / Stopwatch.Frequency;
                        render.Restart();
                        camera.Render();
                        render.Stop();
                        var statsDrawCalls = UnityEditor.UnityStats.drawCalls;
                        var statsSetPass = UnityEditor.UnityStats.setPassCalls;
                        var renderUs = render.Elapsed.TotalMilliseconds * 1000.0;
                        yield return null; // deferred counters are separate from the synchronous CPU sample
                        samples[i] = new Sample
                        {
                            RefreshUs = refreshUs,
                            ExtractUs = withRenderer ? refreshStats.ExtractMicroseconds : 0,
                            SubmitUs = withRenderer ? refreshStats.SubmitMicroseconds : 0,
                            RenderUs = renderUs,
                            ShadowUs = withRenderer ? refreshStats.ShadowMicroseconds : 0,
                            GcBytes = meter.BytesWork ? gcAfter - gcBefore : -1,
                            GcSamples = meter.SamplesWork ? gcSamples : -1,
                            DrawCalls = statsDrawCalls > 0 ? statsDrawCalls : drawCalls.Valid ? drawCalls.LastValue : -1,
                            Batches = batches.Valid ? batches.LastValue : -1,
                            SetPass = statsDrawCalls > 0 ? statsSetPass : setPass.Valid ? setPass.LastValue : -1,
                        };
                    }
                    results[withRenderer] = samples;
                    WriteRaw(count, withRenderer, samples, prefix);
                    rows.Add(Row(count, withRenderer, samples, withRenderer ? renderer : null));
                    Write(count, rows, prefix); // preserve raw data even if a later allocation assertion fails
                    if (meter.BytesWork || meter.SamplesWork)
                        foreach (var sample in samples) AssertNoAllocation(sample.GcBytes, sample.GcSamples);
                    if (withRenderer)
                    {
                        Assert.That(renderer.Stats.Instances, Is.EqualTo(count));
                        Assert.That(renderer.Stats.ShadowInstances, Is.EqualTo(groundShadows ? count : 0));
                        Assert.That(renderer.Stats.ShadowSubmissions, Is.EqualTo(groundShadows ? 1 : 0));
                        Assert.That(renderer.Stats.InstanceBuffersCreated, Is.EqualTo(buffersAfterWarmup), "No buffer re-creation per frame.");
                        Assert.That(renderer.Atlas.GpuBuffersCreated, Is.EqualTo(atlasBuffers));
                        Assert.That(renderer.Stats.Submissions, Is.EqualTo(1), "One draw submission for all fighters on one page.");
                        if (realArt)
                        {
                            // Outside the GC/timing sample: a fast batchmode run can finish 120 frames before
                            // one real clip cycle. Observe at least three simulation-time cycles as well.
                            var atlas = renderer.Atlas;
                            var timingBuilds = renderer.TimingBuilds;
                            var buffers = renderer.Stats.InstanceBuffersCreated;
                            var frameBuffers = atlas.GpuBuffersCreated;
                            var firstTick = Tick;
                            var span = (uint)Math.Ceiling(3 * Math.Max(renderer.AnimationTiming.DurationSeconds, renderer.RunAnimationTiming.DurationSeconds) * SimulationTiming.TickRateHz);
                            var seen = new HashSet<uint>();
                            yield return Until(() =>
                            {
                                renderer.Refresh();
                                foreach (var instance in renderer.Extraction.Instances) seen.Add(instance.Frame);
                                return Tick - firstTick >= span;
                            }, 30);
                            var definition = renderer.Atlas.Definition;
                            var ids = renderer.Extraction.InstanceIds.ToArray();
                            var expected = new HashSet<uint>();
                            void Clip(string clip, bool present)
                            {
                                for (var f = 0; present && definition.TryGetClipFrame(clip, f, out var index); f++)
                                    expected.Add((uint)index);
                            }
                            Clip("run", ids.Any(id => id % 2 == 0));
                            Clip("idle_combat", ids.Any(id => id % 4 == 3));
                            Clip("punch_left", ids.Any(id => id % 4 == 1));
                            Assert.That(seen, Is.SupersetOf(expected), "Observe every idle, run and punch_left frame of the roles present across >=3 cycles.");
                            Assert.That(renderer.Atlas, Is.SameAs(atlas));
                            Assert.That(renderer.TimingBuilds, Is.EqualTo(timingBuilds));
                            Assert.That(renderer.Stats.InstanceBuffersCreated, Is.EqualTo(buffers));
                            Assert.That(atlas.GpuBuffersCreated, Is.EqualTo(frameBuffers));
                        }
                    }
                }
                if (results[true][MeasuredFrames - 1].DrawCalls > 0)
                {
                    var added = Median(results[true].Select(s => (double)s.DrawCalls)) - Median(results[false].Select(s => (double)s.DrawCalls));
                    Assert.That(added, Is.LessThanOrEqualTo(groundShadows ? 5 : 4), "Pages × passes plus one shared shadow submission.");
                }
                if (!meter.BytesWork && !meter.SamplesWork)
                    Assert.Inconclusive("Allocation result NOT RUN: neither counter detected the live allocation; timing TSV retained.");
            }
            finally
            {
                if (arena != null) arena.Release(arenaLease);
                SpriteRunTestProducer.Remove(DefaultWorld, movementProducer);
                Profiler.enabled = profilerWasEnabled;
                if (camera != null) camera.targetTexture = null;
                DestroyAll(renderer, camera, target);
            }
        }

        [UnityTest]
        public IEnumerator StrikeEffectsCost2_100_400([Values(2, 100, 400)] int count,
            [Values(false, true)] bool mass)
        {
            RequireGraphicsDevice();
            var lines = new List<string> { "vfx\tframe\twarmup\tfull_tick_us\trefresh_us\textract_us\tbody_submit_us\teffects_us\ttotal_cpu_us\thost_frame_ms\tgc_bytes\tgc_samples\talive\teffect_submissions\teffect_capacity\taccepted_hits\thistory_dropped\tresource_dropped\tnative_motion_bytes\teffect_native_scratch_bytes\teffect_native_frame_bytes\teffect_gpu_frame_bytes\teffect_instance_gpu_bytes\thistory_capacity\tcontact_burst_managed\tfacing_burst_managed" };
            var profilerWasEnabled = Profiler.enabled;
            try
            {
                Profiler.enabled = true;
                using var meter = new AllocationMeter(); meter.Calibrate(); meter.AssertControls();
                TestContext.WriteLine(Controls(meter));
                var allocationFailed = false;
                foreach (var effects in new[] { false, true })
                {
                    using var world = StrikeEffectsPlayModeSupport.Create(count, out var actors, out _);
                    var target = CreateTarget(512); var camera = CreateCamera("VFX benchmark", new Vector3(0, 1, count / 30f), 35, target);
                    var renderer = CreateRenderer(camera); renderer.BindWorld(world); renderer.DrawEffects = effects;
                    try
                    {
                        var owner = world.GetExistingSystemManaged<CombatSpatialIndexSystem>();
                        using var effectQuery = world.EntityManager.CreateEntityQuery(typeof(CombatEffectResources));
                        var resources = world.EntityManager.GetComponentObject<CombatEffectResources>(effectQuery.GetSingletonEntity());
                        // Identical commands and settings in both cases. Warm-up is reported separately, never hidden in steady state.
                        for (uint tick = 1; tick <= WarmupFrames + MeasuredFrames; tick++)
                        {
                            StrikeEffectsPlayModeSupport.Submit(world, actors, tick, mass);
                            meter.Begin(); var beforeGc = GC.GetAllocatedBytesForCurrentThread();
                            var start = Stopwatch.GetTimestamp(); StrikeEffectsPlayModeSupport.Step(world, tick);
                            var afterTick = Stopwatch.GetTimestamp(); renderer.Refresh(); var afterRefresh = Stopwatch.GetTimestamp();
                            var gc = GC.GetAllocatedBytesForCurrentThread() - beforeGc; var samples = meter.End();
                            var stats = renderer.Stats; camera.Render(); var end = Stopwatch.GetTimestamp();
                            var history = world.EntityManager.GetComponentData<CombatEffectStream>(effectQuery.GetSingletonEntity());
                            var hits = world.Unmanaged.GetUnsafeSystemRef<CombatHitSystem>(world.GetExistingSystem<CombatHitSystem>()).LastHits;
                            var confirmed = hits.Tick == tick ? hits.AcceptedHits : 0;
                            var managedFacing = world.Unmanaged.GetUnsafeSystemRef<CombatFacingSystem>(world.GetExistingSystem<CombatFacingSystem>()).LastKernelRanManaged; var managedContact = owner.LastContacts.KernelRanManaged;
                            var warmup = tick <= WarmupFrames;
                            if (!warmup && ((meter.BytesWork && gc != 0) || (meter.SamplesWork && samples != 0))) allocationFailed = true;
                            double Us(long ticks) => ticks * 1e6 / Stopwatch.Frequency;
                            lines.Add(FormattableString.Invariant($"{effects}\t{tick}\t{warmup}\t{Us(afterTick-start):F3}\t{Us(afterRefresh-afterTick):F3}\t{stats.ExtractMicroseconds:F3}\t{stats.SubmitMicroseconds:F3}\t{stats.EffectsMicroseconds:F3}\t{Us(end-start):F3}\t{Time.unscaledDeltaTime*1000:F3}\t{(meter.BytesWork?gc:-1)}\t{(meter.SamplesWork?samples:-1)}\t{stats.Effects.Alive}\t{stats.Effects.Submissions}\t{stats.Effects.BufferCapacity}\t{confirmed}\t{history.Dropped}\t{resources.CapacityDropped}\t{owner.MotionCapacityBytes + owner.MovementScratchCapacityBytes}\t{renderer.Effects.NativeCapacityBytes}\t{resources.NativeFrameCapacityBytes}\t{resources.GpuFrameCapacityBytes}\t{stats.Effects.BufferCapacity * 32}\t{CombatEffectStream.Capacity}\t{managedContact}\t{managedFacing}"));
                            Assert.That(managedContact || managedFacing, Is.False, "Actual synchronous kernels must report Burst ON for this benchmark.");
                            yield return null;
                        }
                    }
                    finally { DestroyAll(renderer, camera, target); }
                }
                File.WriteAllLines(OutputPath($"stage05-3-effects-n{count}-{(mass ? "mass" : "ordinary")}.tsv"), lines);
                TestContext.WriteLine($"Hardware: {SystemInfo.processorType}; OS: {SystemInfo.operatingSystem}; Unity: {Application.unityVersion}; GPU: {SystemInfo.graphicsDeviceName}. GPU milliseconds NOT MEASURED. Host frame includes Editor/default World; total_cpu_us is isolated tick+Refresh+Camera.Render.");
                if (!meter.BytesWork && !meter.SamplesWork) Assert.Inconclusive("NOT RUN: allocation counters failed positive control; timings retained.");
                Assert.That(allocationFailed, Is.False, "Validated post-warmup full tick + Refresh allocation region; inspect raw TSV.");
            }
            finally { Profiler.enabled = profilerWasEnabled; }
        }

        static void AssertNoAllocation(long bytes, int samples)
        {
            if (bytes >= 0) Assert.That(bytes, Is.EqualTo(0), "Validated allocation bytes in Refresh.");
            if (samples >= 0) Assert.That(samples, Is.EqualTo(0), "Validated current-thread GC.Alloc samples in Refresh.");
        }

        static string Controls(AllocationMeter meter) =>
            "device\tpositive_bytes\tempty_bytes\tbyte_counter_valid\tpositive_samples\tempty_samples\tsample_counter_valid\n" +
            $"{SystemInfo.graphicsDeviceType}\t{meter.PositiveBytes}\t{meter.EmptyBytes}\t{meter.BytesWork}\t{meter.PositiveSamples}\t{meter.EmptySamples}\t{meter.SamplesWork}\n";

        static string OutputPath(string name)
        {
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "TestResults", "performance"));
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, name);
        }

        static string Allocation(long value) => value < 0 ? "NOT MEASURED" : value.ToString(CultureInfo.InvariantCulture);

        static void WriteRaw(int count, bool withRenderer, Sample[] samples, string prefix)
        {
            var rows = new List<string> { "sample\trefresh_us\textract_us\tsubmit_us\tcamera_render_us\tgc_bytes\tgc_alloc_samples\tdraw_calls\tbatches\tsetpass\tshadow_us" };
            for (var i = 0; i < samples.Length; i++)
            {
                var s = samples[i];
                rows.Add(string.Join("\t", i.ToString(CultureInfo.InvariantCulture),
                    s.RefreshUs.ToString("R", CultureInfo.InvariantCulture), s.ExtractUs.ToString("R", CultureInfo.InvariantCulture),
                    s.SubmitUs.ToString("R", CultureInfo.InvariantCulture), s.RenderUs.ToString("R", CultureInfo.InvariantCulture),
                    Allocation(s.GcBytes), Allocation(s.GcSamples), s.DrawCalls.ToString(CultureInfo.InvariantCulture),
                    s.Batches.ToString(CultureInfo.InvariantCulture), s.SetPass.ToString(CultureInfo.InvariantCulture), s.ShadowUs.ToString("R", CultureInfo.InvariantCulture)));
            }
            File.WriteAllLines(OutputPath($"{prefix}-n{count}-{(withRenderer ? "renderer" : "no-renderer")}-raw.tsv"), rows);
        }

        static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            return sorted.Length == 0 ? double.NaN : sorted[sorted.Length / 2];
        }

        static double Percentile(IEnumerable<double> values, double p)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            return sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(p * sorted.Length))];
        }

        static string Row(int count, bool withRenderer, Sample[] samples, CombatSpriteRenderer renderer)
        {
            string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
            // Draw/SetPass calls: UnityEditor.UnityStats after each Camera.Render; batches only via ProfilerRecorder, which
            // batchmode does not publish. A counter without data is NOT RUN, never 0.
            string Counter(Func<Sample, long> f) => !HasGraphicsDevice || samples.Any(s => f(s) <= 0)
                ? "NOT RUN" : Median(samples.Select(s => (double)f(s))).ToString(CultureInfo.InvariantCulture);
            var stats = renderer != null ? renderer.Stats : default;
            return string.Join("\t", new[]
            {
                count.ToString(CultureInfo.InvariantCulture),
                withRenderer ? "renderer" : "no-renderer",
                SystemInfo.graphicsDeviceType.ToString(),
                F(Median(samples.Select(s => s.RefreshUs))),
                F(Percentile(samples.Select(s => s.RefreshUs), 0.95)),
                F(Median(samples.Select(s => s.ExtractUs))),
                F(Median(samples.Select(s => s.SubmitUs))),
                F(Median(samples.Select(s => s.RenderUs))),
                samples.Any(s => s.GcBytes < 0) ? "NOT MEASURED" : Allocation(samples.Sum(s => s.GcBytes)),
                samples.Any(s => s.GcSamples < 0) ? "NOT MEASURED" : Allocation(samples.Sum(s => (long)s.GcSamples)),
                Counter(s => s.DrawCalls),
                Counter(s => s.Batches),
                Counter(s => s.SetPass),
                stats.Submissions.ToString(CultureInfo.InvariantCulture),
                stats.InstanceBuffersCreated.ToString(CultureInfo.InvariantCulture),
                (stats.InstanceBufferCapacity * CombatSpriteExtraction.InstanceStride).ToString(CultureInfo.InvariantCulture),
                renderer != null ? renderer.Atlas.PageBytes.ToString(CultureInfo.InvariantCulture) : "0",
                renderer != null ? renderer.Atlas.FrameTableBytes.ToString(CultureInfo.InvariantCulture) : "0",
                F(Median(samples.Select(s => s.ShadowUs))),
                stats.ShadowSubmissions.ToString(CultureInfo.InvariantCulture),
                stats.ShadowInstances.ToString(CultureInfo.InvariantCulture),
                renderer != null && renderer.GroundShadows != null ? (renderer.GroundShadows.Capacity * CombatGroundShadows.Stride).ToString(CultureInfo.InvariantCulture) : "0",
            });
        }

        static void Write(int count, List<string> rows, string prefix)
        {
            var header = "fighters\tmode\tdevice\trefresh_median_us\trefresh_p95_us\textract_median_us\tsubmit_median_us\tcamera_render_median_us\tgc_bytes_measured\tgc_alloc_samples\t"
                + "draw_calls_median\tbatches_median\tsetpass_median\tsubmissions\tinstance_buffers_created\tinstance_buffer_bytes\t"
                + "atlas_page_bytes\tframe_table_bytes\tshadow_median_us\tshadow_submissions\tshadow_instances\tshadow_buffer_bytes";
            File.WriteAllLines(OutputPath($"{prefix}-n{count}.tsv"), new[] { header }.Concat(rows));
        }
    }
}
#endif
