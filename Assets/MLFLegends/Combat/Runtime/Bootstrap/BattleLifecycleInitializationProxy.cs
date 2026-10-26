using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Initialization entry point of the single <see cref="BattleLifecycleSystem"/>: updates that existing instance at
    /// the start of every frame, so End/Restart apply on paused frames with no fixed step.
    /// </summary>
    /// <remarks>
    /// No logic and no state of its own: it never reads <see cref="BattleRequest"/>, never touches the session, IDs,
    /// combatants, queue or clock. The lifecycle stays registered only in FixedStep (one <c>[UpdateInGroup]</c>;
    /// Entities 6.5 forbids a second one) and its own RequireForUpdate still decides whether it runs. Skips worlds
    /// without a lifecycle (no session) and a destroyed lifecycle. Not Burst-compiled: a thin managed-side forward.
    /// OrderFirst only orders it among Initialization's normal systems; other OrderFirst systems (Unity's
    /// BeginInitialization ECB) are not ordered against it, which the lifecycle does not depend on.
    /// </remarks>
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    public partial struct BattleLifecycleInitializationProxy : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<BattleSession>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // Looked up every frame: no cached handle can outlive the lifecycle or depend on creation order.
            var lifecycle = state.WorldUnmanaged.GetExistingUnmanagedSystem<BattleLifecycleSystem>();
            if (lifecycle != SystemHandle.Null)
                lifecycle.Update(state.WorldUnmanaged);
        }
    }
}
