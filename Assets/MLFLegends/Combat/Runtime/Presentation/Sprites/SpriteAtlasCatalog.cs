using System;
using System.Collections.Generic;
using UnityEngine;

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
        [Tooltip("Manifest v1 JSON (Docs/SPRITE_ATLAS_CONTRACT.md).")]
        [SerializeField] TextAsset m_Manifest;

        [Tooltip("One entry per manifest page; File must equal the page's 'file'. Import: Point, Clamp, no mipmaps, no compression.")]
        [SerializeField] SpriteAtlasPageBinding[] m_Pages = Array.Empty<SpriteAtlasPageBinding>();

        public TextAsset Manifest => m_Manifest;

        public IReadOnlyList<SpriteAtlasPageBinding> Pages => m_Pages ?? Array.Empty<SpriteAtlasPageBinding>();

        /// <summary>Sets the content (tests and tools). Bound renderers reload on their next frame.</summary>
        public void Set(TextAsset manifest, SpriteAtlasPageBinding[] pages)
        {
            m_Manifest = manifest;
            m_Pages = pages ?? Array.Empty<SpriteAtlasPageBinding>();
            Version++;
        }

        /// <summary>Changes on every <see cref="Set"/> or Inspector edit; renderers reload when it differs.</summary>
        public int Version { get; private set; }

        void OnValidate() => Version++;
    }
}
