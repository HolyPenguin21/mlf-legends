using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace MLFLegends.Combat
{
    [Serializable]
    public sealed class CombatArenaSource
    {
        public CombatArenaKind Kind;
        public Transform Transform;
        public Vector3 Center;
        public Vector3 Size = Vector3.one;
    }

    // One scene adapter. Transforms/colliders are read only on setup or explicit Rebuild, never per fighter/tick.
    [DisallowMultipleComponent]
    public sealed class CombatArenaAuthoring : MonoBehaviour
    {
        public CombatArenaSource[] Sources = Array.Empty<CombatArenaSource>();
        public bool DrawGeometry;
        public Material RampMaterial;
        readonly List<GameObject> m_RampVisuals = new List<GameObject>();
        readonly List<Mesh> m_RampMeshes = new List<Mesh>();
        World m_World;
        ulong m_Lease;
        EntityQuery m_Diagnostics;
        bool m_HasDiagnostics;
        CombatArenaSystem m_ArenaOwner;
        bool m_AttemptedBind, m_CanAutoRebind = true;
        uint m_ReportedRevision;
        bool m_ReportedFailure;
        void OnEnable()
        {
            if (!m_AttemptedBind) Bind(true); // Bootstrap may already have ensured this activation before our callback.
        }
        void Update()
        {
            var current = World.DefaultGameObjectInjectionWorld;
            var owner = current != null && current.IsCreated ? current.GetExistingSystemManaged<CombatArenaSystem>() : null;
            if (!m_AttemptedBind || m_World != current || m_ArenaOwner != owner) Bind(false);
            if (m_World == null || !m_World.IsCreated || m_Lease == 0) return;
            if (!m_HasDiagnostics || m_Diagnostics.CalculateEntityCount() != 1) return;
            var state = m_Diagnostics.GetSingleton<CombatArenaState>();
            var diagnostics = m_Diagnostics.GetSingleton<CombatArenaDiagnostics>();
            if (state.Lease != m_Lease) m_CanAutoRebind = false;
            // While our staged geometry is not yet published, Revision/diagnostics still describe the previous source (e.g.
            // the interval after another lease released this World): they are not failures of this lease (#69).
            if (state.Lease == m_Lease && !state.Pending && diagnostics.Revision == state.Revision && diagnostics.FailedBodies > 0
                && (!m_ReportedFailure || state.Revision != m_ReportedRevision))
            {
                Debug.LogError($"Arena movement frozen for {diagnostics.FailedBodies} bodies: {diagnostics.LastFailure}, revision {state.Revision}.", this);
                m_ReportedFailure = true; m_ReportedRevision = state.Revision;
            }
        }
        void Bind(bool explicitClaim)
        {
            if (!explicitClaim && m_Lease != 0 && m_World != null && m_World.IsCreated && m_HasDiagnostics
                && m_World.GetExistingSystemManaged<CombatArenaSystem>() == m_ArenaOwner)
                m_CanAutoRebind &= m_Diagnostics.CalculateEntityCount() == 1
                    && m_Diagnostics.GetSingleton<CombatArenaState>().Lease == m_Lease;
            Release();
            ClearRampVisuals();
            m_AttemptedBind = true;
            if (explicitClaim) m_CanAutoRebind = true;
            m_World = World.DefaultGameObjectInjectionWorld;
            m_ArenaOwner = m_World != null && m_World.IsCreated ? m_World.GetExistingSystemManaged<CombatArenaSystem>() : null;
            if (m_ArenaOwner == null || !m_CanAutoRebind) return;
            m_Diagnostics = m_World.EntityManager.CreateEntityQuery(typeof(CombatArenaState), typeof(CombatArenaDiagnostics));
            m_HasDiagnostics = true;
            if (!explicitClaim && m_Diagnostics.GetSingleton<CombatArenaState>().Lease != 0)
            {
                // Automatic World/system rebinding never steals a scene that has already claimed the new owner.
                m_CanAutoRebind = false;
                m_Diagnostics.Dispose(); m_HasDiagnostics = false;
                return;
            }
            m_Lease = m_ArenaOwner.Acquire();
            Rebuild();
        }
        public void EnsureForBattle(World world)
        {
            if (world == null || !world.IsCreated || world != World.DefaultGameObjectInjectionWorld || !isActiveAndEnabled) return;
            if (m_World != world || m_ArenaOwner != world.GetExistingSystemManaged<CombatArenaSystem>() || !m_HasDiagnostics
                || m_Diagnostics.CalculateEntityCount() != 1 || m_Diagnostics.GetSingleton<CombatArenaState>().Lease != m_Lease)
                Bind(true); // An explicit owned Start/Restart may take scene ownership; a stale Update may not.
        }
        [ContextMenu("Rebuild static arena")]
        public void Rebuild()
        {
            if (m_World == null || !m_World.IsCreated || m_Lease == 0) return;
            if (m_ArenaOwner != m_World.GetExistingSystemManaged<CombatArenaSystem>() || !m_HasDiagnostics
                || m_Diagnostics.CalculateEntityCount() != 1 || m_Diagnostics.GetSingleton<CombatArenaState>().Lease != m_Lease)
            {
                ClearRampVisuals();
                Debug.LogWarning("Stale arena rebuild ignored; the current owner's geometry is unchanged.", this);
                return;
            }
            var shapes = new List<CombatArenaShape>();
            string error = null;
            foreach (var source in Sources ?? Array.Empty<CombatArenaSource>())
            {
                if (!TryBuild(source, out var shape, out error)) break;
                shapes.Add(shape);
            }
            var valid = error == null && shapes.Count > 0 && shapes.Count <= CombatArenaTuning.MaxShapes;
            var accepted = m_ArenaOwner.Stage(m_Lease, valid ? shapes.ToArray() : null);
            if (!accepted || !valid)
                Debug.LogError("Static arena rejected: " + (error ?? "missing source, invalid dimensions or shape limit") + ". Required geometry will be invalid at the next boundary; no spawn-height fallback.", this);
            m_ReportedFailure = false;
            ClearRampVisuals();
            if (valid && accepted && RampMaterial != null)
                foreach (var source in Sources)
                    if (source.Kind == CombatArenaKind.Ramp) CreateRampVisual(source);
        }
        // One shared static scene mesh per ramp at rebuild, never one object/resource per fighter.
        void CreateRampVisual(CombatArenaSource source)
        {
            var mesh = new Mesh { name = "Combat arena ramp" };
            var h = source.Size * .5f; var c = source.Center;
            mesh.vertices = new[] { c + new Vector3(-h.x,-h.y,-h.z), c + new Vector3(-h.x,-h.y,h.z),
                c + new Vector3(h.x,-h.y,-h.z), c + new Vector3(h.x,-h.y,h.z),
                c + new Vector3(h.x,h.y,-h.z), c + new Vector3(h.x,h.y,h.z) };
            mesh.triangles = new[] { 0,3,1,0,2,3, 0,5,4,0,1,5, 2,5,3,2,4,5, 0,4,2, 1,3,5 };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            var visual = new GameObject("Static ramp visual");
            visual.transform.SetParent(source.Transform, false);
            visual.AddComponent<MeshFilter>().sharedMesh = mesh;
            visual.AddComponent<MeshRenderer>().sharedMaterial = RampMaterial;
            m_RampVisuals.Add(visual); m_RampMeshes.Add(mesh);
        }
        void ClearRampVisuals()
        {
            foreach (var visual in m_RampVisuals) if (visual != null) { visual.SetActive(false); Destroy(visual); }
            foreach (var mesh in m_RampMeshes) if (mesh != null) Destroy(mesh);
            m_RampVisuals.Clear(); m_RampMeshes.Clear();
        }
        public static bool TryBuild(CombatArenaSource source, out CombatArenaShape shape, out string error)
        {
            shape = default; error = null;
            if (source == null || source.Transform == null) { error = "missing Transform"; return false; }
            var t = source.Transform;
            if (!t.gameObject.activeInHierarchy || t.GetComponentInParent<Rigidbody>() != null)
            { error = "inactive source or Rigidbody hierarchy unsupported"; return false; }
            var colliders = t.GetComponents<Collider>();
            if (colliders.Length > 1 || (colliders.Length == 1 && !(colliders[0] is BoxCollider)))
            { error = "only a single solid BoxCollider or explicit collider-free source is supported"; return false; }
            var matrix = t.localToWorldMatrix;
            var x = (Vector3)matrix.GetColumn(0); var y = (Vector3)matrix.GetColumn(1); var z = (Vector3)matrix.GetColumn(2);
            var scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            if (!math.all(math.isfinite(new float3(scale.x, scale.y, scale.z))) || scale.x <= 0 || scale.y <= 0 || scale.z <= 0
                || Vector3.Dot(Vector3.Cross(x, y), z) <= 0 || Mathf.Abs(Vector3.Dot(x.normalized, z.normalized)) > .0001f
                || (y.normalized - Vector3.up).sqrMagnitude > 1e-10f || Mathf.Abs(x.normalized.y) > .00001f || Mathf.Abs(z.normalized.y) > .00001f)
            { error = "require positive non-sheared scale and yaw-only world rotation"; return false; }
            var center = source.Center; var size = source.Size;
            var collider = t.GetComponent<BoxCollider>();
            if (collider != null)
            {
                if (collider.isTrigger || !collider.enabled || source.Kind != CombatArenaKind.Box)
                { error = "only enabled solid BoxCollider for Box authoring"; return false; }
                center = collider.center; size = collider.size;
            }
            var world = t.TransformPoint(center);
            size = Vector3.Scale(size, scale);
            shape = new CombatArenaShape { Kind = source.Kind, Center = new float2(world.x, world.z),
                HalfSize = new float2(size.x, size.z) * .5f, Bottom = world.y - size.y * .5f, Top = world.y + size.y * .5f,
                AxisX = new float2(x.x, x.z) / scale.x };
            if (!shape.IsValid) { error = "non-finite/too small geometry or unsupported kind"; return false; }
            return true;
        }
        void OnDrawGizmos()
        {
            if (!DrawGeometry) return;
            Gizmos.color = Color.cyan;
            foreach (var source in Sources ?? Array.Empty<CombatArenaSource>())
            {
                if (!TryBuild(source, out var shape, out _)) continue;
                var vertices = new Vector3[8];
                for (var i = 0; i < 8; i++)
                {
                    var local = new float2((i & 1) == 0 ? -shape.HalfSize.x : shape.HalfSize.x,
                        (i & 2) == 0 ? -shape.HalfSize.y : shape.HalfSize.y);
                    var xz = shape.Center + shape.WorldVector(local);
                    var top = shape.Kind == CombatArenaKind.Ramp && (i & 1) == 0 ? shape.Bottom : shape.Top;
                    vertices[i] = new Vector3(xz.x, (i & 4) == 0 ? shape.Bottom : top, xz.y);
                }
                for (var i = 0; i < 8; i++)
                    foreach (var bit in new[] { 1, 2, 4 }) if ((i & bit) == 0) Gizmos.DrawLine(vertices[i], vertices[i | bit]);
            }
        }
        void OnDisable() { Release(); ClearRampVisuals(); m_World = null; m_AttemptedBind = false; }
        void Release()
        {
            if (m_Lease != 0 && m_World != null && m_World.IsCreated && m_World.GetExistingSystemManaged<CombatArenaSystem>() == m_ArenaOwner)
                m_ArenaOwner.Release(m_Lease);
            if (m_HasDiagnostics && m_World != null && m_World.IsCreated) m_Diagnostics.Dispose();
            m_HasDiagnostics = false;
            m_ArenaOwner = null;
            m_Lease = 0;
        }
    }
}
