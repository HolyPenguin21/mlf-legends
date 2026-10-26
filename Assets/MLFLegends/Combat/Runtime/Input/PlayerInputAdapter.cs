using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Scene entry point of the human player's input in the default ECS world: while enabled it owns one
    /// <see cref="PlayerInputCapture"/> and keeps it attached to the current default World's
    /// <see cref="PlayerInputCommandSystem"/>. Holds no combat state and submits nothing itself.
    /// </summary>
    /// <remarks>
    /// The binding follows <c>World.DefaultGameObjectInjectionWorld</c>: checked on enable and once per frame (a reference
    /// comparison), it attaches when a World appears after this component was enabled and moves from World A to B without
    /// a disable/enable, detaching from A (if A still exists). Each World is tried once: an adapter that lost to a later
    /// one does not retry every frame. The producer resolves competition by enable order (the latest enabled adapter wins,
    /// whichever attaches first), so an additive scene switch or a duplicate adapter replaces the input instead of adding a
    /// second producer, and a replaced adapter's disable cannot detach its successor. Disable/destroy detaches and disposes
    /// the capture (its action clone and subscriptions). Application focus changes are forwarded to the capture. The
    /// actions asset is only read (cloned), never enabled or disabled. The static enable counter only orders adapters
    /// (like the bootstrap's owner token); it holds no combat state.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class PlayerInputAdapter : MonoBehaviour
    {
        [Tooltip("Asset with a Player map (Move, Attack, Jump, Guard). Bindings are provisional and remappable.")]
        [SerializeField] InputActionAsset m_Actions;

        static uint s_LastEnable;

        PlayerInputCapture m_Capture;
        World m_World;
        uint m_Order;

        /// <summary>The capture this adapter owns while enabled; null otherwise.</summary>
        public PlayerInputCapture Capture => m_Capture;

        /// <summary>The default World this adapter last bound to (attached, or lost to a later adapter); null without one.</summary>
        public World BoundWorld => m_World;

        void OnEnable()
        {
            if (m_Actions == null)
            {
                Debug.LogError($"{nameof(PlayerInputAdapter)} on '{name}' has no input actions asset.", this);
                return;
            }

            m_Order = ++s_LastEnable;
            if (m_Order == 0)
                m_Order = ++s_LastEnable; // wrapped: 0 is the producer's "no order"
            m_Capture = new PlayerInputCapture(m_Actions);
            m_World = null;
            SyncWorld();
        }

        void Update()
        {
            if (m_Capture != null)
                SyncWorld();
        }

        void OnDisable()
        {
            if (m_Capture == null)
                return;

            ReleaseWorld();
            m_Capture.Dispose();
            m_Capture = null;
        }

        void OnApplicationFocus(bool hasFocus) => m_Capture?.SetFocused(hasFocus);

        void SyncWorld()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world != null && !world.IsCreated)
                world = null;
            if (world == m_World)
                return;

            ReleaseWorld();
            m_World = world;
            var producer = world?.GetExistingSystemManaged<PlayerInputCommandSystem>();
            if (producer != null)
                producer.Attach(m_Capture, m_Order); // false: a later adapter owns this World's input
            else
                m_Capture.ClearEdges(); // no combat World: nothing may carry over to the next one
        }

        void ReleaseWorld()
        {
            // On Play Mode exit or a World switch the old World may already be disposed, together with its producer.
            if (m_World != null && m_World.IsCreated)
                m_World.GetExistingSystemManaged<PlayerInputCommandSystem>()?.Detach(m_Capture);
            m_World = null;
        }
    }
}
