using System;

namespace MLFLegends.Combat
{
    /// <summary>Logical combat buttons. Bindings to physical keys belong to the input layer.</summary>
    /// <remarks>
    /// #69 migration: <see cref="HandSeries"/>/<see cref="FootSeries"/> keep the values of the former general Attack/Kick;
    /// the exact-limb intentions PunchLeft/PunchRight/KickLeft/KickRight/KickSpin (bits 4..8) are retired — the processor
    /// rejects them as InvalidValue, so a pre-#69 replay that used them is NOT compatible and their bits are never reused.
    /// </remarks>
    [Flags]
    public enum CommandButtons : ushort
    {
        None = 0,

        /// <summary>Hand series (U): Hand1, Hand1, Hand2, Hand1, Hand2, …</summary>
        HandSeries = 1 << 0,
        Jump = 1 << 1,

        /// <summary>Reserved for the Stage 06 block (future K); unbound and ignored by the simulation.</summary>
        Guard = 1 << 2,

        /// <summary>Foot series (I): Foot1, Foot1, Foot2, Foot1, Foot2, …</summary>
        FootSeries = 1 << 3,

        /// <summary>Stand-alone physical ability (O); never part of a series, never buffered.</summary>
        PhysicalAbility = 1 << 9,

        /// <summary>Toggle nearest-enemy orientation (F); Pressed only, retired bits 4..8 stay unused.</summary>
        FocusToggle = 1 << 10,
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
