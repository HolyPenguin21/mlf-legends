using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>Horizontal facing along world X. The value is the sign of the X axis.</summary>
    public enum FacingDirection : sbyte
    {
        Left = -1,
        Right = 1,
    }

    /// <summary>
    /// Authoritative horizontal facing of a combatant. Attacks and front guard are resolved
    /// against this value; sprites only display it.
    /// </summary>
    /// <remarks>
    /// Initialised by the spawner (0 is not a valid direction); afterwards written only by
    /// CombatFacingSystem (Stage 02.2) on the fixed simulation tick, from the sign of the accepted MoveX.
    /// The command processor, movement and presentation do not write it.
    /// </remarks>
    public struct CombatFacing : IComponentData
    {
        public FacingDirection Value;
    }
}
