using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Presentation controller of the scene's existing camera: orthographic, one fixed configurable angle, following the
    /// <see cref="CombatPosition"/> of <see cref="BattleSession.HumanCombatant"/> in the default ECS world, with pan and zoom.
    /// </summary>
    /// <remarks>
    /// Runs in <c>LateUpdate</c>, after the frame's ECS simulation (Update phase) and outside render callbacks. Writes only
    /// its own Transform and Camera; reads the session and one combatant, never writes ECS, never decides what is
    /// simulated (no visibility, distance or frustum filter). Pan/zoom use unscaled frame time, so they work while
    /// <c>Time.timeScale</c> is 0 without advancing the battle. A new battle number or human target drops the pan offset
    /// and the old binding; zoom is kept within its limits. Without a target (idle, despawned, not in combat, no World)
    /// it keeps the last focus. Angle, offset, distance, size limits and speeds are provisional Inspector calibration.
    /// The actions asset is only read (its Camera map is cloned and owned by this component while enabled).
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class CombatCameraController : MonoBehaviour
    {
        public const string MapName = "Camera";

        static readonly string[] s_Actions = { "Pan", "Zoom", "ZoomStep", "Recenter" };

        [Tooltip("Asset with a Camera map (Pan, Zoom, ZoomStep, Recenter). Bindings are provisional and remappable.")]
        [SerializeField] InputActionAsset m_Actions;

        [Header("Fixed view (degrees)")]
        [SerializeField, Range(5f, 90f)] float m_Pitch = 40f;
        [SerializeField, Range(-180f, 180f)] float m_Yaw;

        [Header("Follow")]
        [Tooltip("World offset added to the followed position (the look-at point).")]
        [SerializeField] Vector3 m_FollowOffset = new Vector3(0f, 1f, 0f);
        [Tooltip("Share of the target's altitude (Y) the camera follows: 0 keeps jumps visible on screen, 1 follows them.")]
        [SerializeField, Range(0f, 1f)] float m_AltitudeFollow;
        [Tooltip("Distance from the look-at point back along the view direction; affects clipping only (orthographic).")]
        [SerializeField, Min(0.5f)] float m_Distance = 30f;

        [Header("Zoom (orthographic size, world units)")]
        [SerializeField, Min(0.01f)] float m_OrthographicSize = 6f;
        [SerializeField, Min(0.01f)] float m_MinOrthographicSize = 2f;
        [SerializeField, Min(0.01f)] float m_MaxOrthographicSize = 20f;
        [Tooltip("Continuous zoom (keys, d-pad): size factor e^rate per second of full input.")]
        [SerializeField, Min(0f)] float m_ZoomRate = 1.5f;
        [Tooltip("Stepped zoom (mouse wheel): size factor per wheel notch.")]
        [SerializeField, Min(1f)] float m_ZoomStepFactor = 1.15f;

        [Header("Pan (relative to follow, ground plane)")]
        [Tooltip("Pan speed in orthographic sizes per second of full input, so the screen speed is independent of zoom.")]
        [SerializeField, Min(0f)] float m_PanSpeed = 1f;
        [SerializeField, Min(0f)] float m_MaxPan = 20f;

        Camera m_Camera;
        InputActionMap m_Map;
        InputAction m_Pan;
        InputAction m_Zoom;
        InputAction m_ZoomStep;
        InputAction m_Recenter;

        World m_World;
        EntityQuery m_Sessions;
        EntityQuery m_Combatants;
        Entity m_Target;
        uint m_BoundBattle;
        CombatantId m_BoundHuman;

        Vector3 m_Focus;
        Vector3 m_PanOffset;
        float m_Size;
        bool m_Initialized;

        /// <summary>Camera input of one frame, in action units (sticks/keys −1…1, wheel notches).</summary>
        public struct FrameInput
        {
            public Vector2 Pan;
            public float Zoom;
            public float ZoomSteps;
            public bool Recenter;
        }

        /// <summary>Last followed point (ECS position, altitude weighted), kept while there is no target.</summary>
        public Vector3 Focus => m_Focus;

        public Vector3 PanOffset => m_PanOffset;

        public float OrthographicSize => m_Size;

        /// <summary>Whether the last refresh found the human combatant in combat.</summary>
        public bool HasTarget { get; private set; }

        /// <summary>The fixed view rotation; independent of follow, pan and zoom.</summary>
        public Quaternion ViewRotation => Quaternion.Euler(m_Pitch, m_Yaw, 0f);

        public float MinOrthographicSize => Mathf.Max(0.01f, m_MinOrthographicSize);

        public float MaxOrthographicSize => Mathf.Max(MinOrthographicSize, m_MaxOrthographicSize);

        void OnEnable()
        {
            Initialize();
            if (m_Actions == null)
                return; // Follow still works; pan/zoom need the Camera map.

            // Validated on the shared map first, so a misconfigured asset leaves no clone behind.
            var shared = m_Actions.FindActionMap(MapName, throwIfNotFound: true)
                ?? throw new System.ArgumentException($"No '{MapName}' action map in '{m_Actions.name}'.");
            foreach (var name in s_Actions)
                shared.FindAction(name, throwIfNotFound: true);
            m_Map = shared.Clone();
            m_Pan = m_Map.FindAction("Pan", throwIfNotFound: true);
            m_Zoom = m_Map.FindAction("Zoom", throwIfNotFound: true);
            m_ZoomStep = m_Map.FindAction("ZoomStep", throwIfNotFound: true);
            m_Recenter = m_Map.FindAction("Recenter", throwIfNotFound: true);
            m_Map.Enable();
        }

        void OnDisable()
        {
            if (m_Map != null)
            {
                m_Map.Disable();
                m_Map.Dispose();
                m_Map = null;
                m_Pan = m_Zoom = m_ZoomStep = m_Recenter = null;
            }

            ReleaseWorld();
        }

        void LateUpdate()
        {
            var input = default(FrameInput);
            if (m_Map != null)
            {
                input.Pan = m_Pan.ReadValue<Vector2>();
                input.Zoom = m_Zoom.ReadValue<float>();
                input.ZoomSteps = m_ZoomStep.ReadValue<float>();
                input.Recenter = m_Recenter.WasPressedThisFrame();
            }

            Refresh(World.DefaultGameObjectInjectionWorld, input, Time.unscaledDeltaTime);
        }

        void OnValidate()
        {
            m_MinOrthographicSize = Mathf.Max(0.01f, m_MinOrthographicSize);
            m_MaxOrthographicSize = Mathf.Max(m_MinOrthographicSize, m_MaxOrthographicSize);
            m_OrthographicSize = Mathf.Clamp(m_OrthographicSize, m_MinOrthographicSize, m_MaxOrthographicSize);
        }

        /// <summary>
        /// One presentation update: reads the human combatant's ECS position from <paramref name="world"/> (may be null or
        /// disposed), applies pan/zoom for <paramref name="unscaledDeltaTime"/> seconds and writes this camera. Read-only
        /// towards ECS. Called by <c>LateUpdate</c>; public for tests and tools.
        /// </summary>
        public void Refresh(World world, in FrameInput input, float unscaledDeltaTime)
        {
            Initialize();
            var dt = float.IsFinite(unscaledDeltaTime) && unscaledDeltaTime > 0f ? unscaledDeltaTime : 0f;

            HasTarget = TryReadTarget(world, out var target);
            if (HasTarget)
                m_Focus = new Vector3(target.x, target.y * m_AltitudeFollow, target.z);

            if (input.Recenter)
                m_PanOffset = Vector3.zero;
            var pan = Finite(input.Pan);
            if (pan != Vector2.zero && dt > 0f)
            {
                var yaw = Quaternion.Euler(0f, m_Yaw, 0f);
                var move = yaw * new Vector3(Mathf.Clamp(pan.x, -1f, 1f), 0f, Mathf.Clamp(pan.y, -1f, 1f));
                m_PanOffset = Vector3.ClampMagnitude(m_PanOffset + move * (m_PanSpeed * m_Size * dt), m_MaxPan);
            }

            var zoom = Finite(Mathf.Clamp(input.Zoom, -1f, 1f)) * m_ZoomRate * dt;
            var steps = Finite(input.ZoomSteps);
            // Positive input zooms in (smaller size).
            m_Size = Mathf.Clamp(m_Size * Mathf.Exp(-zoom) * Mathf.Pow(m_ZoomStepFactor, -steps),
                MinOrthographicSize, MaxOrthographicSize);

            var rotation = ViewRotation;
            var lookAt = m_Focus + m_FollowOffset + m_PanOffset;
            transform.SetPositionAndRotation(lookAt - rotation * Vector3.forward * m_Distance, rotation);
            m_Camera.orthographic = true;
            m_Camera.orthographicSize = m_Size;
        }

        void Initialize()
        {
            if (m_Initialized)
                return;
            m_Initialized = true;
            m_Camera = GetComponent<Camera>();
            m_Size = Mathf.Clamp(m_OrthographicSize, MinOrthographicSize, MaxOrthographicSize);
        }

        bool TryReadTarget(World world, out Unity.Mathematics.float3 position)
        {
            position = default;
            if (world == null || !world.IsCreated)
            {
                ReleaseWorld();
                return false;
            }

            if (world != m_World)
            {
                ReleaseWorld();
                m_World = world;
                // Persistent until disposed in ReleaseWorld (or by the World itself); built once per World, not per frame.
                m_Sessions = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<BattleSession>());
                m_Combatants = world.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<CombatantId>(), ComponentType.ReadOnly<CombatPosition>(), ComponentType.ReadOnly<InCombat>());
            }

            if (m_Sessions.CalculateEntityCount() != 1)
                return false;
            var session = m_Sessions.GetSingleton<BattleSession>();
            var human = session.IsActive ? session.HumanCombatant : CombatantId.Invalid;
            if (session.BattleNumber != m_BoundBattle || !human.Equals(m_BoundHuman))
            {
                // New battle or target: transient pan and the old entity go; zoom stays.
                m_BoundBattle = session.BattleNumber;
                m_BoundHuman = human;
                m_Target = Entity.Null;
                m_PanOffset = Vector3.zero;
            }

            if (!human.IsValid)
                return false;

            var manager = world.EntityManager;
            if (!IsTarget(manager, m_Target, human))
            {
                m_Target = Entity.Null;
                using var entities = m_Combatants.ToEntityArray(Allocator.Temp);
                using var ids = m_Combatants.ToComponentDataArray<CombatantId>(Allocator.Temp);
                for (var i = 0; i < ids.Length; i++)
                {
                    if (ids[i].Value == human.Value)
                    {
                        m_Target = entities[i];
                        break;
                    }
                }
            }

            if (m_Target == Entity.Null)
                return false;
            position = manager.GetComponentData<CombatPosition>(m_Target).Value;
            return true;
        }

        /// <summary>The cached entity is still the live, in-combat combatant <paramref name="human"/> (handles are recycled).</summary>
        static bool IsTarget(EntityManager manager, Entity entity, CombatantId human) =>
            entity != Entity.Null && manager.Exists(entity) && manager.HasComponent<CombatantId>(entity)
            && manager.GetComponentData<CombatantId>(entity).Value == human.Value
            && manager.HasComponent<CombatPosition>(entity) && manager.HasComponent<InCombat>(entity)
            && manager.IsComponentEnabled<InCombat>(entity);

        void ReleaseWorld()
        {
            // Queries of a disposed World were released with it.
            if (m_World != null && m_World.IsCreated)
            {
                m_Sessions.Dispose();
                m_Combatants.Dispose();
            }

            m_World = null;
            m_Sessions = default;
            m_Combatants = default;
            m_Target = Entity.Null;
            // Battle numbers and IDs are World-scoped: the next World always rebinds (and drops the pan).
            m_BoundBattle = 0;
            m_BoundHuman = CombatantId.Invalid;
        }

        static float Finite(float value) => float.IsFinite(value) ? value : 0f;

        static Vector2 Finite(Vector2 value) => float.IsFinite(value.x) && float.IsFinite(value.y) ? value : Vector2.zero;
    }
}
