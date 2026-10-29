using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>Logical vertical cylinder, independent of art: X/Z radius and height above the position at its base.</summary>
    /// <summary>Spawn height and Movement's requested height; accepted height lives only in CombatBody.Height.</summary>
    public struct CombatBodyProfile : IComponentData
    {
        public float BaseHeight, RequestedHeight;
        byte m_RestorePending;
        public bool RestorePending { get => m_RestorePending != 0; set => m_RestorePending = value ? (byte)1 : (byte)0; }
    }

    public struct CombatBody : IComponentData
    {
        public float Radius;
        public float Height;
        public float Mass;
    }
}
