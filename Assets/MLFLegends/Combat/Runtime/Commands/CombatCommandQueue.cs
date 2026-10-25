using System;
using Unity.Collections;
using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Producer entry point: one <see cref="CombatCommand"/> submitted to the single command queue.
    /// Player input adapters and AI append to the singleton buffer on the
    /// <see cref="CombatCommandQueue"/> entity and never touch any other command or combat state.
    /// </summary>
    /// <remarks>
    /// Append order is submission order, which decides the winner of a duplicate key (first wins).
    /// Producers inside <see cref="CombatCommandCollectionSystemGroup"/> target the current
    /// <see cref="SimulationClock.Tick"/>; producers outside the fixed step target <c>Tick + 1</c> or later.
    /// The buffer is drained by <see cref="CombatCommandProcessorSystem"/> on every tick.
    /// </remarks>
    [InternalBufferCapacity(0)]
    public struct CombatCommandSubmission : IBufferElementData
    {
        public CombatCommand Command;

        public CombatCommandSubmission(CombatCommand command) => Command = command;
    }

    /// <summary>
    /// Future commands kept by the processor until their tick, in submission order.
    /// Private storage of <see cref="CombatCommandProcessorSystem"/>; bounded by
    /// <see cref="CombatCommandQueue.MaxPendingCommands"/>.
    /// </summary>
    [InternalBufferCapacity(0)]
    internal struct PendingCombatCommand : IBufferElementData
    {
        public CombatCommand Command;
    }

    /// <summary>What the processor decided for one submitted command. Identical for every <see cref="CommandSource"/>.</summary>
    public enum CommandOutcome : byte
    {
        None = 0,

        /// <summary>First valid command for its (combatant, tick) key; applied on this tick.</summary>
        Accepted = 1,

        /// <summary>Valid future command, kept until its tick (it is reported again with its final outcome then).</summary>
        Deferred = 2,

        /// <summary>Later command for an already accepted key, equal to the accepted one.</summary>
        Duplicate = 3,

        /// <summary>Later command for an already accepted key that differs from it (values or source).</summary>
        Conflict = 4,

        /// <summary>Tick already simulated (including future commands whose tick was not processed).</summary>
        Stale = 5,

        /// <summary>Tick at or before <see cref="CombatCommandQueue.BattleStartTick"/>: belongs to a previous battle.</summary>
        BeforeBattleStart = 6,

        /// <summary>Tick more than <see cref="CombatCommandQueue.MaxLeadTicks"/> ahead of the current tick.</summary>
        TooFarAhead = 7,

        /// <summary><see cref="SimulationClock.NoTick"/>.</summary>
        InvalidTick = 8,

        /// <summary><see cref="CombatantId.Invalid"/>.</summary>
        InvalidCombatant = 9,

        /// <summary>No entity with this <see cref="CombatantId"/> and an enabled <see cref="InCombat"/> on its tick.</summary>
        UnknownCombatant = 10,

        /// <summary>Move outside -1..1, undefined button bits, or a source other than Player/AI.</summary>
        InvalidValue = 11,

        /// <summary>Future command dropped because the pending store is full.</summary>
        QueueFull = 12,
    }

    /// <summary>Observable outcome of one command processed on <see cref="CombatCommandQueue.LastProcessedTick"/>.</summary>
    /// <remarks>
    /// Written only by <see cref="CombatCommandProcessorSystem"/>; rebuilt every tick, sorted by
    /// (<c>Combatant</c>, <c>Tick</c>, submission order). For tests, logs and diagnostics; gameplay reads
    /// <see cref="AppliedCombatCommand"/>.
    /// </remarks>
    [InternalBufferCapacity(0)]
    public struct CombatCommandResult : IBufferElementData
    {
        public CombatCommand Command;
        public CommandOutcome Outcome;
    }

    /// <summary>
    /// Singleton of the single command queue. Its entity also holds the <see cref="CombatCommandSubmission"/>,
    /// pending and <see cref="CombatCommandResult"/> buffers.
    /// </summary>
    /// <remarks>
    /// Created and destroyed by <see cref="CombatCommandProcessorSystem"/>, which is its only writer apart
    /// from <see cref="ResetForBattle"/>. See COMMANDS.md next to this file.
    /// </remarks>
    public struct CombatCommandQueue : IComponentData
    {
        /// <summary>How far ahead of the current tick a command may be submitted (32 ticks ≈ 0.53 s at 60 Hz).</summary>
        public const uint MaxLeadTicks = 32;

        /// <summary>Bound of the pending store: one command per tick of lead for 400 combatants.</summary>
        public const int MaxPendingCommands = 400 * (int)MaxLeadTicks;

        uint m_BattleStartTick;
        uint m_LastProcessedTick;

        /// <summary>
        /// Boundary passed to the last <see cref="ResetForBattle"/>: commands for this tick or earlier are
        /// rejected. <see cref="SimulationClock.NoTick"/> until the first reset.
        /// </summary>
        public uint BattleStartTick => m_BattleStartTick;

        /// <summary>Tick whose outcomes the <see cref="CombatCommandResult"/> buffer holds.</summary>
        public uint LastProcessedTick => m_LastProcessedTick;

        internal void MarkProcessed(uint tick) => m_LastProcessedTick = tick;

        /// <summary>
        /// Battle cleanup contract for the battle lifecycle owner (#5), used when a battle ends and when a new
        /// one begins in the same <see cref="World"/>. Drops every submitted, pending and reported command,
        /// clears <see cref="AppliedCombatCommand"/> on all entities and rejects every later command whose tick
        /// is not after <paramref name="battleStartTick"/>.
        /// </summary>
        /// <remarks>
        /// Call on the main thread at a tick boundary: between fixed steps, or inside the command-collection
        /// phase before the processor. <paramref name="battleStartTick"/> is the battle's <c>BattleStartTick</c>
        /// (normally the current <see cref="SimulationClock.Tick"/>); the first tick the new battle accepts
        /// commands for is <c>battleStartTick + 1</c>. Does not touch the clock, combatants or any other state.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The world has no command processor.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="battleStartTick"/> is before the current tick or before the previous boundary.
        /// </exception>
        public static void ResetForBattle(EntityManager entityManager, uint battleStartTick)
        {
            Entity queueEntity;
            using (var queueQuery = entityManager.CreateEntityQuery(ComponentType.ReadWrite<CombatCommandQueue>()))
            {
                if (queueQuery.CalculateEntityCount() != 1)
                    throw new InvalidOperationException($"{nameof(ResetForBattle)} requires exactly one {nameof(CombatCommandQueue)}.");
                queueEntity = queueQuery.GetSingletonEntity();
            }

            using (var clockQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<SimulationClock>()))
            {
                if (!clockQuery.IsEmpty && battleStartTick < clockQuery.GetSingleton<SimulationClock>().Tick)
                    throw new ArgumentOutOfRangeException(nameof(battleStartTick), battleStartTick, "A battle cannot start on an already simulated tick.");
            }

            var queue = entityManager.GetComponentData<CombatCommandQueue>(queueEntity);
            if (battleStartTick < queue.m_BattleStartTick)
                throw new ArgumentOutOfRangeException(nameof(battleStartTick), battleStartTick, "Battle boundaries never move back.");

            entityManager.GetBuffer<CombatCommandSubmission>(queueEntity).Clear();
            entityManager.GetBuffer<PendingCombatCommand>(queueEntity).Clear();
            entityManager.GetBuffer<CombatCommandResult>(queueEntity).Clear();
            queue.m_BattleStartTick = battleStartTick;
            entityManager.SetComponentData(queueEntity, queue);

            using (var appliedQuery = entityManager.CreateEntityQuery(ComponentType.ReadWrite<AppliedCombatCommand>()))
            using (var cleared = new NativeArray<AppliedCombatCommand>(appliedQuery.CalculateEntityCount(), Allocator.Temp))
            {
                appliedQuery.CopyFromComponentDataArray(cleared);
            }
        }
    }
}
