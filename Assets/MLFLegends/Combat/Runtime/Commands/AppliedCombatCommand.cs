using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>
    /// The command a combatant executes on the current tick: the accepted one, or an implicit None when no
    /// command was accepted. The only input of the combat state owners (Stage 02+).
    /// </summary>
    /// <remarks>
    /// Added to combatants and written only by <see cref="CombatCommandProcessorSystem"/>, once per tick for
    /// every combatant with an enabled <see cref="InCombat"/>; read-only outside this assembly.
    /// <c>Command.Tick</c> equals <see cref="SimulationClock.Tick"/> after the command-collection phase.
    /// A combatant whose <see cref="InCombat"/> is disabled keeps its last value; consumers skip it anyway.
    /// Cleared to default by <see cref="CombatCommandQueue.ResetForBattle"/>.
    /// </remarks>
    public struct AppliedCombatCommand : IComponentData
    {
        CombatCommand m_Command;

        public CombatCommand Command => m_Command;

        /// <summary>False when no command was accepted for the tick and <see cref="Command"/> is the implicit None.</summary>
        public bool WasSubmitted => m_Command.Source != CommandSource.None;

        internal AppliedCombatCommand(CombatCommand command) => m_Command = command;

        internal static AppliedCombatCommand Missing(CombatantId combatant, uint tick) =>
            new AppliedCombatCommand(new CombatCommand { Combatant = combatant, Tick = tick });
    }
}
