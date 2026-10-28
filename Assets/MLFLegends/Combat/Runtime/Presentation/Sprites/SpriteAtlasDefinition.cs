using System.Collections.Generic;
using Unity.Mathematics;

namespace MLFLegends.Combat
{
    /// <summary>Layer of an anchor relative to the body (for future weapon/VFX layers).</summary>
    public enum SpriteAnchorLayer : byte
    {
        Back = 0,
        Front = 1,
    }

    /// <summary>One anchor record of a frame, in source-canvas pixels before trim (top-left origin, Y down).</summary>
    public readonly struct SpriteAnchor
    {
        public readonly float2 Position;
        public readonly float2 Direction;
        public readonly SpriteAnchorLayer Layer;
        public readonly bool Visible;

        public SpriteAnchor(float2 position, float2 direction, SpriteAnchorLayer layer, bool visible)
        {
            Position = position;
            Direction = direction;
            Layer = layer;
            Visible = visible;
        }
    }

    /// <summary>One page (PNG) of an atlas as declared by the manifest.</summary>
    public readonly struct SpriteAtlasPage
    {
        public readonly string File;
        public readonly int2 Size;

        public SpriteAtlasPage(string file, int2 size)
        {
            File = file;
            Size = size;
        }
    }

    /// <summary>One frame: its useful rect on a page and where that rect lies on the full source canvas.</summary>
    public readonly struct SpriteAtlasFrame
    {
        public readonly string Id;
        public readonly int Page;

        /// <summary>x, y, w, h on the page in pixels, top-left origin, without extrusion.</summary>
        public readonly int4 Rect;

        /// <summary>Top-left corner of <see cref="Rect"/> on the source canvas.</summary>
        public readonly int2 TrimOffset;

        public readonly SpriteAnchor HandLeft;
        public readonly SpriteAnchor HandRight;

        /// <summary>Valid only when <see cref="HasCastOrigin"/>.</summary>
        public readonly SpriteAnchor CastOrigin;

        public readonly bool HasCastOrigin;

        public SpriteAtlasFrame(string id, int page, int4 rect, int2 trimOffset,
            in SpriteAnchor handLeft, in SpriteAnchor handRight, in SpriteAnchor castOrigin, bool hasCastOrigin)
        {
            Id = id;
            Page = page;
            Rect = rect;
            TrimOffset = trimOffset;
            HandLeft = handLeft;
            HandRight = handRight;
            CastOrigin = castOrigin;
            HasCastOrigin = hasCastOrigin;
        }
    }

    /// <summary>A clip: ordered frame indices with durations, playback flags and graphic-only markers.</summary>
    public sealed class SpriteAtlasClip
    {
        public readonly string Id;

        /// <summary>Indices into <see cref="SpriteAtlasDefinition.Frames"/>; the same frame may appear in several clips.</summary>
        public readonly IReadOnlyList<int> Frames;

        public readonly IReadOnlyList<float> DurationsMs;
        public readonly bool Loop;
        public readonly bool HoldLast;

        /// <summary>marker name → local (zero-based) frame indices inside this clip.</summary>
        public readonly IReadOnlyDictionary<string, int[]> Markers;

        public SpriteAtlasClip(string id, int[] frames, float[] durationsMs, bool loop, bool holdLast, Dictionary<string, int[]> markers)
        {
            Id = id;
            Frames = frames;
            DurationsMs = durationsMs;
            Loop = loop;
            HoldLast = holdLast;
            Markers = markers;
        }
    }

    /// <summary>A graphically compatible clip change (local zero-based frame indices). Never a gameplay cancel window.</summary>
    public readonly struct SpriteAtlasTransition
    {
        public readonly int FromClip;
        public readonly int ExitFrame;
        public readonly int ToClip;
        public readonly int EntryFrame;

        public SpriteAtlasTransition(int fromClip, int exitFrame, int toClip, int entryFrame)
        {
            FromClip = fromClip;
            ExitFrame = exitFrame;
            ToClip = toClip;
            EntryFrame = entryFrame;
        }
    }

    /// <summary>
    /// Validated, immutable content of one atlas manifest (contract v1, Docs/SPRITE_ATLAS_CONTRACT.md). String ids are
    /// resolved to indices once here; nothing looks frames up by string per render frame.
    /// </summary>
    /// <remarks>Created only by <see cref="SpriteAtlasManifest.Parse"/>. Holds no Unity objects or GPU resources.</remarks>
    public sealed class SpriteAtlasDefinition
    {
        public const int SupportedSchemaVersion = 1;

        readonly Dictionary<string, int> m_FrameIndices;
        readonly Dictionary<string, int> m_ClipIndices;

        public string CharacterId { get; }
        public int2 SourceSize { get; }
        public float NominalHeightPx { get; }
        /// <summary>JSON fallback image pivot, used only for a frame without an imported Sprite binding.</summary>
        public float2 Pivot { get; }
        /// <summary>Fixed world support in the virtual source canvas (top-left origin): 50% X, 15% above the bottom.</summary>
        public float2 WorldSupport { get; }
        public IReadOnlyList<SpriteAtlasPage> Pages { get; }

        /// <summary>Frames in manifest order; the index is the runtime frame index.</summary>
        public IReadOnlyList<SpriteAtlasFrame> Frames { get; }

        public IReadOnlyList<SpriteAtlasClip> Clips { get; }
        public IReadOnlyList<SpriteAtlasTransition> Transitions { get; }

        internal SpriteAtlasDefinition(string characterId, int2 sourceSize, float nominalHeightPx, float2 pivot,
            SpriteAtlasPage[] pages, SpriteAtlasFrame[] frames, SpriteAtlasClip[] clips, SpriteAtlasTransition[] transitions)
        {
            CharacterId = characterId;
            SourceSize = sourceSize;
            NominalHeightPx = nominalHeightPx;
            Pivot = pivot;
            WorldSupport = new float2(sourceSize.x * 0.5f, sourceSize.y * 0.85f);
            Pages = pages;
            Frames = frames;
            Clips = clips;
            Transitions = transitions;
            m_FrameIndices = new Dictionary<string, int>(frames.Length);
            for (var i = 0; i < frames.Length; i++)
                m_FrameIndices.Add(frames[i].Id, i);
            m_ClipIndices = new Dictionary<string, int>(clips.Length);
            for (var i = 0; i < clips.Length; i++)
                m_ClipIndices.Add(clips[i].Id, i);
        }

        public bool TryGetFrameIndex(string frameId, out int index)
        {
            index = -1;
            return frameId != null && m_FrameIndices.TryGetValue(frameId, out index);
        }

        public bool TryGetClipIndex(string clipId, out int index)
        {
            index = -1;
            return clipId != null && m_ClipIndices.TryGetValue(clipId, out index);
        }

        /// <summary>Frame index of local frame <paramref name="localIndex"/> of clip <paramref name="clipId"/>.</summary>
        public bool TryGetClipFrame(string clipId, int localIndex, out int frameIndex)
        {
            frameIndex = -1;
            if (!TryGetClipIndex(clipId, out var clip))
                return false;
            var frames = Clips[clip].Frames;
            if (localIndex < 0 || localIndex >= frames.Count)
                return false;
            frameIndex = frames[localIndex];
            return true;
        }
    }
}
