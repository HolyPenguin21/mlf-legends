using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;

namespace MLFLegends.Combat
{
    /// <summary>Stable frame_id → imported Sprite subasset. Sprite.rect/pivot are read once at atlas load, also in builds.</summary>
    [Serializable]
    public struct SpriteAtlasPivotBinding
    {
        public string FrameId;
        public Sprite Sprite;

        public SpriteAtlasPivotBinding(string frameId, Sprite sprite)
        {
            FrameId = frameId;
            Sprite = sprite;
        }
    }

    /// <summary>Explicit binding of one manifest page <c>file</c> to an imported texture.</summary>
    [Serializable]
    public struct SpriteAtlasPageBinding
    {
        [Tooltip("Exactly the 'file' string of the manifest page.")]
        public string File;
        public Texture2D Texture;

        public SpriteAtlasPageBinding(string file, Texture2D texture)
        {
            File = file;
            Texture = texture;
        }
    }

    /// <summary>
    /// Content asset of one character atlas: the manifest JSON and its imported page textures, bound by file name.
    /// Holds references only; parsing/validation happens in <see cref="SpriteAtlas.Load"/> when a renderer binds it.
    /// </summary>
    /// <remarks>The textures and this asset belong to the project; consumers never destroy or modify them.</remarks>
    [CreateAssetMenu(menuName = "MLF Legends/Sprite Atlas Catalog", fileName = "SpriteAtlasCatalog")]
    public sealed class SpriteAtlasCatalog : ScriptableObject
    {
        public enum FrameStorage : byte
        {
            TrimmedRects = 0,
            SourceCanvasCells = 1,
        }

        [Tooltip("Manifest v1 JSON (Docs/SPRITE_ATLAS_CONTRACT.md).")]
        [SerializeField] TextAsset m_Manifest;

        [Tooltip("One entry per manifest page; File must equal the page's 'file'. Import: Point, Clamp, no mipmaps, no compression.")]
        [SerializeField] SpriteAtlasPageBinding[] m_Pages = Array.Empty<SpriteAtlasPageBinding>();

        [Tooltip("TrimmedRects: genuinely packed useful rectangles. SourceCanvasCells: each frame retains its ENTIRE source_size canvas on the page; origin = rect.xy - trim_offset. Use for the editable mage sheet so old trim cannot cut off repainted limbs. Never enable for a packed page.")]
        [SerializeField] FrameStorage m_FrameStorage;

        [Tooltip("frame_id to Sprite Editor image pivot. Missing frame entry explicitly uses the legacy JSON pivot. A present but broken binding is an error. Editor migrates unbound page Sprites by exact rect, never array order.")]
        [SerializeField] SpriteAtlasPivotBinding[] m_ImagePivots = Array.Empty<SpriteAtlasPivotBinding>();

        public IReadOnlyList<SpriteAtlasPivotBinding> ImagePivots => m_ImagePivots ?? Array.Empty<SpriteAtlasPivotBinding>();

        public void SetImagePivots(SpriteAtlasPivotBinding[] bindings)
        {
            m_ImagePivots = bindings == null ? Array.Empty<SpriteAtlasPivotBinding>() : (SpriteAtlasPivotBinding[])bindings.Clone();
            NotifyContentChanged();
        }

        public FrameStorage Storage => m_FrameStorage;

        public TextAsset Manifest => m_Manifest;

        public IReadOnlyList<SpriteAtlasPageBinding> Pages => m_Pages ?? Array.Empty<SpriteAtlasPageBinding>();

        /// <summary>Sets the content (tests and tools). Bound renderers reload on their next frame.</summary>
        public void Set(TextAsset manifest, SpriteAtlasPageBinding[] pages) => Set(manifest, pages, m_FrameStorage);

        /// <summary>Explicitly replaces content AND its storage layout. The two-argument overload preserves the layout.</summary>
        public void Set(TextAsset manifest, SpriteAtlasPageBinding[] pages, FrameStorage storage)
        {
            m_Manifest = manifest;
            m_Pages = pages ?? Array.Empty<SpriteAtlasPageBinding>();
            m_FrameStorage = storage;
            Version++;
        }

        /// <summary>Changes on every <see cref="Set"/> or Inspector edit; renderers reload when it differs.</summary>
        public int Version { get; private set; }

        // Importer calls this after Sprite Editor Apply / manifest reimport, even when subasset references stay identical.
        public void NotifyContentChanged() => Version++;

        void OnValidate() => NotifyContentChanged();

