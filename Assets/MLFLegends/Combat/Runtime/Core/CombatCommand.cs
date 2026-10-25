using System;
using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>Logical combat buttons. Bindings to physical keys belong to the input layer.</summary>
    [Flags]
    public enum CommandButtons : byte
    {
        None = 0,
        Attack = 1 << 0,
        Jump = 1 << 1,
        Guard = 1 << 2,
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
    /// The intention of one combatant for one simulation tick. Exactly one slot per combatant;
    /// the producer of that combatant (player input or AI) overwrites it, the command processor
    /// only reads it. See CONTRACTS.md next to this file for the full lifecycle.
    /// </summary>
    /// <remarks>
    /// A command applies only on the tick equal to <see cref="Tick"/>. A command whose
    /// <see cref="Tick"/> differs from the tick being simulated is stale and is treated exactly as
    /// <see cref="None"/>. Tick 0 is never simulated, so <c>default</c> is always a no-op command.
    /// </remarks>
    public struct CombatCommand : IComponentData
    {
        /// <summary>Simulation tick this command is meant for. 0 means "no command".</summary>
        public uint Tick;

        /// <summary>Desired movement along world X: -1, 0 or +1.</summary>
        public sbyte MoveX;

        /// <summary>Desired movement along world Z (depth): -1, 0 or +1.</summary>
        public sbyte MoveZ;

        /// <summary>Buttons held down when the command was sampled.</summary>
        public CommandButtons Held;

        /// <summary>
        /// Buttons pressed at least once since the producer's previous command. May contain a
        /// button that is no longer <see cref="Held"/> (a tap shorter than one tick).
        /// </summary>
        public CommandButtons Pressed;

        public CommandSource Source;

        /// <summary>No movement, no buttons: the combatant performs no new action.</summary>
        public static CombatCommand None => default;
    }
}
