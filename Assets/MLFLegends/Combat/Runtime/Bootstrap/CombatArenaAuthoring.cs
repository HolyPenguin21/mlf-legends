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
        uint m_ReportedRevision;
        bool m_ReportedFailure;
        void OnEnable() => Bind();
        void Update()
        {
            if (m_World != World.DefaultGameObjectInjectionWorld || m_World == null || !m_World.IsCreated || m_Lease == 0) Bind();
            if (m_World == null || !m_World.IsCreated || m_Lease == 0) return;
            using var query = m_World.EntityManager.CreateEntityQuery(typeof(CombatArenaState), typeof(CombatArenaDiagnostics));
            if (query.CalculateEntityCount() != 1) return;
            var state = query.GetSingleton<CombatArenaState>();
            var diagnostics = query.GetSingleton<CombatArenaDiagnostics>();
            if (state.Lease == m_Lease && diagnostics.Revision == state.Revision && diagnostics.FailedBodies > 0
                && (!m_ReportedFailure || state.Revision != m_ReportedRevision))
            {
                Debug.LogError($"Arena movement frozen for {diagnostics.FailedBodies} bodies: {diagnostics.LastFailure}, revision {state.Revision}.", this);
                m_ReportedFailure = true; m_ReportedRevision = state.Revision;
            }
        }
        void Bind()
        {
            Release();
            m_World = World.DefaultGameObjectInjectionWorld;
            if (m_World == null || !m_World.IsCreated || m_World.GetExistingSystemManaged<CombatArenaSystem>() == null) return;
            m_Lease = CombatArenaSystem.Acquire(m_World.EntityManager);
            Rebuild();
        }
        [ContextMenu("Rebuild static arena")]
        public void Rebuild()
        {
            if (m_World == null || !m_World.IsCreated || m_Lease == 0) return;
            var shapes = new List<CombatArenaShape>();
            string error = null;
            foreach (var source in Sources ?? Array.Empty<CombatArenaSource>())
            {
                if (!TryBuild(source, out var shape, out error)) break;
                shapes.Add(shape);
            }
            var valid = error == null && shapes.Count > 0 && shapes.Count <= CombatArenaTuning.MaxShapes;
            var accepted = CombatArenaSystem.Stage(m_World.EntityManager, m_Lease, valid ? shapes.ToArray() : null);
            if (!accepted || !valid)
                Debug.LogError("Static arena rejected: " + (error ?? "missing source, invalid dimensions or lost lease") + ". Movement is frozen; no spawn-height floor fallback.", this);
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
            if (t.GetComponent<MeshCollider>() != null || t.GetComponent<Rigidbody>() != null)
            { error = "MeshCollider/Rigidbody unsupported"; return false; }
            var matrix = t.localToWorldMatrix;
            var x = (Vector3)matrix.GetColumn(0); var y = (Vector3)matrix.GetColumn(1); var z = (Vector3)matrix.GetColumn(2);
            var scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            if (!math.all(math.isfinite(new float3(scale.x, scale.y, scale.z))) || scale.x <= 0 || scale.y <= 0 || scale.z <= 0
                || Vector3.Dot(Vector3.Cross(x, y), z) <= 0 || Mathf.Abs(Vector3.Dot(x.normalized, z.normalized)) > .0001f
                || Mathf.Abs(y.normalized.y - 1) > .0001f || Mathf.Abs(x.y) > .0001f || Mathf.Abs(z.y) > .0001f)
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
        void OnDisable() { Release(); ClearRampVisuals(); m_World = null; }
        void Release()
        {
            if (m_Lease != 0 && m_World != null && m_World.IsCreated && m_World.GetExistingSystemManaged<CombatArenaSystem>() != null)
                CombatArenaSystem.Release(m_World.EntityManager, m_Lease);
            m_Lease = 0;
        }
    }
}
