using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>
    /// The single allocator of <see cref="CombatantId"/>: monotonic and never reusing a value for the lifetime of
    /// its <see cref="World"/>, across any number of battles. Lives on the <see cref="BattleSession"/> entity.
    /// </summary>
    /// <remarks>
    /// Written only by <see cref="BattleLifecycleSystem"/>. Starts again at 1 only in a new World. Never wraps to
    /// <see cref="CombatantId.Invalid"/>: when too few values remain, the reservation fails as a whole.
    /// </remarks>
    internal struct CombatantIdAllocator : IComponentData
    {
        uint m_LastIssued;

        /// <summary>Last value handed out; 0 before the first allocation.</summary>
        public uint LastIssued => m_LastIssued;

        /// <summary>Reserves <paramref name="count"/> consecutive IDs starting at <paramref name="first"/>, or none.</summary>
        public bool TryReserve(uint count, out uint first)
        {
            first = 0;
            if (count == 0 || count > uint.MaxValue - m_LastIssued)
                return false;

            first = m_LastIssued + 1;
            m_LastIssued += count;
            return true;
        }

        /// <summary>Test seam only: an allocator whose last issued value is <paramref name="lastIssued"/>.</summary>
        internal static CombatantIdAllocator AfterIssued(uint lastIssued) => new CombatantIdAllocator { m_LastIssued = lastIssued };
    }
}
