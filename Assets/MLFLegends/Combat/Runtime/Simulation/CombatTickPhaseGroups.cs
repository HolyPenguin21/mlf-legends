using Unity.Entities;

namespace MLFLegends.Combat
{
    // Ordering anchors for the phases of one fixed simulation tick. They contain no logic and no
    // systems yet; future owners join them with [UpdateInGroup]. Order inside FixedStepSimulationSystemGroup:
    // BattleLifecycleSystem (battle requests, before the tick advances) → CombatClockSystem → command collection
    // → state update → publication. See SIMULATION.md.

    /// <summary>
    /// Base of the combat phase groups: a phase runs only while the world's clock runs, so no
    /// combat system ever updates without a freshly advanced <see cref="SimulationClock.Tick"/>.
    /// </summary>
    public abstract partial class CombatTickPhaseSystemGroup : ComponentSystemGroup
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            RequireForUpdate<SimulationClockRunning>();
        }
    }

    /// <summary>
    /// Phase 1 of a tick: producers (player input adapter, AI) write their <c>CombatCommand</c> for the
    /// current <see cref="SimulationClock.Tick"/> in a fixed order; the single command processor (#4)
    /// runs last in this group and decides which commands are accepted.
    /// </summary>
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(CombatClockSystem))]
    public partial class CombatCommandCollectionSystemGroup : CombatTickPhaseSystemGroup
    {
    }

    /// <summary>
    /// Phase 2 of a tick: the single owners of authoritative combat state (action state, movement,
    /// later collisions, hits, defeat) apply the accepted commands of the current tick.
    /// </summary>
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(CombatCommandCollectionSystemGroup))]
    public partial class CombatStateUpdateSystemGroup : CombatTickPhaseSystemGroup
    {
    }

    /// <summary>
    /// Phase 3 of a tick: read-only publication of the tick's results (snapshots, events) for
    /// presentation, UI and AI. Nothing here writes authoritative combat state.
    /// </summary>
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(CombatStateUpdateSystemGroup))]
    public partial class CombatPublicationSystemGroup : CombatTickPhaseSystemGroup
    {
    }
}
