using System;
using System.Collections.Generic;
using System.Text;
using MLFLegends.Combat;
using UnityEditor;
using UnityEngine;

namespace MLFLegends.Editor
{
    /// <summary>Target-owned, revision-cached diagnostics. No raster preview or per-GUI-event timing integration.</summary>
    [CustomEditor(typeof(CharacterAnimationConfig))]
    public sealed class CharacterAnimationConfigEditor : UnityEditor.Editor
    {
        sealed class ManifestCache
        {
            public TextAsset Manifest;
            public int CatalogVersion = -1;
            public Hash128 Hash;
            public SpriteAtlasDefinition Definition;
            public readonly List<string> Errors = new List<string>();
        }

        sealed class ClipDiagnostics
        {
            public CharacterAnimationSettings Settings;
            public SpriteAtlasClip Clip;
            public SpriteAnimationTiming Timing;
            public bool Expanded;
            public string[] PhaseLabels;
            public string[] FrameLabels; // materialized only when the owner first expands frame details
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
        }

        sealed class CharacterDiagnostics
        {
            public string Signature;
            public string Landing;
            public string Strikes;
            public readonly List<ClipDiagnostics> Clips = new List<ClipDiagnostics>();
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
        }

        readonly Dictionary<SpriteAtlasCatalog, ManifestCache> m_Manifests = new Dictionary<SpriteAtlasCatalog, ManifestCache>();
        readonly List<CharacterDiagnostics> m_Characters = new List<CharacterDiagnostics>();
        int m_ConfigVersion = -1;
        bool m_CheckDependencies = true;
        bool m_CheckSettings = true;

        void OnEnable()
        {
            Undo.undoRedoPerformed += Invalidate;
            EditorApplication.projectChanged += ProjectChanged;
        }

        void OnDisable()
        {
            Undo.undoRedoPerformed -= Invalidate;
            EditorApplication.projectChanged -= ProjectChanged;
            m_Characters.Clear();
            m_Manifests.Clear();
        }

        void Invalidate()
        {
            m_CheckDependencies = m_CheckSettings = true;
            Repaint();
        }

        void ProjectChanged()
        {
            m_CheckDependencies = true;
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            if (DrawDefaultInspector()) m_CheckSettings = true;
            EditorGUILayout.HelpBox("Frames are LOCAL clip indices from 0. StartFrame..EndFrame plays over T; the speed curve " +
                "distributes those frames. Preparation / Active / Recovery and T/curve supply the simulated strike windows. " +
                "LandingStartFrame supplies the landing lock. Runtime edits use Config.Set() or this Inspector.", MessageType.Info);
            EditorGUILayout.HelpBox("Sprite Editor pivot positions each image relative to CombatPosition. Catalog Image Pivots " +
                "maps frame_id to Sprite; only an absent binding uses the legacy JSON pivot. Image Offset Pixels adds one " +
                "translation (+X right, +Y down), mirrored with the image and anchors. Reference support for 192×192 is " +
                "[96,163.2]; physics does not move. Raster preview has been removed.", MessageType.Info);
            var config = (CharacterAnimationConfig)target;
            EnsureCache(config);
            var characters = serializedObject.FindProperty("m_Characters");
            for (var c = 0; c < config.Characters.Count; c++)
            {
                var character = config.Characters[c];
                if (character == null) continue;
                var diagnostics = m_Characters[c];
                EditorGUILayout.LabelField(character.CharacterId, EditorStyles.boldLabel);
                if (!float.IsFinite(character.ImageOffsetPixels.x) || !float.IsFinite(character.ImageOffsetPixels.y))
                    EditorGUILayout.HelpBox("Image Offset Pixels must be finite", MessageType.Error);
                foreach (var message in diagnostics.Errors) EditorGUILayout.HelpBox(message, MessageType.Error);
                foreach (var message in diagnostics.Warnings) EditorGUILayout.HelpBox(message, MessageType.Warning);
                for (var a = 0; a < diagnostics.Clips.Count; a++)
                {
                    var item = diagnostics.Clips[a];
                    if (item.Settings == null) continue;
                    if (item.Clip != null)
                        EditorGUILayout.LabelField(item.Settings.ClipId + " clip frames", $"local 0..{item.Clip.Frames.Count - 1} ({item.Clip.Frames.Count})");
                    if (item.Timing != null)
                    {
                        EditorGUILayout.LabelField("Average FPS", (item.Timing.FrameCount / item.Timing.DurationSeconds).ToString("0.###"));
                        if (item.PhaseLabels != null)
                        {
                            EditorGUILayout.LabelField("Preparation", item.PhaseLabels[0]);
                            EditorGUILayout.LabelField("Active", item.PhaseLabels[1]);
                            EditorGUILayout.LabelField("Recovery", item.PhaseLabels[2]);
                            EditorGUILayout.LabelField("Strike", item.PhaseLabels[3]);
                        }
                        item.Expanded = EditorGUILayout.Foldout(item.Expanded, item.Settings.ClipId + " frame duration details", true);
                        if (item.Expanded)
                        {
                            if (item.FrameLabels == null)
                            {
                                item.FrameLabels = new string[item.Timing.FrameCount];
                                for (var f = 0; f < item.FrameLabels.Length; f++)
                                    item.FrameLabels[f] = item.Timing.FrameStartSeconds(f).ToString("0.######") +
                                        " s, hold " + item.Timing.FrameDurationSeconds(f).ToString("0.######") + " s";
                            }
                            for (var f = 0; f < item.FrameLabels.Length; f++)
                                EditorGUILayout.LabelField("Frame " + (item.Settings.StartFrame + f), item.FrameLabels[f]);
                        }
                    }
                    foreach (var message in item.Errors) EditorGUILayout.HelpBox(message, MessageType.Error);
                    foreach (var message in item.Warnings) EditorGUILayout.HelpBox(message, MessageType.Warning);
                    if (item.Clip != null && CombatAnimationSet.IsAttackClip(item.Settings.ClipId))
                        DrawMarkerButton(characters, c, a, item.Settings, item.Clip);
                }
                if (diagnostics.Landing != null)
                {
                    EditorGUILayout.LabelField("Landing recovery", diagnostics.Landing, EditorStyles.wordWrappedLabel);
                    EditorGUILayout.LabelField("Strike windows (ticks)", diagnostics.Strikes, EditorStyles.wordWrappedLabel);
                }
            }
        }

