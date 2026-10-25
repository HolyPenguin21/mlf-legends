using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Immutable identity of a combatant. Written once by the spawner when the entity is created
    /// and never changed afterwards; every other system only reads it.
    /// </summary>
    /// <remarks>
    /// <see cref="Id"/> is unique within one battle and is never reused, even after the combatant
    /// is defeated or its entity is destroyed. <see cref="InvalidId"/> (0) is reserved.
    /// Use <see cref="Id"/>, not <see cref="Entity"/>, for anything that must stay stable across
    /// structural changes or replays (ledgers, logs, AI memory, deterministic ordering).
    /// </remarks>
    public struct CombatantIdentity : IComponentData
    {
        public const uint InvalidId = 0;

        public uint Id;

        /// <summary>Side the combatant fights for. Combatants with equal values are allies.</summary>
        public byte Team;
    }
}
