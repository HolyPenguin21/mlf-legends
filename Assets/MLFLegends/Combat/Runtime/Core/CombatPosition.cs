using Unity.Entities;
using Unity.Mathematics;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Authoritative simulation position of a combatant, in world units.
    /// X/Z is the horizontal ground plane (Z is depth); Y is altitude above the world origin.
    /// </summary>
    /// <remarks>
    /// This is the only source of truth for combat position. Rendering transforms
    /// (e.g. <c>LocalTransform</c>) are derived from it and never written back.
    /// Initialised by the spawner; afterwards written only by the single movement integration
    /// owner on the fixed simulation tick. Later collision resolution extends that owner's
    /// pipeline instead of becoming a second writer.
    /// </remarks>
    public struct CombatPosition : IComponentData
    {
        public float3 Value;
    }
}
