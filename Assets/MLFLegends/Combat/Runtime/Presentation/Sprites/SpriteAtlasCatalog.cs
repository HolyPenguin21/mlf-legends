using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;

namespace MLFLegends.Combat
{
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

        public FrameStorage Storage => m_FrameStorage;

        public TextAsset Manifest => m_Manifest;

        public IReadOnlyList<SpriteAtlasPageBinding> Pages => m_Pages ?? Array.Empty<SpriteAtlasPageBinding>();

        /// <summary>Sets the content (tests and tools). Bound renderers reload on their next frame.</summary>
        public void Set(TextAsset manifest, SpriteAtlasPageBinding[] pages, FrameStorage storage = FrameStorage.TrimmedRects)
        {
            m_Manifest = manifest;
            m_Pages = pages ?? Array.Empty<SpriteAtlasPageBinding>();
            m_FrameStorage = storage;
            Version++;
        }

        /// <summary>Changes on every <see cref="Set"/> or Inspector edit; renderers reload when it differs.</summary>
        public int Version { get; private set; }

        void OnValidate() => Version++;

        /// <summary>Actual sampled page region and its placement on the virtual source canvas. Shared by loading and preview.</summary>
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
