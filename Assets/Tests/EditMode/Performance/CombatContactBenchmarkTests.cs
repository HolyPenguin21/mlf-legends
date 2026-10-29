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
using Unity.Burst;
using Unity.Jobs;
using UnityEngine.Profiling;

namespace MLFLegends.Tests.EditMode
{
    [DisableAutoCreation]
    [UpdateBefore(typeof(CombatCommandProcessorSystem))]
    public partial class ContactLoadProducer : SystemBase
    {
        public bool OnlyControlled;
        /// <summary>0 none, 1 the controlled fighter, 2 every fighter; every 30 ticks (Stage 04.4 load).</summary>
        public int Impulses;
        /// <summary>Strike every 30 ticks instead of 60, so Active ticks with targets dominate the measured window.</summary>
        public bool Strikes;
        protected override void OnUpdate()
        {
            var tick = SystemAPI.GetSingleton<SimulationClock>().Tick;
            var session = SystemAPI.GetSingleton<BattleSession>();
            var controlled = session.HumanCombatant;
            var buffer = SystemAPI.GetSingletonBuffer<CombatCommandSubmission>();
            var impulses = SystemAPI.GetSingletonBuffer<CombatImpulse>();
            foreach (var (id, team) in SystemAPI.Query<RefRO<CombatantId>, RefRO<CombatTeam>>().WithAll<InCombat>())
            {
                var moving = !OnlyControlled || id.ValueRO.Equals(controlled);
                var period = Strikes ? 30u : 60u;
                var direction = team.ValueRO.Value == 1 ? 1f : -1f;
                buffer.Add(new CombatCommandSubmission(new CombatCommand { Combatant = id.ValueRO, Tick = tick,
                    MoveX = moving ? (sbyte)direction : (sbyte)0,
                    Source = CommandSource.AI, Pressed = moving && tick % period == 1 ? CommandButtons.HandSeries : CommandButtons.None }));
                if (tick % 30 == 5 && (Impulses == 2 || Impulses == 1 && id.ValueRO.Equals(controlled)))
                    impulses.Add(new CombatImpulse { Target = id.ValueRO, BattleNumber = session.BattleNumber,
                        Impulse = new float3(8 * direction, id.ValueRO.Value % 4 == 0 ? 3 : 0, id.ValueRO.Value % 2 == 0 ? 2 : -2) });
            }
        }
    }

    // Same final snapshot, same Active attackers, same box rule: complete scan versus the index batch. Counts must agree.
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
    struct HitSearchProbeJob : IJob
    {
        [ReadOnly] public NativeArray<CombatSpatialBody> Bodies; // brute force: every fighter
        [ReadOnly] public NativeArray<CombatSpatialBody> Candidates; // indexed: flat candidate rows
        [ReadOnly] public NativeArray<int2> Ranges;
        [ReadOnly] public NativeArray<float4> Attackers; // position xyz, facing
        [ReadOnly] public NativeArray<CombatStrikeParameters> Strikes; // frozen per execution (#69), as Hit reads them
        [ReadOnly] public NativeArray<byte> Teams;
        public bool Indexed;
        public NativeArray<long> Result; // [0] Inside tests, [1] inside, [2] checksum of inside (attacker index, target id)

        public void Execute()
        {
            long tests = 0, inside = 0, sum = 0;
            for (var a = 0; a < Attackers.Length; a++)
            {
                var strike = Strikes[a];
                var rows = Indexed ? Candidates.GetSubArray(Ranges[a].x, Ranges[a].y) : Bodies;
                for (var t = 0; t < rows.Length; t++)
                {
                    if (rows[t].Team.Value == Teams[a]) continue;
                    tests++;
                    if (!CombatHitSystem.Inside(strike, Attackers[a].xyz, Attackers[a].w, rows[t].Position)) continue;
                    inside++; sum += (a + 1) * 1000003L + rows[t].Id.Value;
                }
            }
            Result[0] = tests; Result[1] = inside; Result[2] = sum;
        }
    }

