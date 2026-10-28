using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Scene entry point for a battle in the default ECS world: requests a (re)start when enabled and the end of the battle
    /// when disabled or destroyed, and publishes the strike windows of the selected character's animation phases.
    /// Holds no combat state; the battle lives in ECS.
    /// </summary>
    /// <remarks>
    /// Only appends <see cref="BattleRequest"/>s; <see cref="BattleLifecycleSystem"/> creates and destroys the
    /// combatants. A restart is used on enable so that a second bootstrap, or a scene reload, replaces the
    /// fighters instead of duplicating them. The end request carries this bootstrap's owner token, so it ends
    /// only the battle this bootstrap started: with an async scene load the next scene's bootstrap is enabled
    /// before the previous one is disabled, and that late end must not stop the new battle.
    /// <para>Team sizes (leader included) become <see cref="BattleSetup.HumanAllies"/>/<see cref="BattleSetup.AiAllies"/>
    /// of the same Restart request: they are read only when a (re)start is requested (enable, <see cref="RestartBattle"/>),
    /// so editing them never touches a running battle. Out-of-range sizes are rejected with an error, never clamped.</para>
    /// <para>Strike windows: the five strike clips of <see cref="m_StrikeCharacterId"/> in <see cref="m_AnimationConfig"/>
    /// give <see cref="CombatStrikeSchedule"/> (phases, duration and curve → ticks), published on enable and again whenever
    /// the config or its atlas catalog changes (one version compare per frame; derived only on change). Running strikes keep
    /// their timing; an invalid config is reported and the previous table stays. Without a config the World keeps its table
    /// (the <see cref="CombatAttackTuning"/> defaults unless something else published); a World without the table (no
    /// CombatActionSystem) is skipped, and a replaced default World gets the table again. On disable the table this bootstrap
    /// replaced is restored, unless someone published after it (e.g. the next scene's bootstrap), so a scene's windows never
    /// outlive it.</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class CombatSmokeBootstrap : MonoBehaviour
    {
        [SerializeField] byte m_HumanTeam = 1;
        [SerializeField] Vector3 m_HumanPosition = new Vector3(-2f, 0f, 0f);
        [SerializeField] FacingDirection m_HumanFacing = FacingDirection.Right;

        [Tooltip("Fighters on the human team, the human leader included (1..200). Applied on the next battle start/restart.")]
        [SerializeField] int m_HumanTeamSize = 1;

        [SerializeField] byte m_AiTeam = 2;
        [SerializeField] Vector3 m_AiPosition = new Vector3(2f, 0f, 0f);
        [SerializeField] FacingDirection m_AiFacing = FacingDirection.Left;

        [Tooltip("Fighters on the AI team, the AI leader included (1..200). Applied on the next battle start/restart.")]
        [SerializeField] int m_AiTeamSize = 1;

        [Tooltip("Shared animation config whose strike phases (Preparation / Active / Recovery frames, duration, curve) set the simulated strike windows.")]
        [SerializeField] CharacterAnimationConfig m_AnimationConfig;

        [Tooltip("Character entry of the config that supplies the strike windows (the whole roster uses one character type).")]
        [SerializeField] string m_StrikeCharacterId = "mage_female_01";

        // Owner token per enable; only needs to differ between bootstraps that are alive at the same time.
        static uint s_LastOwner;
        uint m_Owner;

        // What the last publish attempt was derived from (and into which World); any change triggers one new derivation.
        World m_ScheduleWorld;
        CharacterAnimationConfig m_ScheduleConfig;
        string m_ScheduleCharacter;
        int m_ScheduleVersion;
        SpriteAtlasCatalog m_ScheduleCatalog;
        int m_ScheduleCatalogVersion;
        bool m_ScheduleAttempted;

        // The table in place before this bootstrap's first publish since enable, restored on disable.
        CombatStrikeSchedule m_ReplacedSchedule;
        bool m_HasReplacedSchedule;

        /// <summary>Fighters on the human team, leader included. Stored only; applied by the next start/restart.</summary>
        public int HumanTeamSize
        {
            get => m_HumanTeamSize;
            set => m_HumanTeamSize = value;
        }

        /// <summary>Fighters on the AI team, leader included. Stored only; applied by the next start/restart.</summary>
        public int AiTeamSize
        {
            get => m_AiTeamSize;
            set => m_AiTeamSize = value;
        }

        /// <summary>Fighters the next start spawns: both teams.</summary>
        public int TotalCombatants => m_HumanTeamSize + m_AiTeamSize;

        public CharacterAnimationConfig AnimationConfig
        {
            get => m_AnimationConfig;
            set => m_AnimationConfig = value;
        }

        public string StrikeCharacterId
        {
            get => m_StrikeCharacterId;
            set => m_StrikeCharacterId = value;
        }

        /// <summary><see cref="CombatStrikeSchedule.Revision"/> of this bootstrap's last successful publish; 0 = none.</summary>
        public uint PublishedScheduleRevision { get; private set; }

        /// <summary>
        /// The battle this bootstrap requests; false (with <paramref name="error"/>) for an out-of-range team size, equal
        /// teams or a facing other than Left/Right.
        /// </summary>
        public bool TryGetSetup(out BattleSetup setup, out string error)
        {
            setup = default;
            error = null;
            const int max = BattleSetup.MaxCombatantsPerTeam;
            if (m_HumanTeamSize < 1 || m_HumanTeamSize > max)
                error = $"Human team size {m_HumanTeamSize} is outside 1..{max} (leader included)";
            else if (m_AiTeamSize < 1 || m_AiTeamSize > max)
                error = $"AI team size {m_AiTeamSize} is outside 1..{max} (leader included)";
            if (error != null)
                return false;

            setup = new BattleSetup
            {
                Human = new CombatantSetup { Team = m_HumanTeam, Position = (float3)m_HumanPosition, Facing = m_HumanFacing },
                Ai = new CombatantSetup { Team = m_AiTeam, Position = (float3)m_AiPosition, Facing = m_AiFacing },
                HumanAllies = (ushort)(m_HumanTeamSize - 1),
                AiAllies = (ushort)(m_AiTeamSize - 1),
            };
            if (!setup.IsValid)
            {
                error = "both facings must be Left/Right and the teams must differ";
                return false;
            }

            return true;
        }

        void OnEnable()
        {
            if (!TryGetSetup(out var setup, out var error))
            {
                Debug.LogError($"{nameof(CombatSmokeBootstrap)} on '{name}': {error}; no battle requested.", this);
                return;
            }

            m_Owner = ++s_LastOwner;
            if (m_Owner == 0)
                m_Owner = ++s_LastOwner; // 0 means "no owner"

            m_ScheduleAttempted = false;
            if (TryGetEntityManager(out var entityManager))
            {
                PublishSchedule(entityManager);
                BattleRequest.Submit(entityManager, BattleRequest.Restart(setup, m_Owner));
            }
        }

        void Update()
        {
            if (m_Owner == 0 || !ScheduleSourceChanged())
                return;
            if (TryGetEntityManager(out var entityManager))
                PublishSchedule(entityManager);
        }

        void OnDisable()
        {
            // On Play Mode exit the default world may already be disposed; it takes its combatants with it.
            // Nothing to end if this bootstrap never requested a battle (invalid setup).
            if (m_Owner != 0 && TryGetEntityManager(out var entityManager))
            {
                BattleRequest.Submit(entityManager, BattleRequest.End(m_Owner));
                RestoreSchedule(entityManager);
            }
            m_Owner = 0;
            m_HasReplacedSchedule = false;
            PublishedScheduleRevision = 0;
        }

        void RestoreSchedule(EntityManager entityManager)
        {
            if (!m_HasReplacedSchedule || !TryGetSchedule(entityManager, out var current) || current.Revision != PublishedScheduleRevision)
                return;
            CombatStrikeSchedule.Publish(entityManager, m_ReplacedSchedule);
        }

        static bool TryGetSchedule(EntityManager entityManager, out CombatStrikeSchedule schedule)
        {
            schedule = default;
            var query = entityManager.CreateEntityQuery(typeof(CombatStrikeSchedule));
            var found = query.CalculateEntityCount() == 1;
            if (found)
                schedule = query.GetSingleton<CombatStrikeSchedule>();
            query.Dispose();
            return found;
        }

        /// <summary>
        /// Restarts the battle with the current settings (team sizes, positions): the same owned Restart request as on
        /// enable, applied at the next safe boundary. Only while this bootstrap is enabled; an invalid setup is reported and
        /// the running battle is kept.
        /// </summary>
        [ContextMenu("Restart battle (apply team sizes)")]
        public void RestartBattle()
        {
            if (!isActiveAndEnabled)
            {
                Debug.LogWarning($"{nameof(CombatSmokeBootstrap)} on '{name}': enable it (Play Mode) to restart the battle.", this);
                return;
            }

            if (!TryGetSetup(out var setup, out var error))
            {
                Debug.LogError($"{nameof(CombatSmokeBootstrap)} on '{name}': {error}; the running battle is kept.", this);
                return;
            }

            if (m_Owner == 0)
            {
                m_Owner = ++s_LastOwner;
                if (m_Owner == 0)
                    m_Owner = ++s_LastOwner;
            }

            if (TryGetEntityManager(out var entityManager))
                BattleRequest.Submit(entityManager, BattleRequest.Restart(setup, m_Owner));
        }

        bool ScheduleSourceChanged() =>
            !m_ScheduleAttempted || m_ScheduleWorld != World.DefaultGameObjectInjectionWorld || m_ScheduleConfig != m_AnimationConfig || m_ScheduleCharacter != m_StrikeCharacterId
            || (m_AnimationConfig != null && m_ScheduleVersion != m_AnimationConfig.Version)
            || (m_ScheduleCatalog != null && m_ScheduleCatalogVersion != m_ScheduleCatalog.Version);

        /// <summary>
        /// Derives the strike windows from the config (manifest parsed once per change) and publishes them. Errors keep the
        /// World's previous table; the attempt is remembered, so a broken config is reported once per change, not per frame.
        /// </summary>
        void PublishSchedule(EntityManager entityManager)
        {
            m_ScheduleAttempted = true;
            if (m_ScheduleWorld != World.DefaultGameObjectInjectionWorld)
                m_HasReplacedSchedule = false; // the table to restore belongs to the World it was taken from
            m_ScheduleWorld = World.DefaultGameObjectInjectionWorld;
            m_ScheduleConfig = m_AnimationConfig;
            m_ScheduleCharacter = m_StrikeCharacterId;
            m_ScheduleVersion = m_AnimationConfig != null ? m_AnimationConfig.Version : 0;
            m_ScheduleCatalog = null;
            var prefix = $"{nameof(CombatSmokeBootstrap)} on '{name}'";
            // A World without CombatActionSystem (e.g. a partial test World) simulates no strikes and has no table.
            if (!TryGetSchedule(entityManager, out _))
                return;
            if (m_AnimationConfig == null)
            {
                Debug.LogWarning($"{prefix}: no CharacterAnimationConfig assigned; strikes keep the World's current windows " +
                    "(CombatAttackTuning defaults unless published elsewhere).", this);
                return;
            }

            var errors = new List<string>();
            var warnings = new List<string>();
            var schedule = default(CombatStrikeSchedule);
            var path = $"characters.{m_StrikeCharacterId}.animations";
            if (!m_AnimationConfig.TryResolve(m_StrikeCharacterId, CombatAnimationSet.AttackClip(CombatAttackKind.PunchLeft), out var catalog, out _, out var error))
                errors.Add(error);
            else
            {
                m_ScheduleCatalog = catalog;
                m_ScheduleCatalogVersion = catalog.Version;
                var definition = catalog.Manifest != null ? SpriteAtlasManifest.Parse(catalog.Manifest.text, errors) : null;
                if (catalog.Manifest == null)
                    errors.Add($"characters.{m_StrikeCharacterId}.AtlasCatalog: manifest missing");
                if (definition != null)
                {
                    CombatAnimationSet.TryCreateStrikeSchedule(definition, clipId =>
                            m_AnimationConfig.TryResolve(m_StrikeCharacterId, clipId, out _, out var settings, out var resolveError)
                                ? settings : Missing(errors, resolveError),
                        path, errors, warnings, out schedule);
                }
            }

            foreach (var warning in warnings)
                Debug.LogWarning($"{prefix}: {warning}", this);
            if (errors.Count > 0 || !schedule.IsValid)
            {
                Debug.LogError($"{prefix}: strike windows rejected, strikes keep the previous windows.\n  " + string.Join("\n  ", errors), this);
                return;
            }

            try
            {
                if (!m_HasReplacedSchedule && TryGetSchedule(entityManager, out var replaced))
                {
                    m_ReplacedSchedule = replaced;
                    m_HasReplacedSchedule = true;
                }

                PublishedScheduleRevision = CombatStrikeSchedule.Publish(entityManager, schedule);
            }
            catch (System.InvalidOperationException exception)
            {
                Debug.LogError($"{prefix}: {exception.Message}", this);
                return;
            }

            Debug.Log($"{prefix}: strike windows (ticks @{SimulationTiming.TickRateHz} Hz, Preparation/Active/Recovery) revision " +
                $"{PublishedScheduleRevision}: {Describe(schedule)}. New strikes use them; running strikes finish with their own.", this);
        }

        static CharacterAnimationSettings Missing(List<string> errors, string error)
        {
            // TryCreateStrikeSchedule reports "settings missing" itself; the resolver's reason (e.g. duplicate ClipId) is added.
            if (error != null && !error.EndsWith("animation settings missing"))
                errors.Add(error);
            return null;
        }

        /// <summary>"punch_left 14/6/9, ..." for logs and the Inspector.</summary>
        public static string Describe(in CombatStrikeSchedule schedule)
        {
            var parts = new string[5];
            for (var kind = CombatAttackKind.PunchLeft; kind <= CombatAttackKind.KickSpin; kind++)
            {
                var t = schedule.Get(kind);
                parts[(int)kind - 1] = $"{CombatAnimationSet.AttackClip(kind)} {t.WindupTicks}/{t.ActiveTicks}/{t.RecoveryTicks}";
            }

            return string.Join(", ", parts);
        }

        static bool TryGetEntityManager(out EntityManager entityManager)
        {
            entityManager = default;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
                return false;

            entityManager = world.EntityManager;
            // Persistent until the World is disposed, and this runs on every enable/disable: released here.
            var query = entityManager.CreateEntityQuery(typeof(BattleSession));
            var hasSession = !query.IsEmptyIgnoreFilter;
            query.Dispose();
            return hasSession;
        }
    }
}
