using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Scene entry point of the human player's input in the default ECS world: while enabled it owns one
    /// <see cref="PlayerInputCapture"/> and attaches it to that World's <see cref="PlayerInputCommandSystem"/>.
    /// Holds no combat state and submits nothing itself.
    /// </summary>
    /// <remarks>
    /// Disable/destroy detaches and disposes the capture (its action clone and subscriptions). The latest enabled adapter
    /// wins, so an additive scene switch or a duplicate adapter replaces the input instead of adding a second producer;
    /// a replaced adapter's later disable does not detach its successor. Application focus changes drop pending presses
    /// and neutralise sampling while unfocused. The actions asset is only read (cloned), never enabled or disabled.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class PlayerInputAdapter : MonoBehaviour
    {
        [Tooltip("Asset with a Player map (Move, Attack, Jump, Guard). Bindings are provisional and remappable.")]
        [SerializeField] InputActionAsset m_Actions;

        PlayerInputCapture m_Capture;
        World m_World;

        /// <summary>The capture this adapter owns while enabled; null otherwise.</summary>
        public PlayerInputCapture Capture => m_Capture;

        void OnEnable()
        {
            if (m_Actions == null)
            {
                Debug.LogError($"{nameof(PlayerInputAdapter)} on '{name}' has no input actions asset.", this);
                return;
            }

            var world = World.DefaultGameObjectInjectionWorld;
            var producer = world != null && world.IsCreated ? world.GetExistingSystemManaged<PlayerInputCommandSystem>() : null;
            if (producer == null)
                return; // No combat World (e.g. during Play Mode exit): nothing to control.

            m_Capture = new PlayerInputCapture(m_Actions);
            m_World = world;
            producer.Attach(m_Capture);
        }

        void OnDisable()
        {
            if (m_Capture == null)
                return;

            // On Play Mode exit the default world may already be disposed, together with its producer.
            if (m_World != null && m_World.IsCreated)
                m_World.GetExistingSystemManaged<PlayerInputCommandSystem>()?.Detach(m_Capture);
            m_Capture.Dispose();
            m_Capture = null;
            m_World = null;
        }

        void OnApplicationFocus(bool hasFocus) => m_Capture?.SetFocused(hasFocus);
    }
}
