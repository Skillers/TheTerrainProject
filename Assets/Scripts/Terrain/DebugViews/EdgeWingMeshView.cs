using System.Collections.Generic;
using Terrain.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace Terrain.DebugViews
{
    // Builds one mesh per region pair on the same integer corner grid the heightmap
    // mesh uses. For each integer cell (wx, wy) inside the wing's bounding box, emits
    // an axis-aligned quad iff all four corners (wx, wy), (wx+1, wy), (wx, wy+1),
    // (wx+1, wy+1) live in the pair's Collection. Each quad gets 6 unshared vertices
    // so RecalculateNormals produces flat per-cell shading — same as HeightmapDebugView.
    public class EdgeWingMeshView : MonoBehaviour
    {
        public bool regenerateOnStart = true;

        [Header("Rendering")]
        public Material sharedMaterial;
        public float worldYOffset = 0.05f;
        public Color tint = new Color(0.85f, 0.35f, 0.95f, 1f);

        private Transform _meshesRoot;
        private Material _fallbackMat;

        private void Start()
        {
            if (regenerateOnStart && TerrainDataSource.Instance != null) Regenerate();
        }

        [ContextMenu("Regenerate")]
        public void Regenerate()
        {
            if (TerrainDataSource.Instance == null)
            {
                Debug.LogError("[EdgeWingMeshView] No TerrainDataSource found in scene.", this);
                return;
            }
            TerrainDataSource.Instance.Regenerate();
            Rebuild();
        }

        public void Rebuild()
        {
            var data = TerrainDataSource.Instance?.Data;
            if (data?.WingPairs == null) return;
            float invPpu = 1f / TerrainDataSource.Instance.config.pixelsPerUnit;

            EnsureMeshesRoot();
            ClearMeshes();

            int totalPixels = 0;
            for (int t = 0; t < data.WingPairs.Count; t++)
            {
                var pair = data.WingPairs[t];
                totalPixels += pair.Collection?.Count ?? 0;
                var mesh = BuildGridQuadMesh(pair, invPpu);
                if (mesh == null) continue;

                var go = new GameObject($"EdgeWing_{pair.RegionA}_{pair.RegionB}");
                go.transform.SetParent(_meshesRoot, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = ResolveMaterial();
            }
            Debug.Log($"[EdgeWingMeshView] pairs={data.WingPairs.Count}  uniquePixels={totalPixels}", this);
        }

        private Mesh BuildGridQuadMesh(EdgeWingPair pair, float invPpu)
        {
            var coll = pair.Collection;
            if (coll == null || coll.Count == 0) return null;

            int xMin = int.MaxValue, yMin = int.MaxValue;
            int xMax = int.MinValue, yMax = int.MinValue;
            foreach (var p in coll.Keys)
            {
                if (p.x < xMin) xMin = p.x;
                if (p.y < yMin) yMin = p.y;
                if (p.x > xMax) xMax = p.x;
                if (p.y > yMax) yMax = p.y;
            }
            if (xMax <= xMin || yMax <= yMin) return null;

            var verts = new List<Vector3>();
            var tris  = new List<int>();

            for (int wy = yMin; wy < yMax; wy++)
            for (int wx = xMin; wx < xMax; wx++)
            {
                var c00 = new Vector2Int(wx,     wy    );
                var c10 = new Vector2Int(wx + 1, wy    );
                var c01 = new Vector2Int(wx,     wy + 1);
                var c11 = new Vector2Int(wx + 1, wy + 1);
                if (!coll.TryGetValue(c00, out var w00)) continue;
                if (!coll.TryGetValue(c10, out var w10)) continue;
                if (!coll.TryGetValue(c01, out var w01)) continue;
                if (!coll.TryGetValue(c11, out var w11)) continue;

                var v00 = new Vector3( wx      * invPpu, w00.Height + worldYOffset,  wy      * invPpu);
                var v10 = new Vector3((wx + 1) * invPpu, w10.Height + worldYOffset,  wy      * invPpu);
                var v01 = new Vector3( wx      * invPpu, w01.Height + worldYOffset, (wy + 1) * invPpu);
                var v11 = new Vector3((wx + 1) * invPpu, w11.Height + worldYOffset, (wy + 1) * invPpu);

                int b = verts.Count;
                verts.Add(v00); tris.Add(b);
                verts.Add(v01); tris.Add(b + 1);
                verts.Add(v10); tris.Add(b + 2);
                verts.Add(v10); tris.Add(b + 3);
                verts.Add(v01); tris.Add(b + 4);
                verts.Add(v11); tris.Add(b + 5);
            }

            if (tris.Count == 0) return null;

            var mesh = new Mesh { hideFlags = HideFlags.DontSave };
            mesh.indexFormat = verts.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private Material ResolveMaterial()
        {
            if (sharedMaterial != null)
            {
                var inst = new Material(sharedMaterial) { hideFlags = HideFlags.DontSave };
                ApplyColor(inst);
                return inst;
            }
            if (_fallbackMat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Standard");
                _fallbackMat = new Material(shader) { hideFlags = HideFlags.DontSave };
            }
            var mat = new Material(_fallbackMat) { hideFlags = HideFlags.DontSave };
            ApplyColor(mat);
            return mat;
        }

        private void ApplyColor(Material mat)
        {
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
            else mat.color = tint;
        }

        private void EnsureMeshesRoot()
        {
            if (_meshesRoot != null) return;
            string rootName = $"EdgeWingMeshes ({gameObject.name})";
            var found = transform.Find(rootName);
            if (found != null) { _meshesRoot = found; return; }
            var go = new GameObject(rootName);
            go.transform.SetParent(transform, false);
            _meshesRoot = go.transform;
        }

        private void ClearMeshes()
        {
            for (int i = _meshesRoot.childCount - 1; i >= 0; i--)
            {
                var child = _meshesRoot.GetChild(i);
                var mf = child.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) SafeDestroy(mf.sharedMesh);
                var mr = child.GetComponent<MeshRenderer>();
                if (mr != null && mr.sharedMaterial != null && mr.sharedMaterial != sharedMaterial)
                    SafeDestroy(mr.sharedMaterial);
                SafeDestroy(child.gameObject);
            }
        }

        private static void SafeDestroy(Object obj)
        {
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }
    }
}
