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
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;
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
            public long GcBytes;
            public long DrawCalls;
            public long Batches;
            public long SetPass;
        }

        [UnityTest]
        public IEnumerator RendererCostAndAllocationsFor2_100_400([Values(0, 49, 199)] int allies)
        {
            RequireGraphicsDevice();
            var count = 2 + 2 * allies;
            var target = CreateTarget(512);
            var camera = CreateCamera("Sprite perf camera", new Vector3(0f, 1f, 0f), 14f, target);
            var renderer = CreateRenderer(camera);
            yield return RestartBattle(Duel(new float3(-2f, 0f, 0f), FacingDirection.Right, new float3(2f, 0f, 0f), FacingDirection.Left, allies));

            // Render counters are only published while the profiler collects (batchmode: off by default).
            var profilerWasEnabled = UnityEngine.Profiling.Profiler.enabled;
            UnityEngine.Profiling.Profiler.enabled = true;
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
                    if (withRenderer)
                        renderer.Refresh();
                    camera.Render();
                    yield return null;
                }

                var buffersAfterWarmup = renderer.Stats.InstanceBuffersCreated;
                var atlasBuffers = withRenderer ? renderer.Atlas.GpuBuffersCreated : 0;
                var samples = new Sample[MeasuredFrames];
                var watch = new Stopwatch();
                var render = new Stopwatch();
                for (var i = 0; i < MeasuredFrames; i++)
                {
                    var gcBefore = GC.GetAllocatedBytesForCurrentThread();
                    watch.Restart();
                    if (withRenderer)
                        renderer.Refresh();
                    watch.Stop();
                    var gcAfter = GC.GetAllocatedBytesForCurrentThread();
                    render.Restart();
                    camera.Render();
                    render.Stop();
                    // Editor statistics of the render just made (Game view Stats source); ProfilerRecorder as fallback.
                    var statsDrawCalls = UnityEditor.UnityStats.drawCalls;

                    var statsSetPass = UnityEditor.UnityStats.setPassCalls;
                    yield return null; // recorder counters are published at frame end
                    samples[i] = new Sample
                    {
                        RefreshUs = watch.Elapsed.TotalMilliseconds * 1000.0,
                        ExtractUs = withRenderer ? renderer.Stats.ExtractMicroseconds : 0,
                        SubmitUs = withRenderer ? renderer.Stats.SubmitMicroseconds : 0,
                        RenderUs = render.Elapsed.TotalMilliseconds * 1000.0,
                        GcBytes = gcAfter - gcBefore,
                        DrawCalls = statsDrawCalls > 0 ? statsDrawCalls : drawCalls.Valid ? drawCalls.LastValue : -1,
                        Batches = batches.Valid ? batches.LastValue : -1,
                        SetPass = statsDrawCalls > 0 ? statsSetPass : setPass.Valid ? setPass.LastValue : -1,
                    };
                }

                results[withRenderer] = samples;
                if (withRenderer)
                {
                    Assert.That(renderer.Stats.Instances, Is.EqualTo(count));
                    Assert.That(samples.Sum(s => s.GcBytes), Is.EqualTo(0), "No GC.Alloc per frame from extraction/submission after warm-up.");
                    Assert.That(renderer.Stats.InstanceBuffersCreated, Is.EqualTo(buffersAfterWarmup), "No buffer re-creation per frame.");
                    Assert.That(renderer.Atlas.GpuBuffersCreated, Is.EqualTo(atlasBuffers));
                    if (HasGraphicsDevice)
                        Assert.That(renderer.Stats.Submissions, Is.EqualTo(1), "One draw submission for all fighters on one page.");
                }

                rows.Add(Row(count, withRenderer, samples, withRenderer ? renderer : null));
            }

            if (HasGraphicsDevice && results[true][MeasuredFrames - 1].DrawCalls > 0)
            {
                var added = Median(results[true].Select(s => (double)s.DrawCalls)) - Median(results[false].Select(s => (double)s.DrawCalls));
                Assert.That(added, Is.LessThanOrEqualTo(4), "Sprite draw calls per camera render do not grow with the fighter count (pages × passes).");
            }

            UnityEngine.Profiling.Profiler.enabled = profilerWasEnabled;
            Write(count, rows);
            DestroyAll(camera, target);
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
                samples.Sum(s => s.GcBytes).ToString(CultureInfo.InvariantCulture),
                Counter(s => s.DrawCalls),
                Counter(s => s.Batches),
                Counter(s => s.SetPass),
                stats.Submissions.ToString(CultureInfo.InvariantCulture),
                stats.InstanceBuffersCreated.ToString(CultureInfo.InvariantCulture),
                (stats.InstanceBufferCapacity * CombatSpriteExtraction.InstanceStride).ToString(CultureInfo.InvariantCulture),
                renderer != null ? renderer.Atlas.PageBytes.ToString(CultureInfo.InvariantCulture) : "0",
                renderer != null ? renderer.Atlas.FrameTableBytes.ToString(CultureInfo.InvariantCulture) : "0",
            });
        }

        static void Write(int count, List<string> rows)
        {
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "TestResults", "performance"));
            Directory.CreateDirectory(directory);
            var header = "fighters\tmode\tdevice\trefresh_median_us\trefresh_p95_us\textract_median_us\tsubmit_median_us\tcamera_render_median_us\tgc_bytes_measured\t"
                + "draw_calls_median\tbatches_median\tsetpass_median\tsubmissions\tinstance_buffers_created\tinstance_buffer_bytes\t"
                + "atlas_page_bytes\tframe_table_bytes";
            File.WriteAllLines(Path.Combine(directory, $"stage03-sprites-n{count}.tsv"), new[] { header }.Concat(rows));
        }
    }
}
#endif
