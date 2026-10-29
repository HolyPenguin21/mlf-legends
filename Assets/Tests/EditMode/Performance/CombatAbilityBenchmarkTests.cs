using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace MLFLegends.Tests.EditMode
{
    [Category("Performance")]
    public class CombatAbilityBenchmarkTests
    {
        [Test]
        public void AbilityCost2_100_400FreePackedAndCorridor()
        {
            var lines = new List<string> { "count\tscenario\tlaunchers\tJ\tfull_mean_us\tfull_p95_us\tfacing_us\tmovement_us\tindex_us\tpairs\tsegments\thit_candidates\thits\tholds\tdiscards\tgc_bytes\tnative_motion_bytes\tburst_managed\tfacing_burst_managed" };
            foreach (var count in new[] { 2, 100, 400 })
            foreach (var scenario in new[] { "free", "packed", "corridor" })
            foreach (var all in new[] { false, true })
            foreach (var impulse in new[] { 4f, 12f })
            {
                using var world = new World("ability benchmark");
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, ReplayWorld.RuntimeSystems);
                var manager = world.EntityManager;
                var table = CombatStrikeSchedule.Default; var strike = table.PhysicalAbilityStrike;
                strike.SelfImpulse = impulse; strike.ElevationDegrees = 35;
                strike.LaunchTick = table.PhysicalAbility.ActiveStart; strike.CompactHeight = 1;
                strike.CompactEnd = table.PhysicalAbility.End;
                table.PhysicalAbilityStrike = strike; CombatStrikeSchedule.Publish(manager, table);
                if (scenario == "corridor")
                {
                    var arena = world.GetExistingSystemManaged<CombatArenaSystem>(); var lease = arena.Acquire();
                    CombatArenaShape Box(float z, float width, float top) => new CombatArenaShape { Kind = CombatArenaKind.Box,
                        AxisX = new float2(1, 0), Center = new float2(0, z), HalfSize = new float2(100, width), Bottom = -1, Top = top };
                    arena.Stage(lease, new[] { Box(0, 100, 0), Box(-1.5f, .2f, 4), Box(1.5f, .2f, 4) });
                }
                BattleRequest.Submit(manager, BattleRequest.Start(new BattleSetup
                {
                    Human = new CombatantSetup { Team = 1, Position = float3.zero, Facing = FacingDirection.Right },
                    Ai = new CombatantSetup { Team = 2, Position = new float3(2, 0, 0), Facing = FacingDirection.Left },
                    HumanAllies = (ushort)(count / 2 - 1), AiAllies = (ushort)(count / 2 - 1),
                }));
                world.GetExistingSystemManaged<InitializationSystemGroup>().Update();
                using var actors = manager.CreateEntityQuery(typeof(CombatantId), typeof(CombatTeam), typeof(CombatPosition));
                using var entities = actors.ToEntityArray(Allocator.Temp);
                var perTeam = new int[3];
                foreach (var entity in entities)
                {
                    var team = manager.GetComponentData<CombatTeam>(entity).Value; var i = perTeam[team]++;
                    var spacing = scenario == "packed" ? .3f : 1f;
                    manager.SetComponentData(entity, new CombatPosition { Value = new float3((team == 1 ? -1 : 1) * (1 + i / 8 * spacing), 0,
                        scenario == "corridor" ? (i % 3 - 1) * .7f : (i % 8 - 4) * spacing) });
                }
                using var submissions = manager.CreateEntityQuery(typeof(CombatCommandQueue));
                var pending = manager.GetBuffer<CombatCommandSubmission>(submissions.GetSingletonEntity());
                foreach (var entity in entities)
                {
                    var id = manager.GetComponentData<CombatantId>(entity);
                    if (!all && entity != entities[0]) continue;
                    pending.Add(new CombatCommandSubmission(new CombatCommand { Combatant = id, Tick = 1,
                        Pressed = CommandButtons.PhysicalAbility, Source = CommandSource.AI }));
                }
                var fixedStep = world.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
                var index = world.GetExistingSystemManaged<CombatSpatialIndexSystem>();
                var samples = new List<double>(); long pairs = 0, hits = 0, candidates = 0, holds = 0, discards = 0;
                double movement = 0, builds = 0, facingUs = 0; var managed = false; var facingManaged = false; var maxSegments = 0;
                // Explicitly includes first-run/JIT and capacity growth. Hardware/raw output required before claiming performance.
                var gc = GC.GetAllocatedBytesForCurrentThread();
                for (var frame = 1; frame <= 60; frame++)
                {
                    world.SetTime(new TimeData((frame - .5) * SimulationTiming.TickDuration, SimulationTiming.TickDuration));
                    var start = Stopwatch.GetTimestamp(); fixedStep.Update();
                    samples.Add((Stopwatch.GetTimestamp() - start) * 1e6 / Stopwatch.Frequency);
                    ref var facing = ref world.Unmanaged.GetUnsafeSystemRef<CombatFacingSystem>(world.GetExistingSystem<CombatFacingSystem>());
                    facingUs += facing.LastMicroseconds; facingManaged |= facing.LastKernelRanManaged;
                    var d = index.LastContacts; pairs += d.PairVisits; holds += d.LocalHolds + d.TraceHolds;
                    discards += d.DiscardedVelocities; movement += index.LastMovementKernelMicroseconds; builds += index.LastBuildMicroseconds;
                    managed |= d.KernelRanManaged; maxSegments = Math.Max(maxSegments, index.MotionSegmentCount);
                    ref var hit = ref world.Unmanaged.GetUnsafeSystemRef<CombatHitSystem>(world.GetExistingSystem<CombatHitSystem>());
                    if (hit.LastHits.Tick == frame) { hits += hit.LastHits.AcceptedHits; candidates += hit.LastHits.Candidates; }
                }
                gc = GC.GetAllocatedBytesForCurrentThread() - gc;
                samples.Sort();
                lines.Add(FormattableString.Invariant($"{count}\t{scenario}\t{(all ? count : 1)}\t{impulse}\t{samples.Average():F2}\t{samples[56]:F2}\t{facingUs/60:F2}\t{movement/60:F2}\t{builds/60:F2}\t{pairs}\t{maxSegments}\t{candidates}\t{hits}\t{holds}\t{discards}\t{gc}\t{index.MotionCapacityBytes}\t{managed}\t{facingManaged}"));
            }
            var path = Path.Combine(Application.temporaryCachePath, "stage05-ability.tsv"); File.WriteAllLines(path, lines);
            TestContext.WriteLine($"Hardware: {SystemInfo.processorType}; OS: {SystemInfo.operatingSystem}; Unity: {Application.unityVersion}; raw: {path}");
        }
    }
}
