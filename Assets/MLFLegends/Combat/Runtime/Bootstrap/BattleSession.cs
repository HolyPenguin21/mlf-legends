using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace MLFLegends.Combat
{
    /// <summary>Whether a battle is running in the <see cref="World"/>.</summary>
    public enum BattleState : byte
    {
        /// <summary>No battle: no combatants exist (never started, or ended).</summary>
        Idle = 0,
        Active = 1,
    }

    /// <summary>Spawn values of one combatant. Identity (<see cref="CombatantId"/>) is allocated by the lifecycle, never supplied.</summary>
    public struct CombatantSetup
    {
        public byte Team;

        /// <summary>Initial <see cref="CombatPosition"/>, world units (X/Z ground plane, Y altitude).</summary>
        public float3 Position;

        /// <summary>Initial <see cref="CombatFacing"/>; must be <see cref="FacingDirection.Left"/> or <see cref="FacingDirection.Right"/>.</summary>
        public FacingDirection Facing;

        internal bool IsValid =>
            (Facing == FacingDirection.Left || Facing == FacingDirection.Right) && math.all(math.isfinite(Position));
    }

    /// <summary>
    /// The combatants of a battle: one controlled by the human player, one by AI, and optionally allies of each
    /// on the same team. All get identical components; which one is human is recorded only in <see cref="BattleSession"/>.
    /// </summary>
    /// <remarks>
    /// With no allies (the default) the battle is the 1v1 duel. Allies are ordinary combatants of the same simulation,
    /// spawned behind their leader (<see cref="AllyOf"/>); a team has at most <see cref="MaxCombatantsPerTeam"/> combatants.
    /// </remarks>
    public struct BattleSetup
    {
        /// <summary>Largest team, leader included: 200 vs 200 at most.</summary>
        public const int MaxCombatantsPerTeam = 200;

        /// <summary>Allies per row of the formation behind a leader (a row runs along Z).</summary>
        public const int AlliesPerRow = 10;

        /// <summary>Distance between neighbouring places of a formation, world units.</summary>
        public const float AllySpacing = 1f;

        public CombatantSetup Human;
        public CombatantSetup Ai;

        /// <summary>Combatants on <see cref="Human"/>'s team besides the human one; at most <see cref="MaxCombatantsPerTeam"/> - 1.</summary>
        public ushort HumanAllies;

        /// <summary>Combatants on <see cref="Ai"/>'s team besides the AI leader; at most <see cref="MaxCombatantsPerTeam"/> - 1.</summary>
        public ushort AiAllies;

        /// <summary>Human on team 1 at X = -2 facing right, AI on team 2 at X = +2 facing left, no allies.</summary>
        public static BattleSetup Default => new BattleSetup
        {
            Human = new CombatantSetup { Team = 1, Position = new float3(-2f, 0f, 0f), Facing = FacingDirection.Right },
            Ai = new CombatantSetup { Team = 2, Position = new float3(2f, 0f, 0f), Facing = FacingDirection.Left },
        };

        /// <summary>Combatants the battle spawns: both leaders and all allies.</summary>
        public int CombatantCount => 2 + HumanAllies + AiAllies;

        /// <summary>Valid facings and finite positions for both leaders, on different teams, each team within <see cref="MaxCombatantsPerTeam"/>.</summary>
        public bool IsValid => Human.IsValid && Ai.IsValid && Human.Team != Ai.Team
            && HumanAllies < MaxCombatantsPerTeam && AiAllies < MaxCombatantsPerTeam;

        /// <summary>
        /// Spawn values of ally <paramref name="index"/> (0-based) of <paramref name="leader"/>: same team and facing, in rows of
        /// <see cref="AlliesPerRow"/> behind the leader (away from where it faces), centred on the leader's Z.
        /// </summary>
        internal static CombatantSetup AllyOf(in CombatantSetup leader, int index)
        {
            var back = 1 + index / AlliesPerRow;
            var side = index % AlliesPerRow - (AlliesPerRow - 1) * 0.5f;
            var offset = new float3(-(sbyte)leader.Facing * back, 0f, side) * AllySpacing;
            return new CombatantSetup { Team = leader.Team, Position = leader.Position + offset, Facing = leader.Facing };
        }
    }

    public enum BattleRequestKind : byte
    {
        None = 0,

        /// <summary>Start a battle; rejected with <see cref="BattleRequestOutcome.AlreadyActive"/> while one runs.</summary>
        Start = 1,

        /// <summary>End the running battle (if any) and start a new one.</summary>
        Restart = 2,

        /// <summary>
        /// End the running battle; <see cref="BattleRequestOutcome.NotActive"/> when idle. With a non-zero
        /// <see cref="BattleRequest.Owner"/>, only a battle started by that owner is ended.
        /// </summary>
        End = 3,

        /// <summary>Destroy one combatant of the running battle. Its ID is never reused.</summary>
        Despawn = 4,
    }

    /// <summary>What <see cref="BattleLifecycleSystem"/> did with a <see cref="BattleRequest"/>. Rejections have no side effects.</summary>
    public enum BattleRequestOutcome : byte
    {
        None = 0,
        Started = 1,
        Ended = 2,
        Despawned = 3,

        /// <summary>Start while a battle is running.</summary>
        AlreadyActive = 4,

        /// <summary>End or Despawn while idle.</summary>
        NotActive = 5,

        /// <summary>Despawn of an ID that has no entity.</summary>
        UnknownCombatant = 6,

        /// <summary><see cref="BattleSetup.IsValid"/> is false.</summary>
        InvalidSetup = 7,

        /// <summary>The <see cref="SimulationClock"/> reached its last tick: the World cannot host a new battle.</summary>
        ClockStopped = 8,

        /// <summary>Not enough <see cref="CombatantId"/> values left in this World; IDs never wrap to 0.</summary>
        IdsExhausted = 9,

        /// <summary>End with an <see cref="BattleRequest.Owner"/> that did not start the running battle.</summary>
        NotOwner = 10,

        /// <summary>
        /// Start, Restart or End whose command queue reset is invalid on this tick
        /// (<see cref="CombatCommandQueue.CanResetForBattle"/> is false, e.g. another caller of
        /// <see cref="CombatCommandQueue.ResetForBattle"/> moved the boundary past the current tick).
        /// Checked before anything is destroyed: the running battle, its combatants and IDs are kept.
        /// </summary>
        CommandQueueRejected = 11,
    }

    /// <summary>
    /// A request to the battle lifecycle. Anyone may append it (main thread); <see cref="BattleLifecycleSystem"/>
    /// applies all requests in append order at the next boundary: Initialization of a frame (also while paused)
    /// or the start of a fixed step, before its tick advances (also between catch-up steps of one frame).
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct BattleRequest : IBufferElementData
    {
        public BattleRequestKind Kind;
        public BattleSetup Setup;

        /// <summary>Target of <see cref="BattleRequestKind.Despawn"/>.</summary>
        public CombatantId Combatant;

        /// <summary>
        /// Requester token, 0 = none. Recorded by Start/Restart as <see cref="BattleSession.Owner"/>; an End with a
        /// non-zero owner ends only that owner's battle, so a requester that leaves (e.g. a scene being unloaded after
        /// the next scene already restarted the battle) cannot end a battle someone else started.
        /// </summary>
        public uint Owner;

        public static BattleRequest Start(in BattleSetup setup, uint owner = 0) =>
            new BattleRequest { Kind = BattleRequestKind.Start, Setup = setup, Owner = owner };

        public static BattleRequest Restart(in BattleSetup setup, uint owner = 0) =>
            new BattleRequest { Kind = BattleRequestKind.Restart, Setup = setup, Owner = owner };

        public static BattleRequest End(uint owner = 0) => new BattleRequest { Kind = BattleRequestKind.End, Owner = owner };

        public static BattleRequest Despawn(CombatantId combatant) => new BattleRequest { Kind = BattleRequestKind.Despawn, Combatant = combatant };

        /// <summary>Appends <paramref name="request"/> to the World's battle request buffer.</summary>
        /// <exception cref="InvalidOperationException">The world has no <see cref="BattleLifecycleSystem"/>.</exception>
        public static void Submit(EntityManager entityManager, in BattleRequest request)
        {
            // A query built from an EntityManager is Persistent until the World is disposed: released before any throw.
            var builder = new EntityQueryBuilder(Allocator.Temp);
            var query = builder.WithAllRW<BattleRequest>().Build(entityManager);
            builder.Dispose();
            var session = query.CalculateEntityCount() == 1 ? query.GetSingletonEntity() : Entity.Null;
            query.Dispose();
            if (session == Entity.Null)
                throw new InvalidOperationException("BattleRequest.Submit requires exactly one BattleSession (BattleLifecycleSystem).");
            entityManager.GetBuffer<BattleRequest>(session).Add(request);
        }
    }

    /// <summary>
    /// Singleton battle lifecycle state, one per <see cref="World"/>: the authoritative <see cref="BattleStartTick"/>
    /// and which combatant the human controls. Its entity also holds the <see cref="BattleRequest"/> buffer.
    /// </summary>
    /// <remarks>
    /// Created, destroyed and written only by <see cref="BattleLifecycleSystem"/>. It is not a clock: relative
    /// battle time is derived from <see cref="SimulationClock.Tick"/>. See BOOTSTRAP.md next to this file.
    /// </remarks>
    public struct BattleSession : IComponentData
    {
        BattleState m_State;
        BattleRequestOutcome m_LastOutcome;
        uint m_BattleStartTick;
        uint m_BattleNumber;
        uint m_Owner;
        CombatantId m_Human;
        CombatantId m_Ai;

        public BattleState State => m_State;

        public bool IsActive => m_State == BattleState.Active;

        /// <summary>
        /// <see cref="SimulationClock.Tick"/> when the current (or last) battle started. Its first simulated tick is
        /// <c>BattleStartTick + 1</c>; the same value is the command queue's boundary.
        /// </summary>
        public uint BattleStartTick => m_BattleStartTick;

        /// <summary>Battles started in this World so far (1 for the first one).</summary>
        public uint BattleNumber => m_BattleNumber;

        /// <summary>Combatant controlled by the human player in the current battle; <see cref="CombatantId.Invalid"/> when idle.</summary>
        public CombatantId HumanCombatant => m_Human;

        /// <summary>Combatant controlled by AI in the current battle; <see cref="CombatantId.Invalid"/> when idle.</summary>
        public CombatantId AiCombatant => m_Ai;

        /// <summary><see cref="BattleRequest.Owner"/> of the request that started the current battle; 0 when idle or unowned.</summary>
        public uint Owner => m_Owner;

        /// <summary>Outcome of the last applied <see cref="BattleRequest"/>.</summary>
        public BattleRequestOutcome LastOutcome => m_LastOutcome;

        /// <summary>Battle-relative tick: 1 on the first simulated tick of the battle. Only for ticks at or after <see cref="BattleStartTick"/>.</summary>
        public uint RelativeTick(uint absoluteTick) => absoluteTick - m_BattleStartTick;

        internal void SetOutcome(BattleRequestOutcome outcome) => m_LastOutcome = outcome;

        internal void Begin(uint battleStartTick, uint owner, CombatantId human, CombatantId ai)
        {
            m_State = BattleState.Active;
            m_Owner = owner;
            m_BattleStartTick = battleStartTick;
            m_BattleNumber++;
            m_Human = human;
            m_Ai = ai;
        }

        internal void Finish()
        {
            m_State = BattleState.Idle;
            m_Owner = 0;
            m_Human = CombatantId.Invalid;
            m_Ai = CombatantId.Invalid;
        }
    }
}