        /// <summary>
        /// Valid Sprite regions for a frame: its useful rect OR its complete source canvas (Unity Y up). With
        /// <paramref name="customPosition"/> (#70) a region of either size may also be shifted on the page by at most half the
        /// source canvas: the shift is the owner's custom image position, returned in <paramref name="origin"/> (it moves the
        /// image pivot; the drawn pixels stay the manifest's frame region, so a shifted rect never samples a neighbour).
        /// </summary>
        public static bool TrySpriteCanvasOrigin(SpriteAtlasDefinition definition, in SpriteAtlasFrame frame, Rect spriteRect,
            out float2 origin, bool customPosition = false)
        {
            var pageHeight = definition.Pages[frame.Page].Size.y;
            var topLeft = new float2(spriteRect.x, pageHeight - spriteRect.yMax);
            var size = new float2(spriteRect.width, spriteRect.height);
            var canvasOrigin = frame.Rect.xy - frame.TrimOffset;
            var useful = math.all(size == (float2)frame.Rect.zw);
            var canvas = math.all(size == (float2)definition.SourceSize);
            // The useful rect sits at trim_offset inside the canvas; the shift is measured from where that region belongs.
            var expected = useful && !canvas ? (float2)frame.Rect.xy : canvasOrigin;
            origin = topLeft - canvasOrigin;
            if (!useful && !canvas) return false;
            if (math.all(topLeft == expected)) return true;
            return customPosition && math.all(math.abs(topLeft - expected) <= (float2)definition.SourceSize * .5f);
        }

        /// <summary>Resolve once before native/GPU allocation. Center is real data; only an absent entry uses JSON.</summary>
        internal bool TryImagePivots(SpriteAtlasDefinition definition, Texture2D[] pages, List<string> errors, out float2[] pivots,
            List<string> warnings = null)
        {
            pivots = new float2[definition.Frames.Count];
            for (var i = 0; i < pivots.Length; i++) pivots[i] = definition.Pivot;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var before = errors.Count;
            foreach (var binding in ImagePivots)
            {
                if (string.IsNullOrWhiteSpace(binding.FrameId) || !definition.TryGetFrameIndex(binding.FrameId, out var index))
                {
                    errors.Add($"image pivots.{binding.FrameId}: frame_id missing from manifest");
                    continue;
                }
                if (!seen.Add(binding.FrameId))
                {
                    errors.Add($"image pivots.{binding.FrameId}: duplicate binding");
                    continue;
                }
                var sprite = binding.Sprite;
                var frame = definition.Frames[index];
                if (sprite == null || sprite.texture != pages[frame.Page]
                    || !TrySpriteCanvasOrigin(definition, frame, sprite.rect, out var origin, customPosition: true))
                {
                    errors.Add($"image pivots.{binding.FrameId}: Sprite must belong to its page and have the size of its useful rect or full source canvas, at most half a canvas from it; repair the binding. Legacy JSON fallback requires no imported Sprite binding for this frame");
                    continue;
                }
                if (!TrySpriteCanvasOrigin(definition, frame, sprite.rect, out _))
                    warnings?.Add($"image pivots.{binding.FrameId}: Sprite rect is shifted from its frame by {Shift(definition, frame, sprite.rect)} px: " +
                        "used as a custom image position (pixels still come from the manifest region; prefer a custom pivot on the exact cell)");
                // Sprite.pivot is already in rect-local PIXELS with Y up, regardless of PPU or importer alignment.
                var pivot = origin + new float2(sprite.pivot.x, sprite.rect.height - sprite.pivot.y);
                if (!math.all(math.isfinite(pivot)))
                    errors.Add($"image pivots.{binding.FrameId}: non-finite Sprite pivot");
                else
                    pivots[index] = pivot;
            }
            return errors.Count == before;
        }

        static float2 Shift(SpriteAtlasDefinition definition, in SpriteAtlasFrame frame, Rect rect)
        {
            var useful = math.all(new float2(rect.width, rect.height) == (float2)frame.Rect.zw) && !math.all(new float2(rect.width, rect.height) == (float2)definition.SourceSize);
            var expected = useful ? (float2)frame.Rect.xy : (float2)(frame.Rect.xy - frame.TrimOffset);
            return new float2(rect.x, definition.Pages[frame.Page].Size.y - rect.yMax) - expected;
        }

        /// <summary>Actual sampled page region and its placement on the virtual source canvas.</summary>
        public static bool TryFrameRegion(SpriteAtlasDefinition definition, in SpriteAtlasFrame frame, FrameStorage storage,
            out int4 rect, out int2 trim, out string error)
        {
            rect = frame.Rect;
            trim = frame.TrimOffset;
            error = null;
            if (storage == FrameStorage.TrimmedRects)
                return true;
            if (storage != FrameStorage.SourceCanvasCells)
            {
                error = "unknown frame storage";
                return false;
            }
            var origin = rect.xy - trim;
            var size = definition.SourceSize;
            var page = definition.Pages[frame.Page].Size;
            if (math.any(origin < 0) || math.any(origin + size > page))
            {
                error = $"frames.{frame.Id}: complete source canvas at {origin} with size {size} exceeds page {page}";
                return false;
            }
            rect = new int4(origin, size);
            trim = int2.zero;
            return true;
        }
    }
}
