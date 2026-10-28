using MLFLegends.Combat;
using UnityEditor;
using UnityEngine;

namespace MLFLegends.Editor
{
    /// <summary>
    /// Bootstrap fields plus the derived total roster, the validation error of the current team sizes and, in Play Mode, an
    /// explicit Restart button. Editing a size never restarts a running battle by itself.
    /// </summary>
    [CustomEditor(typeof(CombatSmokeBootstrap))]
    public sealed class CombatSmokeBootstrapEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var bootstrap = (CombatSmokeBootstrap)target;
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.IntField(new GUIContent("Total fighters (read-only)", "Human team size + AI team size."), bootstrap.TotalCombatants);
            if (!bootstrap.TryGetSetup(out _, out var error))
                EditorGUILayout.HelpBox(error + ". The battle will not start/restart with these values (nothing is clamped).", MessageType.Error);
            EditorGUILayout.HelpBox($"Team sizes include the leader, 1..{BattleSetup.MaxCombatantsPerTeam} each. They are applied when the " +
                "battle starts or restarts (enable, or the button below in Play Mode); a running battle is not rebuilt by editing them. " +
                "Strike windows follow the assigned animation config's strike phases and update for new strikes when it changes.",
                MessageType.Info);
            if (!Application.isPlaying)
                return;
            if (bootstrap.PublishedScheduleRevision != 0)
                EditorGUILayout.LabelField("Strike windows revision", bootstrap.PublishedScheduleRevision.ToString());
            using (new EditorGUI.DisabledScope(!bootstrap.isActiveAndEnabled))
            {
                if (GUILayout.Button("Restart battle (apply team sizes)"))
                    bootstrap.RestartBattle();
            }
        }
    }
}
