using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Raw combat input of the human player: the current device state of the Player actions and the button presses not yet
    /// consumed by a tick. Not a command queue: it holds no <see cref="CombatCommand"/>, no tick and no combatant.
    /// </summary>
    /// <remarks>
    /// Owns a private clone of the asset's Player map, its callbacks and one <see cref="InputSystem.onDeviceChange"/> /
    /// <see cref="InputSystem.onAfterUpdate"/> subscription; <see cref="Dispose"/> releases all of them. The shared asset
    /// (the project-wide actions) is never enabled or disabled here. Presses are recorded by the action callbacks during
    /// the Input System update (dynamic update: PreUpdate of the frame, before the frame's fixed ticks), so a tap that is
    /// pressed and released between two ticks still reaches the next tick. Edges are dropped, never replayed, while
    /// <c>Time.timeScale</c> is 0, while unfocused, on device loss/reset and by <see cref="ClearEdges"/> (the producer
    /// calls it on every rebinding). Read only by <see cref="PlayerInputCommandSystem"/> on the main thread.
    /// </remarks>
    public sealed class PlayerInputCapture : IDisposable
    {
        public const string MapName = "Player";

        /// <summary>Stick magnitude below which the move is idle.</summary>
        public const float MoveDeadZone = 0.25f;

        // sin(22.5°): an axis counts when it is at least this fraction of the stick's magnitude, i.e. 8 equal sectors.
        const float k_AxisFraction = 0.38268343f;

        readonly InputActionMap m_Map;
        readonly InputAction m_Move;
        readonly InputAction m_Attack;
        readonly InputAction m_Jump;
        readonly InputAction m_Guard;
        readonly Action<InputAction.CallbackContext> m_OnAttack;
        readonly Action<InputAction.CallbackContext> m_OnJump;
        readonly Action<InputAction.CallbackContext> m_OnGuard;
        readonly Action<InputDevice, InputDeviceChange> m_OnDeviceChange;
        readonly Action m_OnAfterUpdate;
        CommandButtons m_Pressed;
        bool m_Focused = true;

        /// <exception cref="ArgumentNullException"><paramref name="actions"/> is null.</exception>
        /// <exception cref="ArgumentException">The asset has no Player map with Move, Attack, Jump and Guard.</exception>
        public PlayerInputCapture(InputActionAsset actions)
        {
            if (actions == null)
                throw new ArgumentNullException(nameof(actions));

            // Lookups first: a missing action throws before anything is subscribed or enabled.
            m_Map = actions.FindActionMap(MapName, throwIfNotFound: true).Clone();
            m_Move = m_Map.FindAction("Move", throwIfNotFound: true);
            m_Attack = m_Map.FindAction("Attack", throwIfNotFound: true);
            m_Jump = m_Map.FindAction("Jump", throwIfNotFound: true);
            m_Guard = m_Map.FindAction("Guard", throwIfNotFound: true);

            m_OnAttack = _ => OnPressed(CommandButtons.Attack);
            m_OnJump = _ => OnPressed(CommandButtons.Jump);
            m_OnGuard = _ => OnPressed(CommandButtons.Guard);
            m_OnDeviceChange = OnDeviceChange;
            m_OnAfterUpdate = OnAfterUpdate;
            m_Attack.performed += m_OnAttack;
            m_Jump.performed += m_OnJump;
            m_Guard.performed += m_OnGuard;
            InputSystem.onDeviceChange += m_OnDeviceChange;
            InputSystem.onAfterUpdate += m_OnAfterUpdate;

            // Button actions perform no initial state check: a button already held now is Held, never Pressed.
            m_Map.Enable();
        }

        public bool IsDisposed { get; private set; }

        /// <summary>Presses recorded since the last <see cref="TakePressed"/>/<see cref="ClearEdges"/>; for diagnostics and tests.</summary>
        public CommandButtons PendingPressed => m_Pressed;

        /// <summary>
        /// Current device state: digital move axes and held buttons. Neutral while unfocused or disposed, so losing focus
        /// never leaves the fighter moving.
        /// </summary>
        public void Sample(out sbyte moveX, out sbyte moveZ, out CommandButtons held)
        {
            moveX = moveZ = 0;
            held = CommandButtons.None;
            if (IsDisposed || !m_Focused)
                return;

            Quantize(m_Move.ReadValue<Vector2>(), out moveX, out moveZ);
            if (m_Attack.IsPressed())
                held |= CommandButtons.Attack;
            if (m_Jump.IsPressed())
                held |= CommandButtons.Jump;
            if (m_Guard.IsPressed())
                held |= CommandButtons.Guard;
        }

        /// <summary>Returns the presses since the previous call and forgets them: each press reaches exactly one tick.</summary>
        public CommandButtons TakePressed()
        {
            var pressed = m_Pressed;
            m_Pressed = CommandButtons.None;
            return pressed;
        }

        /// <summary>Drops unconsumed presses; Held/Move stay the live device state.</summary>
        public void ClearEdges() => m_Pressed = CommandButtons.None;

        /// <summary>Application focus. Both directions drop pending presses; unfocused input samples as neutral.</summary>
        public void SetFocused(bool focused)
        {
            m_Focused = focused;
            m_Pressed = CommandButtons.None;
        }

        /// <summary>
        /// The one explicit quantization policy of an analog or composite move to the digital command axes: idle inside
        /// <see cref="MoveDeadZone"/>, otherwise each axis whose share of the magnitude is at least sin 22.5° gives its
        /// sign (8 equal 45° sectors). Keyboard composites (±1, normalized diagonals) map exactly. Input Y is world Z.
        /// </summary>
        public static void Quantize(Vector2 value, out sbyte x, out sbyte z)
        {
            x = z = 0;
            var magnitude = value.magnitude;
            if (!float.IsFinite(magnitude) || magnitude < MoveDeadZone)
                return;

            var threshold = magnitude * k_AxisFraction;
            if (Mathf.Abs(value.x) >= threshold)
                x = (sbyte)(value.x > 0f ? 1 : -1);
            if (Mathf.Abs(value.y) >= threshold)
                z = (sbyte)(value.y > 0f ? 1 : -1);
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;
            IsDisposed = true;
            m_Pressed = CommandButtons.None;
            InputSystem.onAfterUpdate -= m_OnAfterUpdate;
            InputSystem.onDeviceChange -= m_OnDeviceChange;
            m_Attack.performed -= m_OnAttack;
            m_Jump.performed -= m_OnJump;
            m_Guard.performed -= m_OnGuard;
            m_Map.Disable();
            m_Map.Dispose();
        }

        void OnPressed(CommandButtons button)
        {
            // Combat presses are not collected on pause or without focus; resuming never fires an old press.
            if (m_Focused && Time.timeScale > 0f)
                m_Pressed |= button;
        }

        void OnAfterUpdate()
        {
            // Also drops a tap of the last unpaused frame that no tick consumed before the pause.
            if (Time.timeScale <= 0f)
                m_Pressed = CommandButtons.None;
        }

        void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            switch (change)
            {
                case InputDeviceChange.Removed:
                case InputDeviceChange.Disconnected:
                case InputDeviceChange.Disabled:
                case InputDeviceChange.SoftReset:
                case InputDeviceChange.HardReset:
                    // The actions cancel their lost controls themselves (Held/Move drop to neutral).
                    m_Pressed = CommandButtons.None;
                    break;
            }
        }
    }
}
