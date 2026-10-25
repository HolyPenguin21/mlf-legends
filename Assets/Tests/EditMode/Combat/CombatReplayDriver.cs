using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MLFLegends.Combat;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;

namespace MLFLegends.Tests.EditMode
{
    // Replay driver for the Stage 01 integration tests (Issue #6). Test-only: it adds no runtime system, owns no combat
    // state and copies no runtime rule. It builds an isolated World from the real runtime systems, ordered by their own
    // [UpdateInGroup]/[UpdateBefore]/[UpdateAfter] attributes through the same path the default world uses, and adds:
    //   - two producers in the command-collection phase (player, then AI) that append a scripted command list to the
    //     single CombatCommandSubmission buffer, keyed by battle-relative tick and logical role;
    //   - a recorder in the publication phase that captures every completed tick canonically (sorted by CombatantId,
    //     never by EntityQuery/chunk order), raw and normalized (roles H/A, relative ticks);
    //   - optional in-step requesters that submit a BattleRequest from a chosen phase of a chosen tick.
    // Replay input = BattleSetup + script (+ the between-frame requests a test makes). There is no seed: Stage 01 has no
    // RNG, movement or attack system that could consume one, so the record covers only state that exists today.

    /// <summary>Logical role of a command target; the actual <see cref="CombatantId"/> differs per battle and World.</summary>
    public enum ReplayRole : byte
    {
        Human,
        Ai,

        /// <summary>Human combatant of the previous battle in this World; a never-issued ID while there is none.</summary>
        PreviousHuman,
        Invalid,
    }

    public struct ScriptedCommand
    {
        /// <summary>Relative tick in whose command-collection phase the producer submits the command.</summary>
        public uint SubmitOn;

        /// <summary>Relative tick the command is for (<c>BattleStartTick + For</c>).</summary>
        public uint For;

        public ReplayRole Role;
        public sbyte MoveX;
        public sbyte MoveZ;
        public CommandButtons Held;
        public CommandButtons Pressed;

        /// <summary>Player commands are submitted by the player producer, AI commands by the AI producer (after it).</summary>
        public CommandSource Source;
    }

    /// <summary>The scripted producer input of one battle, identical for every battle it is used in.</summary>
    public sealed class ReplayScript
    {
        readonly List<ScriptedCommand> m_Commands = new List<ScriptedCommand>();

        public IReadOnlyList<ScriptedCommand> Commands => m_Commands;

        public ReplayScript Player(uint on, uint @for, ReplayRole role, sbyte moveX = 0, sbyte moveZ = 0,
            CommandButtons held = CommandButtons.None, CommandButtons pressed = CommandButtons.None) =>
            Add(on, @for, role, moveX, moveZ, held, pressed, CommandSource.Player);

        public ReplayScript Ai(uint on, uint @for, ReplayRole role, sbyte moveX = 0, sbyte moveZ = 0,
            CommandButtons held = CommandButtons.None, CommandButtons pressed = CommandButtons.None) =>
            Add(on, @for, role, moveX, moveZ, held, pressed, CommandSource.AI);

        ReplayScript Add(uint on, uint @for, ReplayRole role, sbyte moveX, sbyte moveZ, CommandButtons held,
            CommandButtons pressed, CommandSource source)
        {
            m_Commands.Add(new ScriptedCommand
            {
                SubmitOn = on, For = @for, Role = role, MoveX = moveX, MoveZ = moveZ, Held = held, Pressed = pressed, Source = source,
            });
            return this;
        }
    }

    public struct CombatantRecord
    {
        public uint Id;
        public string Label;
        public byte Team;
        public Unity.Mathematics.float3 Position;
        public FacingDirection Facing;
        public bool InCombat;
        public bool HasApplied;
        public AppliedCombatCommand Applied;
    }

    public struct ResultRecord
    {
        public string Label;
        public long RelativeTick;
        public CombatCommand Command;
        public CommandOutcome Outcome;

        /// <summary>Compact form for expectations: <c>"H r3 Conflict"</c>.</summary>
        public override string ToString() => $"{Label} r{RelativeTick} {Outcome}";
    }

    /// <summary>What one completed fixed tick left behind, read in its publication phase.</summary>
    public sealed class TickRecord
    {
        public uint Tick;
        public bool Active;
        public uint BattleNumber;
        public uint BattleStartTick;
        public uint QueueBattleStartTick;
        public uint LastProcessedTick;
        public uint HumanId;
        public uint AiId;

        /// <summary>All combatants (defeated ones too), sorted by <see cref="CombatantId"/>.</summary>
        public CombatantRecord[] Combatants;

