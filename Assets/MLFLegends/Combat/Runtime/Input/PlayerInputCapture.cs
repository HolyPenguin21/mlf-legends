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
    /// calls it on every rebinding). Across a focus change or a device reset/disable/enable (the Input System's own
    /// focus cycle: reset or disable on loss, enable + sync or reset on regain) a button that was held stays
    /// <em>blocked</em> until the device reports it released: its restored hold is Held, never a new Pressed; the next
    /// real press after that release counts. Read only by <see cref="PlayerInputCommandSystem"/> on the main thread.
    /// </remarks>
    public sealed class PlayerInputCapture : IDisposable
    {
        public const string MapName = "Player";

        /// <summary>Stick magnitude below which the move is idle.</summary>
        public const float MoveDeadZone = 0.25f;

        // sin(22.5°): an axis counts when it is at least this fraction of the stick's magnitude, i.e. 8 equal sectors.
        const float k_AxisFraction = 0.38268343f;

        // #69 layout: U hand series, I foot series, O physical ability, Space jump. K (Stage 06 block), double-tap dash and
        // 7/8/9/0 magic slots are deliberately unbound; the old exact-limb actions were removed from the asset.
        static readonly string[] s_ButtonNames = { "HandSeries", "FootSeries", "PhysicalAbility", "Jump", "FocusToggle" };
        static readonly CommandButtons[] s_ButtonFlags =
        {
            CommandButtons.HandSeries, CommandButtons.FootSeries, CommandButtons.PhysicalAbility, CommandButtons.Jump, CommandButtons.FocusToggle,
        };

        readonly InputActionMap m_Map;
        readonly InputAction m_Move;
        readonly InputAction[] m_Buttons;
        readonly Action<InputAction.CallbackContext>[] m_OnButtons;
        readonly Action<InputDevice, InputDeviceChange> m_OnDeviceChange;
        readonly Action m_OnAfterUpdate;
        CommandButtons m_Pressed;
        CommandButtons m_LastHeld;  // device state at the end of the last Input System update
        CommandButtons m_Blocked;   // held across an interruption; presses ignored until a real release
        bool m_Interrupted;         // a reset/disable/focus change since the last update: its releases are not real
        bool m_Focused = true;

        /// <exception cref="ArgumentNullException"><paramref name="actions"/> is null.</exception>
        /// <exception cref="ArgumentException">The asset has no Player map with Move, HandSeries, FootSeries, PhysicalAbility and Jump.</exception>
        public PlayerInputCapture(InputActionAsset actions)
        {
            if (actions == null)
                throw new ArgumentNullException(nameof(actions));

            // Validated on the shared map first: a missing action throws before anything is cloned, subscribed or enabled.
            // FindActionMap returns null (does not throw) on an asset without maps.
            var shared = actions.FindActionMap(MapName, throwIfNotFound: true)
                ?? throw new ArgumentException($"No '{MapName}' action map in '{actions.name}'.", nameof(actions));
            shared.FindAction("Move", throwIfNotFound: true);
            foreach (var name in s_ButtonNames)
                shared.FindAction(name, throwIfNotFound: true);
            m_Map = shared.Clone();
            m_Move = m_Map.FindAction("Move", throwIfNotFound: true);
            // Arrays/delegates are created once per capture, never while sampling or producing a tick.
            m_Buttons = new InputAction[s_ButtonNames.Length];
            m_OnButtons = new Action<InputAction.CallbackContext>[s_ButtonNames.Length];
            for (var i = 0; i < m_Buttons.Length; i++)
            {
                var flag = s_ButtonFlags[i]; // one captured VALUE per callback; no loop-index aliasing
                m_Buttons[i] = m_Map.FindAction(s_ButtonNames[i], throwIfNotFound: true);
                m_OnButtons[i] = _ => OnPressed(flag);
                m_Buttons[i].performed += m_OnButtons[i];
            }
            m_OnDeviceChange = OnDeviceChange;
            m_OnAfterUpdate = OnAfterUpdate;
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
            held = HeldButtons();
        }

        /// <summary>Buttons a press of which is ignored until the device reports them released (diagnostics and tests).</summary>
        public CommandButtons BlockedButtons => m_Blocked;

        CommandButtons HeldButtons()
        {
            var held = CommandButtons.None;
            for (var i = 0; i < m_Buttons.Length; i++)
                if (IsHeld(m_Buttons[i]))
                    held |= s_ButtonFlags[i];
            return held;
        }

        /// <summary>
        /// Device state of the action's bound buttons, not the action phase: a button already down when this capture was
        /// enabled is Held at once (Button actions have no initial state check), still without a Pressed.
        /// </summary>
        static bool IsHeld(InputAction action)
        {
            var controls = action.controls;
            for (var i = 0; i < controls.Count; i++)
            {
                if (controls[i].IsPressed())
                    return true;
            }

            return false;
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

        /// <summary>
        /// Application focus. Both directions drop pending presses and block the buttons held so far; unfocused input
        /// samples as neutral.
        /// </summary>
        public void SetFocused(bool focused)
        {
            m_Focused = focused;
            Interrupt();
        }

        void Interrupt()
        {
            m_Pressed = CommandButtons.None;
            m_Blocked |= m_LastHeld;
            m_Interrupted = true;
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
            m_Pressed = m_Blocked = m_LastHeld = CommandButtons.None;
            InputSystem.onAfterUpdate -= m_OnAfterUpdate;
            InputSystem.onDeviceChange -= m_OnDeviceChange;
            for (var i = 0; i < m_Buttons.Length; i++)
                m_Buttons[i].performed -= m_OnButtons[i];
            m_Map.Disable();
            m_Map.Dispose();
        }

        void OnPressed(CommandButtons button)
        {
            // Combat presses are not collected on pause or without focus; resuming never fires an old press. A button
            // held across a focus change/device reset is re-reported by the device as a press: blocked until released.
            if (m_Focused && Time.timeScale > 0f && (m_Blocked & button) == 0)
                m_Pressed |= button;
        }

        void OnAfterUpdate()
        {
            var held = HeldButtons();
            // A held → released transition unblocks, unless it is the synthetic release of a reset/disable.
            if (m_Interrupted)
                m_Interrupted = false;
            else
                m_Blocked &= ~(m_LastHeld & ~held);
            m_LastHeld = held;

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
                    // The actions cancel their lost controls themselves (Held/Move drop to neutral).
                    m_Pressed = CommandButtons.None;
                    break;
                case InputDeviceChange.Disabled:
                case InputDeviceChange.Enabled:
                case InputDeviceChange.SoftReset:
                case InputDeviceChange.HardReset:
                    // The focus cycle and explicit resets: a still-held button may come back as a press after it.
                    Interrupt();
                    break;
            }
        }
    }
}