        void EnsureCache(CharacterAnimationConfig config)
        {
            var settingsChanged = m_CheckSettings || m_ConfigVersion != config.Version || m_Characters.Count != config.Characters.Count;
            var dependenciesChanged = m_CheckDependencies;
            foreach (var character in config.Characters)
            {
                var catalog = character?.AtlasCatalog;
                if (catalog != null && (!m_Manifests.TryGetValue(catalog, out var cached) || cached.CatalogVersion != catalog.Version))
                    dependenciesChanged = true;
            }
            if (!settingsChanged && !dependenciesChanged) return; // no lookups, parsing, JSON snapshots or tables on repaint
            var definitionsChanged = false;
            var used = new HashSet<SpriteAtlasCatalog>();
            foreach (var character in config.Characters)
            {
                var catalog = character?.AtlasCatalog;
                if (catalog == null) continue;
                used.Add(catalog);
                if (!m_Manifests.TryGetValue(catalog, out var cache))
                {
                    cache = new ManifestCache();
                    m_Manifests.Add(catalog, cache);
                }
                if (cache.CatalogVersion == catalog.Version && !m_CheckDependencies) continue;
                var manifest = catalog.Manifest;
                var path = AssetDatabase.GetAssetPath(manifest);
                var hash = string.IsNullOrEmpty(path) ? default : AssetDatabase.GetAssetDependencyHash(path);
                if (cache.CatalogVersion == -1 || cache.Manifest != manifest || cache.Hash != hash)
                {
                    cache.Manifest = manifest;
                    cache.Hash = hash;
                    cache.Errors.Clear();
                    cache.Definition = manifest != null ? SpriteAtlasManifest.Parse(manifest.text, cache.Errors) : null;
                    if (manifest == null) cache.Errors.Add("Manifest missing");
                    definitionsChanged = true;
                }
                cache.CatalogVersion = catalog.Version;
            }
            // Prune references as soon as a character/catalog is removed from this target.
            var removed = new List<SpriteAtlasCatalog>();
            foreach (var catalog in m_Manifests.Keys) if (!used.Contains(catalog)) removed.Add(catalog);
            foreach (var catalog in removed) m_Manifests.Remove(catalog);
            m_CheckDependencies = m_CheckSettings = false;
            m_ConfigVersion = config.Version;
            if (!settingsChanged && !definitionsChanged) return;
            // Resolution uniqueness is a config-wide input: adding/removing a duplicate invalidates both entries.
            var identities = new StringBuilder();
            foreach (var entry in config.Characters) identities.Append('|').Append(entry?.CharacterId);
            while (m_Characters.Count > config.Characters.Count) m_Characters.RemoveAt(m_Characters.Count - 1);
            for (var c = 0; c < config.Characters.Count; c++)
            {
                var character = config.Characters[c];
                if (c == m_Characters.Count) m_Characters.Add(new CharacterDiagnostics());
                if (character == null) { m_Characters[c] = new CharacterDiagnostics(); continue; }
                var signature = new StringBuilder(identities.ToString());
                signature.Append('|').Append(character.CharacterId);
                signature.Append('|').Append(character.AtlasCatalog != null ? character.AtlasCatalog.GetEntityId().ToString() : "0");
                foreach (var settings in character.Animations ?? Array.Empty<CharacterAnimationSettings>())
                    signature.Append('|').Append(settings == null ? "null" : JsonUtility.ToJson(settings));
                // Offset-only edits and unchanged Undo/Apply do not integrate the speed curve again.
                if (!definitionsChanged && m_Characters[c].Signature == signature.ToString()) continue;
                var item = Build(config, character);
                item.Signature = signature.ToString();
                m_Characters[c] = item;
            }
        }

