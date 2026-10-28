using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>Logical vertical cylinder, independent of art: X/Z radius and height above the position at its base.</summary>
    public struct CombatBody : IComponentData
    {
        public float Radius;
        public float Height;
        public float Mass;
    }
}
