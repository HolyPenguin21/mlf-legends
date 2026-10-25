using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Side a combatant fights for. Combatants with equal values are allies.
    /// Assigned once by the spawner and never changed during a battle.
    /// </summary>
    public struct CombatTeam : IComponentData
    {
        public byte Value;
    }
}
