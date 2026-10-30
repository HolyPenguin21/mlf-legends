using MLFLegends.Combat;
using Unity.Collections;
using Unity.Entities;
using UnityEditor;
using UnityEngine;

namespace MLFLegends.Editor
{
    /// <summary>Editor-only command source for owner smoke checks; it never moves actors or writes combat state.</summary>
    public sealed class CombatGuardSmokeWindow : EditorWindow
    {
        bool m_Attack, m_Queued;
        uint m_LastTick, m_Battle;
        World m_World;
        CombatAttackKind m_Kind = CombatAttackKind.Hand1;
        int m_Interval = 40;

        [MenuItem("MLF Legends/Guard smoke commands")]
        static void Open() => GetWindow<CombatGuardSmokeWindow>("Guard smoke");
        void OnEnable() => EditorApplication.update += Pump;
        void OnDisable() { EditorApplication.update -= Pump; m_Attack = false; m_World = null; m_Queued = false; m_LastTick = 0; }

        void OnGUI()
        {
            EditorGUILayout.HelpBox("CombatSmoke in Play Mode. Approach the opponent, then hold K or press K just before contact. " +
                "This window submits ordinary AI-source commands for the session's opponent; all hits use the existing queue and owners.", MessageType.Info);
            m_Kind = (CombatAttackKind)EditorGUILayout.EnumPopup("Opponent strike", m_Kind);
            m_Interval = EditorGUILayout.IntField("Interval (fixed ticks)", m_Interval);
            m_Attack = EditorGUILayout.Toggle("Repeat opponent attacks", m_Attack);
            EditorGUILayout.LabelField("Windows close / battle or World replacement stops the producer.");
        }

        void Pump()
        {
            if (!m_Attack || !EditorApplication.isPlaying || EditorApplication.isPaused || Time.timeScale <= 0) return;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;
            using var sessions = em.CreateEntityQuery(typeof(BattleSession));
            using var clocks = em.CreateEntityQuery(typeof(SimulationClock));
            using var queues = em.CreateEntityQuery(typeof(CombatCommandQueue));
            if (sessions.CalculateEntityCount() != 1 || clocks.CalculateEntityCount() != 1 || queues.CalculateEntityCount() != 1) return;
            var session = sessions.GetSingleton<BattleSession>(); if (!session.IsActive) return;
            if (m_World != null && (m_World != world || m_Battle != session.BattleNumber))
            { m_Attack = false; m_World = null; m_Queued = false; m_LastTick = 0; return; }
            m_World = world; m_Battle = session.BattleNumber;
            var tick = clocks.GetSingleton<SimulationClock>().Tick;
            if (m_Queued && tick < m_LastTick + (uint)Mathf.Max(1,m_Interval)) return;
            using var actors = em.CreateEntityQuery(typeof(CombatantId), typeof(InCombat));
            using var ids = actors.ToComponentDataArray<CombatantId>(Allocator.Temp);
            var exists = false; foreach (var id in ids) if (id.Equals(session.AiCombatant)) { exists = true; break; }
            if (!exists) { m_Attack = false; return; }
            var button = m_Kind == CombatAttackKind.PhysicalAbility ? CommandButtons.PhysicalAbility
                : m_Kind == CombatAttackKind.Foot1 || m_Kind == CombatAttackKind.Foot2 ? CommandButtons.FootSeries
                : m_Kind == CombatAttackKind.Hand1 || m_Kind == CombatAttackKind.Hand2 ? CommandButtons.HandSeries : CommandButtons.None;
            if (button == CommandButtons.None) return;
            em.GetBuffer<CombatCommandSubmission>(queues.GetSingletonEntity()).Add(new CombatCommandSubmission(new CombatCommand {
                Combatant = session.AiCombatant, Tick = tick + 1, Pressed = button, Source = CommandSource.AI,
            }));
            m_LastTick = tick; m_Queued = true;
        }
    }
}
