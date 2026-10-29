using Unity.Burst;
using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>The sole fixed-tick writer of <see cref="CombatAttackState"/>: starts, chains and ends strikes.</summary>
    /// <remarks>
    /// Runs first in State, before facing and movement, so both see this tick's strike (turning and jump locks, phase-scaled
    /// planar movement). Landing recovery suppresses new action starts and buffering. Per actor, from accepted commands only
    /// (#69 layout): one press per tick by the priority U → I → O (<see cref="CombatAttackTuning.StrikeButton"/>). A fresh
    /// press (Held never repeats) starts a strike when free and grounded at the start of the tick and does not press Jump on
    /// the same tick (the jump wins); airborne presses are dropped. Hand (U) and foot (I) series run 1, 1, 2, 1, 2, …
    /// (bounded <see cref="CombatAttackState.SeriesStep"/>, no length limit). During Windup presses are dropped; from Active
    /// to the end of Recovery the latest SERIES press is buffered in one slot. Once ChainDelayTicks of Recovery have played,
    /// a buffered press starts the next strike on that tick: the same series continues its sequence, the other series starts
    /// its own at 1. The physical ability (O) only starts from the free state, is never buffered, never interrupts a strike
    /// and is a finisher (full Recovery, no follow-up). Otherwise Recovery runs out, the buffer is dropped, the series resets
    /// and the actor is free on the following tick, where a fresh press may start a new series (no hidden timeout). Dropped,
    /// unexecuted or replaced presses never advance a sequence: the step changes only when a strike starts. Every strike
    /// start clears the hit ledger.
    /// Every strike start copies its phase timing and its damage/hit box from the World's <see cref="CombatStrikeSchedule"/>
    /// (created here with the defaults; a scene publishes its character's table) into <see cref="CombatAttackState.Timing"/>
    /// and <see cref="CombatAttackState.Strike"/>; the rest of the strike reads only that copy, so publishing a new table
    /// mid-strike changes the next strike, not this one.
    /// One linear, synchronous Burst pass; no allocations or structural changes.
    /// </remarks>
    [BurstCompile]
    [UpdateInGroup(typeof(CombatStateUpdateSystemGroup))]
    [UpdateBefore(typeof(CombatFacingSystem))]
    public partial struct CombatActionSystem : ISystem
    {
        Entity m_Schedule;

        public void OnCreate(ref SystemState state)
        {
            // Throws if a table already exists in this world: one owner, like the battle session.
            m_Schedule = state.EntityManager.CreateSingleton(CombatStrikeSchedule.Default, nameof(CombatStrikeSchedule));
            state.RequireForUpdate<SimulationClockRunning>();
            state.RequireForUpdate<SimulationClock>();
        }

        public void OnDestroy(ref SystemState state)
        {
            if (state.EntityManager.Exists(m_Schedule))
                state.EntityManager.DestroyEntity(m_Schedule);
        }

        [BurstCompile(CompileSynchronously = true)]
        public void OnUpdate(ref SystemState state)
        {
            var tick = SystemAPI.GetSingleton<SimulationClock>().Tick;
            var schedule = SystemAPI.GetComponent<CombatStrikeSchedule>(m_Schedule);
            foreach (var (attack, motion, applied, hits) in
                SystemAPI.Query<RefRW<CombatAttackState>, RefRO<CombatMotion>, RefRO<AppliedCombatCommand>, DynamicBuffer<CombatHitRecord>>()
                    .WithAll<InCombat>())
            {
                var current = attack.ValueRO;
                // Input is still polled/consumed. Landing presses are dropped, never added to the strike buffer.
                var landing = motion.ValueRO.IsLandingRecovery(tick);
                if (landing) current.Buffered = CommandButtons.None;
                // Landing suppresses inputs, not the already accepted execution's timeline.
                var pressed = landing ? CommandButtons.None : CombatAttackTuning.StrikeButton(applied.ValueRO.Command.Pressed);
                if (current.IsAttacking)
                {
                    var timing = current.Timing;
                    var elapsed = tick - current.StartTick;
                    // Only series presses enter the one-slot buffer; the physical ability is never buffered and never cuts a strike.
                    if (CombatAttackTuning.IsSeriesButton(pressed) && elapsed >= timing.ActiveStart)
                        current.Buffered = pressed;
                    if (current.Buffered != CommandButtons.None && timing.CanChain && elapsed >= timing.ChainPoint)
                    {
                        var step = CombatAttackTuning.ChainStep(current.Buffered, current.Kind, current.SeriesStep);
                        Begin(ref current, CombatAttackTuning.Select(current.Buffered, step), tick, schedule, hits);
                        current.SeriesStep = step;
                    }
                    else if (elapsed >= timing.End)
                    {
                        // Series over on this tick: free now, a press of this very tick starts a new series below.
                        current = new CombatAttackState { EndTick = tick };
                    }
                    else
                    {
                        attack.ValueRW = current;
                        continue;
                    }
                }

                // A grounded Jump press on the same tick wins: the movement owner takes off, the strike press is dropped.
                var jumps = (applied.ValueRO.Command.Pressed & CommandButtons.Jump) != 0;
                if (!current.IsAttacking && pressed != CommandButtons.None && motion.ValueRO.Grounded && !jumps && !landing)
                {
                    Begin(ref current, CombatAttackTuning.Select(pressed, 0), tick, schedule, hits);
                    current.SeriesStep = 0;
                }

                attack.ValueRW = current;
            }
        }

        static void Begin(ref CombatAttackState state, CombatAttackKind kind, uint tick, in CombatStrikeSchedule schedule,
            DynamicBuffer<CombatHitRecord> hits)
        {
            state.Kind = kind;
            state.StartTick = tick;
            state.Timing = schedule.Get(kind);
            state.Strike = schedule.GetStrike(kind);
            state.Buffered = CommandButtons.None;
            hits.Clear();
        }
    }
}
