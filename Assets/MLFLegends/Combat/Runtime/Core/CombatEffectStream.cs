using Unity.Entities;
using Unity.Mathematics;

namespace MLFLegends.Combat
{
    public enum CombatEffectKind : byte { Swing, Impact }

    /// <summary>Numerical confirmation, with no assets or gameplay authority. Times are completed-clock tick coordinates.</summary>
    [InternalBufferCapacity(0)]
    public struct CombatEffectFact : IBufferElementData
    {
        public ulong Sequence;
        public uint Epoch, BattleNumber, AttackerId, TargetId, StartTick;
        public Entity Attacker;
        public CombatEffectKind Kind;
        public int Binding;
        public float Facing;
        public float3 Position;
        public double Time, End;
        public CombatStrikeTiming Timing;
    }

    /// <summary>Hit-owned bounded history. Readers never consume it. Overflow overwrites oldest presentation fact only.</summary>
    public struct CombatEffectStream : IComponentData
    {
        public const int Capacity = 8192;
        public const int RetentionTicks = 120;
        public uint Epoch, LastSwingTick;
        public int First, Count;
        public ulong Sequence, Published, VisualPublished, Expired, Dropped;

        public void Reset()
        {
            Epoch++; LastSwingTick = 0; First = Count = 0; Sequence = Published = VisualPublished = Expired = Dropped = 0;
        }
        public void Trim(DynamicBuffer<CombatEffectFact> facts, uint tick)
        {
            while (Count > 0 && facts[First].End + RetentionTicks <= tick)
            { First = (First + 1) % Capacity; Count--; Expired++; }
        }
        public void Append(DynamicBuffer<CombatEffectFact> facts, CombatEffectFact fact)
        {
            fact.Sequence = ++Sequence; fact.Epoch = Epoch;
            if (Count == Capacity) { First = (First + 1) % Capacity; Count--; Dropped++; }
            facts[(First + Count) % Capacity] = fact; Count++; Published++; if (fact.Binding != 0) VisualPublished++;
        }
    }
}
