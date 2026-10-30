using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace MLFLegends.Combat
{
    /// <summary>Immutable, World-owned presentation versions, prepared only on config/catalog revision outside jobs.</summary>
    public sealed class CombatEffectResources : IComponentData, IDisposable
    {
        public const int MaxBindings = 640; // 64 complete five-slot publications; counted VFX-only degradation, no handle reuse.
        public sealed class Binding
        {
            public CharacterEffectSettings Settings;
            public SpriteAtlas Atlas;
            public SpriteAtlasClip Clip;
            public double[] FrameEnds;
            public SpriteAnimationTiming BodyTiming;
            public float2[] AnchorPixels, AnchorDirections;
            public bool[] AnchorVisible;
            public SpriteAnchorLayer[] AnchorLayers;
            public float BodyNominalPixels;
            public int AtlasIndex;
            public double DurationTicks;
            public uint FrameAt(double progress)
            {
                for (var i = 0; i < FrameEnds.Length - 1; i++) if (progress < FrameEnds[i]) return (uint)Clip.Frames[i];
                return (uint)Clip.Frames[FrameEnds.Length - 1];
            }
        }
        readonly List<Binding> m_Bindings = new List<Binding>();
        readonly List<SpriteAtlas> m_Atlases = new List<SpriteAtlas>();
        readonly List<SpriteAtlasCatalog> m_Catalogs = new List<SpriteAtlasCatalog>();
        readonly List<int> m_Versions = new List<int>();
        public IReadOnlyList<SpriteAtlas> Atlases => m_Atlases;
        SpriteAtlas m_Technical;
        Texture2D[] m_TechnicalPages;
        public ulong CapacityDropped { get; private set; }
        public int Count => m_Bindings.Count;
        public long NativeFrameCapacityBytes
        {
            get { long bytes = 0; foreach (var atlas in m_Atlases) bytes += atlas.FrameTableBytes + (long)atlas.FrameCount * sizeof(int); return bytes; }
        }
        public long GpuFrameCapacityBytes
        {
            get { long bytes = 0; foreach (var atlas in m_Atlases) if (atlas.GpuBuffersCreated > 0) bytes += atlas.FrameTableBytes; return bytes; }
        }
        public Binding Get(int handle) => handle > 0 && handle <= m_Bindings.Count ? m_Bindings[handle - 1] : null;

        public void Prepare(ref CombatStrikeSchedule table, CharacterCombatSettings combat, CombatAnimationSet animations,
            SpriteAtlasCatalog bodyCatalog, Vector2 imageOffset)
        {
            var enabled = 0;
            for (var k = CombatAttackKind.Hand1; k <= CombatAttackKind.PhysicalAbility; k++)
            {
                var slot = combat.Strike(k);
                if (slot.Swing != null && slot.Swing.Source != CombatEffectSource.Disabled) enabled++;
                if (slot.Impact != null && slot.Impact.Source != CombatEffectSource.Disabled) enabled++;
            }
            if (enabled == 0)
            {
                for (var k = CombatAttackKind.Hand1; k <= CombatAttackKind.PhysicalAbility; k++)
                { var strike = table.GetStrike(k); strike.SwingEffect = strike.ImpactEffect = 0; table.SetStrike(k, strike); }
                return;
            }
            if (Count + 10 > MaxBindings)
            {
                CapacityDropped += (ulong)enabled;
                for (var kind = CombatAttackKind.Hand1; kind <= CombatAttackKind.PhysicalAbility; kind++)
                { var strike = table.GetStrike(kind); strike.SwingEffect = strike.ImpactEffect = 0; table.SetStrike(kind, strike); }
                Debug.LogWarning("VFX version capacity reached (640 bindings): effects disabled for new executions; damage/focus/physics publication continues. Recreate World to clear versions.");
                return;
            }
            var atlasCount = m_Atlases.Count;
            var oldTechnical = m_Technical;
            var pending = new List<Binding>(10);
            SpriteAtlas body = null;
            try
            {
                for (var kind = CombatAttackKind.Hand1; kind <= CombatAttackKind.PhysicalAbility; kind++)
                {
                    var settings = combat.Strike(kind);
                    foreach (var effect in new[] { settings.Swing, settings.Impact })
                    {
                        if (effect == null || effect.Source == CombatEffectSource.Disabled) { pending.Add(null); continue; }
                        if (effect.Anchor != CombatEffectAnchor.Offset && body == null)
                        {
                            var errors = new List<string>(); body = SpriteAtlas.Load(bodyCatalog, errors, null);
                            if (body == null) throw new ArgumentException("VFX body anchors: " + string.Join("; ", errors));
                        }
                        pending.Add(Prepare(effect, animations.Attack(kind), body, imageOffset));
                    }
                }
                // Commit handles only after every binding succeeds. Body/phase/damage publication remains atomic.
                var first = Count;
                m_Bindings.AddRange(pending);
                for (var kind = CombatAttackKind.Hand1; kind <= CombatAttackKind.PhysicalAbility; kind++)
                {
                    var p = table.GetStrike(kind); var i = ((int)kind - 1) * 2;
                    p.SwingEffect = pending[i] == null ? 0 : first + i + 1;
                    p.ImpactEffect = pending[i + 1] == null ? 0 : first + i + 2;
                    table.SetStrike(kind, p);
                }
            }
            catch
            {
                for (var i = m_Atlases.Count - 1; i >= atlasCount; i--)
                { m_Atlases[i].Dispose(); m_Atlases.RemoveAt(i); m_Catalogs.RemoveAt(i); m_Versions.RemoveAt(i); }
                if (oldTechnical == null && m_TechnicalPages != null)
                {
                    m_Technical = null;
                    foreach (var page in m_TechnicalPages)
                        if (Application.isPlaying) UnityEngine.Object.Destroy(page); else UnityEngine.Object.DestroyImmediate(page);
                    m_TechnicalPages = null;
                }
                throw;
            }
            finally { body?.Dispose(); }
        }
        Binding Prepare(CharacterEffectSettings settings, SpriteAnimationTiming bodyTiming, SpriteAtlas body, Vector2 imageOffset)
        {
            var errors = new List<string>(); settings.Validate("VFX", errors);
            if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));
            SpriteAtlas atlas;
            var owns = settings.Source == CombatEffectSource.Catalog;
            if (owns)
            {
                atlas = null;
                for (var i = 0; i < m_Catalogs.Count; i++)
                    if (m_Catalogs[i] == settings.Catalog && m_Versions[i] == settings.Catalog.Version) atlas = m_Atlases[i];
                if (atlas == null)
                {
                    atlas = SpriteAtlas.Load(settings.Catalog, errors, null);
                    if (atlas != null) { m_Atlases.Add(atlas); m_Catalogs.Add(settings.Catalog); m_Versions.Add(settings.Catalog.Version); }
                }
            }
            else
            {
                if (m_Technical == null)
                {
                    m_TechnicalPages = TechnicalEffectFixture.CreatePages();
                    m_Technical = SpriteAtlas.Load(TechnicalEffectFixture.ManifestJson,
                        TechnicalEffectFixture.Bind(m_TechnicalPages), errors, null);
                    if (m_Technical != null) { m_Atlases.Add(m_Technical); m_Catalogs.Add(null); m_Versions.Add(0); }
                }
                atlas = m_Technical;
            }
            if (atlas == null) throw new ArgumentException("VFX atlas: " + string.Join("; ", errors));
            if (!atlas.Definition.TryGetClipIndex(settings.ClipId, out var clipIndex))
            { throw new ArgumentException("VFX clip missing: " + settings.ClipId); }
            var clip = atlas.Definition.Clips[clipIndex];
            var b = new Binding { Settings = settings.Copy(), Atlas = atlas, Clip = clip, AtlasIndex = m_Atlases.IndexOf(atlas),
                BodyTiming = bodyTiming, DurationTicks = settings.DurationSeconds * SimulationTiming.TickRateHz,
                FrameEnds = new double[clip.Frames.Count] };
            double sum = 0;
            for (var i = 0; i < clip.Frames.Count; i++) sum += clip.DurationsMs[i];
            double time = 0;
            for (var i = 0; i < clip.Frames.Count; i++) { time += clip.DurationsMs[i]; b.FrameEnds[i] = time / sum; }
            if (settings.Anchor != CombatEffectAnchor.Offset)
            {
                b.AnchorPixels = new float2[bodyTiming.FrameCount]; b.AnchorDirections = new float2[bodyTiming.FrameCount]; b.AnchorVisible = new bool[bodyTiming.FrameCount];
                b.AnchorLayers = new SpriteAnchorLayer[bodyTiming.FrameCount]; b.BodyNominalPixels = body.Definition.NominalHeightPx;
                for (var i = 0; i < bodyTiming.FrameCount; i++)
                {
                    var f = (int)bodyTiming.Frame(i); var frame = body.Definition.Frames[f];
                    var anchor = settings.Anchor == CombatEffectAnchor.HandLeft ? frame.HandLeft : settings.Anchor == CombatEffectAnchor.HandRight
                        ? frame.HandRight : frame.CastOrigin;
                    b.AnchorVisible[i] = anchor.Visible && (settings.Anchor != CombatEffectAnchor.CastOrigin || frame.HasCastOrigin);
                    b.AnchorLayers[i] = anchor.Layer;
                    b.AnchorDirections[i] = SpriteCardMath.CanvasDirectionToCard(anchor.Direction, FacingDirection.Right);
                    b.AnchorPixels[i] = SpriteCardMath.AlignedCanvasToCard(anchor.Position, body.ImagePivot(f),
                        new float2(imageOffset.x, -imageOffset.y), FacingDirection.Right);
                }
            }
            return b;
        }
        public void Dispose()
        {
            foreach (var atlas in m_Atlases) atlas.Dispose();
            m_Atlases.Clear(); m_Catalogs.Clear(); m_Versions.Clear(); m_Bindings.Clear(); m_Technical = null;
            if (m_TechnicalPages != null) foreach (var p in m_TechnicalPages)
                if (Application.isPlaying) UnityEngine.Object.Destroy(p); else UnityEngine.Object.DestroyImmediate(p);
            m_TechnicalPages = null;
        }
    }
}
