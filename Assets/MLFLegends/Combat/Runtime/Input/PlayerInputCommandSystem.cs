using Unity.Entities;

namespace MLFLegends.Combat
{
    /// <summary>
    /// The single tick-aligned producer of the human player's <see cref="CombatCommand"/>: on every real fixed tick it
    /// submits at most one Player command for the current <see cref="BattleSession.HumanCombatant"/>, for the tick being
    /// simulated, to the existing <see cref="CombatCommandSubmission"/> buffer, before the processor (OrderLast).
    /// </summary>
    /// <remarks>
    /// Reads one attached <see cref="PlayerInputCapture"/>: Move/Held are the live device state on every tick (so catch-up
    /// ticks of one frame repeat them), Pressed is taken once, by the first tick that produces a command. Without an
    /// attached capture (replay, benchmark and manual worlds, or a scene without <see cref="PlayerInputAdapter"/>) it does
    /// nothing. It produces nothing and drops pending presses while the battle is idle or the human is despawned or not in
    /// combat. <see cref="Attach"/> binds to the session as it is at that moment and drops older presses, so a fresh tap
    /// right after attaching to a running battle reaches its first tick. A later change of battle number or human
    /// combatant (and a new World, i.e. a new system instance) drops pending presses before the first command of the new
    /// binding, so no press of a previous battle reaches the next one, also when a Restart lands between catch-up ticks. Writes nothing but the submission; never the clock, session, IDs, applied commands,
    /// position, motion or facing. One instance per World (auto-created in the default world); no static state.
    /// </remarks>
    [UpdateInGroup(typeof(CombatCommandCollectionSystemGroup))]
    public partial class PlayerInputCommandSystem : SystemBase
    {
        PlayerInputCapture m_Source;
        uint m_SourceOrder;
        uint m_BoundBattle;
        CombatantId m_BoundHuman;
        EntityQuery m_Sessions;

        /// <summary>The attached capture, or null.</summary>
        public PlayerInputCapture Source => m_Source;

        /// <summary>
        /// Makes <paramref name="source"/> the World's only player input, unless another capture with a higher
        /// <paramref name="order"/> is attached (returns false, nothing changes). Scene adapters pass their enable order,
        /// so the latest enabled adapter wins whichever attaches first, also after a World change. Presses recorded before
        /// this call are dropped; the binding is the current session, so presses after it reach the next tick.
        /// </summary>
        public bool Attach(PlayerInputCapture source, uint order = 0)
        {
            if (source == null)
                throw new System.ArgumentNullException(nameof(source));
            if (m_Source != null && m_Source != source && order < m_SourceOrder)
                return false;

            if (m_Source != source)
                m_Source?.ClearEdges();
            m_Source = source;
            m_SourceOrder = order;
            source.ClearEdges();
            if (m_Sessions.CalculateEntityCount() == 1)
            {
                var session = m_Sessions.GetSingleton<BattleSession>();
                m_BoundBattle = session.BattleNumber;
                m_BoundHuman = session.IsActive ? session.HumanCombatant : CombatantId.Invalid;
            }
            else
            {
                Unbind();
            }

            return true;
        }

        /// <summary>Detaches <paramref name="source"/> if it is the attached one (a replaced adapter cannot detach its successor).</summary>
        public bool Detach(PlayerInputCapture source)
        {
            if (source == null || source != m_Source)
                return false;
            source.ClearEdges();
            m_Source = null;
            m_SourceOrder = 0;
            Unbind();
            return true;
        }

        protected override void OnCreate()
        {
            m_Sessions = GetEntityQuery(ComponentType.ReadOnly<BattleSession>());
            RequireForUpdate<BattleSession>();
            RequireForUpdate<CombatCommandQueue>();
        }

        protected override void OnDestroy()
        {
            // The World goes away; the adapter owns and disposes the capture.
            m_Source = null;
        }

        protected override void OnUpdate()
        {
            var source = m_Source;
            if (source == null || source.IsDisposed)
                return;

            var session = SystemAPI.GetSingleton<BattleSession>();
            var human = session.IsActive ? session.HumanCombatant : CombatantId.Invalid;
            if (session.BattleNumber != m_BoundBattle || !human.Equals(m_BoundHuman))
            {
                source.ClearEdges();
                m_BoundBattle = session.BattleNumber;
                m_BoundHuman = human;
            }

            var inCombat = false;
            if (human.IsValid)
            {
                // Linear in the in-combat actors (≤ 400), no Entity handle kept between ticks.
                foreach (var id in SystemAPI.Query<RefRO<CombatantId>>().WithAll<InCombat>())
                {
                    if (id.ValueRO.Value == human.Value)
                    {
                        inCombat = true;
                        break;
                    }
                }
            }

            if (!inCombat)
            {
                source.ClearEdges();
                return;
            }

            source.Sample(out var moveX, out var moveZ, out var held);
            SystemAPI.GetSingletonBuffer<CombatCommandSubmission>().Add(new CombatCommandSubmission(new CombatCommand
            {
                Combatant = human,
                Tick = SystemAPI.GetSingleton<SimulationClock>().Tick,
                MoveX = moveX,
                MoveZ = moveZ,
                Held = held,
                Pressed = source.TakePressed(),
                Source = CommandSource.Player,
            }));
        }

        void Unbind()
        {
            m_BoundBattle = 0;
            m_BoundHuman = CombatantId.Invalid;
        }
    }
}
