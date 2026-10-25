using System;
using Unity.Burst;
using Unity.Entities;
using UnityEngine;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Owner and single writer of <see cref="SimulationClock"/>. Advances the tick by exactly one at
    /// the start of every fixed simulation step, before any combat phase of that step runs.
    /// </summary>
    /// <remarks>
    /// Lifetime equals the <see cref="World"/>: <c>OnCreate</c> creates the one clock singleton and
    /// applies <see cref="SimulationTiming.TickDuration"/> to the world's
    /// <see cref="FixedStepSimulationSystemGroup"/>; <c>OnDestroy</c> removes the clock. There is no
    /// static state, so leaving and re-entering Play Mode (with or without domain reload) builds a
    /// fresh world whose clock starts again at <see cref="SimulationClock.NoTick"/>.
    /// <c>OnCreate</c> throws if a <see cref="SimulationClock"/> already exists, so a second
    /// instance of this system (e.g. an explicit <c>World.CreateSystem</c>) is rejected and a world
    /// can never hold two clocks or two writers.
    /// The frame-to-step catch-up is Unity's <see cref="FixedStepSimulationSystemGroup"/> rate manager;
    /// nothing in combat code accumulates frame time on its own.
    /// </remarks>
    [BurstCompile]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(BeginFixedStepSimulationEntityCommandBufferSystem))]
    public partial struct CombatClockSystem : ISystem
    {
        Entity m_Clock;

        public void OnCreate(ref SystemState state)
        {
            var fixedStepGroup = state.World.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
            if (fixedStepGroup == null)
            {
                throw new InvalidOperationException(
                    $"{nameof(CombatClockSystem)} requires a {nameof(FixedStepSimulationSystemGroup)} in world '{state.World.Name}'.");
            }

            // Throws if a clock already exists in this world.
            m_Clock = state.EntityManager.CreateSingleton(new SimulationClock(), nameof(SimulationClock));
            fixedStepGroup.Timestep = SimulationTiming.TickDuration;
        }

        public void OnDestroy(ref SystemState state)
        {
            if (state.EntityManager.Exists(m_Clock))
                state.EntityManager.DestroyEntity(m_Clock);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // GetSingletonRW also fails loudly if a second clock was added behind the owner's back.
            ref var clock = ref SystemAPI.GetSingletonRW<SimulationClock>().ValueRW;
            if (!clock.TryAdvance())
            {
                // Unreachable in practice (~828 days of continuous simulation at 60 Hz). The tick never
                // wraps to 0 and is never simulated twice: the clock stops and the battle must end.
                Debug.LogError("SimulationClock reached its last tick; the clock stopped advancing.");
                state.Enabled = false;
            }
        }
    }
}
