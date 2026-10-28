using MLFLegends.Combat;
using UnityEditor;
using UnityEngine;

namespace MLFLegends.Editor
{
    /// <summary>
    /// One clip's settings: StartFrame (read-only) / EndFrame (editable) / FrameCount (derived), duration, curve and, for the
    /// five strike clips only, Preparation / Active / Recovery as inclusive frame ranges. Only EndFrame, PreparationEndFrame
    /// and ActiveEndFrame are stored; every other boundary is derived, so phases cannot have gaps or overlaps. Values are
    /// never clamped here: invalid ranges are reported by the config Inspector and rejected at runtime.
    /// </summary>
    [CustomPropertyDrawer(typeof(CharacterAnimationSettings))]
    public sealed class CharacterAnimationSettingsDrawer : PropertyDrawer
    {
        const string IndexNote = "Frame numbers: local clip indices from 0 (not page cells or atlas frame numbers).";

        static float Line => EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

        static bool IsAttack(SerializedProperty property) =>
            CombatAnimationSet.IsAttackClip(property.FindPropertyRelative(nameof(CharacterAnimationSettings.ClipId)).stringValue);

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (!property.isExpanded)
                return Line;
            var curve = property.FindPropertyRelative(nameof(CharacterAnimationSettings.SpeedCurve));
            var rows = 7; // foldout, ClipId, note, Start, End, Count, Duration
            var height = rows * Line + EditorGUI.GetPropertyHeight(curve, true) + EditorGUIUtility.standardVerticalSpacing;
            if (IsAttack(property))
                height += 4 * Line; // header + three phases
            return height;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var clipId = property.FindPropertyRelative(nameof(CharacterAnimationSettings.ClipId));
            var end = property.FindPropertyRelative(nameof(CharacterAnimationSettings.EndFrame));
            var duration = property.FindPropertyRelative(nameof(CharacterAnimationSettings.TotalDurationSeconds));
            var curve = property.FindPropertyRelative(nameof(CharacterAnimationSettings.SpeedCurve));
            var preparationEnd = property.FindPropertyRelative(nameof(CharacterAnimationSettings.PreparationEndFrame));
            var activeEnd = property.FindPropertyRelative(nameof(CharacterAnimationSettings.ActiveEndFrame));
            const int start = 0; // CharacterAnimationSettings.StartFrame: the source clip's first frame

            var row = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            var title = string.IsNullOrEmpty(clipId.stringValue) ? label.text : clipId.stringValue;
            property.isExpanded = EditorGUI.Foldout(row, property.isExpanded,
                $"{title}  [{start}..{end.intValue}] {end.intValue - start + 1} frames, {duration.doubleValue:0.###} s", true);
            if (!property.isExpanded)
            {
                EditorGUI.EndProperty();
                return;
            }

            EditorGUI.indentLevel++;
            row.y += Line;
            EditorGUI.PropertyField(row, clipId);
            row.y += Line;
            EditorGUI.LabelField(row, IndexNote, EditorStyles.miniLabel);
            row.y += Line;
            ReadOnly(row, "Start Frame (read-only)", start, "First frame of the source clip (local index 0).");
            row.y += Line;
            EditorGUI.PropertyField(row, end, new GUIContent("End Frame", end.tooltip));
            row.y += Line;
            ReadOnly(row, "Frame Count (EndFrame − StartFrame + 1)", end.intValue - start + 1, "Derived; not stored.");
            row.y += Line;
            EditorGUI.PropertyField(row, duration);
            row.y += Line;
            var curveHeight = EditorGUI.GetPropertyHeight(curve, true);
            EditorGUI.PropertyField(new Rect(row.x, row.y, row.width, curveHeight), curve, true);
            row.y += curveHeight + EditorGUIUtility.standardVerticalSpacing;

            if (IsAttack(property))
            {
                EditorGUI.LabelField(row, "Strike phases (inclusive local frames; drive the simulated strike)", EditorStyles.boldLabel);
                row.y += Line;
                Phase(row, "Preparation (no hits, may move)", start, null, preparationEnd);
                row.y += Line;
                Phase(row, "Active (hits)", preparationEnd.intValue + 1, null, activeEnd);
                row.y += Line;
                Phase(row, "Recovery (return to stance)", activeEnd.intValue + 1, end.intValue, null);
            }

            EditorGUI.indentLevel--;
            EditorGUI.EndProperty();
        }

        static void ReadOnly(Rect row, string label, int value, string tooltip)
        {
            using (new EditorGUI.DisabledScope(true))
                EditorGUI.IntField(row, new GUIContent(label, tooltip), value);
        }

        /// <summary>"label  start [ro] .. end [edit or ro]": one stored end per phase at most, starts always derived.</summary>
        static void Phase(Rect row, string label, int first, int? fixedLast, SerializedProperty last)
        {
            var field = EditorGUI.PrefixLabel(row, new GUIContent(label, "Inclusive range; the start follows the previous phase's end."));
            var indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            var half = (field.width - 24) / 2;
            using (new EditorGUI.DisabledScope(true))
                EditorGUI.IntField(new Rect(field.x, field.y, half, field.height), first);
            EditorGUI.LabelField(new Rect(field.x + half + 4, field.y, 16, field.height), "..");
            var lastRect = new Rect(field.x + half + 24, field.y, half, field.height);
            if (last != null)
                EditorGUI.PropertyField(lastRect, last, GUIContent.none);
            else
                using (new EditorGUI.DisabledScope(true))
                    EditorGUI.IntField(lastRect, fixedLast ?? 0);
            EditorGUI.indentLevel = indent;
        }
    }
}
