using Unity.Entities;
using Unity.Mathematics;

namespace MLFLegends.Combat
{
    /// <summary>Singleton tag of the World's only external impulse input buffer; created and destroyed by CombatMovementSystem.</summary>
    public struct CombatImpulseQueue : IComponentData
    {
    }

    /// <summary>
    /// One external physical impulse J (world units × mass / s) for one combatant: Δv = J / CombatBody.Mass. Producers only
    /// append; CombatMovementSystem is the sole consumer. Everything present before Movement of tick T is applied exactly once
    /// in that Movement call, in buffer order, and then removed (also when rejected). An entry appended after Movement T (for
    /// example by a future Hit-phase producer) is therefore applied in Movement T+1 and never changes the published index of T.
    /// </summary>
    /// <remarks>
    /// <see cref="BattleNumber"/> is the producer's battle epoch: an entry of another battle (paused Restart/End) is dropped.
    /// An unknown, despawned or out-of-combat target is dropped as well. No entry survives its consumption boundary.
    /// </remarks>
    [InternalBufferCapacity(0)]
    public struct CombatImpulse : IBufferElementData
    {
        public CombatantId Target;
        public float3 Impulse;
        public uint BattleNumber;

        /// <summary>Appends <paramref name="impulse"/> for <paramref name="target"/> in the current battle (main thread, between or inside ticks).</summary>
        public static void Submit(EntityManager manager, CombatantId target, float3 impulse)
        {
            using var sessions = manager.CreateEntityQuery(ComponentType.ReadOnly<BattleSession>());
            using var queues = manager.CreateEntityQuery(ComponentType.ReadOnly<CombatImpulseQueue>());
            manager.GetBuffer<CombatImpulse>(queues.GetSingletonEntity()).Add(new CombatImpulse
            {
                Target = target, Impulse = impulse, BattleNumber = sessions.GetSingleton<BattleSession>().BattleNumber,
            });
        }
    }
}
