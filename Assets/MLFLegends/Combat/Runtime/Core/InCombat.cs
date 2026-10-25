using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Marks a combatant that currently takes part in the battle simulation.
    /// Simulation systems query enabled <see cref="InCombat"/>; disabled entities are skipped.
    /// </summary>
    /// <remarks>
    /// Enableable so that leaving the battle (defeat) is a flag flip, not a structural change.
    /// Added and enabled by the spawner; disabled only by the single defeat/removal owner.
    /// A disabled combatant is never re-enabled within the same battle (no respawn).
    /// </remarks>
    public struct InCombat : IComponentData, IEnableableComponent
    {
    }
}
