using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace MLFLegends.Combat
{
    /// <summary>GPU frame table entry (32 bytes); layout mirrored by <c>SpriteFrame</c> in CombatSprite.shader.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SpriteFrameGpu
    {
        /// <summary>u0, v0 (bottom edge), u1, v1 (top edge) of the useful rect on its page.</summary>
        public float4 Uv;

        /// <summary>Authored card rect in pixels relative to the pivot: left, bottom, right, top (y up).</summary>
        public float4 CardRect;
    }

    /// <summary>
    /// A loaded, validated atlas shared by every fighter drawn with it: definition, page textures (not owned), CPU frame
    /// tables and, once a graphics device asks for it, one GPU frame buffer (owned).
    /// </summary>
    /// <remarks>
    /// Created all-or-nothing by <see cref="Load"/>: a rejected manifest or page binding returns null and allocates
    /// nothing. Dispose releases the native tables and the GPU buffer; page textures belong to their importer/creator.
    /// </remarks>
    public sealed class SpriteAtlas : IDisposable
    {
        public static readonly int FrameStride = Marshal.SizeOf<SpriteFrameGpu>();

        readonly Texture2D[] m_Pages;
        NativeArray<SpriteFrameGpu> m_Frames;
        NativeArray<int> m_FramePages;
        GraphicsBuffer m_FrameBuffer;

        public SpriteAtlasDefinition Definition { get; }

        public int FrameCount => Definition.Frames.Count;

        public int PageCount => m_Pages.Length;

        /// <summary>Frame table on the CPU (index = runtime frame index).</summary>
        public NativeArray<SpriteFrameGpu>.ReadOnly Frames => m_Frames.AsReadOnly();

        /// <summary>Page of each frame.</summary>
        public NativeArray<int>.ReadOnly FramePages => m_FramePages.AsReadOnly();

        /// <summary>Largest |x| of any frame's card rect (pixels), so both facings fit.</summary>
        public float MaxHalfWidthPx { get; }

        /// <summary>Highest card y above the pivot over all frames (pixels, ≥ 0).</summary>
        public float MaxTopPx { get; }

        /// <summary>Deepest card y below the pivot over all frames (pixels, ≥ 0).</summary>
        public float MaxBelowPx { get; }

        /// <summary>Uncompressed page memory: Σ width × height × 4 bytes (mip 0 only; mipmaps are rejected/warned).</summary>
        public long PageBytes { get; }

        /// <summary>Bytes of the frame table (CPU copy; the GPU buffer has the same size).</summary>
        public int FrameTableBytes => FrameCount * FrameStride;

        public bool IsDisposed { get; private set; }

        SpriteAtlas(SpriteAtlasDefinition definition, Texture2D[] pages)
        {
            Definition = definition;
            m_Pages = pages;
            var frames = definition.Frames;
            m_Frames = new NativeArray<SpriteFrameGpu>(frames.Count, Allocator.Persistent);
            m_FramePages = new NativeArray<int>(frames.Count, Allocator.Persistent);
            float halfWidth = 0f, top = 0f, below = 0f;
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                var card = SpriteCardMath.FrameCardRect(frame.Rect, frame.TrimOffset, definition.Pivot);
                m_Frames[i] = new SpriteFrameGpu { Uv = SpriteCardMath.FrameUv(frame.Rect, definition.Pages[frame.Page].Size), CardRect = card };
                m_FramePages[i] = frame.Page;
                halfWidth = math.max(halfWidth, math.max(math.abs(card.x), math.abs(card.z)));
                top = math.max(top, card.w);
                below = math.max(below, -card.y);
            }

            MaxHalfWidthPx = halfWidth;
            MaxTopPx = top;
            MaxBelowPx = below;
            long bytes = 0;
            foreach (var page in definition.Pages)
                bytes += (long)page.Size.x * page.Size.y * 4;
            PageBytes = bytes;
        }

        /// <summary>Loads a catalog asset (null catalog or manifest → error).</summary>
        public static SpriteAtlas Load(SpriteAtlasCatalog catalog, List<string> errors, List<string> warnings)
        {
            if (catalog == null || catalog.Manifest == null)
            {
                errors.Add("catalog: no manifest assigned");
                return null;
            }

            return Load(catalog.Manifest.text, catalog.Pages, errors, warnings);
        }

        /// <summary>
        /// Parses <paramref name="json"/> and binds every manifest page to exactly one texture of the same file name and size.
        /// Returns null (and allocates nothing) on any error.
        /// </summary>
        public static SpriteAtlas Load(string json, IReadOnlyList<SpriteAtlasPageBinding> bindings, List<string> errors, List<string> warnings)
        {
            if (errors == null)
                throw new ArgumentNullException(nameof(errors));
            var definition = SpriteAtlasManifest.Parse(json, errors);
            if (definition == null)
                return null;

            var pages = new Texture2D[definition.Pages.Count];
            var ok = true;
            bindings ??= Array.Empty<SpriteAtlasPageBinding>();
            for (var b = 0; b < bindings.Count; b++)
            {
                var binding = bindings[b];
                var index = -1;
                for (var p = 0; p < definition.Pages.Count; p++)
                    if (definition.Pages[p].File == binding.File)
                        index = p;
                if (index < 0)
                {
                    errors.Add($"pages binding {b}: file \"{binding.File}\" is not a page of the manifest");
                    ok = false;
                    continue;
                }

                if (pages[index] != null)
                {
                    errors.Add($"pages binding {b}: page \"{binding.File}\" is bound twice");
                    ok = false;
                    continue;
                }

                var texture = binding.Texture;
                var expected = definition.Pages[index].Size;
                if (texture == null)
                {
                    errors.Add($"pages[{index}] \"{binding.File}\": no texture assigned");
                    ok = false;
                    continue;
                }

                if (texture.width != expected.x || texture.height != expected.y)
                {
                    errors.Add($"pages[{index}] \"{binding.File}\": texture '{texture.name}' is {texture.width}x{texture.height}, manifest says {expected.x}x{expected.y} (import resized?)");
                    ok = false;
                }

                if (GraphicsFormatUtility.IsCompressedFormat(texture.graphicsFormat))
                {
                    errors.Add($"pages[{index}] \"{binding.File}\": texture '{texture.name}' uses compressed {texture.graphicsFormat}; import with Compression None");
                    ok = false;
                }

                if (texture.mipmapCount > 1)
                    warnings?.Add($"pages[{index}] \"{binding.File}\": texture '{texture.name}' has {texture.mipmapCount} mipmaps; disable Generate Mipmaps (sampling uses mip 0, the rest wastes memory)");
                if (!texture.isDataSRGB)
                    warnings?.Add($"pages[{index}] \"{binding.File}\": texture '{texture.name}' is not sRGB; colour pages should be imported as sRGB");
                pages[index] = texture;
            }

            for (var p = 0; p < pages.Length; p++)
            {
                if (pages[p] != null)
                    continue;
                errors.Add($"pages[{p}] \"{definition.Pages[p].File}\": no binding");
                ok = false;
            }

            return ok ? new SpriteAtlas(definition, pages) : null;
        }

        public Texture2D GetPage(int page) => m_Pages[page];

        /// <summary>The GPU frame table, created on first use (requires a graphics device) and owned by this atlas.</summary>
        public GraphicsBuffer GetFrameBuffer()
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(SpriteAtlas));
            if (m_FrameBuffer == null)
            {
                m_FrameBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, FrameCount, FrameStride);
                m_FrameBuffer.SetData(m_Frames);
                GpuBuffersCreated++;
            }

            return m_FrameBuffer;
        }

        /// <summary>GPU buffers this atlas created so far (1 after the first draw; never grows per frame).</summary>
        public int GpuBuffersCreated { get; private set; }

        public void Dispose()
        {
            if (IsDisposed)
                return;
            IsDisposed = true;
            m_FrameBuffer?.Release();
            m_FrameBuffer = null;
            if (m_Frames.IsCreated)
                m_Frames.Dispose();
            if (m_FramePages.IsCreated)
                m_FramePages.Dispose();
        }
    }
}
