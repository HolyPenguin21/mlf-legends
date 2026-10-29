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
    [UpdateBefore(typeof(CombatCommandProcessorSystem))]
    public partial class ContactLoadProducer : SystemBase
    {
        public bool OnlyControlled;
        protected override void OnUpdate()
        {
            var tick = SystemAPI.GetSingleton<SimulationClock>().Tick;
            var controlled = SystemAPI.GetSingleton<BattleSession>().HumanCombatant;
            var buffer = SystemAPI.GetSingletonBuffer<CombatCommandSubmission>();
            foreach (var (id, team) in SystemAPI.Query<RefRO<CombatantId>, RefRO<CombatTeam>>().WithAll<InCombat>())
            {
                var moving = !OnlyControlled || id.ValueRO.Equals(controlled);
                buffer.Add(new CombatCommandSubmission(new CombatCommand { Combatant = id.ValueRO, Tick = tick,
                    MoveX = moving ? (sbyte)(team.ValueRO.Value == 1 ? 1 : -1) : (sbyte)0,
                    Source = CommandSource.AI, Pressed = moving && tick % 60 == 1 ? CommandButtons.PunchLeft : CommandButtons.None }));
            }
        }
    }

    public partial class CombatStrikeBenchmarkTests
    {
        [Test]
        public void BodyContactScenarios2_100_400ReportColdWarmWorkAndOwnerCosts()
        {
            var enabled = Profiler.enabled; var recorder = Recorder.Get("GC.Alloc"); var recorderEnabled = recorder.enabled;
            try
            {
                Profiler.enabled = true;
                var lines = BenchmarkEnvironment.Capture().Select(p => "# " + p.Key + ": " + p.Value).ToList();
                lines.Add("# Numerical kernel is Schedule+Complete; movement includes main-thread gather/write and job dispatch. Kernel flag reports actual managed execution, never assumes Burst.");
                lines.Add("# Cold means first Movement in a new World; Burst may already be compiled by earlier tests.");
                lines.Add("# Dense work is not linear. Native capacity is payload lower bound, process delta also includes allocator/ECS effects. Full fixed group measured separately.");
                lines.Add("bodies\tscenario\tcold_move_us\tcold_native_payload\twarm_native_payload\tmove_mean_us\tmove_p95_us\tkernel_mean_us\tindex_mean_us\thit_mean_us\tfull_tick_mean_us\tfull_tick_p95_us\tcandidates\tpair_visits\tpeak_pairs\titerations\tfinal_residual\tresidual_complete\tunresolved_penetration\tconstraint_violation\tfrozen\tyield_attempts\tworld_rejections\twork_limits\tmanaged_kernel\tgc_samples\tprocess_native_delta");
                foreach (var count in new[] { 2, 100, 400 })
                foreach (var scenario in new[] { "sparse", "dense_allies", "opponents", "corridor", "wall_pressure" })
                {
                    using var bench = new CombatBenchmarkWorld("body contact benchmark"); bench.Producer.Load = ProducerLoad.None;
                    var rows = new List<CombatArenaShape> { CombatArenaTests.Box(0, -.5f, 0, 500, 0, 500) };
                    if (scenario == "corridor")
                    {
                        rows.Add(CombatArenaTests.Box(0, 0, .5f, 500, 4, .1f));
                        rows.Add(CombatArenaTests.Box(0, 0, -.5f, 500, 4, .1f));
                    }
                    if (scenario == "wall_pressure") rows.Add(CombatArenaTests.Box(0, 0, 0, .1f, 4, 500));
                    var arena = bench.World.GetExistingSystemManaged<CombatArenaSystem>();
                    Assert.That(arena.Stage(arena.Acquire(), rows.ToArray()), Is.True);
                    var setup = BattleSetup.Default; setup.HumanAllies = setup.AiAllies = (ushort)(count / 2 - 1);
                    bench.Submit(BattleRequest.Start(setup)); bench.ApplyRequests();
                    using (var actorRows = bench.Combatants.ToEntityArray(Allocator.Temp))
                    {
                        var actors = actorRows.ToArray().OrderBy(e => bench.Manager.GetComponentData<CombatantId>(e).Value).ToArray();
                        for (var i = 0; i < actors.Length; i++)
                        {
                            var team = i < count / 2 ? (byte)1 : (byte)2;
                            float3 position;
                            if (scenario == "sparse") position = new float3((i % 20) * 3 - 30, 0, (i / 20) * 3);
                            else if (scenario == "corridor") position = new float3((i - count / 2) * .6f, 0, 0);
                            else if (scenario == "wall_pressure") { team = 1; position = new float3(-.4f - (i / 10) * .6f, 0, (i % 10) * .6f); }
                            else if (scenario == "dense_allies") { team = 1; position = new float3((i % 20) * .6f, 0, (i / 20) * .6f); }
                            else position = new float3(team == 1 ? -.3f - (i / 20) * .6f : .3f + ((i - count / 2) / 20) * .6f, 0, ((i < count / 2 ? i : i - count / 2) % 20) * .6f);
                            bench.Manager.SetComponentData(actors[i], new CombatTeam { Value = team });
                            bench.Manager.SetComponentData(actors[i], new CombatPosition { Value = position });
                        }
                    }
                    var collection = bench.World.GetExistingSystemManaged<CombatCommandCollectionSystemGroup>();
                    var producer = bench.World.CreateSystemManaged<ContactLoadProducer>(); producer.OnlyControlled = scenario == "dense_allies";
                    collection.AddSystemToUpdateList(producer); collection.SortSystems();
                    var clock = bench.World.GetExistingSystem<CombatClockSystem>();
                    var action = bench.World.GetExistingSystem<CombatActionSystem>(); var facing = bench.World.GetExistingSystem<CombatFacingSystem>();
                    var movement = bench.World.GetExistingSystem<CombatMovementSystem>(); var hit = bench.World.GetExistingSystem<CombatHitSystem>();
                    var index = bench.World.GetExistingSystemManaged<CombatSpatialIndexSystem>();
                    var move = new BenchmarkSamples(MeasuredTicks); var grid = new BenchmarkSamples(MeasuredTicks);
                    var hits = new BenchmarkSamples(MeasuredTicks); var kernel = new BenchmarkSamples(MeasuredTicks);
                    long candidates = 0, visits = 0, frozen = 0, yields = 0, rejects = 0, workLimits = 0, iterations = 0;
                    var peak = 0; var residual = 0f; var unresolved = 0f; var constraint = 0f; var complete = true; var managed = false;
                    double Tick(bool measure)
                    {
                        arena.Update(); clock.Update(bench.World.Unmanaged); collection.Update(); action.Update(bench.World.Unmanaged); facing.Update(bench.World.Unmanaged);
                        var start = Stopwatch.GetTimestamp(); movement.Update(bench.World.Unmanaged); var elapsed = Stopwatch.GetTimestamp() - start;
                        if (measure)
                        {
                            move.Add(elapsed); kernel.Add((long)(index.LastMovementKernelMicroseconds * Stopwatch.Frequency / 1e6));
                            var d = index.LastContacts; candidates += d.Candidates; visits += d.PairVisits; iterations += d.Iterations;
                            frozen += d.FrozenBodies; yields += d.YieldAttempts; rejects += d.WorldRejections; if (d.WorkLimit) workLimits++;
                            peak = Math.Max(peak, d.PeakPairs); residual = Math.Max(residual, d.ResidualPenetration); complete &= d.ResidualComplete;
                            unresolved = Math.Max(unresolved, d.UnresolvedPenetration); constraint = Math.Max(constraint, d.ResidualConstraint); managed |= d.KernelRanManaged;
                        }
                        start = Stopwatch.GetTimestamp(); index.Update(); if (measure) grid.Add(Stopwatch.GetTimestamp() - start);
                        start = Stopwatch.GetTimestamp(); hit.Update(bench.World.Unmanaged); if (measure) hits.Add(Stopwatch.GetTimestamp() - start);
                        return BenchmarkSamples.Microseconds(elapsed);
                    }
                    var cold = Tick(false);
                    index.TryGetSnapshotInfo(out var coldInfo); var coldCapacity = coldInfo.NativePayloadCapacityBytes + index.MovementScratchCapacityBytes;
                    for (var i = 0; i < WarmupTicks; i++) Tick(false);
                    index.TryGetSnapshotInfo(out var warmInfo); var warmCapacity = warmInfo.NativePayloadCapacityBytes + index.MovementScratchCapacityBytes;
                    var control = GcAllocationScope.Begin(); var positiveControl = new byte[4096]; GC.KeepAlive(positiveControl);
                    if (control.End() == 0) Assert.Inconclusive("NOT RUN: allocation counter positive control failed.");
                    var nativeBefore = Profiler.GetTotalAllocatedMemoryLong(); var gc = GcAllocationScope.Begin(); int gcSamples;
                    try { for (var i = 0; i < MeasuredTicks; i++) Tick(true); } finally { gcSamples = gc.End(); }
                    var nativeDelta = Profiler.GetTotalAllocatedMemoryLong() - nativeBefore;
                    Assert.That(gcSamples, Is.Zero, "Warm Movement + contacts + index + Hit owner loop.");
                    index.TryGetSnapshotInfo(out var finalInfo);
                    Assert.That(finalInfo.NativePayloadCapacityBytes + index.MovementScratchCapacityBytes, Is.EqualTo(warmCapacity));
                    for (var i = 0; i < WarmupTicks; i++) bench.Frames(1, 1);
                    var full = new BenchmarkSamples(MeasuredTicks);
                    for (var i = 0; i < MeasuredTicks; i++) { bench.PrepareFrame(1); var start = Stopwatch.GetTimestamp(); bench.RunFrame(); full.Add(Stopwatch.GetTimestamp() - start); }
                    var m = move.Stats(); var g = grid.Stats(); var h = hits.Stats(); var k = kernel.Stats(); var f = full.Stats();
                    lines.Add(string.Join("\t", count, scenario, cold, coldCapacity, warmCapacity, m.Mean, m.P95, k.Mean, g.Mean, h.Mean,
                        f.Mean, f.P95, candidates, visits, peak, iterations, residual, complete, unresolved, constraint, frozen, yields, rejects, workLimits, managed, gcSamples, nativeDelta));
                }
                Directory.CreateDirectory(BenchmarkReport.Directory);
                File.WriteAllLines(Path.Combine(BenchmarkReport.Directory, "stage04-body-contacts.tsv"), lines);
            }
            finally { recorder.enabled = recorderEnabled; Profiler.enabled = enabled; }
        }
    }
}