        /// <summary>The tick's <see cref="CombatCommandResult"/> buffer, in its own (canonical) order.</summary>
        public ResultRecord[] Results;

        /// <summary>Pending future commands after the tick, sorted by (<see cref="CombatantId"/>, tick).</summary>
        public CombatCommand[] Pending;

        /// <summary>Same values with raw IDs and absolute ticks.</summary>
        public string[] Raw;

        /// <summary>Same values with roles (H, A, foreign, invalid) and battle-relative ticks: comparable across battles and Worlds.</summary>
        public string[] Normalized;

        public uint RelativeTick => Tick - BattleStartTick;

        public string[] ResultSummary => Results.Select(r => r.ToString()).ToArray();

        public CombatantRecord Combatant(uint id) => Combatants.Single(c => c.Id == id);
    }

    /// <summary>An isolated World with the real clock, lifecycle and command processor, driven frame by frame.</summary>
    public sealed class ReplayWorld : IDisposable
    {
        /// <summary>
        /// The runtime systems of one battle World, as the default world has them. No hand-made order: the root-level
        /// initialisation sorts them by their attributes.
        /// </summary>
        public static readonly Type[] RuntimeSystems =
        {
            typeof(FixedStepSimulationSystemGroup),
            typeof(BeginFixedStepSimulationEntityCommandBufferSystem),
            typeof(EndFixedStepSimulationEntityCommandBufferSystem),
            typeof(BattleLifecycleSystem),
            typeof(CombatClockSystem),
            typeof(CombatCommandCollectionSystemGroup),
            typeof(CombatCommandProcessorSystem),
            typeof(CombatStateUpdateSystemGroup),
            typeof(CombatPublicationSystemGroup),
        };

        /// <summary>A <see cref="CombatantId"/> that no test World ever issues.</summary>
        public const uint NeverIssuedId = uint.MaxValue;

        public readonly World World;
        public readonly FixedStepSimulationSystemGroup FixedStep;
        public readonly List<TickRecord> Records = new List<TickRecord>();

        /// <summary>Producer input of the current battle; applied to every battle of this World.</summary>
        public ReplayScript Script = new ReplayScript();

        readonly Dictionary<Type, InStepRequester> m_Requesters = new Dictionary<Type, InStepRequester>();
        uint m_Frame;
        uint m_TrackedBattle;
        CombatantId m_Human;
        CombatantId m_Ai;
        CombatantId m_PreviousHuman;

        public ReplayWorld(string name)
        {
            World = new World(name);
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(World, RuntimeSystems);
            FixedStep = World.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();

            AddToPhase<PlayerCommandProducer>(typeof(CombatCommandCollectionSystemGroup)).Driver = this;
            AddToPhase<AiCommandProducer>(typeof(CombatCommandCollectionSystemGroup)).Driver = this;
            AddToPhase<ReplayRecorder>(typeof(CombatPublicationSystemGroup)).Driver = this;
        }

        public void Dispose()
        {
            if (World.IsCreated)
                World.Dispose();
        }

        public EntityManager Manager => World.EntityManager;

        // ---- time ----

        /// <summary>One render frame per fixed step.</summary>
        public void Step(int ticks = 1)
        {
            for (var i = 0; i < ticks; i++)
                Frame(1);
        }

        /// <summary>
        /// One render frame that runs <paramref name="ticks"/> fixed steps (catch-up when more than one), half a tick past
        /// the last one, as in the existing clock tests. At most 18 so World.MaximumDeltaTime never drops a tick.
        /// </summary>
        public void Frame(int ticks)
        {
            if (ticks < 1 || ticks > 18)
                throw new ArgumentOutOfRangeException(nameof(ticks));
            var dt = (double)SimulationTiming.TickDuration;
            World.SetTime(new TimeData((m_Frame + ticks - 0.5) * dt, (float)(ticks * dt)));
            FixedStep.Update();
            m_Frame += (uint)ticks;
        }

        // ---- input between frames ----

        public void Request(BattleRequest request) => BattleRequest.Submit(Manager, request);

        public void SubmitBetweenFrames(params CombatCommand[] commands)
        {
            var buffer = Manager.GetBuffer<CombatCommandSubmission>(QueueEntity);
            foreach (var command in commands)
                buffer.Add(new CombatCommandSubmission(command));
        }

