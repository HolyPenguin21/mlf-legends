using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

namespace MLFLegends.Tests.EditMode
{
    // Extends the existing strike benchmark and reuses CombatBenchmarkWorld, BenchmarkSamples, environment capture
    // and GC scope. No parallel profiling runtime. Cold first build/capacity growth is reported separately.
    public partial class CombatStrikeBenchmarkTests
    {
        static object s_SpatialAllocationControl;

        [Test]
        public void SpatialBuildAndQueriesForSparseAndDense2_100_400()
        {
            const int queriesPerTick = 32;
            var lines = new List<string>();
            lines.AddRange(BenchmarkEnvironment.Capture().Select(p => $"# {p.Key}: {p.Value}"));
            lines.Add("# Spatial owner is managed SystemBase/native containers; other runtime owners keep their Burst paths.");
            lines.Add("# Native payload capacity is a LOWER BOUND; total native memory is the process-wide Unity Profiler counter.");
            lines.Add("# Query time includes exact live-membership validation (O(N)), grid traversal, deduplication and sorting.");
            lines.Add("scenario\tbodies\tcold_build_us\tcold_body_capacity\tcold_entry_capacity\tbuild_mean_us\tbuild_p95_us\tquery_batch_mean_us\tquery_batch_p95_us\tqueries_per_tick\tcandidates_mean\tduplicates_mean\tentries\toccupied_cells\tbody_capacity\tentry_capacity\tnative_payload_capacity_lower_bound_bytes\tprocess_native_delta_bytes\tgc_alloc_samples\tgc_control_valid");
            foreach (var count in new[] { 2, 100, 400 })
            foreach (var dense in new[] { false, true })
            {
                using var bench = new CombatBenchmarkWorld("spatial benchmark");
                bench.Producer.Load = ProducerLoad.None;
                var setup = BattleSetup.Default;
                setup.HumanAllies = setup.AiAllies = (ushort)(count / 2 - 1);
                bench.Submit(BattleRequest.Start(setup));
                bench.ApplyRequests();
                var boxes = new CombatSpatialBounds[queriesPerTick];
                using (var entities = bench.Combatants.ToEntityArray(Allocator.Temp))
                {
                    for (var i = 0; i < entities.Length; i++)
                    {
                        var position = dense ? new float3(i % 20 * 0.015f, 0, i / 20 * 0.015f)
                            : new float3(i % 20 * 3f - 30f, 0, i / 20 * 3f - 30f);
                        bench.Manager.SetComponentData(entities[i], new CombatPosition { Value = position });
                        if (i < queriesPerTick)
                            boxes[i] = new CombatSpatialBounds { Min = position - new float3(0.5f, 0, 0.5f), Max = position + new float3(1.5f, 3, 0.5f) };
                    }
                    for (var i = entities.Length; i < boxes.Length; i++)
                        boxes[i] = boxes[i % entities.Length];
                }
                var index = bench.World.GetExistingSystemManaged<CombatSpatialIndexSystem>();
                using var results = new NativeList<CombatSpatialBody>(count, Allocator.Persistent);
                bench.Frames(1, 1);
                var coldBuild = index.LastBuildMicroseconds;
                Assert.That(index.TryGetSnapshotInfo(out var cold), Is.True);
                for (var i = 0; i < WarmupTicks; i++)
                {
                    bench.Frames(1, 1);
                    foreach (var box in boxes)
                        index.TryQueryAabb(box, results);
                }
                // Positive allocation control uses the SAME recorder as the measured loop; report validity explicitly.
                var control = GcAllocationScope.Begin();
                s_SpatialAllocationControl = new byte[4096];
                var validControl = control.End() > 0;
                GC.KeepAlive(s_SpatialAllocationControl);
                s_SpatialAllocationControl = null;
                var build = new double[MeasuredTicks];
                var query = new BenchmarkSamples(MeasuredTicks);
                long candidates = 0, duplicates = 0;
                var rejectedQueries = 0;
                var nativeBefore = Profiler.GetTotalAllocatedMemoryLong();
                var gc = GcAllocationScope.Begin();
                var allocations = 0;
                try
                {
                    for (var i = 0; i < MeasuredTicks; i++)
                    {
                        bench.Frames(1, 1);
                        build[i] = index.LastBuildMicroseconds;
                        var start = Stopwatch.GetTimestamp();
                        foreach (var box in boxes)
                        {
                            if (!index.TryQueryAabb(box, results))
                                rejectedQueries++;
                            candidates += index.LastQueryCandidates;
                            duplicates += index.LastQueryDuplicatesEliminated;
                        }
                        query.Add(Stopwatch.GetTimestamp() - start);
                    }
                }
                finally
                {
                    // Restore the process-wide recorder even when an update/query throws.
                    allocations = gc.End();
                }
                var nativeDelta = Profiler.GetTotalAllocatedMemoryLong() - nativeBefore;
                Assert.That(index.TryGetSnapshotInfo(out var info), Is.True);
                Assert.That(rejectedQueries, Is.Zero, "All measured queries used current snapshots.");
                Assert.That(info.BodyCount, Is.EqualTo(count));
                Assert.That(info.BodyCapacity, Is.EqualTo(cold.BodyCapacity));
                Assert.That(info.EntryCapacity, Is.EqualTo(cold.EntryCapacity), "Warm measurements must not hide capacity growth.");
                Assert.That(results.Length, Is.EqualTo(dense ? count : 1));
                Array.Sort(build);
                var q = query.Stats();
                var values = new object[]
                {
                    dense ? "dense" : "sparse", count, coldBuild, cold.BodyCapacity, cold.EntryCapacity,
                    build.Average(), build[(int)Math.Ceiling(0.95 * build.Length) - 1], q.Mean, q.P95, queriesPerTick,
                    (double)candidates / (MeasuredTicks * queriesPerTick), (double)duplicates / (MeasuredTicks * queriesPerTick),
                    info.Entries, info.OccupiedCells, info.BodyCapacity, info.EntryCapacity, info.NativePayloadCapacityBytes,
                    nativeDelta, allocations, validControl,
                };
                lines.Add(string.Join("\t", values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))));
            }
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "TestResults", "performance"));
            Directory.CreateDirectory(directory);
            File.WriteAllLines(Path.Combine(directory, "stage04-spatial.tsv"), lines);
            UnityEngine.Debug.Log(string.Join("\n", lines));
        }
    }
}
