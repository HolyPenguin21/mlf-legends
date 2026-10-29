using MLFLegends.Combat;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
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
            DrawAnimationBinding(bootstrap);
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.IntField(new GUIContent("Total fighters (read-only)", "Human team size + AI team size."), bootstrap.TotalCombatants);
            if (!bootstrap.TryGetSetup(out _, out var error))
                EditorGUILayout.HelpBox(error + ". The battle will not start/restart with these values (nothing is clamped).", MessageType.Error);
            EditorGUILayout.HelpBox($"Team sizes include the leader, 1..{BattleSetup.MaxCombatantsPerTeam} each. They are applied when the " +
                "battle starts or restarts (enable, or the button below in Play Mode); a running battle is not rebuilt by editing them. " +
                "Strike windows and jump landing recovery follow the assigned animation config and update for new strikes/landings when it changes.",
                MessageType.Info);
            if (!Application.isPlaying)
                return;
            if (bootstrap.PublishedScheduleRevision != 0)
                EditorGUILayout.LabelField("Combat timing revision (strikes + landing)", bootstrap.PublishedScheduleRevision.ToString());
            using (new EditorGUI.DisabledScope(!bootstrap.isActiveAndEnabled))
            {
                if (GUILayout.Button("Restart battle (apply team sizes)"))
                    bootstrap.RestartBattle();
            }
            DrawTestImpulses();
        }

        // Explicit test-only impulse source for manual Stage 04.4 checks (#66): no key binding, no runtime producer.
        // It appends to the World's single CombatImpulse input; Movement of the next tick applies it.
        static void DrawTestImpulses()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Test-only impulses (manual physics check)", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Push controlled +X")) Submit(false, new float3(8, 0, 0));
                if (GUILayout.Button("Push controlled -X")) Submit(false, new float3(-8, 0, 0));
                if (GUILayout.Button("Lift controlled")) Submit(false, new float3(0, 7, 0));
            }
            if (GUILayout.Button("Blast: everyone away from the controlled fighter"))
                Submit(true, new float3(8, 3, 0));
        }

        static void Submit(bool everyone, float3 impulse)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            var manager = world.EntityManager;
            using var sessions = manager.CreateEntityQuery(ComponentType.ReadOnly<BattleSession>());
            if (sessions.CalculateEntityCount() != 1 || !sessions.GetSingleton<BattleSession>().IsActive) return;
            var human = sessions.GetSingleton<BattleSession>().HumanCombatant;
            using var fighters = manager.CreateEntityQuery(ComponentType.ReadOnly<CombatantId>(), ComponentType.ReadOnly<CombatPosition>(),
                ComponentType.ReadOnly<CombatBody>(), ComponentType.ReadOnly<InCombat>());
            using var ids = fighters.ToComponentDataArray<CombatantId>(Allocator.Temp);
            using var positions = fighters.ToComponentDataArray<CombatPosition>(Allocator.Temp);
            using var bodies = fighters.ToComponentDataArray<CombatBody>(Allocator.Temp);
            var center = float3.zero;
            for (var i = 0; i < ids.Length; i++) if (ids[i].Equals(human)) center = positions[i].Value;
            for (var i = 0; i < ids.Length; i++)
            {
                if (everyone == ids[i].Equals(human)) continue;
                var j = impulse;
                if (everyone)
                {
                    var away = math.normalizesafe((positions[i].Value - center).xz, new float2(1, 0));
                    j = new float3(away.x * impulse.x, impulse.y, away.y * impulse.x);
                }
                CombatImpulse.Submit(manager, ids[i], j * bodies[i].Mass); // Same Δv for every mass.
            }
        }

        static void DrawAnimationBinding(CombatSmokeBootstrap bootstrap)
        {
            if (bootstrap.AnimationConfig == null)
                EditorGUILayout.HelpBox("Animation Config is empty: no configured strike/landing timing will be published. " +
                    "A new World's landing recovery remains ZERO. Assign the SAME CharacterAnimationConfig as the sprite renderer.", MessageType.Warning);
            // Inspector-only diagnostic: no runtime search, automatic assignment, extra publisher or per-frame battle work.
            foreach (var renderer in Object.FindObjectsByType<CombatSpriteRenderer>(FindObjectsInactive.Include))
            {
                if (renderer.gameObject.scene != bootstrap.gameObject.scene || !renderer.UsesCharacterAnimation)
                    continue;
                if (renderer.AnimationConfig != bootstrap.AnimationConfig || renderer.CharacterId != bootstrap.StrikeCharacterId)
                    EditorGUILayout.HelpBox($"Renderer '{renderer.name}' uses a different config or CharacterId. " +
                        "Its preview can look valid while the simulation uses different timing. Match both fields.", MessageType.Error);
            }
        }
    }
}