        CharacterDiagnostics Build(CharacterAnimationConfig config, CharacterAnimationEntry character)
        {
            var result = new CharacterDiagnostics();
            if (character.AtlasCatalog == null) { result.Errors.Add("No catalog assigned"); return result; }
            var manifest = m_Manifests[character.AtlasCatalog];
            result.Errors.AddRange(manifest.Errors);
            var definition = manifest.Definition;
            if (definition == null) return result;
            if (definition.CharacterId != character.CharacterId)
                result.Errors.Add($"Catalog character_id is {definition.CharacterId}, expected {character.CharacterId}");
            var set = CombatAnimationSet.Create(definition, clipId =>
            {
                if (config.TryResolve(character.CharacterId, clipId, out _, out var settings, out var error)) return settings;
                result.Errors.Add(error);
                return null;
            }, character.CharacterId, result.Errors, result.Warnings);
            if (set != null)
            {
                result.Landing = $"jump {set.LandingStartFrame}..{set.LandingEndFrame}: {set.LandingTicks} ticks = {set.LandingTicks / (double)SimulationTiming.TickRateHz:0.######} s (movement/new actions locked)";
                result.Strikes = CombatSmokeBootstrap.Describe(set.StrikeSchedule);
            }
            foreach (var settings in character.Animations ?? Array.Empty<CharacterAnimationSettings>())
            {
                var item = new ClipDiagnostics { Settings = settings };
                result.Clips.Add(item);
                if (settings == null) continue;
                if (!config.TryResolve(character.CharacterId, settings.ClipId, out _, out _, out var error))
                { item.Errors.Add(error); continue; }
                if (!definition.TryGetClipIndex(settings.ClipId, out var index))
                { item.Errors.Add("Clip missing: " + settings.ClipId); continue; }
                item.Clip = definition.Clips[index];
                // Reuse the valid runtime set's timing. If the set is invalid, show remaining per-clip diagnostics too.
                if (set != null)
                {
                    if (settings.ClipId == CombatAnimationSet.IdleClip) item.Timing = set.Idle;
                    else if (settings.ClipId == CombatAnimationSet.RunClip) item.Timing = set.Run;
                    else if (settings.ClipId == CombatAnimationSet.JumpClip) item.Timing = set.Jump;
                    else
                        for (var kind = CombatAttackKind.PunchLeft; kind <= CombatAttackKind.KickSpin; kind++)
                            if (CombatAnimationSet.AttackClip(kind) == settings.ClipId) item.Timing = set.Attack(kind);
                }
                if (item.Timing == null)
                    item.Timing = SpriteAnimationTiming.Create(item.Clip, settings, character.CharacterId + "/" + settings.ClipId,
                        item.Errors, item.Warnings, item.Clip.Loop, CombatAnimationSet.IsAttackClip(settings.ClipId));
                if (item.Timing != null && CombatAnimationSet.IsAttackClip(settings.ClipId))
                    item.PhaseLabels = StrikeLabels(settings.ClipId, item.Timing, item.Errors);
            }
            return result;
        }

        static string[] StrikeLabels(string clipId, SpriteAnimationTiming timing, List<string> errors)
        {
            var kind = CombatAttackKind.None;
            for (var k = CombatAttackKind.PunchLeft; k <= CombatAttackKind.KickSpin; k++)
                if (CombatAnimationSet.AttackClip(k) == clipId) kind = k;
            if (!timing.TryStrikeTiming(CombatAttackTuning.Get(kind).ChainDelayTicks, out var strike, out var error))
            { errors.Add(clipId + ": " + error); return null; }
            return new[]
            {
                $"frames 0..{timing.PreparationEnd}, 0 – {timing.ActiveStartSeconds:0.####} s → ticks {Ticks(0, strike.ActiveStart)}",
                $"frames {timing.PreparationEnd + 1}..{timing.ActiveEnd}, {timing.ActiveStartSeconds:0.####} – {timing.RecoveryStartSeconds:0.####} s → ticks {Ticks(strike.ActiveStart, strike.RecoveryStart)}",
                $"frames {timing.ActiveEnd + 1}..{timing.FrameCount - 1}, {timing.RecoveryStartSeconds:0.####} – {timing.DurationSeconds:0.####} s → ticks {Ticks(strike.RecoveryStart, strike.End)}",
                $"{strike.End} ticks = {strike.End / (double)SimulationTiming.TickRateHz:0.####} s" +
                    (strike.CanChain ? $", follow-up from tick {strike.ChainPoint}" : ", finisher (no follow-up)"),
            };
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
