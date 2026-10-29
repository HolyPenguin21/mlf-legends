using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Profiling;

namespace MLFLegends.Tests.EditMode
{
    [DisableAutoCreation]
    public partial class ArenaLoadProducer : SystemBase
    {
        protected override void OnUpdate()
        {
            var tick = SystemAPI.GetSingleton<SimulationClock>().Tick;
            var buffer = SystemAPI.GetSingletonBuffer<CombatCommandSubmission>();
            foreach (var (id, team) in SystemAPI.Query<RefRO<CombatantId>, RefRO<CombatTeam>>().WithAll<InCombat>())
                buffer.Add(new CombatCommandSubmission(new CombatCommand { Combatant = id.ValueRO, Tick = tick,
                    MoveX = (sbyte)(team.ValueRO.Value == 1 ? 1 : -1), Source = CommandSource.AI,
                    Pressed = tick % 90 == 1 ? CommandButtons.Jump : tick % 90 == 65 ? CommandButtons.PunchLeft : CommandButtons.None }));
        }
    }

    public partial class CombatStrikeBenchmarkTests
    {
        [Test]
        public void ArenaPressureAndJumpingForSparseDense2_100_400()
        {
            var profilerWasEnabled = Profiler.enabled;
            var allocationRecorder = Recorder.Get("GC.Alloc");
            var allocationRecorderWasEnabled = allocationRecorder.enabled;
            try
            {
            Profiler.enabled = true;
            var lines = BenchmarkEnvironment.Capture().Select(p => $"# {p.Key}: {p.Value}").ToList();
            lines.Add("# Movement includes contacts. Contact math is a separate MANAGED fixed wall-pressure microbenchmark, NOT additive, Burst-contact time or a live-tick split.");
            lines.Add("# Owner splits directly call the same runtime owners in pipeline order; full fixed-group cost is separately measured after warmup.");
            lines.Add("bodies\tdense\twalls\tmovement_mean_us\tmovement_p95_us\tcontact_math_mean_us\tindex_mean_us\thit_mean_us\tfixed_group_mean_us\tfixed_group_p95_us\tgc_samples\tprocess_native_delta_bytes");
            foreach (var count in new[] { 2, 100, 400 })
            foreach (var dense in new[] { false, true })
            foreach (var walls in new[] { 1, 64 })
            {
                using var bench = new CombatBenchmarkWorld("static arena benchmark");
                bench.Producer.Load = ProducerLoad.None;
                var rows = new List<CombatArenaShape> { CombatArenaTests.Box(0, -.5f, 0, 1000, 0, 1000),
                    CombatArenaTests.Box(0, 0, 0, .1f, 4, 1000) };
                for (var i = 1; i < walls; i++) rows.Add(CombatArenaTests.Box(10 + i * 2, 0, 0, .1f, 4, 1000));
                var lease = bench.World.GetExistingSystemManaged<CombatArenaSystem>().Acquire();
                Assert.That(bench.World.GetExistingSystemManaged<CombatArenaSystem>().Stage( lease, rows.ToArray()), Is.True);
                var schedule = CombatStrikeSchedule.Default; schedule.LandingRecoveryTicks = 12;
                CombatStrikeSchedule.Publish(bench.Manager, schedule);
                var setup = BattleSetup.Default; setup.HumanAllies = setup.AiAllies = (ushort)(count / 2 - 1);
                bench.Submit(BattleRequest.Start(setup)); bench.ApplyRequests();
                using (var entities = bench.Combatants.ToEntityArray(Allocator.Temp))
                    for (var i = 0; i < entities.Length; i++)
                    {
                        var team = bench.Manager.GetComponentData<CombatTeam>(entities[i]).Value;
                        bench.Manager.SetComponentData(entities[i], new CombatPosition { Value = new float3(team == 1 ? -.5f : .5f, 0, i / 2 * (dense ? .02f : 3)) });
                    }
                var arena = bench.World.GetExistingSystemManaged<CombatArenaSystem>(); arena.Update();
                var clock = bench.World.GetExistingSystem<CombatClockSystem>();
                var collection = bench.World.GetExistingSystemManaged<CombatCommandCollectionSystemGroup>();
                var producer = bench.World.CreateSystemManaged<ArenaLoadProducer>();
                collection.AddSystemToUpdateList(producer); collection.SortSystems();
                var action = bench.World.GetExistingSystem<CombatActionSystem>();
                var facing = bench.World.GetExistingSystem<CombatFacingSystem>();
                var movement = bench.World.GetExistingSystem<CombatMovementSystem>();
                var index = bench.World.GetExistingSystemManaged<CombatSpatialIndexSystem>();
                var hit = bench.World.GetExistingSystem<CombatHitSystem>();
                var move = new BenchmarkSamples(MeasuredTicks); var grid = new BenchmarkSamples(MeasuredTicks);
                var hits = new BenchmarkSamples(MeasuredTicks); var contact = new BenchmarkSamples(MeasuredTicks);
                using var geometry = new NativeArray<CombatArenaShape>(rows.ToArray(), Allocator.Persistent);
                void Tick(bool measure)
                {
                    clock.Update(bench.World.Unmanaged); collection.Update(); action.Update(bench.World.Unmanaged); facing.Update(bench.World.Unmanaged);
                    var start = Stopwatch.GetTimestamp(); movement.Update(bench.World.Unmanaged);
                    if (measure) move.Add(Stopwatch.GetTimestamp() - start);
                    start = Stopwatch.GetTimestamp(); index.Update(); if (measure) grid.Add(Stopwatch.GetTimestamp() - start);
                    start = Stopwatch.GetTimestamp(); hit.Update(bench.World.Unmanaged); if (measure) hits.Add(Stopwatch.GetTimestamp() - start);
                    if (!measure) return;
                    start = Stopwatch.GetTimestamp();
                    for (var i = 0; i < count; i++)
                    {
                        var result = CombatArenaGeometry.Move(geometry, CombatBodyTuning.Default, new float3(-.4f, 0, i * .02f),
                            new float2(4f / 60, 0), 0, true, false, 12);
                        if (result.Failure != CombatArenaFailure.None) throw new InvalidOperationException(result.Failure.ToString());
                    }
                    contact.Add(Stopwatch.GetTimestamp() - start);
                }
                for (var i = 0; i < WarmupTicks; i++) Tick(false);
                using var arenaQuery = bench.Manager.CreateEntityQuery(typeof(CombatArenaState));
                var arenaEntity = arenaQuery.GetSingletonEntity();
                var activeCapacity = bench.Manager.GetBuffer<CombatArenaShape>(arenaEntity).Capacity;
                var pendingCapacity = bench.Manager.GetBuffer<PendingCombatArenaShape>(arenaEntity).Capacity;
                var control = GcAllocationScope.Begin(); var live = new byte[4096]; GC.KeepAlive(live);
                var valid = control.End() > 0;
                if (!valid) Assert.Inconclusive("NOT RUN: allocation counter failed its positive control.");
                var nativeBefore = Profiler.GetTotalAllocatedMemoryLong();
                var gc = GcAllocationScope.Begin(); int samples;
                try { for (var i = 0; i < MeasuredTicks; i++) Tick(true); }
                finally { samples = gc.End(); }
                Assert.That(samples, Is.Zero, "Measured owner loop after warmup.");
                GC.KeepAlive(live);
                var nativeDelta = Profiler.GetTotalAllocatedMemoryLong() - nativeBefore;
                // Measure the real fixed group independently; no owner is executed twice in any one tick.
                for (var i = 0; i < WarmupTicks; i++) bench.Frames(1, 1);
                var full = new BenchmarkSamples(MeasuredTicks);
                for (var i = 0; i < MeasuredTicks; i++)
                {
                    bench.PrepareFrame(1); var start = Stopwatch.GetTimestamp(); bench.RunFrame(); full.Add(Stopwatch.GetTimestamp() - start);
                }
                Assert.That(bench.Manager.GetBuffer<CombatArenaShape>(arenaEntity).Capacity, Is.EqualTo(activeCapacity));
                Assert.That(bench.Manager.GetBuffer<PendingCombatArenaShape>(arenaEntity).Capacity, Is.EqualTo(pendingCapacity));
                var m = move.Stats(); var g = grid.Stats(); var h = hits.Stats(); var c = contact.Stats(); var f = full.Stats();
                lines.Add(string.Join("\t", count, dense, walls, m.Mean, m.P95, c.Mean, g.Mean, h.Mean, f.Mean, f.P95, samples, nativeDelta));
            }
            Directory.CreateDirectory(BenchmarkReport.Directory);
            File.WriteAllLines(Path.Combine(BenchmarkReport.Directory, "stage04-arena.tsv"), lines);
            }
            finally { allocationRecorder.enabled = allocationRecorderWasEnabled; Profiler.enabled = profilerWasEnabled; }
        }
    }
}