    public partial class CombatStrikeBenchmarkTests
    {
        [Test, Timeout(1800000)] // Dense 400 is not linear: 24 scenarios × ~2400 ticks exceed the default 180 s.
        public void BodyContactScenarios2_100_400ReportColdWarmWorkAndOwnerCosts()
        {
            var enabled = Profiler.enabled; var recorder = Recorder.Get("GC.Alloc"); var recorderEnabled = recorder.enabled;
            try
            {
                Profiler.enabled = true;
                var lines = BenchmarkEnvironment.Capture().Select(p => "# " + p.Key + ": " + p.Value).ToList();
                lines.Add("# Numerical kernel is Schedule+Complete; movement includes main-thread gather/write and job dispatch. Kernel flag reports actual managed execution, never assumes Burst.");
                lines.Add("# Cold means first Movement in a new World; Burst may already be compiled by earlier tests.");
                lines.Add("# Dense work is not linear. Native capacity is payload lower bound. Full fixed group measured separately.");
                lines.Add("# #69 native columns (Profiler.GetTotalAllocatedMemoryLong, process-wide, NOT a leak test by itself): native_owner_profiler_on = the measured owner loop with the Profiler enabled (GC.Alloc recorder);");
                lines.Add("#   native_compare_pass = the following test-only scan/index comparison pass; native_owner_profiler_off = another owner pass with the Profiler disabled;");
                lines.Add("#   native_after_dispose = after World disposal minus before World creation (test-side scratch of this scenario is still alive, ~100 KB).");
                lines.Add("# #69 contact velocity: velocity_contacts = recorded contact pairs summed over measured ticks; velocity_passes_max = the largest per-tick pass count.");
                lines.Add("# Stage 04.4/04.5 (#66): impulses every 30 ticks (single = controlled fighter, mass = everyone); strikes = presses every 30 ticks.");
                lines.Add("# Hit search: the same final snapshot and Active attackers, complete scan vs index batch (+Inside kernel); counts must match. Overlapping timings are not additive.");
                lines.Add("bodies\tscenario\tcold_move_us\tcold_native_payload\twarm_native_payload\tmove_mean_us\tmove_p95_us\tkernel_mean_us\tindex_mean_us\thit_mean_us\tfull_tick_mean_us\tfull_tick_p95_us\tcandidates\tpair_visits\tpeak_pairs\titerations\tfinal_residual\tresidual_complete\tunresolved_penetration\tconstraint_violation\tfrozen\tyield_attempts\tworld_rejections\twork_limits\tmanaged_kernel\tgc_samples\tprocess_native_delta"
                    + "\timpulses_applied\timpulses_rejected\tphysical_bodies\tdiscarded_velocities\tover_speed\tmax_external_speed"
                    + "\tactive_attacker_ticks\taccepted_hits\thit_queries\thit_candidates\thit_entries\thit_inside_tests\thit_managed_kernel"
                    + "\tbruteforce_search_mean_us\tindexed_search_mean_us\tbruteforce_inside_tests\tindexed_inside_tests\tsearch_inside\tsearch_match"
                    + "\tlocal_holds\trolled_back_bodies\tslop_pairs\tmax_slop_depth"
                    + "\tnative_compare_pass\tnative_owner_profiler_off\tnative_after_dispose\tvelocity_contacts\tvelocity_passes_max");
                lines[lines.FindIndex(l => l.StartsWith("bodies\t"))] = lines[lines.FindIndex(l => l.StartsWith("bodies\t"))]
                    .Replace("\tprocess_native_delta\t", "\tnative_owner_profiler_on\t");
                var failures = new List<string>();
                foreach (var count in new[] { 2, 100, 400 })
                foreach (var scenario in new[] { "sparse", "dense_allies", "opponents", "corridor", "wall_pressure", "impulse_single", "impulse_mass", "strikes" })
                {
                    var nativeScenarioStart = Profiler.GetTotalAllocatedMemoryLong();
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
                    // Commit geometry before spawn (as the production fixed/Initialization boundary does), so spawn support
                    // is real: a body frozen by an unresolved island keeps its spawn Motion and could never strike otherwise.
                    arena.Update();
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
                            // impulse_*/strikes reuse the opposing-front layout below: enemies .6 apart, inside punch reach.
                            else position = new float3(team == 1 ? -.3f - (i / 20) * .6f : .3f + ((i - count / 2) / 20) * .6f, 0, ((i < count / 2 ? i : i - count / 2) % 20) * .6f);
                            bench.Manager.SetComponentData(actors[i], new CombatTeam { Value = team });
                            bench.Manager.SetComponentData(actors[i], new CombatPosition { Value = position });
                        }
                    }
                    var collection = bench.World.GetExistingSystemManaged<CombatCommandCollectionSystemGroup>();
                    var producer = bench.World.CreateSystemManaged<ContactLoadProducer>(); producer.OnlyControlled = scenario == "dense_allies";
                    producer.Impulses = scenario == "impulse_single" ? 1 : scenario == "impulse_mass" ? 2 : 0; producer.Strikes = scenario == "strikes";
                    collection.AddSystemToUpdateList(producer); collection.SortSystems();
                    var clock = bench.World.GetExistingSystem<CombatClockSystem>();
                    var action = bench.World.GetExistingSystem<CombatActionSystem>(); var facing = bench.World.GetExistingSystem<CombatFacingSystem>();
                    var movement = bench.World.GetExistingSystem<CombatMovementSystem>(); var hit = bench.World.GetExistingSystem<CombatHitSystem>();
                    var index = bench.World.GetExistingSystemManaged<CombatSpatialIndexSystem>();
                    var move = new BenchmarkSamples(MeasuredTicks); var grid = new BenchmarkSamples(MeasuredTicks);
                    var hits = new BenchmarkSamples(MeasuredTicks); var kernel = new BenchmarkSamples(MeasuredTicks);
                    long candidates = 0, visits = 0, frozen = 0, yields = 0, rejects = 0, workLimits = 0, iterations = 0;
                    var peak = 0; var residual = 0f; var unresolved = 0f; var constraint = 0f; var complete = true; var managed = false;
                    long applied = 0, rejected = 0, physical = 0, discarded = 0, overSpeed = 0, activeTicks = 0, accepted = 0, queries = 0, hitCandidates = 0, entries = 0, insideTests = 0;
                    long bfTests = 0, ixTests = 0, searchInside = 0; var maxSpeed = 0f; var hitManaged = false; var match = true;
                    long holds = 0, rolledBack = 0, slopPairs = 0, velocityContacts = 0; var slopDepth = 0f; var velocityPasses = 0;
                    var bruteSearch = new BenchmarkSamples(MeasuredTicks); var indexedSearch = new BenchmarkSamples(MeasuredTicks);
                    using var attackerQuery = bench.Manager.CreateEntityQuery(typeof(CombatAttackState), typeof(CombatFacing), typeof(CombatPosition), typeof(CombatTeam), typeof(InCombat));
                    using var all = new NativeList<CombatSpatialBody>(1024, Allocator.Persistent); using var hitRows = new NativeList<CombatSpatialBody>(4096, Allocator.Persistent);
                    using var ranges = new NativeList<int2>(512, Allocator.Persistent); using var boxes = new NativeList<CombatSpatialBounds>(512, Allocator.Persistent);
                    using var attackers = new NativeList<float4>(512, Allocator.Persistent); using var strikes = new NativeList<CombatStrikeParameters>(512, Allocator.Persistent);
                    using var attackerTeams = new NativeList<byte>(512, Allocator.Persistent); using var result = new NativeArray<long>(3, Allocator.Persistent);
                    var everywhere = new CombatSpatialBounds { Min = new float3(-float.MaxValue), Max = new float3(float.MaxValue) };
                    void CompareSearch()
                    {
                        attackers.Clear(); strikes.Clear(); attackerTeams.Clear(); boxes.Clear();
                        var tick = bench.Tick;
                        using (var states = attackerQuery.ToComponentDataArray<CombatAttackState>(Allocator.Temp))
                        using (var facings = attackerQuery.ToComponentDataArray<CombatFacing>(Allocator.Temp))
                        using (var positions = attackerQuery.ToComponentDataArray<CombatPosition>(Allocator.Temp))
                        using (var teams = attackerQuery.ToComponentDataArray<CombatTeam>(Allocator.Temp))
                            for (var i = 0; i < states.Length; i++)
                            {
                                if (!states[i].IsAttacking || states[i].Timing.PhaseAt(tick - states[i].StartTick) != CombatAttackPhase.Active) continue;
                                var facingSign = facings[i].Value == FacingDirection.Left ? -1f : 1f;
                                attackers.Add(new float4(positions[i].Value, facingSign)); strikes.Add(states[i].Strike); attackerTeams.Add(teams[i].Value);
                                boxes.Add(CombatHitSystem.StrikeQuery(states[i].Strike, positions[i].Value, facingSign));
                            }
                        if (attackers.Length == 0) return;
                        var start = Stopwatch.GetTimestamp();
                        if (!index.TryQueryAabb(everywhere, all)) { match = false; return; }
                        var probe = new HitSearchProbeJob { Bodies = all.AsArray(), Candidates = all.AsArray(), Ranges = ranges.AsArray(), Attackers = attackers.AsArray(),
                            Strikes = strikes.AsArray(), Teams = attackerTeams.AsArray(), Result = result };
                        probe.Run(); bruteSearch.Add(Stopwatch.GetTimestamp() - start);
                        long bt = result[0], bi = result[1], bs = result[2];
                        start = Stopwatch.GetTimestamp();
                        if (!index.TryQueryAabbBatch(boxes.AsArray(), hitRows, ranges)) { match = false; return; }
                        probe.Candidates = hitRows.AsArray(); probe.Ranges = ranges.AsArray(); probe.Indexed = true;
                        probe.Run(); indexedSearch.Add(Stopwatch.GetTimestamp() - start);
                        bfTests += bt; ixTests += result[0]; searchInside += bi;
                        match &= bi == result[1] && bs == result[2];
                    }
                    double Tick(bool measure, bool compare = false)
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
                            applied += d.ImpulsesApplied; rejected += d.ImpulsesRejected; physical += d.PhysicalBodies; discarded += d.DiscardedVelocities;
                            overSpeed += d.OverSpeedBodies; maxSpeed = Math.Max(maxSpeed, d.MaxExternalSpeed);
                            holds += d.LocalHolds; rolledBack += d.RolledBackBodies; slopPairs += d.SlopAcceptedPairs; slopDepth = Math.Max(slopDepth, d.SlopAcceptedDepth);
                            velocityContacts += d.VelocityContacts; velocityPasses = Math.Max(velocityPasses, d.VelocityIterations);
                        }
                        start = Stopwatch.GetTimestamp(); index.Update(); if (measure) grid.Add(Stopwatch.GetTimestamp() - start);
                        if (compare) CompareSearch();
                        start = Stopwatch.GetTimestamp(); hit.Update(bench.World.Unmanaged); if (measure) hits.Add(Stopwatch.GetTimestamp() - start);
                        if (measure)
                        {
                            var h = bench.World.Unmanaged.GetUnsafeSystemRef<CombatHitSystem>(hit).LastHits;
                            if (h.Tick == bench.Tick && h.ActiveAttackers > 0)
                            {
                                activeTicks++; accepted += h.AcceptedHits; queries += h.Queries; hitCandidates += h.Candidates;
                                entries += h.EntriesVisited; insideTests += h.InsideTests; hitManaged |= h.KernelRanManaged;
                            }
                        }
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
                    // Separate pass on the following ticks: the search comparison is test code, outside the GC/owner timings.
                    var compareBefore = Profiler.GetTotalAllocatedMemoryLong();
                    for (var i = 0; i < MeasuredTicks; i++) Tick(false, true);
                    var nativeCompare = Profiler.GetTotalAllocatedMemoryLong() - compareBefore;
                    // The same owner loop with the Profiler (and its sample buffers) disabled: separates measurement cost.
                    Profiler.enabled = false;
                    var offBefore = Profiler.GetTotalAllocatedMemoryLong();
                    for (var i = 0; i < MeasuredTicks; i++) Tick(false);
                    var nativeOwnerOff = Profiler.GetTotalAllocatedMemoryLong() - offBefore;
                    Profiler.enabled = true;
                    Assert.That(gcSamples, Is.Zero, "Warm Movement + contacts + index + Hit owner loop.");
                    if (!match) failures.Add($"{count} {scenario}: indexed and complete-scan searches differ");
                    if ((scenario == "strikes" || scenario == "opponents" || scenario.StartsWith("impulse")) && activeTicks == 0)
                        failures.Add($"{count} {scenario}: no Active strike in the measured window (empty Hit phase)");
                    if (scenario == "strikes" && accepted == 0) failures.Add($"{count} {scenario}: no accepted hits (only full ledgers)");
                    if (scenario.StartsWith("impulse") && applied == 0) failures.Add($"{count} {scenario}: no impulse applied");
                    if (rolledBack > 0 && (scenario == "opponents" || scenario == "strikes" || scenario.StartsWith("impulse")))
                        failures.Add($"{count} {scenario}: {rolledBack} body-ticks of whole-island rollback in a pressing front");
                    index.TryGetSnapshotInfo(out var finalInfo);
                    Assert.That(finalInfo.NativePayloadCapacityBytes + index.MovementScratchCapacityBytes, Is.EqualTo(warmCapacity));
                    for (var i = 0; i < WarmupTicks; i++) bench.Frames(1, 1);
                    var full = new BenchmarkSamples(MeasuredTicks);
                    for (var i = 0; i < MeasuredTicks; i++) { bench.PrepareFrame(1); var start = Stopwatch.GetTimestamp(); bench.RunFrame(); full.Add(Stopwatch.GetTimestamp() - start); }
                    var m = move.Stats(); var g = grid.Stats(); var hs = hits.Stats(); var k = kernel.Stats(); var f = full.Stats();
                    bench.Dispose(); // idempotent; the using declaration disposes again at the end of the scope
                    var nativeAfterDispose = Profiler.GetTotalAllocatedMemoryLong() - nativeScenarioStart;
                    var bf = bruteSearch.Count > 0 ? bruteSearch.Stats().Mean : 0; var ix = indexedSearch.Count > 0 ? indexedSearch.Stats().Mean : 0;
                    lines.Add(string.Join("\t", count, scenario, cold, coldCapacity, warmCapacity, m.Mean, m.P95, k.Mean, g.Mean, hs.Mean,
                        f.Mean, f.P95, candidates, visits, peak, iterations, residual, complete, unresolved, constraint, frozen, yields, rejects, workLimits, managed, gcSamples, nativeDelta,
                        applied, rejected, physical, discarded, overSpeed, maxSpeed, activeTicks, accepted, queries, hitCandidates, entries, insideTests, hitManaged,
                        bf, ix, bfTests, ixTests, searchInside, match, holds, rolledBack, slopPairs, slopDepth,
                        nativeCompare, nativeOwnerOff, nativeAfterDispose, velocityContacts, velocityPasses));
                }
                Directory.CreateDirectory(BenchmarkReport.Directory);
                File.WriteAllLines(Path.Combine(BenchmarkReport.Directory, "stage05-contact-velocity.tsv"), lines);
                Assert.That(failures, Is.Empty);
            }
            finally { recorder.enabled = recorderEnabled; Profiler.enabled = enabled; }
        }
    }
}
