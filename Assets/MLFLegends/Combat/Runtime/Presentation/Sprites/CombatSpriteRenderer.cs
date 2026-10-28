using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace MLFLegends.Combat
{
    /// <summary>
    /// The one scene-level GPU sprite renderer of the combat World and its Camera: every in-combat combatant of the active
    /// battle is one instance of one shared card mesh, drawn with <c>Graphics.RenderMeshPrimitives</c> once per atlas page.
    /// Reads <see cref="CombatantId"/>, <see cref="CombatPosition"/> and <see cref="CombatFacing"/>; writes nothing to ECS.
    /// </summary>
    /// <remarks>
    /// <para><b>Order:</b> <c>LateUpdate</c> with <c>[DefaultExecutionOrder(100)]</c>: after the frame's Update-phase
    /// simulation (all fixed ticks of the frame) and after <see cref="CombatCameraController"/> (order 0) placed the camera;
    /// URP renders the queued draws for that camera in the same frame. No own tick, clock or accumulator.</para>
    /// <para><b>Depth:</b> alpha-clipped, depth-writing cards; equal view depth is resolved by a deterministic push ordered by
    /// <see cref="CombatantId"/> (lower ID in front), independent of chunk/page order (<see cref="CombatSpriteExtraction"/>).</para>
    /// <para><b>Ownership:</b> creates and destroys its material, mesh, instance buffer, extraction, loaded
    /// <see cref="SpriteAtlas"/> (with its frame buffer) and generated technical pages; never destroys the catalog, imported
    /// textures or shader. Two enabled renderers on the same World and Camera: the later enabled one draws, the other logs
    /// once and stays idle while superseded (no double draw). Explicitly opt-in per scene; replay/benchmark Worlds have none.</para>
    /// </remarks>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(ExecutionOrder)]
    public sealed class CombatSpriteRenderer : MonoBehaviour
    {
        /// <summary>After <see cref="CombatCameraController"/> (default order 0) in the same LateUpdate.</summary>
        public const int ExecutionOrder = 100;

        public enum AtlasSource : byte
        {
            /// <summary>The assigned <see cref="SpriteAtlasCatalog"/> (art).</summary>
            Catalog = 0,

            /// <summary><see cref="TechnicalAtlasFixture"/> generated at load (no art needed).</summary>
            TechnicalFixture = 1,

            /// <summary>One character and its combat clips (CombatAnimationSet) from the shared config.</summary>
            CharacterConfig = 2,
        }

        /// <summary>Anchor of a frame, for <see cref="TryGetAnchor"/>.</summary>
        public enum AnchorKind : byte
        {
            HandLeft,
            HandRight,
            CastOrigin,
        }

        /// <summary>Counters of the last <see cref="Refresh"/> and of owned GPU resources.</summary>
        public struct RenderStats
        {
            public int Instances;

            /// <summary>RenderMeshPrimitives calls (= pages with instances); URP draws each in its forward and depth passes.</summary>
            public int Submissions;

            public int InstanceBufferCapacity;

            /// <summary>Instance buffers created since enable (capacity growth only, never per frame).</summary>
            public int InstanceBuffersCreated;

            public float MaxDepthOffset;

            /// <summary>CPU time of the last ECS extraction (depth order, page grouping), microseconds.</summary>
            public double ExtractMicroseconds;

            /// <summary>CPU time of the last GPU submission (buffer upload, property blocks, draw calls), microseconds.</summary>
            public double SubmitMicroseconds;
        }

        static readonly int s_MainTex = Shader.PropertyToID("_MainTex");
        static readonly int s_Frames = Shader.PropertyToID("_SpriteFrames");
        static readonly int s_Instances = Shader.PropertyToID("_SpriteInstances");
        static readonly int s_InstanceOffset = Shader.PropertyToID("_InstanceOffset");
        static readonly int s_CardRight = Shader.PropertyToID("_CardRight");
        static readonly int s_CardUp = Shader.PropertyToID("_CardUp");
        static readonly int s_CardGround = Shader.PropertyToID("_CardGround");
        static readonly int s_CardView = Shader.PropertyToID("_CardView");
        static readonly int s_PixelWorldSize = Shader.PropertyToID("_PixelWorldSize");
        static readonly int s_DepthBias = Shader.PropertyToID("_DepthBias");
        static readonly int s_AlphaCutoff = Shader.PropertyToID("_AlphaCutoff");
        static readonly int s_ImageOffsetCard = Shader.PropertyToID("_ImageOffsetCard");

        static readonly ProfilerMarker s_SubmitMarker = new ProfilerMarker("CombatSpriteRenderer.Submit");

        const int MinCapacity = 64;

        // Enabled renderers, only to arbitrate owners of the same World + Camera (presentation, not combat state).
        static readonly List<CombatSpriteRenderer> s_Enabled = new List<CombatSpriteRenderer>();
        static uint s_LastEnable;

        [Header("Atlas")]
        [SerializeField] AtlasSource m_Source = AtlasSource.Catalog;
        [SerializeField] SpriteAtlasCatalog m_Catalog;
        [SerializeField] CharacterAnimationConfig m_AnimationConfig;
        [SerializeField] string m_CharacterId = "mage_female_01";
        [Tooltip("Frame drawn for every combatant without an explicit selection. Empty: first frame of the first clip.")]
        [SerializeField] string m_DefaultFrameId = "";

        [Header("Rendering")]
        [SerializeField] Shader m_Shader;
        [Tooltip("Camera to draw in. Empty: the Camera on this GameObject.")]
        [SerializeField] Camera m_Camera;
        [Tooltip("World height of nominal_height_px (mage calibration 128 px = 2 units); one scale for the whole atlas.")]
        [SerializeField, Min(0.01f)] float m_NominalWorldHeight = 2f;
        [SerializeField, Range(0.01f, 1f)] float m_AlphaCutoff = 0.5f;

        [Header("Depth (render only)")]
        [Tooltip("Constant push of every card towards the camera, world units; keeps feet above a coplanar floor.")]
        [SerializeField, Range(0f, 0.25f)] float m_DepthBias = 0.02f;
        [Tooltip("Requested view-depth gap within overlapping cards; compressed to a fixed 1/16-world-unit total budget.")]
        [SerializeField, Range(0.0001f, 0.05f)] float m_DepthSeparation = 0.002f;

        [Header("Debug")]
        [SerializeField] bool m_DrawAnchorGizmos = true;

        uint m_Order;
        bool m_Live;
        bool m_SupersededLogged;
        bool m_ShaderErrorLogged;

        // Atlas
        SpriteAtlas m_Atlas;
        Texture2D[] m_FixturePages;
        AtlasSource m_LoadedSource;
        SpriteAtlasCatalog m_LoadedCatalog;
        int m_LoadedVersion;
        bool m_LoadAttempted;
        uint m_DefaultFrame;
        string m_ResolvedDefaultId;
        CharacterAnimationConfig m_ResolvedConfig;
        int m_ConfigVersion;
        string m_ResolvedCharacter;
        bool m_ConfigAttempted;
        bool m_ConfigValid;
        SpriteAtlasCatalog m_CharacterCatalog;
        readonly Dictionary<string, CharacterAnimationSettings> m_ClipSettings = new Dictionary<string, CharacterAnimationSettings>();
        bool m_TimingDirty;
        float2 m_ImageOffsetCard;
        CombatAnimationSet m_Animations;

        /// <summary>Shared timing of every combat clip of the selected character (null while rejected).</summary>
        public CombatAnimationSet Animations => m_Animations;

        /// <summary>Shared idle_combat timing (not the current per-combatant clip).</summary>
        public SpriteAnimationTiming AnimationTiming => m_Animations?.Idle;
        public SpriteAnimationTiming RunAnimationTiming => m_Animations?.Run;
        public int TimingBuilds { get; private set; }
        public bool UsesCharacterAnimation => m_Source == AtlasSource.CharacterConfig;

        // World
        World m_ExplicitWorld;
        bool m_UseExplicitWorld;
        CombatSpriteExtraction m_Extraction;
        SpriteCardBasis m_Basis;

        // GPU
        Material m_Material;
        Mesh m_Mesh;
        GraphicsBuffer m_InstanceBuffer;
        MaterialPropertyBlock[] m_PageBlocks = Array.Empty<MaterialPropertyBlock>();
        RenderStats m_Stats;

        public SpriteAtlas Atlas => m_Atlas;

        /// <summary>The World read on the last refresh (explicit or default), null without a live one.</summary>
        public World BoundWorld => m_Extraction?.BoundWorld;

        public Camera TargetCamera => m_Camera != null ? m_Camera : GetComponent<Camera>();

        public RenderStats Stats => m_Stats;

        public SpriteCardBasis Basis => m_Basis;

        /// <summary>World units per source pixel: nominal world height / nominal_height_px.</summary>
        public float PixelWorldSize => m_Atlas != null ? m_NominalWorldHeight / m_Atlas.Definition.NominalHeightPx : 0f;

        public float DepthBias => m_DepthBias;

        /// <summary>Frame drawn for combatants without an explicit selection.</summary>
        public uint DefaultFrame => m_DefaultFrame;

        /// <summary>Extraction of the last refresh (instances grouped by page); null while disabled.</summary>
        public CombatSpriteExtraction Extraction => m_Extraction;

        /// <summary>Owned GPU resources currently alive (material, mesh, instance buffer, atlas frame buffer); for leak checks.</summary>
        public int LiveGpuResources => (m_Material != null ? 1 : 0) + (m_Mesh != null ? 1 : 0) + (m_InstanceBuffer != null ? 1 : 0)
            + (m_Atlas != null && m_Atlas.GpuBuffersCreated > 0 ? 1 : 0);

        /// <summary>Whether this renderer draws: enabled and the latest enabled renderer of its World + Camera.</summary>
        public bool IsActiveOwner
        {
            get
            {
                if (!m_Live)
                    return false;
                var world = ResolveWorld();
                var camera = TargetCamera;
                foreach (var other in s_Enabled)
                    if (other != this && other.m_Order > m_Order && other.TargetCamera == camera && other.ResolveWorld() == world)
                        return false;
                return true;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Domain reload may be disabled: no renderer survives into the next Play session's list.
            s_Enabled.Clear();
            s_LastEnable = 0;
        }

        void OnEnable()
        {
            m_Order = ++s_LastEnable;
            m_Live = true;
            m_SupersededLogged = false;
            m_ShaderErrorLogged = false;
            s_Enabled.Add(this);
            m_Extraction = new CombatSpriteExtraction(MinCapacity);
            m_Stats = default;
            EnsureAtlas();
        }

        void OnDisable()
        {
            m_Live = false;
            s_Enabled.Remove(this);
            m_Extraction?.Dispose();
            m_Extraction = null;
            UnloadAtlas();
            ReleaseGpu();
            m_Stats = default;
        }

        void OnValidate()
        {
            // Default frame edits apply on the next refresh.
            m_ResolvedDefaultId = null;
        }

        void LateUpdate() => Refresh();

        void Update()
        {
            if (!m_Live)
                return;
            if (IsActiveOwner)
            {
                m_SupersededLogged = false; // logs again if superseded once more
                return;
            }

            if (m_SupersededLogged)
                return;
            m_SupersededLogged = true;
            Debug.LogWarning($"{nameof(CombatSpriteRenderer)} on '{name}' is superseded by a later enabled renderer of the same World and Camera; it draws nothing while that one is enabled.", this);
        }

        /// <summary>Reads ECS of <paramref name="world"/> instead of following the default World.</summary>
        public void BindWorld(World world)
        {
            m_UseExplicitWorld = true;
            m_ExplicitWorld = world;
        }

        /// <summary>Follows <c>World.DefaultGameObjectInjectionWorld</c> again (the default).</summary>
        public void BindDefaultWorld()
        {
            m_UseExplicitWorld = false;
            m_ExplicitWorld = null;
        }

        /// <summary>Sets the shader, camera and atlas source when created from code (tests/tools); reloads on the next refresh.</summary>
        public void Configure(Shader shader, Camera targetCamera, AtlasSource source, SpriteAtlasCatalog catalog = null)
        {
            m_Shader = shader;
            m_Camera = targetCamera;
            m_Source = source;
            m_Catalog = catalog;
        }

        /// <summary>Uses the combat clips of a shared character config; manual selections are disabled in this mode.</summary>
        public void ConfigureAnimation(Shader shader, Camera targetCamera, CharacterAnimationConfig config, string characterId)
        {
            m_Shader = shader;
            m_Camera = targetCamera;
            m_Source = AtlasSource.CharacterConfig;
            m_Catalog = null;
            m_AnimationConfig = config;
            m_CharacterId = characterId;
        }

        /// <summary>Selects frame <paramref name="frameId"/> for every combatant without its own selection.</summary>
        public bool SetDefaultFrame(string frameId)
        {
            if (UsesCharacterAnimation)
                return false;
            m_DefaultFrameId = frameId ?? "";
            m_ResolvedDefaultId = null;
            return m_Atlas == null || m_Atlas.Definition.TryGetFrameIndex(frameId, out _);
        }

        /// <summary>
        /// Preview/test seam: draws <paramref name="frameId"/> for one combatant until cleared, a new battle, another World or
        /// atlas. Not an animation system: no clock, no state binding. False (nothing changes) if the frame does not exist.
        /// </summary>
        public bool SetFrame(CombatantId combatant, string frameId)
        {
            if (UsesCharacterAnimation || m_Extraction == null || m_Atlas == null || !combatant.IsValid || !m_Atlas.Definition.TryGetFrameIndex(frameId, out var index))
                return false;
            m_Extraction.Select(combatant, (uint)index);
            return true;
        }

        /// <summary>Like <see cref="SetFrame"/> with local frame <paramref name="localIndex"/> of clip <paramref name="clipId"/>.</summary>
        public bool SetClipFrame(CombatantId combatant, string clipId, int localIndex)
        {
            if (UsesCharacterAnimation || m_Extraction == null || m_Atlas == null || !combatant.IsValid || !m_Atlas.Definition.TryGetClipFrame(clipId, localIndex, out var index))
                return false;
            m_Extraction.Select(combatant, (uint)index);
            return true;
        }

        public void ClearFrame(CombatantId combatant) => m_Extraction?.ClearSelection(combatant);

        /// <summary>The last refreshed instance of <paramref name="combatant"/>.</summary>
        public bool TryGetInstance(CombatantId combatant, out SpriteInstanceGpu instance)
        {
            instance = default;
            if (m_Extraction == null)
                return false;
            var ids = m_Extraction.InstanceIds;
            for (var i = 0; i < ids.Length; i++)
            {
                if (ids[i] != combatant.Value)
                    continue;
                instance = m_Extraction.Instances[i];
                return true;
            }

            return false;
        }

        /// <summary>
        /// World position/direction of an anchor of <paramref name="combatant"/>'s current frame, through the same card
        /// transform as the body (flip, scale, depth push). False if not drawn or (cast origin) absent in that frame.
        /// </summary>
        public bool TryGetAnchor(CombatantId combatant, AnchorKind kind, out float3 position, out float3 direction, out SpriteAnchor anchor)
        {
            position = direction = default;
            anchor = default;
            if (m_Atlas == null || !TryGetInstance(combatant, out var instance))
                return false;
            var frame = m_Atlas.Definition.Frames[(int)instance.Frame];
            switch (kind)
            {
                case AnchorKind.HandLeft:
                    anchor = frame.HandLeft;
                    break;
                case AnchorKind.HandRight:
                    anchor = frame.HandRight;
                    break;
                default:
                    if (!frame.HasCastOrigin)
                        return false;
                    anchor = frame.CastOrigin;
                    break;
            }

            var facing = instance.Facing < 0f ? FacingDirection.Left : FacingDirection.Right;
            var definition = m_Atlas.Definition;
            var card = SpriteCardMath.AlignedCanvasToCard(anchor.Position, definition.WorldSupport,
                definition.LegacyPivotOffsetCard + m_ImageOffsetCard, facing);
            position = SpriteCardMath.CardToWorld(m_Basis, instance.Position, card, PixelWorldSize, instance.DepthOffset + m_DepthBias);
            direction = SpriteCardMath.CanvasDirectionToWorld(m_Basis, anchor.Direction, facing);
            return true;
        }

        /// <summary>
        /// One presentation update (the LateUpdate body): reload the atlas if its source changed, extract the instances from
        /// ECS and, with a graphics device and as the active owner, queue this frame's draws for <see cref="TargetCamera"/>.
        /// </summary>
        public void Refresh()
        {
            if (!m_Live)
                return;
            EnsureAtlas();
            var camera = TargetCamera;
            if (camera != null)
                m_Basis = SpriteCardBasis.FromCamera(camera.transform.forward, camera.transform.up);
            m_Stats.Submissions = 0;
            m_Stats.SubmitMicroseconds = 0;
            var start = Stopwatch.GetTimestamp();
            // A superseded renderer neither reads ECS nor draws (its World binding and selections are kept).
            if (IsActiveOwner)
                m_Extraction.Run(camera != null ? ResolveWorld() : null,
                    UsesCharacterAnimation && m_Animations == null ? null : m_Atlas, m_Basis, m_DepthSeparation,
                    m_DefaultFrame, PixelWorldSize, UsesCharacterAnimation ? m_Animations : null, m_ImageOffsetCard);
            else
                m_Extraction.Clear();
            m_Stats.ExtractMicroseconds = Microseconds(start);
            m_Stats.Instances = m_Extraction.Count;
            m_Stats.MaxDepthOffset = m_Extraction.MaxDepthOffset;
            if (m_Extraction.Count == 0)
                return;
            start = Stopwatch.GetTimestamp();
            Submit(camera);
            m_Stats.SubmitMicroseconds = Microseconds(start);
        }

        static double Microseconds(long start) => (Stopwatch.GetTimestamp() - start) * 1e6 / Stopwatch.Frequency;

        World ResolveWorld()
        {
            var world = m_UseExplicitWorld ? m_ExplicitWorld : World.DefaultGameObjectInjectionWorld;
            return world != null && world.IsCreated ? world : null;
        }

        // ---------------------------------------------------------------- atlas

        void EnsureAtlas()
        {
            if (!UsesCharacterAnimation) m_ImageOffsetCard = float2.zero;
            var catalog = m_Catalog;
            if (UsesCharacterAnimation)
            {
                if (!ResolveCharacter())
                {
                    if (m_Atlas != null) UnloadAtlas();
                    return;
                }
                catalog = m_CharacterCatalog;
            }
            var version = catalog != null ? catalog.Version : 0;
            if (m_LoadAttempted && m_LoadedSource == m_Source && m_LoadedCatalog == catalog && m_LoadedVersion == version)
            {
                if (UsesCharacterAnimation) PrepareAnimation();
                else ResolveDefaultFrame();
                return;
            }

            UnloadAtlas();
            m_LoadAttempted = true;
            m_LoadedSource = m_Source;
            m_LoadedCatalog = catalog;
            m_LoadedVersion = version;

            var errors = new List<string>();
            var warnings = new List<string>();
            if (m_Source == AtlasSource.TechnicalFixture)
            {
                m_FixturePages = TechnicalAtlasFixture.CreatePages();
                m_Atlas = SpriteAtlas.Load(TechnicalAtlasFixture.ManifestJson, TechnicalAtlasFixture.Bind(m_FixturePages), errors, warnings);
            }
            else if (catalog != null)
                m_Atlas = SpriteAtlas.Load(catalog, errors, warnings);
            else
                errors.Add("no catalog assigned");

            foreach (var warning in warnings)
                Debug.LogWarning($"{nameof(CombatSpriteRenderer)} on '{name}': {warning}", this);
            if (m_Atlas == null)
            {
                DestroyFixturePages();
                // Once per source change (not per frame); nothing is drawn and the simulation is unaffected.
                Debug.LogError($"{nameof(CombatSpriteRenderer)} on '{name}': atlas rejected, nothing is drawn.\n  " + string.Join("\n  ", errors), this);
                return;
            }

            if (UsesCharacterAnimation) PrepareAnimation();
            else ResolveDefaultFrame();
        }

        bool ResolveCharacter()
        {
            var version = m_AnimationConfig != null ? m_AnimationConfig.Version : 0;
            if (m_ConfigAttempted && m_ResolvedConfig == m_AnimationConfig && m_ConfigVersion == version && m_ResolvedCharacter == m_CharacterId)
                return m_ConfigValid;
            m_ConfigAttempted = true;
            m_ResolvedConfig = m_AnimationConfig;
            m_ConfigVersion = version;
            m_ResolvedCharacter = m_CharacterId;
            m_Animations = null;
            m_TimingDirty = true;
            m_ConfigValid = false;
            m_CharacterCatalog = null;
            m_ClipSettings.Clear();
            var imageOffset = m_AnimationConfig != null ? m_AnimationConfig.ImageOffsetFor(m_CharacterId) : Vector2.zero;
            m_ImageOffsetCard = new float2(imageOffset.x, -imageOffset.y);
            var errors = new List<string>();
            if (!math.all(math.isfinite(m_ImageOffsetCard)))
                errors.Add("ImageOffsetPixels must be finite");
            if (m_AnimationConfig == null)
                errors.Add("no CharacterAnimationConfig assigned");
            else
            {
                // Every required clip is resolved (and every failure reported) before anything is accepted.
                foreach (var clipId in CombatAnimationSet.ClipIds)
                {
                    if (m_AnimationConfig.TryResolve(m_CharacterId, clipId, out var catalog, out var settings, out var error))
                    {
                        m_CharacterCatalog = catalog;
                        m_ClipSettings[clipId] = settings;
                    }
                    else
                        errors.Add(error);
                }
            }
            m_ConfigValid = errors.Count == 0;
            if (!m_ConfigValid)
            {
                m_CharacterCatalog = null;
                m_ClipSettings.Clear();
                Debug.LogError($"{nameof(CombatSpriteRenderer)} on '{name}': animation rejected, nothing is drawn.\n  " + string.Join("\n  ", errors), this);
            }
            return m_ConfigValid;
        }

        void PrepareAnimation()
        {
            if (!m_TimingDirty || m_Atlas == null)
                return;
            m_TimingDirty = false;
            m_Animations = null;
            m_Extraction?.ClearSelections();
            var errors = new List<string>();
            var warnings = new List<string>();
            var definition = m_Atlas.Definition;
            if (definition.CharacterId != m_CharacterId)
                errors.Add($"characters.{m_CharacterId}: catalog character_id is {definition.CharacterId}");
            if (m_CharacterId == "mage_female_01" && (definition.SourceSize.x != 192 || definition.SourceSize.y != 192 ||
                definition.NominalHeightPx != 128))
                errors.Add($"characters.{m_CharacterId}: expected source_size [192,192], nominal_height_px 128 (pivot is artistic alignment)");
            if (errors.Count == 0)
            {
                TimingBuilds++;
                // Atomic set: an invalid clip cannot retain an old table or silently substitute another clip.
                m_Animations = CombatAnimationSet.Create(definition,
                    clipId => m_ClipSettings.TryGetValue(clipId, out var settings) ? settings : null,
                    $"characters.{m_CharacterId}.animations", errors, warnings);
            }
            foreach (var warning in warnings)
                Debug.LogWarning($"{nameof(CombatSpriteRenderer)} on '{name}': {warning}", this);
            if (m_Animations == null)
                Debug.LogError($"{nameof(CombatSpriteRenderer)} on '{name}': animation rejected, nothing is drawn.\n  " + string.Join("\n  ", errors), this);
        }

        void ResolveDefaultFrame()
        {
            if (m_Atlas == null || m_ResolvedDefaultId == m_DefaultFrameId)
                return;
            m_ResolvedDefaultId = m_DefaultFrameId;
            var definition = m_Atlas.Definition;
            if (!string.IsNullOrEmpty(m_DefaultFrameId) && definition.TryGetFrameIndex(m_DefaultFrameId, out var index))
            {
                m_DefaultFrame = (uint)index;
                return;
            }

            if (!string.IsNullOrEmpty(m_DefaultFrameId))
                Debug.LogWarning($"{nameof(CombatSpriteRenderer)} on '{name}': default frame \"{m_DefaultFrameId}\" does not exist; using the first clip's first frame.", this);
            m_DefaultFrame = definition.Clips.Count > 0 ? (uint)definition.Clips[0].Frames[0] : 0u;
        }

        void UnloadAtlas()
        {
            // A later character may use fewer pages: do not retain the old catalog's texture/buffer bindings.
            foreach (var block in m_PageBlocks) block?.Clear();
            m_Atlas?.Dispose();
            m_Atlas = null;
            m_Animations = null;
            m_TimingDirty = true;
            DestroyFixturePages();
            m_LoadAttempted = false;
            m_ResolvedDefaultId = null;
            m_DefaultFrame = 0;
            m_Extraction?.ClearSelections(); // frame indices belong to the unloaded atlas
        }

        void DestroyFixturePages()
        {
            if (m_FixturePages == null)
                return;
            foreach (var page in m_FixturePages)
                DestroyOwned(page);
            m_FixturePages = null;
        }

        // ---------------------------------------------------------------- GPU submission

        void Submit(Camera camera)
        {
            using var scope = s_SubmitMarker.Auto();
            // -nographics / Null device: extraction still runs (tests), nothing is created or drawn.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !EnsureGpu())
                return;

            var count = m_Extraction.Count;
            if (m_InstanceBuffer == null || m_InstanceBuffer.count < count)
            {
                var capacity = math.max(MinCapacity, math.ceilpow2(count));
                m_InstanceBuffer?.Release();
                m_InstanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, CombatSpriteExtraction.InstanceStride);
                m_Stats.InstanceBuffersCreated++;
                m_Stats.InstanceBufferCapacity = capacity;
            }

            m_InstanceBuffer.SetData(m_Extraction.UploadArray, 0, 0, count);
            var frames = m_Atlas.GetFrameBuffer();
            var s = PixelWorldSize;
            var extents = CardExtents(s);
            m_Material.SetFloat(s_AlphaCutoff, m_AlphaCutoff);

            for (var p = 0; p < m_Extraction.PageCount; p++)
            {
                var pageInstances = m_Extraction.PageInstances(p);
                if (pageInstances == 0)
                    continue;
                var block = m_PageBlocks[p];
                block.SetTexture(s_MainTex, m_Atlas.GetPage(p));
                block.SetBuffer(s_Frames, frames);
                block.SetBuffer(s_Instances, m_InstanceBuffer);
                block.SetInteger(s_InstanceOffset, m_Extraction.PageStart(p));
                block.SetVector(s_CardRight, ToVector(m_Basis.Right));
                block.SetVector(s_CardUp, ToVector(m_Basis.Up));
                block.SetVector(s_CardGround, ToVector(m_Basis.Ground));
                block.SetVector(s_CardView, ToVector(m_Basis.ViewDirection));
                block.SetFloat(s_PixelWorldSize, s);
                block.SetFloat(s_DepthBias, m_DepthBias);
                block.SetVector(s_ImageOffsetCard, new Vector4(m_ImageOffsetCard.x, m_ImageOffsetCard.y, 0f, 0f));

                var (supportMin, supportMax) = m_Extraction.PageSupportBounds(p);
                var min = supportMin + extents.min;
                var max = supportMax + extents.max;
                var renderParams = new RenderParams(m_Material)
                {
                    camera = camera,
                    matProps = block,
                    worldBounds = new Bounds((Vector3)((min + max) * 0.5f), (Vector3)(max - min)),
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = false,
                    layer = gameObject.layer,
                };
                Graphics.RenderMeshPrimitives(renderParams, m_Mesh, 0, pageInstances);
                m_Stats.Submissions++;
            }
        }

        static Vector4 ToVector(float3 value) => new Vector4(value.x, value.y, value.z, 0f);

        /// <summary>
        /// Conservative offset box of any card around its support point: widest frame both ways (flip), highest top, deepest
        /// part below the pivot and the depth push range. Jumps are covered because positions already include Y.
        /// </summary>
        (float3 min, float3 max) CardExtents(float s)
        {
            var min = float3.zero;
            var max = float3.zero;
            void Add(float3 axis, float from, float to)
            {
                var a = axis * from;
                var b = axis * to;
                min += math.min(a, b);
                max += math.max(a, b);
            }

            var rect = m_Atlas.CardBoundsPx + new float4(m_ImageOffsetCard, m_ImageOffsetCard);
            var halfWidth = math.max(math.abs(rect.x), math.abs(rect.z));
            Add(m_Basis.Right, -halfWidth * s, halfWidth * s);
            Add(m_Basis.Up, 0f, math.max(0f, rect.w) * s);
            Add(m_Basis.Ground, 0f, math.max(0f, -rect.y) * s);
            Add(-m_Basis.ViewDirection, 0f, m_Extraction.MaxDepthOffset + m_DepthBias);
            return (min, max);
        }

        bool EnsureGpu()
        {
            if (m_Material == null)
            {
                if (m_Shader == null || !m_Shader.isSupported)
                {
                    if (!m_ShaderErrorLogged)
                        Debug.LogError($"{nameof(CombatSpriteRenderer)} on '{name}': shader missing or unsupported; nothing is drawn.", this);
                    m_ShaderErrorLogged = true;
                    return false;
                }

                m_Material = new Material(m_Shader) { name = "Combat Sprite (runtime)", hideFlags = HideFlags.DontSave };
            }

            if (m_Mesh == null)
                m_Mesh = CreateCardMesh();
            if (m_PageBlocks.Length < m_Atlas.PageCount)
            {
                var blocks = new MaterialPropertyBlock[m_Atlas.PageCount];
                for (var i = 0; i < blocks.Length; i++)
                    blocks[i] = i < m_PageBlocks.Length ? m_PageBlocks[i] : new MaterialPropertyBlock();
                m_PageBlocks = blocks;
            }

            return true;
        }

        /// <summary>Two quads (above/below the pivot line) of one card; positions only select corners for the vertex shader.</summary>
        static Mesh CreateCardMesh()
        {
            var mesh = new Mesh { name = "Combat Sprite Card", hideFlags = HideFlags.DontSave };
            mesh.SetVertices(new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(1f, 1f, 0f),
                new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 1f), new Vector3(0f, 1f, 1f), new Vector3(1f, 1f, 1f),
            });
            mesh.SetTriangles(new[] { 0, 2, 1, 1, 2, 3, 4, 6, 5, 5, 6, 7 }, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e4f); // unused: RenderParams.worldBounds culls
            mesh.UploadMeshData(markNoLongerReadable: true);
            return mesh;
        }

        void ReleaseGpu()
        {
            m_InstanceBuffer?.Release();
            m_InstanceBuffer = null;
            DestroyOwned(m_Material);
            m_Material = null;
            DestroyOwned(m_Mesh);
            m_Mesh = null;
            foreach (var block in m_PageBlocks)
                block?.Clear();
            m_PageBlocks = Array.Empty<MaterialPropertyBlock>();
        }

        static void DestroyOwned(UnityEngine.Object owned)
        {
            if (owned == null)
                return;
            if (Application.isPlaying)
                Destroy(owned);
            else
                DestroyImmediate(owned);
        }

        void OnDrawGizmos()
        {
            if (!m_DrawAnchorGizmos || m_Atlas == null || m_Extraction == null)
                return;
            var ids = m_Extraction.InstanceIds;
            for (var i = 0; i < ids.Length; i++)
            {
                var id = new CombatantId { Value = ids[i] };
                var instance = m_Extraction.Instances[i];
                var definition = m_Atlas.Definition;
                // Cyan is the fixed virtual canvas; amber is the source image after legacy pivot + artistic offset.
                DrawCanvas(instance, definition.WorldSupport, float2.zero, Color.cyan);
                DrawCanvas(instance, definition.WorldSupport, definition.LegacyPivotOffsetCard + m_ImageOffsetCard, new Color(1f, 0.7f, 0.2f));
                Gizmos.color = Color.white;
                Gizmos.DrawWireSphere(instance.Position, 0.05f);
                DrawAnchor(id, AnchorKind.HandLeft, new Color(0.3f, 0.6f, 1f));
                DrawAnchor(id, AnchorKind.HandRight, new Color(1f, 0.6f, 0.2f));
                DrawAnchor(id, AnchorKind.CastOrigin, Color.magenta);
            }
        }

        void DrawCanvas(in SpriteInstanceGpu instance, float2 pivot, float2 offset, Color color)
        {
            var size = m_Atlas.Definition.SourceSize;
            var left = -pivot.x + offset.x;
            var right = size.x - pivot.x + offset.x;
            var bottom = pivot.y - size.y + offset.y;
            var top = pivot.y + offset.y;
            var split = math.clamp(0f, bottom, top);
            // The side edges bend at world support height, just like the two shader quads.
            var p0 = CanvasGizmoPoint(instance, left, bottom);
            var p1 = CanvasGizmoPoint(instance, right, bottom);
            var p2 = CanvasGizmoPoint(instance, right, split);
            var p3 = CanvasGizmoPoint(instance, right, top);
            var p4 = CanvasGizmoPoint(instance, left, top);
            var p5 = CanvasGizmoPoint(instance, left, split);
            Gizmos.color = color;
            Gizmos.DrawLine(p0, p1);
            Gizmos.DrawLine(p1, p2);
            Gizmos.DrawLine(p2, p3);
            Gizmos.DrawLine(p3, p4);
            Gizmos.DrawLine(p4, p5);
            Gizmos.DrawLine(p5, p0);
        }

        Vector3 CanvasGizmoPoint(in SpriteInstanceGpu instance, float x, float y) =>
            SpriteCardMath.CardToWorld(m_Basis, instance.Position, new float2(x * instance.Facing, y), PixelWorldSize,
                instance.DepthOffset + m_DepthBias);

        void DrawAnchor(CombatantId id, AnchorKind kind, Color color)
        {
            if (!TryGetAnchor(id, kind, out var position, out var direction, out var anchor))
                return;
            // Front anchors bright, back anchors dimmed; hidden ones as wire spheres.
            Gizmos.color = anchor.Layer == SpriteAnchorLayer.Front ? color : color * 0.6f;
            if (anchor.Visible)
                Gizmos.DrawSphere(position, 0.04f);
            else
                Gizmos.DrawWireSphere(position, 0.04f);
            Gizmos.DrawLine(position, position + direction * 0.25f);
        }
    }
}