        /// <summary>Submits <paramref name="request"/> from <paramref name="phase"/> of relative tick <paramref name="relativeTick"/> of battle <paramref name="battle"/>.</summary>
        public void RequestInStep(Type phase, uint battle, uint relativeTick, BattleRequest request)
        {
            if (!m_Requesters.TryGetValue(phase, out var requester))
            {
                requester = phase == typeof(CombatCommandCollectionSystemGroup) ? AddToPhase<CollectionPhaseRequester>(phase)
                    : phase == typeof(CombatStateUpdateSystemGroup) ? AddToPhase<StateUpdatePhaseRequester>(phase)
                    : phase == typeof(CombatPublicationSystemGroup) ? (InStepRequester)AddToPhase<PublicationPhaseRequester>(phase)
                    : throw new ArgumentException($"Not a combat phase: {phase.Name}", nameof(phase));
                m_Requesters.Add(phase, requester);
            }

            requester.Pending.Add((battle, relativeTick, request));
        }

        // ---- reads ----

        public uint Tick => Singleton<SimulationClock>().Tick;

        public BattleSession Session => Singleton<BattleSession>();

        public CombatCommandQueue Queue => Singleton<CombatCommandQueue>();

        public Entity QueueEntity
        {
            get
            {
                using var query = Manager.CreateEntityQuery(typeof(CombatCommandQueue));
                return query.GetSingletonEntity();
            }
        }

        public int SubmissionCount => Manager.GetBuffer<CombatCommandSubmission>(QueueEntity).Length;

        public int PendingCount => Manager.GetBuffer<PendingCombatCommand>(QueueEntity).Length;

        public int ResultCount => Manager.GetBuffer<CombatCommandResult>(QueueEntity).Length;

