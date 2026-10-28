using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>
    /// The single consumer of <see cref="CombatCommand"/> and the single writer of
    /// <see cref="AppliedCombatCommand"/>. Runs last in <see cref="CombatCommandCollectionSystemGroup"/>,
    /// once per fixed tick, and applies identical rules to player and AI commands.
    /// </summary>
    /// <remarks>
    /// Per tick T: due pending commands, then new submissions, are validated in submission order; the
    /// commands for T are sorted by (<see cref="CombatantId"/>, submission order) so the first command per
    /// key wins without any dependence on chunk order; every in-combat combatant then receives its accepted
    /// command or an implicit None. Cost is O(C log C + N) for C commands and N combatants: no per-combatant
    /// scan of the queue. Lifetime equals the <see cref="World"/>; <c>OnCreate</c> throws if a queue already
    /// exists, so a world never has two processors. See COMMANDS.md.
    /// </remarks>
    [BurstCompile]
    [UpdateInGroup(typeof(CombatCommandCollectionSystemGroup), OrderLast = true)]
    public partial struct CombatCommandProcessorSystem : ISystem
    {
        Entity m_Queue;
        EntityQuery m_CombatantsWithoutApplied;
        EntityQuery m_ActiveCombatants;

        public void OnCreate(ref SystemState state)
        {
            // Throws if a queue already exists in this world.
            m_Queue = state.EntityManager.CreateSingleton(new CombatCommandQueue(), nameof(CombatCommandQueue));
            state.EntityManager.AddBuffer<CombatCommandSubmission>(m_Queue);
            state.EntityManager.AddBuffer<PendingCombatCommand>(m_Queue);
            state.EntityManager.AddBuffer<CombatCommandResult>(m_Queue);

            m_CombatantsWithoutApplied = SystemAPI.QueryBuilder()
                .WithAll<CombatantId, InCombat>()
                .WithNone<AppliedCombatCommand>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build();
            m_ActiveCombatants = SystemAPI.QueryBuilder().WithAll<CombatantId, InCombat, AppliedCombatCommand>().Build();

            state.RequireForUpdate<SimulationClock>();
        }

        public void OnDestroy(ref SystemState state)
        {
            if (state.EntityManager.Exists(m_Queue))
                state.EntityManager.DestroyEntity(m_Queue);
        }

        [BurstCompile(CompileSynchronously = true)]
        public void OnUpdate(ref SystemState state)
        {
            // Structural change first (only on ticks where combatants were spawned), before any buffer access.
            if (!m_CombatantsWithoutApplied.IsEmptyIgnoreFilter)
                state.EntityManager.AddComponent<AppliedCombatCommand>(m_CombatantsWithoutApplied);

            var tick = SystemAPI.GetSingleton<SimulationClock>().Tick;
            var queue = SystemAPI.GetComponentRW<CombatCommandQueue>(m_Queue);
            var battleStartTick = queue.ValueRO.BattleStartTick;
            var submissions = SystemAPI.GetBuffer<CombatCommandSubmission>(m_Queue);
            var pending = SystemAPI.GetBuffer<PendingCombatCommand>(m_Queue);
            var results = SystemAPI.GetBuffer<CombatCommandResult>(m_Queue);

            // In-combat combatants of this tick (a set, so chunk order never matters).
            var combatantIds = m_ActiveCombatants.ToComponentDataArray<CombatantId>(Allocator.Temp);
            var active = new NativeHashSet<uint>(combatantIds.Length + 1, Allocator.Temp);
            for (var i = 0; i < combatantIds.Length; i++)
                active.Add(combatantIds[i].Value);

            // Sized for the submissions: pending commands that stay pending (up to MaxPendingCommands) are never
            // reported, so reserving for them would waste Temp memory on every catch-up tick of a frame.
            var processed = new NativeList<ProcessedCommand>(submissions.Length, Allocator.Temp);
            var due = new NativeList<ProcessedCommand>(Allocator.Temp);
            var order = 0;

            // 1. Commands kept from earlier ticks were submitted first. Overdue ones (a tick that was not
            // processed) are stale; the rest stay pending in submission order, at most one per key.
            var kept = 0;
            var pendingByKey = new NativeHashMap<ulong, int>(pending.Length + submissions.Length + 1, Allocator.Temp);
            for (var i = 0; i < pending.Length; i++)
            {
                var command = pending[i].Command;
                if (command.Tick == tick)
                    due.Add(new ProcessedCommand(command, order++, CommandOutcome.None));
                else if (command.Tick < tick)
                    processed.Add(new ProcessedCommand(command, order++, CommandOutcome.Stale));
                else
                {
                    pendingByKey.Add(Key(command), kept);
                    pending[kept++] = pending[i];
                }
            }
            pending.ResizeUninitialized(kept);

            // 2. New submissions, in append (= producer) order.
            for (var i = 0; i < submissions.Length; i++)
            {
                var command = submissions[i].Command;
                var outcome = Classify(command, tick, battleStartTick);
                if (outcome == CommandOutcome.None)
                {
                    due.Add(new ProcessedCommand(command, order++, CommandOutcome.None));
                    continue;
                }

                if (outcome == CommandOutcome.Deferred)
                    outcome = Defer(command, active, pending, pendingByKey);
                processed.Add(new ProcessedCommand(command, order++, outcome));
            }
            submissions.Clear();

            // 3. Resolve commands for this tick per combatant: first in submission order wins.
            due.Sort(new CombatantTickOrder());
            var accepted = new NativeHashMap<uint, CombatCommand>(due.Length + 1, Allocator.Temp);
            for (var first = 0; first < due.Length;)
            {
                var winner = due[first].Command;
                var known = active.Contains(winner.Combatant.Value);
                var next = first;
                for (; next < due.Length && due[next].Command.Combatant.Value == winner.Combatant.Value; next++)
                {
                    var entry = due[next];
                    entry.Outcome = !known ? CommandOutcome.UnknownCombatant
                        : next == first ? CommandOutcome.Accepted
                        : SameCommand(entry.Command, winner) ? CommandOutcome.Duplicate
                        : CommandOutcome.Conflict;
                    processed.Add(entry);
                }

                if (known)
                    accepted.Add(winner.Combatant.Value, winner);
                first = next;
            }

            // 4. The single application point: every in-combat combatant gets exactly one command for this tick.
            foreach (var (applied, combatant) in SystemAPI.Query<RefRW<AppliedCombatCommand>, RefRO<CombatantId>>().WithAll<InCombat>())
            {
                applied.ValueRW = accepted.TryGetValue(combatant.ValueRO.Value, out var command)
                    ? new AppliedCombatCommand(command)
                    : AppliedCombatCommand.Missing(combatant.ValueRO, tick);
            }

            // 5. Observable outcome of this tick, in canonical order.
            processed.Sort(new CombatantTickOrder());
            results.Clear();
            for (var i = 0; i < processed.Length; i++)
                results.Add(new CombatCommandResult { Command = processed[i].Command, Outcome = processed[i].Outcome });
            queue.ValueRW.MarkProcessed(tick);

            // Main-thread Temp is only reclaimed at the end of the frame, and this runs once per fixed tick
            // (several per frame on catch-up): release every scratch container here, after its last use,
            // in reverse allocation order.
            accepted.Dispose();
            pendingByKey.Dispose();
            due.Dispose();
            processed.Dispose();
            active.Dispose();
            combatantIds.Dispose();
        }

        /// <summary>
        /// Keeps a valid future command until its tick. Only the first command per key is kept (later ones
        /// can never win), and only for a combatant that is in combat now, so the store holds at most
        /// in-combat combatants × <see cref="CombatCommandQueue.MaxLeadTicks"/> commands; whether the
        /// combatant is still in combat is checked again on the due tick.
        /// </summary>
        static CommandOutcome Defer(in CombatCommand command, NativeHashSet<uint> active,
            DynamicBuffer<PendingCombatCommand> pending, NativeHashMap<ulong, int> pendingByKey)
        {
            if (!active.Contains(command.Combatant.Value))
                return CommandOutcome.UnknownCombatant;
            if (pendingByKey.TryGetValue(Key(command), out var index))
                return SameCommand(command, pending[index].Command) ? CommandOutcome.Duplicate : CommandOutcome.Conflict;
            if (pending.Length >= CombatCommandQueue.MaxPendingCommands)
                return CommandOutcome.QueueFull;

            pendingByKey.Add(Key(command), pending.Length);
            pending.Add(new PendingCombatCommand { Command = command });
            return CommandOutcome.Deferred;
        }

        static ulong Key(in CombatCommand command) => ((ulong)command.Tick << 32) | command.Combatant.Value;

        /// <summary>Source-independent validation. Returns <see cref="CommandOutcome.None"/> for a valid command due on <paramref name="tick"/>.</summary>
        static CommandOutcome Classify(in CombatCommand command, uint tick, uint battleStartTick)
        {
            if (command.Tick == SimulationClock.NoTick)
                return CommandOutcome.InvalidTick;
            if (!command.Combatant.IsValid)
                return CommandOutcome.InvalidCombatant;
            if (command.MoveX < -1 || command.MoveX > 1 || command.MoveZ < -1 || command.MoveZ > 1
                || (command.Held & ~AllButtons) != 0 || (command.Pressed & ~AllButtons) != 0
                || (command.Source != CommandSource.Player && command.Source != CommandSource.AI))
                return CommandOutcome.InvalidValue;
            if (command.Tick <= battleStartTick)
                return CommandOutcome.BeforeBattleStart;
            if (command.Tick < tick)
                return CommandOutcome.Stale;
            if (command.Tick == tick)
                return CommandOutcome.None;
            return command.Tick - tick > CombatCommandQueue.MaxLeadTicks ? CommandOutcome.TooFarAhead : CommandOutcome.Deferred;
        }

        const CommandButtons AllButtons = CommandButtons.Attack | CommandButtons.Jump | CommandButtons.Guard | CommandButtons.Kick;

        static bool SameCommand(in CombatCommand a, in CombatCommand b) =>
            a.Combatant.Value == b.Combatant.Value && a.Tick == b.Tick && a.MoveX == b.MoveX && a.MoveZ == b.MoveZ
            && a.Held == b.Held && a.Pressed == b.Pressed && a.Source == b.Source;

        struct ProcessedCommand
        {
            public CombatCommand Command;
            public int Order;
            public CommandOutcome Outcome;

            public ProcessedCommand(CombatCommand command, int order, CommandOutcome outcome)
            {
                Command = command;
                Order = order;
                Outcome = outcome;
            }
        }

        struct CombatantTickOrder : IComparer<ProcessedCommand>
        {
            public int Compare(ProcessedCommand x, ProcessedCommand y)
            {
                var byCombatant = x.Command.Combatant.Value.CompareTo(y.Command.Combatant.Value);
                if (byCombatant != 0)
                    return byCombatant;
                var byTick = x.Command.Tick.CompareTo(y.Command.Tick);
                return byTick != 0 ? byTick : x.Order.CompareTo(y.Order);
            }
        }
    }
}
