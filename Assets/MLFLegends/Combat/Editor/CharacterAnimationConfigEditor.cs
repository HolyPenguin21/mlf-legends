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
        int m_PreviewClip;
        int m_PreviewFrame;
        bool m_PreviewLeft;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.HelpBox("Frames are LOCAL clip indices from 0, not page cells or atlas frame numbers. A clip plays " +
                "StartFrame..EndFrame (inclusive) over TotalDurationSeconds; the speed curve (X normalized time 0..1, Y relative " +
                "speed >=0, finite unweighted tangents) distributes only those frames. Strike clips split the range into " +
                "Preparation / Active / Recovery: with T and the curve they ARE the simulated strike windows (60 Hz ticks; " +
                "CombatSmokeBootstrap publishes them, new strikes use them, a running strike keeps its own). " +
                "Runtime edits use Config.Set() or this Inspector.", MessageType.Info);
            EditorGUILayout.HelpBox("World support is fixed for every clip: source_size × [0.5, 0.85], i.e. [96, 163.2] for 192×192. " +
                "JSON pivot is legacy ART alignment. Image Offset Pixels adds an artistic translation (+X right, +Y down); " +
                "it and hand/effect anchors mirror together. Neither setting moves the ECS fighter or hit boxes. " +
                "Keep the same character entry/catalog across clips. Random drawing shifts require artistic alignment.", MessageType.Info);
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
                DrawFramePreview(character, definition);
                // The same all-or-nothing set the renderer builds: missing clips/markers/phases and short phases are listed here.
                var setErrors = new List<string>();
                var setWarnings = new List<string>();
                var set = CombatAnimationSet.Create(definition, clipId =>
                        config.TryResolve(character.CharacterId, clipId, out _, out var s, out _) ? s : null,
                    character.CharacterId, setErrors, setWarnings);
                if (set != null)
                {
                    EditorGUILayout.LabelField("Landing recovery", $"jump {set.LandingStartFrame}..{set.LandingEndFrame}: {set.LandingTicks} ticks = {set.LandingTicks / (double)SimulationTiming.TickRateHz:0.######} s (movement/new actions locked)", EditorStyles.wordWrappedLabel);
                    EditorGUILayout.LabelField("Strike windows (ticks)", CombatSmokeBootstrap.Describe(set.StrikeSchedule), EditorStyles.wordWrappedLabel);
                }
                foreach (var message in setErrors) EditorGUILayout.HelpBox(message, MessageType.Error);
                foreach (var message in setWarnings) EditorGUILayout.HelpBox(message, MessageType.Warning);
            }
        }

        void DrawFramePreview(CharacterAnimationEntry character, SpriteAtlasDefinition definition)
        {
            EditorGUILayout.LabelField("Full source canvas preview", EditorStyles.boldLabel);
            var names = new string[definition.Clips.Count];
            for (var i = 0; i < names.Length; i++) names[i] = definition.Clips[i].Id;
            m_PreviewClip = Mathf.Clamp(m_PreviewClip, 0, names.Length - 1);
            m_PreviewClip = EditorGUILayout.Popup("Preview clip", m_PreviewClip, names);
            var clip = definition.Clips[m_PreviewClip];
            m_PreviewFrame = EditorGUILayout.IntSlider("Local frame", m_PreviewFrame, 0, clip.Frames.Count - 1);
            m_PreviewLeft = EditorGUILayout.Toggle("Facing left", m_PreviewLeft);
            var frame = definition.Frames[clip.Frames[m_PreviewFrame]];
            var catalog = character.AtlasCatalog;
            if (!SpriteAtlasCatalog.TryFrameRegion(definition, frame, catalog.Storage, out var rect, out var trim, out var error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
                return;
            }
            Texture2D texture = null;
            foreach (var binding in catalog.Pages)
                if (binding.File == definition.Pages[frame.Page].File) texture = binding.Texture;

            var size = new Vector2(definition.SourceSize.x, definition.SourceSize.y);
            var support = new Vector2(definition.WorldSupport.x, definition.WorldSupport.y);
            var pivot = new Vector2(definition.Pivot.x, definition.Pivot.y);
            var shift = support - pivot + character.ImageOffsetPixels;
            if (!float.IsFinite(shift.x) || !float.IsFinite(shift.y))
            {
                EditorGUILayout.HelpBox("Image Offset Pixels must be finite", MessageType.Error);
                return;
            }
            var panel = GUILayoutUtility.GetRect(64f, 300f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(panel, new Color(0.12f, 0.12f, 0.12f));
            // Stable full-canvas fitting, including the alignment offset in BOTH facings; no pose/bbox auto-centering.
            var minimum = new Vector2(-Mathf.Abs(shift.x), Mathf.Min(0f, shift.y));
            var maximum = new Vector2(size.x + Mathf.Abs(shift.x), size.y + Mathf.Max(0f, shift.y));
            var span = maximum - minimum;
            var scale = Mathf.Min((panel.width - 24f) / span.x, (panel.height - 24f) / span.y);
            var origin = panel.center - span * (scale * 0.5f);
            Vector2 Point(Vector2 p) => origin + (p - minimum) * scale;
            Vector2 ImagePoint(float x, float y)
            {
                var p = new Vector2(x, y) + shift;
                if (m_PreviewLeft) p.x = 2f * support.x - p.x;
                return Point(p);
            }
            Rect ImageRect(float x, float y, float width, float height)
            {
                var a = ImagePoint(x, y);
                var b = ImagePoint(x + width, y + height);
                return Rect.MinMaxRect(Mathf.Min(a.x, b.x), a.y, Mathf.Max(a.x, b.x), b.y);
            }

            var canvas = new Rect(Point(Vector2.zero), size * scale);
            EditorGUI.DrawRect(canvas, new Color(0.22f, 0.22f, 0.22f));
            if (texture != null)
            {
                var uv = SpriteCardMath.FrameUv(rect, definition.Pages[frame.Page].Size);
                GUI.DrawTextureWithTexCoords(ImageRect(trim.x, trim.y, rect.z, rect.w), texture,
                    new Rect(m_PreviewLeft ? uv.z : uv.x, uv.y, m_PreviewLeft ? uv.x - uv.z : uv.z - uv.x, uv.w - uv.y), true);
            }
            Outline(canvas, Color.cyan);
            Outline(ImageRect(0f, 0f, size.x, size.y), new Color(1f, 0.7f, 0.2f));
            Cross(ImagePoint(frame.HandLeft.Position.x, frame.HandLeft.Position.y), new Color(0.3f, 0.6f, 1f), 3f);
            Cross(ImagePoint(frame.HandRight.Position.x, frame.HandRight.Position.y), Color.magenta, 3f);
            if (frame.HasCastOrigin)
                Cross(ImagePoint(frame.CastOrigin.Position.x, frame.CastOrigin.Position.y), Color.green, 3f);
            Cross(Point(support), Color.white, 7f);
            EditorGUILayout.LabelField("World support (white +)", $"[{support.x:0.######}, {support.y:0.######}] px — fixed");
            EditorGUILayout.LabelField("Legacy art pivot", $"[{pivot.x}, {pivot.y}] px; image translation [{shift.x:0.######}, {shift.y:0.######}] px");
            EditorGUILayout.LabelField("Cyan: fixed canvas; amber: aligned source image; blue/magenta: hands; green: effect.", EditorStyles.wordWrappedMiniLabel);
            if (texture == null)
                EditorGUILayout.HelpBox("Preview PNG missing: " + definition.Pages[frame.Page].File + ". Place the external page at its existing asset path, preserving .meta.", MessageType.Warning);
        }

        static void Outline(Rect rect, Color color)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), color);
        }

        static void Cross(Vector2 point, Color color, float radius)
        {
            EditorGUI.DrawRect(new Rect(point.x - radius, point.y, radius * 2f + 1f, 1f), color);
            EditorGUI.DrawRect(new Rect(point.x, point.y - radius, 1f, radius * 2f + 1f), color);
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