        /// <summary>IDs of every combatant entity (defeated ones included), ascending.</summary>
        public uint[] LiveIds
        {
            get
            {
                using var query = Manager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new ComponentType[] { typeof(CombatantId) },
                    Options = EntityQueryOptions.IgnoreComponentEnabledState,
                });
                using var ids = query.ToComponentDataArray<CombatantId>(Allocator.Temp);
                return ids.ToArray().Select(id => id.Value).OrderBy(v => v).ToArray();
            }
        }

        public int CountWith<T>()
        {
            using var query = Manager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { typeof(T) },
                Options = EntityQueryOptions.IgnoreComponentEnabledState | EntityQueryOptions.IncludeDisabledEntities,
            });
            return query.CalculateEntityCount();
        }

        /// <summary>Every entity of the World, systems' own entities included; for before/after leak comparisons.</summary>
        public int EntityCount
        {
            get
            {
                using var all = Manager.GetAllEntities(Allocator.Temp);
                return all.Length;
            }
        }

        T Singleton<T>() where T : unmanaged, IComponentData
        {
            using var query = Manager.CreateEntityQuery(typeof(T));
            return query.GetSingleton<T>();
        }

        // ---- logs ----

        /// <summary>One line per completed tick of battle <paramref name="battle"/>: roles and relative ticks only.</summary>
        public string[] NormalizedLog(uint battle) => Records.Where(r => r.Active && r.BattleNumber == battle)
            .Select(r => $"r{r.RelativeTick}: {string.Join(" | ", r.Normalized)}").ToArray();

        /// <summary>One line per completed tick of the World, idle ticks included: absolute ticks and raw IDs.</summary>
        public string[] RawLog => Records.Select(r =>
            $"t{r.Tick} battle{r.BattleNumber}{(r.Active ? "" : " idle")} start{r.BattleStartTick}: {string.Join(" | ", r.Raw)}").ToArray();

        public TickRecord[] BattleRecords(uint battle) => Records.Where(r => r.Active && r.BattleNumber == battle).ToArray();

        // ---- used by the test systems ----

        internal void Track(in BattleSession session)
        {
            if (!session.IsActive || session.BattleNumber == m_TrackedBattle)
                return;
            if (m_TrackedBattle != 0)
                m_PreviousHuman = m_Human;
            m_TrackedBattle = session.BattleNumber;
            m_Human = session.HumanCombatant;
            m_Ai = session.AiCombatant;
        }

        internal CombatantId Resolve(ReplayRole role)
        {
            switch (role)
            {
                case ReplayRole.Human: return m_Human;
                case ReplayRole.Ai: return m_Ai;
                case ReplayRole.PreviousHuman: return m_PreviousHuman.IsValid ? m_PreviousHuman : new CombatantId { Value = NeverIssuedId };
                default: return CombatantId.Invalid;
            }
        }

        T AddToPhase<T>(Type phase) where T : SystemBase, new()
        {
            var system = World.CreateSystemManaged<T>();
            var group = (ComponentSystemGroup)World.GetExistingSystemManaged(phase);
            group.AddSystemToUpdateList(system);
            group.SortSystems();
            return system;
        }

        internal static TickRecord Capture(EntityManager manager, EntityQuery combatants, Entity queueEntity, uint tick, in BattleSession session)
        {
            var queue = manager.GetComponentData<CombatCommandQueue>(queueEntity);
            var human = session.HumanCombatant.Value;
            var ai = session.AiCombatant.Value;
            var start = session.BattleStartTick;
            string Label(uint id) => id == 0 ? "invalid" : id == human ? "H" : id == ai ? "A" : "foreign";

            var record = new TickRecord
            {
                Tick = tick,
                Active = session.IsActive,
                BattleNumber = session.BattleNumber,
                BattleStartTick = start,
                QueueBattleStartTick = queue.BattleStartTick,
                LastProcessedTick = queue.LastProcessedTick,
                HumanId = human,
                AiId = ai,
            };

            using (var entities = combatants.ToEntityArray(Allocator.Temp))
            {
                record.Combatants = entities.ToArray().Select(entity =>
                {
                    var id = manager.GetComponentData<CombatantId>(entity).Value;
                    var hasApplied = manager.HasComponent<AppliedCombatCommand>(entity);
                    return new CombatantRecord
                    {
                        Id = id,
                        Label = Label(id),
                        Team = manager.GetComponentData<CombatTeam>(entity).Value,
                        Position = manager.GetComponentData<CombatPosition>(entity).Value,
                        Facing = manager.GetComponentData<CombatFacing>(entity).Value,
                        InCombat = manager.IsComponentEnabled<InCombat>(entity),
                        HasApplied = hasApplied,
                        Applied = hasApplied ? manager.GetComponentData<AppliedCombatCommand>(entity) : default,
                    };
                }).OrderBy(c => c.Id).ToArray();
            }

            record.Results = manager.GetBuffer<CombatCommandResult>(queueEntity, true).AsNativeArray().ToArray()
                .Select(r => new ResultRecord
                {
                    Label = Label(r.Command.Combatant.Value),
                    RelativeTick = (long)r.Command.Tick - start,
                    Command = r.Command,
                    Outcome = r.Outcome,
                }).ToArray();
            record.Pending = manager.GetBuffer<PendingCombatCommand>(queueEntity, true).AsNativeArray().ToArray()
                .Select(p => p.Command).OrderBy(c => c.Combatant.Value).ThenBy(c => c.Tick).ToArray();

            record.Raw = Lines(record, record.Results, record.Pending, c => "#" + c.Combatant.Value, t => "t" + t);
            // The processor orders by raw CombatantId, and IDs differ between battles (a previous battle's fighter sorts
            // before the current ones, a never-issued ID after them): re-establish the order in role space. Stable, so
            // the processor's (tick, submission) order is kept within one role.
            int Rank(uint id) => id == 0 ? 3 : id == human ? 0 : id == ai ? 1 : 2;
            record.Normalized = Lines(record,
                record.Results.OrderBy(r => Rank(r.Command.Combatant.Value)).ToArray(),
                record.Pending.OrderBy(p => Rank(p.Combatant.Value)).ThenBy(p => p.Tick).ToArray(),
                c => Label(c.Combatant.Value), t => "r" + ((long)t - start));
            return record;
        }

        static string[] Lines(TickRecord record, ResultRecord[] results, CombatCommand[] pending,
            Func<CombatCommand, string> who, Func<uint, string> when)
        {
            string Command(CombatCommand c) =>
                $"{who(c)} {when(c.Tick)} x{c.MoveX} z{c.MoveZ} held:{c.Held} pressed:{c.Pressed} {c.Source}";
            string Float(float v) => v.ToString("R", CultureInfo.InvariantCulture);

            var lines = new List<string>();
            foreach (var c in record.Combatants)
            {
                var self = new CombatCommand { Combatant = new CombatantId { Value = c.Id } };
                var applied = !c.HasApplied ? "none"
                    : Command(c.Applied.Command) + (c.Applied.WasSubmitted ? "" : " implicit");
                lines.Add($"{who(self)} team{c.Team} pos({Float(c.Position.x)},{Float(c.Position.y)},{Float(c.Position.z)}) "
                    + $"{c.Facing} {(c.InCombat ? "in-combat" : "out")} applied[{applied}]");
            }

            lines.AddRange(results.Select(r => $"result {Command(r.Command)} -> {r.Outcome}"));
            lines.AddRange(pending.Select(p => $"pending {Command(p)}"));
            return lines.ToArray();
        }
    }

    // ---- test systems (never auto-created; added to an isolated World by ReplayWorld only) ----

    /// <summary>Appends the scripted commands of the current relative tick to the single submission buffer.</summary>
    public abstract partial class ScriptedCommandProducer : SystemBase
    {
        public ReplayWorld Driver;
        EntityQuery m_Clock;
        EntityQuery m_Session;
        EntityQuery m_Queue;

        protected abstract CommandSource Source { get; }

        protected override void OnCreate()
        {
            m_Clock = GetEntityQuery(ComponentType.ReadOnly<SimulationClock>());
            m_Session = GetEntityQuery(ComponentType.ReadOnly<BattleSession>());
            m_Queue = GetEntityQuery(ComponentType.ReadWrite<CombatCommandSubmission>());
        }

        protected override void OnUpdate()
        {
            var session = m_Session.GetSingleton<BattleSession>();
            if (!session.IsActive)
                return;
            Driver.Track(session);

            var tick = m_Clock.GetSingleton<SimulationClock>().Tick;
            var relative = session.RelativeTick(tick);
            var buffer = EntityManager.GetBuffer<CombatCommandSubmission>(m_Queue.GetSingletonEntity());
            foreach (var scripted in Driver.Script.Commands)
            {
                if (scripted.SubmitOn != relative || scripted.Source != Source)
                    continue;
                buffer.Add(new CombatCommandSubmission(new CombatCommand
                {
                    Combatant = Driver.Resolve(scripted.Role),
                    Tick = session.BattleStartTick + scripted.For,
                    MoveX = scripted.MoveX,
                    MoveZ = scripted.MoveZ,
                    Held = scripted.Held,
                    Pressed = scripted.Pressed,
                    Source = scripted.Source,
                }));
            }
        }
    }

    // Stand-in for the future player input adapter: same buffer, same tick, runs first.
    [DisableAutoCreation]
    [UpdateBefore(typeof(AiCommandProducer))]
    public partial class PlayerCommandProducer : ScriptedCommandProducer
    {
        protected override CommandSource Source => CommandSource.Player;
    }

    // Stand-in for the future AI: same buffer, same tick, after the player producer.
    [DisableAutoCreation]
    public partial class AiCommandProducer : ScriptedCommandProducer
    {
        protected override CommandSource Source => CommandSource.AI;
    }

    /// <summary>Captures every completed tick in the publication phase (read-only).</summary>
    [DisableAutoCreation]
    public partial class ReplayRecorder : SystemBase
    {
        public ReplayWorld Driver;
        EntityQuery m_Clock;
        EntityQuery m_Session;
        EntityQuery m_Queue;
        EntityQuery m_Combatants;

        protected override void OnCreate()
        {
            m_Clock = GetEntityQuery(ComponentType.ReadOnly<SimulationClock>());
            m_Session = GetEntityQuery(ComponentType.ReadOnly<BattleSession>());
            m_Queue = GetEntityQuery(ComponentType.ReadOnly<CombatCommandQueue>());
            m_Combatants = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CombatantId>() },
                Options = EntityQueryOptions.IgnoreComponentEnabledState,
            });
        }

        protected override void OnUpdate()
        {
            var session = m_Session.GetSingleton<BattleSession>();
            Driver.Track(session);
            Driver.Records.Add(ReplayWorld.Capture(EntityManager, m_Combatants, m_Queue.GetSingletonEntity(),
                m_Clock.GetSingleton<SimulationClock>().Tick, session));
        }
    }

    /// <summary>A requester inside the fixed step (e.g. a future defeat owner), keyed by battle number and relative tick.</summary>
    public abstract partial class InStepRequester : SystemBase
    {
        public readonly List<(uint Battle, uint RelativeTick, BattleRequest Request)> Pending =
            new List<(uint Battle, uint RelativeTick, BattleRequest Request)>();
        EntityQuery m_Clock;
        EntityQuery m_Session;

        protected override void OnCreate()
        {
            m_Clock = GetEntityQuery(ComponentType.ReadOnly<SimulationClock>());
            m_Session = GetEntityQuery(ComponentType.ReadOnly<BattleSession>());
        }

        protected override void OnUpdate()
        {
            var session = m_Session.GetSingleton<BattleSession>();
            if (!session.IsActive)
                return;
            var relative = session.RelativeTick(m_Clock.GetSingleton<SimulationClock>().Tick);
            foreach (var (battle, relativeTick, request) in Pending)
            {
                if (battle == session.BattleNumber && relativeTick == relative)
                    BattleRequest.Submit(EntityManager, request);
            }
        }
    }

    [DisableAutoCreation]
    public partial class CollectionPhaseRequester : InStepRequester
    {
    }

    [DisableAutoCreation]
    public partial class StateUpdatePhaseRequester : InStepRequester
    {
    }

    [DisableAutoCreation]
    public partial class PublicationPhaseRequester : InStepRequester
    {
    }
}
