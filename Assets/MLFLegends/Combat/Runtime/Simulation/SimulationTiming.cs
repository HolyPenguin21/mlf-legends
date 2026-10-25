namespace MLFLegends.Combat
{
    /// <summary>
    /// The single place that defines the combat simulation rate. Nothing in it depends on the
    /// render frame rate.
    /// </summary>
    /// <remarks>
    /// 60 Hz is a calibration value (see Docs/DECISIONS.md, open questions), not an approved final
    /// rate. <see cref="CombatClockSystem"/> applies <see cref="TickDuration"/> to the world's
    /// <c>FixedStepSimulationSystemGroup</c>; no other code sets that group's timestep. Simulation
    /// systems that need a time step use <see cref="TickDuration"/> and count time in ticks.
    /// </remarks>
    public static class SimulationTiming
    {
        /// <summary>Simulation ticks per second of simulated time.</summary>
        public const uint TickRateHz = 60;

        /// <summary>Simulated seconds covered by one tick.</summary>
        public const float TickDuration = 1f / TickRateHz;
    }
}
