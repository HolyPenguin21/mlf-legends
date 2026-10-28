using System;

namespace MLFLegends.Combat
{
    /// <summary>Logical combat buttons. Bindings to physical keys belong to the input layer.</summary>
    [Flags]
    public enum CommandButtons : ushort
    {
        None = 0,
        Attack = 1 << 0,
        Jump = 1 << 1,
        Guard = 1 << 2,

        /// <summary>Foot strike; <see cref="Attack"/> is the hand strike (Stage 05.1).</summary>
        Kick = 1 << 3,

        // Exact strike intentions. Old values above remain stable; no physical keys belong in the simulation.
        PunchLeft = 1 << 4,
        PunchRight = 1 << 5,
        KickLeft = 1 << 6,
        KickRight = 1 << 7,
        KickSpin = 1 << 8,
    }

    /// <summary>
    /// Who produced a command. Diagnostics only: command processing must apply identical rules
    /// regardless of the source.
    /// </summary>
    public enum CommandSource : byte
    {
        None = 0,
        Player = 1,
        AI = 2,
    }

    /// <summary>
    /// The intention of one combatant for one simulation tick, identified by
    /// (<see cref="Combatant"/>, <see cref="Tick"/>). Player input and AI produce the same value.
    /// </summary>
    /// <remarks>
    /// A plain unmanaged value: how commands are queued, stored and consumed is owned by the single
    /// command processor (Issue #4), which wraps it in its own ECS storage. Acceptance rules
    /// (none, stale, future, duplicate, unknown combatant) are defined in CONTRACTS.md next to this file.
    /// </remarks>
    public struct CombatCommand
    {
        public CombatantId Combatant;

        /// <summary>Simulation tick this command is meant for. Tick 0 is never simulated.</summary>
        public uint Tick;

        /// <summary>Desired movement along world X: -1, 0 or +1.</summary>
        public sbyte MoveX;

        /// <summary>Desired movement along world Z (depth): -1, 0 or +1.</summary>
        public sbyte MoveZ;

        /// <summary>Buttons held down when the command was sampled.</summary>
        public CommandButtons Held;

        /// <summary>
        /// Buttons pressed at least once since the producer's previous command for this combatant.
        /// May contain a button that is no longer <see cref="Held"/> (a tap shorter than one tick).
        /// </summary>
        public CommandButtons Pressed;

        public CommandSource Source;

        /// <summary>No movement and no buttons: the combatant performs no new action this tick.</summary>
        public bool IsNone => MoveX == 0 && MoveZ == 0 && Held == CommandButtons.None && Pressed == CommandButtons.None;
    }
}
