using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Authoritative simulation clock: exactly one singleton per combat <see cref="World"/>.
    /// </summary>
    /// <remarks>
    /// Created and destroyed only by <see cref="CombatClockSystem"/>, which is also its only writer
    /// (one increment per fixed simulation tick). Everyone else reads it via
    /// <c>SystemAPI.GetSingleton&lt;SimulationClock&gt;()</c>; the tick cannot be set from outside
    /// this assembly.
    /// <para>
    /// <see cref="Tick"/> is <c>0</c> until the first tick runs. Inside the fixed step it is the tick
    /// being simulated (the first simulated tick is <c>1</c>); outside the fixed step it is the last
    /// completed tick, so the next tick to be simulated is <c>Tick + 1</c>.
    /// </para>
    /// </remarks>
    public struct SimulationClock : IComponentData
    {
        /// <summary>Reserved "no tick" value. Never simulated, never a valid <c>CombatCommand.Tick</c>.</summary>
        public const uint NoTick = 0;

        /// <summary>Largest tick that can be simulated. The clock never wraps back to <see cref="NoTick"/>.</summary>
        public const uint LastTick = uint.MaxValue;

        uint m_Tick;

        public uint Tick => m_Tick;

        /// <summary>True once at least one tick has been simulated in this world.</summary>
        public bool HasStarted => m_Tick != NoTick;

        /// <summary>
        /// Computes the tick that follows <paramref name="tick"/>. Returns false at <see cref="LastTick"/>:
        /// the counter saturates instead of wrapping to the reserved <see cref="NoTick"/>.
        /// </summary>
        public static bool TryGetNextTick(uint tick, out uint next)
        {
            if (tick == LastTick)
            {
                next = LastTick;
                return false;
            }

            next = tick + 1;
            return true;
        }

        internal bool TryAdvance()
        {
            if (!TryGetNextTick(m_Tick, out var next))
                return false;

            m_Tick = next;
            return true;
        }

        /// <summary>Test seam only: a clock positioned at <paramref name="tick"/>.</summary>
        internal static SimulationClock AtTick(uint tick) => new SimulationClock { m_Tick = tick };
    }

    /// <summary>
    /// Tag on the clock entity while the clock can still advance. The clock and every combat phase
    /// group require it, so no combat phase runs in a world without a clock or after the clock stopped.
    /// </summary>
    /// <remarks>
    /// Added and removed only by <see cref="CombatClockSystem"/>. A plain (non-enableable) tag on
    /// purpose: <c>RequireForUpdate</c> ignores the enabled state of enableable components.
    /// </remarks>
    public struct SimulationClockRunning : IComponentData
    {
    }
}
