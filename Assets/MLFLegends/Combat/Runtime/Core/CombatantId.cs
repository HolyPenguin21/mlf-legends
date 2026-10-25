using System;
using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Immutable World-scoped identity of a combatant. Assigned once by the spawner, the single
    /// owner of ID allocation, and never changed afterwards; every other system only reads it.
    /// </summary>
    /// <remarks>
    /// Unique within one ECS World and never reused across battle restarts, even after the fighter is defeated or its
    /// entity is destroyed. <see cref="Invalid"/> (0) is reserved.
    /// Use this, not <see cref="Entity"/>, for anything that must stay stable across structural
    /// changes or replays (commands, ledgers, logs, AI memory, deterministic ordering).
    /// </remarks>
    public struct CombatantId : IComponentData, IEquatable<CombatantId>
    {
        public static CombatantId Invalid => default;

        public uint Value;

        public bool IsValid => Value != 0;

        public bool Equals(CombatantId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is CombatantId other && Equals(other);

        public override int GetHashCode() => (int)Value;
    }
}
