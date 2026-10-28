using System.Collections.Generic;
using MLFLegends.Combat;
using UnityEditor;
using UnityEngine;

namespace MLFLegends.Editor
{
    /// <summary>Regular nested Inspector fields (<see cref="CharacterAnimationSettingsDrawer"/>), plus derived timing and
    /// strike-window diagnostics. No runtime/editor registry.</summary>
    [CustomEditor(typeof(CharacterAnimationConfig))]
    public sealed class CharacterAnimationConfigEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.HelpBox("Frames are LOCAL clip indices from 0, not page cells or atlas frame numbers. A clip plays " +
                "StartFrame..EndFrame (inclusive) over TotalDurationSeconds; the speed curve (X normalized time 0..1, Y relative " +
                "speed >=0, finite unweighted tangents) distributes only those frames. Strike clips split the range into " +
                "Preparation / Active / Recovery: with T and the curve they ARE the simulated strike windows (60 Hz ticks; " +
                "CombatSmokeBootstrap publishes them, new strikes use them, a running strike keeps its own). " +
                "Runtime edits use Config.Set() or this Inspector.", MessageType.Info);
            var config = (CharacterAnimationConfig)target;
            var characters = serializedObject.FindProperty("m_Characters");
            for (var c = 0; c < config.Characters.Count; c++)
            {
                var character = config.Characters[c];
                if (character == null) continue;
                EditorGUILayout.LabelField(character.CharacterId, EditorStyles.boldLabel);
                SpriteAtlasDefinition definition = null;
                var animations = character.Animations ?? System.Array.Empty<CharacterAnimationSettings>();
                for (var a = 0; a < animations.Length; a++)
                {
                    var settings = animations[a];
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
                        var attack = CombatAnimationSet.IsAttackClip(settings.ClipId);
                        EditorGUILayout.LabelField(settings.ClipId + " clip frames", $"local 0..{clip.Frames.Count - 1} ({clip.Frames.Count})");
                        var timing = SpriteAnimationTiming.Create(clip, settings,
                            character.CharacterId + "/" + settings.ClipId, errors, warnings, clip.Loop, attack);
                        if (timing != null)
                        {
                            EditorGUILayout.LabelField("Average FPS", (timing.FrameCount / timing.DurationSeconds).ToString("0.###"));
                            for (var frame = 0; frame < timing.FrameCount; frame++)
                                EditorGUILayout.LabelField("Frame " + (settings.StartFrame + frame), timing.FrameStartSeconds(frame).ToString("0.######") +
                                    " s, hold " + timing.FrameDurationSeconds(frame).ToString("0.######") + " s");
                            if (attack)
                                DrawStrikeWindows(settings.ClipId, timing, errors);
                        }
                        if (attack)
                            DrawMarkerButton(characters, c, a, settings, clip);
                    }
                    else if (definition != null) errors.Add("Clip missing: " + settings.ClipId);
                    foreach (var message in errors) EditorGUILayout.HelpBox(message, MessageType.Error);
                    foreach (var message in warnings) EditorGUILayout.HelpBox(message, MessageType.Warning);
                }

                if (definition == null)
                    continue;
                // The same all-or-nothing set the renderer builds: missing clips/markers/phases and short phases are listed here.
                var setErrors = new List<string>();
                var setWarnings = new List<string>();
                var set = CombatAnimationSet.Create(definition, clipId =>
                        config.TryResolve(character.CharacterId, clipId, out _, out var s, out _) ? s : null,
                    character.CharacterId, setErrors, setWarnings);
                if (set != null)
                {
                    EditorGUILayout.LabelField("Combat set", $"valid, landing frames play {set.LandingTicks} ticks");
                    EditorGUILayout.LabelField("Strike windows (ticks)", CombatSmokeBootstrap.Describe(set.StrikeSchedule), EditorStyles.wordWrappedLabel);
                }
                foreach (var message in setErrors) EditorGUILayout.HelpBox(message, MessageType.Error);
                foreach (var message in setWarnings) EditorGUILayout.HelpBox(message, MessageType.Warning);
            }
        }

        /// <summary>Phase frames → clip seconds → simulated ticks, exactly as the published schedule derives them.</summary>
        static void DrawStrikeWindows(string clipId, SpriteAnimationTiming timing, List<string> errors)
        {
            var kind = CombatAttackKind.None;
            for (var k = CombatAttackKind.PunchLeft; k <= CombatAttackKind.KickSpin; k++)
                if (CombatAnimationSet.AttackClip(k) == clipId) kind = k;
            if (!timing.TryStrikeTiming(CombatAttackTuning.Get(kind).ChainDelayTicks, out var strike, out var error))
            {
                errors.Add(clipId + ": " + error);
                return;
            }

            const double hz = SimulationTiming.TickRateHz;
            EditorGUILayout.LabelField("Preparation", $"frames 0..{timing.PreparationEnd}, 0 – {timing.ActiveStartSeconds:0.####} s → ticks {Ticks(0, strike.ActiveStart)}");
            EditorGUILayout.LabelField("Active", $"frames {timing.PreparationEnd + 1}..{timing.ActiveEnd}, {timing.ActiveStartSeconds:0.####} – {timing.RecoveryStartSeconds:0.####} s → ticks {Ticks(strike.ActiveStart, strike.RecoveryStart)}");
            EditorGUILayout.LabelField("Recovery", $"frames {timing.ActiveEnd + 1}..{timing.FrameCount - 1}, {timing.RecoveryStartSeconds:0.####} – {timing.DurationSeconds:0.####} s → ticks {Ticks(strike.RecoveryStart, strike.End)}");
            EditorGUILayout.LabelField("Strike", $"{strike.End} ticks = {strike.End / hz:0.####} s" +
                (strike.CanChain ? $", follow-up from tick {strike.ChainPoint}" : ", finisher (no follow-up)"));
        }

        static string Ticks(int first, int end) => end > first ? $"{first}..{end - 1} ({end - first})" : "none (shorter than one tick)";

        /// <summary>Explicit owner action (never automatic): copy the manifest's strike_pose marker into the Active range.</summary>
        static void DrawMarkerButton(SerializedProperty characters, int character, int animation, CharacterAnimationSettings settings, SpriteAtlasClip clip)
        {
            if (!clip.Markers.TryGetValue("strike_pose", out var pose) || pose.Length == 0)
                return;
            if (settings.PreparationEndFrame == pose[0] - 1 && settings.ActiveEndFrame == pose[pose.Length - 1])
                return;
            if (!GUILayout.Button($"Set Preparation/Active from manifest strike_pose {pose[0]}..{pose[pose.Length - 1]}"))
                return;
            var element = characters.GetArrayElementAtIndex(character).FindPropertyRelative(nameof(CharacterAnimationEntry.Animations))
                .GetArrayElementAtIndex(animation);
            element.FindPropertyRelative(nameof(CharacterAnimationSettings.PreparationEndFrame)).intValue = pose[0] - 1;
            element.FindPropertyRelative(nameof(CharacterAnimationSettings.ActiveEndFrame)).intValue = pose[pose.Length - 1];
            characters.serializedObject.ApplyModifiedProperties(); // undoable; OnValidate bumps Version
        }
    }
}
