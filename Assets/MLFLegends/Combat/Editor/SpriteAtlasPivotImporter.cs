using System;
using System.Collections.Generic;
using MLFLegends.Combat;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MLFLegends.Editor
{
    /// <summary>Asset-boundary migration/invalidation only. Never changes importer pivots, names, rects or art.</summary>
    [InitializeOnLoad]
    public static class SpriteAtlasPivotImporter
    {
        static readonly HashSet<string> s_Changed = new HashSet<string>(StringComparer.Ordinal);
        static bool s_All;
        static bool s_Queued;

        static SpriteAtlasPivotImporter() => Queue(null); // also restores dependencies after a domain reload

        internal static void Queue(string[] paths)
        {
            if (paths == null) s_All = true;
            else foreach (var path in paths) s_Changed.Add(path);
            if (s_Queued) return;
            s_Queued = true;
            EditorApplication.delayCall += Flush;
        }

        internal static void Flush()
        {
            EditorApplication.delayCall -= Flush;
            s_Queued = false;
            var all = s_All;
            var changed = new HashSet<string>(s_Changed, StringComparer.Ordinal);
            s_All = false;
            s_Changed.Clear(); // queued state contains paths only, never foreign asset references
            foreach (var guid in AssetDatabase.FindAssets("t:SpriteAtlasCatalog"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var catalog = AssetDatabase.LoadAssetAtPath<SpriteAtlasCatalog>(path);
                if (catalog == null) continue;
                var affected = all || changed.Contains(path) || changed.Contains(AssetDatabase.GetAssetPath(catalog.Manifest));
                foreach (var page in catalog.Pages)
                    affected |= changed.Contains(AssetDatabase.GetAssetPath(page.Texture));
                foreach (var binding in catalog.ImagePivots)
                    affected |= changed.Contains(AssetDatabase.GetAssetPath(binding.Sprite));
                if (!affected) continue;
                BindMissing(catalog);
                catalog.NotifyContentChanged(); // Apply can change pivot without changing a single serialized reference
            }
        }

        /// <summary>Explicit frame_id migration by page + exact useful/full-canvas rect. Existing references win.</summary>
        internal static void BindMissing(SpriteAtlasCatalog catalog)
        {
            if (catalog.Manifest == null) return;
            var errors = new List<string>();
            var definition = SpriteAtlasManifest.Parse(catalog.Manifest.text, errors);
            if (definition == null) return; // normal catalog/runtime diagnostics report malformed JSON
            var bindings = new List<SpriteAtlasPivotBinding>(catalog.ImagePivots);
            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in bindings) existing.Add(binding.FrameId);
            var sprites = new List<Sprite>[definition.Pages.Count];
            for (var p = 0; p < sprites.Length; p++)
            {
                sprites[p] = new List<Sprite>();
                foreach (var page in catalog.Pages)
                {
                    if (page.File != definition.Pages[p].File || page.Texture == null) continue;
                    var path = AssetDatabase.GetAssetPath(page.Texture);
                    if (string.IsNullOrEmpty(path)) continue;
                    foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                        if (asset is Sprite sprite && sprite.texture == page.Texture) sprites[p].Add(sprite);
                }
            }
            var added = false;
            foreach (var frame in definition.Frames)
            {
                if (existing.Contains(frame.Id)) continue;
                Sprite match = null;
                var count = 0;
                foreach (var sprite in sprites[frame.Page])
                {
                    if (!SpriteAtlasCatalog.TrySpriteCanvasOrigin(definition, frame, sprite.rect, out _)) continue;
                    match = sprite;
                    count++;
                }
                if (count == 0) continue; // explicit legacy fallback: no imported Sprite data for this frame
                if (count > 1)
                {
                    // Persist an invalid binding: ambiguity must never silently select an arbitrary Sprite/JSON pivot.
                    match = null;
                    Debug.LogError($"{catalog.name}: image pivots.{frame.Id}: {count} matching Sprites; assign the intended Sprite explicitly in Image Pivots.", catalog);
                }
                bindings.Add(new SpriteAtlasPivotBinding(frame.Id, match));
                added = true;
            }
            if (!added) return;
            catalog.SetImagePivots(bindings.ToArray());
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssetIfDirty(catalog); // build receives the same persistent subasset references
        }
    }

    public sealed class SpriteAtlasPivotPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            // Delay until importing finishes; only relevant catalog dependencies are touched.
            SpriteAtlasPivotImporter.Queue(imported);
            SpriteAtlasPivotImporter.Queue(moved);
            if (deleted.Length != 0 || movedFrom.Length != 0) SpriteAtlasPivotImporter.Queue(null);
        }
    }

    public sealed class SpriteAtlasPivotBuildPreparation : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;
        public void OnPreprocessBuild(BuildReport report)
        {
            SpriteAtlasPivotImporter.Queue(null);
            SpriteAtlasPivotImporter.Flush();
        }
    }

    [CustomEditor(typeof(SpriteAtlasCatalog))]
    public sealed class SpriteAtlasCatalogEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            if (DrawDefaultInspector())
            {
                SpriteAtlasPivotImporter.BindMissing((SpriteAtlasCatalog)target);
                ((SpriteAtlasCatalog)target).NotifyContentChanged();
            }
            EditorGUILayout.HelpBox("Image Pivots binds frame_id to a Sprite subasset. Edit that Sprite in Sprite Editor and Apply; " +
                "Center is valid. No entry = legacy JSON pivot. Existing entries are never replaced automatically. " +
                "Missing bindings migrate by exact page/rect when assets are imported.", MessageType.Info);
        }
    }
}
