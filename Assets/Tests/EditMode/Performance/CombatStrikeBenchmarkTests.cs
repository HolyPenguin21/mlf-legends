using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace MLFLegends.Tests.EditMode
{
    /// <summary>Test producer: every in-combat fighter presses Attack or Kick every few ticks (Collection, before the processor).</summary>
    [DisableAutoCreation]
    public partial class StrikeLoadProducer : SystemBase
    {
        public bool Strikes;
        EntityQuery m_InCombat;

        protected override void OnCreate() =>
            m_InCombat = GetEntityQuery(ComponentType.ReadOnly<CombatantId>(), ComponentType.ReadOnly<InCombat>());

        protected override void OnUpdate()
        {
            if (!Strikes)
                return;
            var tick = SystemAPI.GetSingleton<SimulationClock>().Tick;
            var buffer = SystemAPI.GetSingletonBuffer<CombatCommandSubmission>();
            using var ids = m_InCombat.ToComponentDataArray<CombatantId>(Allocator.Temp);
            for (var i = 0; i < ids.Length; i++)
            {
                var phase = (tick + ids[i].Value) % 6;
                buffer.Add(new CombatCommandSubmission(new CombatCommand
                {
                    Combatant = ids[i], Tick = tick,
                    Pressed = phase == 0 ? CommandButtons.Attack : phase == 3 ? CommandButtons.Kick : CommandButtons.None,
                    Source = CommandSource.AI,
                }));
            }
        }
    }

    // Stage 05.1 cost of the strike/hit owners at 2/100/400 in the real runtime system list (ReplayWorld.RuntimeSystems),
    // without the replay recorder. Packed facing rows keep 2–3 enemies inside every strike box, so the brute-force hit
    // scan runs at its worst. Measured: one fixed tick per frame, Stopwatch around FixedStep.Update only; managed GC bytes
    // of the measured loop via GC.GetAllocatedBytesForCurrentThread (main thread). Writes stage05-strikes.tsv; no thresholds.
    [Category("Performance")]
    public partial class CombatStrikeBenchmarkTests
    {
        const int WarmupTicks = 120;
        const int MeasuredTicks = 600;

        static double Percentile(List<double> sorted, double p) => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

        [Test]
        public void StrikeAndHitCostFor2_100_400()
        {
            var rows = new List<string>();
            foreach (var count in new[] { 2, 100, 400 })
            foreach (var strikes in new[] { false, true })
            {
                using var world = new World("strike benchmark");
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, ReplayWorld.RuntimeSystems);
                var fixedStep = world.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
                var producer = world.CreateSystemManaged<StrikeLoadProducer>();
                var collection = world.GetExistingSystemManaged<CombatCommandCollectionSystemGroup>();
                collection.AddSystemToUpdateList(producer);
                collection.SortSystems();

                var setup = new BattleSetup
                {
                    Human = new CombatantSetup { Team = 1, Position = float3.zero, Facing = FacingDirection.Right },
                    Ai = new CombatantSetup { Team = 2, Position = new float3(0.6f, 0f, 0f), Facing = FacingDirection.Left },
                    HumanAllies = (ushort)(count / 2 - 1), AiAllies = (ushort)(count / 2 - 1),
                };
                BattleRequest.Submit(world.EntityManager, BattleRequest.Start(setup));
                world.GetExistingSystemManaged<InitializationSystemGroup>().Update();
                using (var query = world.EntityManager.CreateEntityQuery(typeof(CombatantId), typeof(CombatTeam)))
                using (var entities = query.ToEntityArray(Allocator.Temp))
                {
                    var perTeam = new int[3];
                    foreach (var entity in entities)
                    {
                        var team = world.EntityManager.GetComponentData<CombatTeam>(entity).Value;
                        var index = perTeam[team]++;
                        world.EntityManager.SetComponentData(entity, new CombatPosition { Value = new float3(team == 1 ? 0f : 0.6f, 0f, index * 0.3f) });
                    }
                }

                producer.Strikes = strikes;
                var dt = (double)SimulationTiming.TickDuration;
                uint frame = 0;
                void Tick()
                {
                    frame++;
                    world.SetTime(new TimeData((frame - 0.5) * dt, (float)dt));
                    fixedStep.Update();
                }

                for (var i = 0; i < WarmupTicks; i++)
                    Tick();
                var samples = new List<double>(MeasuredTicks);
                var gcBefore = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < MeasuredTicks; i++)
                {
                    var start = Stopwatch.GetTimestamp();
                    Tick();
                    samples.Add((Stopwatch.GetTimestamp() - start) * 1e6 / Stopwatch.Frequency);
                }

                var gcBytes = GC.GetAllocatedBytesForCurrentThread() - gcBefore;
                long damaged;
                using (var health = world.EntityManager.CreateEntityQuery(typeof(CombatHealth)))
                using (var values = health.ToComponentDataArray<CombatHealth>(Allocator.Temp))
                    damaged = values.ToArray().Count(h => h.Current < h.Max);
                Assert.That(damaged, Is.EqualTo(strikes ? count : 0), "The strike load lands hits on every fighter; idle lands none.");

                samples.Sort();
                rows.Add(string.Join("\t", strikes ? "strikes" : "idle", count.ToString(CultureInfo.InvariantCulture), MeasuredTicks.ToString(CultureInfo.InvariantCulture),
                    samples.Average().ToString("0.0", CultureInfo.InvariantCulture), Percentile(samples, 0.5).ToString("0.0", CultureInfo.InvariantCulture),
                    Percentile(samples, 0.95).ToString("0.0", CultureInfo.InvariantCulture), samples.Last().ToString("0.0", CultureInfo.InvariantCulture),
                    gcBytes.ToString(CultureInfo.InvariantCulture)));
            }

            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "TestResults", "performance"));
            Directory.CreateDirectory(directory);
            var text = new StringBuilder();
            text.Append("# Stage 05.1 strike benchmark: full fixed tick (clock, lifecycle, commands, strike, facing, movement, hit)\n");
            text.Append("# Unity ").Append(Application.unityVersion).Append(", ").Append(SystemInfo.processorType).Append(", Burst ")
                .Append(Unity.Burst.BurstCompiler.IsEnabled ? "on" : "off").Append('\n');
            text.Append("scenario\tcombatants\tticks\tmean_us\tmedian_us\tp95_us\tmax_us\tmanagedGcBytes\n");
            foreach (var row in rows)
                text.Append(row).Append('\n');
            File.WriteAllText(Path.Combine(directory, "stage05-strikes.tsv"), text.ToString());
            UnityEngine.Debug.Log(text.ToString());
        }
    }
}
