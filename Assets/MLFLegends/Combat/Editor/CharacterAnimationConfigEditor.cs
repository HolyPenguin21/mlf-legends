using System.Collections.Generic;
using MLFLegends.Combat;
using UnityEditor;
using UnityEngine;

namespace MLFLegends.Editor
{
    /// <summary>Regular nested Inspector fields, plus derived timing diagnostics. No runtime/editor registry.</summary>
    [CustomEditor(typeof(CharacterAnimationConfig))]
    public sealed class CharacterAnimationConfigEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.HelpBox("Speed curve: X normalized time 0..1, Y relative speed >=0. Full cycle remains T. " +
                "Use finite, unweighted tangents; Constant/step and weighted tangents are rejected. " +
                "FrameCount selects the first N clip elements, not PNG cells. Runtime edits use Config.Set(). " +
                "Strike and jump clips are fitted to simulation windows: N/T/curve set each frame's share, " +
                "CombatAttackTuning/physics set when it shows (hits never move).", MessageType.Info);
            var config = (CharacterAnimationConfig)target;
            foreach (var character in config.Characters)
            {
                if (character == null) continue;
                EditorGUILayout.LabelField(character.CharacterId, EditorStyles.boldLabel);
                SpriteAtlasDefinition definition = null;
                foreach (var settings in character.Animations ?? System.Array.Empty<CharacterAnimationSettings>())
                {
                    if (settings == null) continue;
                    var errors = new List<string>();
                    var warnings = new List<string>();
                    if (!config.TryResolve(character.CharacterId, settings.ClipId, out var catalog, out _, out var error))
                    {
                        EditorGUILayout.HelpBox(error, MessageType.Error);
                        continue;
                    }
                    if (catalog.Manifest == null)
                    {
                        EditorGUILayout.HelpBox("Manifest missing", MessageType.Error);
                        continue;
                    }
                    definition = SpriteAtlasManifest.Parse(catalog.Manifest.text, errors);
                    if (definition != null && definition.TryGetClipIndex(settings.ClipId, out var index))
                    {
                        var clip = definition.Clips[index];
                        EditorGUILayout.LabelField(settings.ClipId + " available frames", clip.Frames.Count.ToString());
                        var timing = SpriteAnimationTiming.Create(clip, settings,
                            character.CharacterId + "/" + settings.ClipId, errors, warnings, clip.Loop);
                        if (timing != null)
                        {
                            EditorGUILayout.LabelField("Average FPS", (timing.FrameCount / timing.DurationSeconds).ToString("0.###"));
                            for (var frame = 0; frame < timing.FrameCount; frame++)
                                EditorGUILayout.LabelField("Frame " + frame, timing.FrameStartSeconds(frame).ToString("0.######") +
                                    " s, hold " + timing.FrameDurationSeconds(frame).ToString("0.######") + " s");
                        }
                        DrawCombatWindows(settings.ClipId);
                    }
                    else if (definition != null) errors.Add("Clip missing: " + settings.ClipId);
                    foreach (var message in errors) EditorGUILayout.HelpBox(message, MessageType.Error);
                    foreach (var message in warnings) EditorGUILayout.HelpBox(message, MessageType.Warning);
                }

                if (definition == null)
                    continue;
                // The same all-or-nothing set the renderer builds: missing clips/markers and T mismatches are listed here.
                var setErrors = new List<string>();
                var setWarnings = new List<string>();
                var set = CombatAnimationSet.Create(definition, clipId =>
                        config.TryResolve(character.CharacterId, clipId, out _, out var s, out _) ? s : null,
                    character.CharacterId, setErrors, setWarnings);
                if (set != null)
                    EditorGUILayout.LabelField("Combat set", $"valid, landing frames play {set.LandingTicks} ticks");
                foreach (var message in setErrors) EditorGUILayout.HelpBox(message, MessageType.Error);
                foreach (var message in setWarnings) EditorGUILayout.HelpBox(message, MessageType.Warning);
            }
        }

        static void DrawCombatWindows(string clipId)
        {
            for (var kind = CombatAttackKind.PunchLeft; kind <= CombatAttackKind.KickSpin; kind++)
            {
                if (CombatAnimationSet.AttackClip(kind) != clipId)
                    continue;
                var strike = CombatAttackTuning.Get(kind);
                EditorGUILayout.LabelField("Combat windows (ticks @60 Hz)",
                    $"windup {strike.WindupTicks}, active {strike.ActiveTicks}, recovery {strike.RecoveryTicks} = " +
                    $"{strike.TotalTicks / (double)SimulationTiming.TickRateHz:0.####} s");
            }
        }
    }
}
