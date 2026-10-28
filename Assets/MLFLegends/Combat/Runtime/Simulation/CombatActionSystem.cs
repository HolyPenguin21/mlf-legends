using Unity.Burst;
using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>The sole fixed-tick writer of <see cref="CombatAttackState"/>: starts, chains and ends strikes.</summary>
    /// <remarks>
    /// Runs first in State, before facing and movement, so both see this tick's strike (they lock turning, planar
    /// movement and jump while a strike runs). Per actor, from its accepted command only (player and AI alike):
    /// a fresh Attack/Kick press (Held never repeats) starts a series when the actor is free and grounded at the start of
    /// the tick and does not press Jump on the same tick (the jump wins); airborne presses are dropped. During Windup
    /// presses are dropped; from Active to the end of Recovery the
    /// latest press is buffered in one slot. Once ChainDelayTicks of Recovery have played, a buffered press starts the next
    /// strike of the series on that tick (sides alternate, Kick after kick_right is the spin finisher), unless the strike
    /// is a finisher or the series is full. Otherwise Recovery runs out, the buffer is dropped and the actor is free on
    /// the following tick, where a fresh press may start a new series. Every strike start clears the hit ledger.
    /// One linear, synchronous Burst pass; no allocations or structural changes.
    /// </remarks>
    [BurstCompile]
    [UpdateInGroup(typeof(CombatStateUpdateSystemGroup))]
    [UpdateBefore(typeof(CombatFacingSystem))]
    public partial struct CombatActionSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimulationClockRunning>();
            state.RequireForUpdate<SimulationClock>();
        }

        [BurstCompile(CompileSynchronously = true)]
        public void OnUpdate(ref SystemState state)
        {
            var tick = SystemAPI.GetSingleton<SimulationClock>().Tick;
            foreach (var (attack, motion, applied, hits) in
                SystemAPI.Query<RefRW<CombatAttackState>, RefRO<CombatMotion>, RefRO<AppliedCombatCommand>, DynamicBuffer<CombatHitRecord>>()
                    .WithAll<InCombat>())
            {
                var current = attack.ValueRO;
                var pressed = CombatAttackTuning.StrikeButton(applied.ValueRO.Command.Pressed);
                if (current.IsAttacking)
                {
                    var strike = CombatAttackTuning.Get(current.Kind);
                    var elapsed = tick - current.StartTick;
                    if (pressed != CommandButtons.None && elapsed >= strike.WindupTicks)
                        current.Buffered = pressed;
                    var chainPoint = (uint)(strike.WindupTicks + strike.ActiveTicks + strike.ChainDelayTicks);
                    if (current.Buffered != CommandButtons.None && strike.CanChain
                        && current.SeriesLength < CombatAttackTuning.MaxSeriesLength && elapsed >= chainPoint)
                    {
                        Begin(ref current, CombatAttackTuning.Select(current.Buffered, current.Kind), tick, hits);
                        current.SeriesLength++;
                    }
                    else if (elapsed >= (uint)strike.TotalTicks)
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
                if (!current.IsAttacking && pressed != CommandButtons.None && motion.ValueRO.Grounded && !jumps)
                {
                    Begin(ref current, CombatAttackTuning.Select(pressed, CombatAttackKind.None), tick, hits);
                    current.SeriesLength = 1;
                }

                attack.ValueRW = current;
            }
        }

        static void Begin(ref CombatAttackState state, CombatAttackKind kind, uint tick, DynamicBuffer<CombatHitRecord> hits)
        {
            state.Kind = kind;
            state.StartTick = tick;
            state.Buffered = CommandButtons.None;
            hits.Clear();
        }
    }
}
