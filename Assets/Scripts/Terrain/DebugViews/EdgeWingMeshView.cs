using System.Collections.Generic;
using Terrain.Data;
using Terrain.Systems;
using UnityEngine;
using UnityEngine.Rendering;

namespace Terrain.DebugViews
{
    // Builds one strip mesh per region pair from the three Bresenham edges produced
    // by EdgeWingBuilder. The strip stacks rows as [SideB, Base, SideA] and triangulates
    // between consecutive rows. Heights come from the per-pair Collection — pixels
    // skipped during dedup (a depth-1 pixel that landed on the base) reuse the base
    // entry's height naturally because the dictionary lookup falls through to it.
    public class EdgeWingMeshView : MonoBehaviour
    {
        public bool regenerateOnStart = true;

        [Header("Rendering")]
        public Material sharedMaterial;
        public float worldYOffset = 0.05f;
        public Color tint = new Color(0.85f, 0.35f, 0.95f, 1f);

        public List<EdgeWingPair> LastBuild { get; private set; }

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
            if (data == null) return;

            LastBuild = EdgeWingBuilder.Build(data.BuildResult, data.PairToCorners);
            RebuildMeshes();

            int totalPixels = 0;
            for (int i = 0; i < LastBuild.Count; i++) totalPixels += LastBuild[i].Collection.Count;
            Debug.Log($"[EdgeWingMeshView] pairs={LastBuild.Count}  uniquePixels={totalPixels}", this);
        }

        private void RebuildMeshes()
        {
            if (TerrainDataSource.Instance?.Data == null || LastBuild == null) return;
            float invPpu = 1f / TerrainDataSource.Instance.config.pixelsPerUnit;

            EnsureMeshesRoot();
            ClearMeshes();

            for (int t = 0; t < LastBuild.Count; t++)
            {
                var pair = LastBuild[t];
                var mesh = BuildStripMesh(pair, invPpu);
                if (mesh == null) continue;

                var go = new GameObject($"EdgeWing_{pair.RegionA}_{pair.RegionB}");
                go.transform.SetParent(_meshesRoot, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = ResolveMaterial();
            }
        }

        private Mesh BuildStripMesh(EdgeWingPair pair, float invPpu)
        {
            // Three parallel rows; same Bresenham dx/dy means same length, but clamp
            // index defensively in case of degenerate input.
            var rows = new[] { pair.SideBLine, pair.BaseLine, pair.SideALine };
            int M = pair.BaseLine.Count;
            if (M < 2) return null;

            var verts = new Vector3[3 * M];
            for (int row = 0; row < 3; row++)
            {
                var line = rows[row];
                int len = line.Count;
                for (int i = 0; i < M; i++)
                {
                    var p = line[Mathf.Min(i, len - 1)];
                    float h = pair.Collection.TryGetValue(p, out var entry) ? entry.Height : 0f;
                    verts[row * M + i] = new Vector3(p.x * invPpu, h + worldYOffset, p.y * invPpu);
                }
            }

            // Wind CW from above so the strip's normal is +Y. With perp defined as
            // (-edgeDir.y, edgeDir.x) the naive winding lands at -Y (back-facing from
            // the camera looking down) — swap the 2nd/3rd index of each triangle.
            var tris = new int[2 * (M - 1) * 6];
            int t = 0;
            for (int row = 0; row < 2; row++)
            {
                int rA = row * M, rB = (row + 1) * M;
                for (int i = 0; i < M - 1; i++)
                {
                    tris[t++] = rA + i;
                    tris[t++] = rB + i;
                    tris[t++] = rA + i + 1;
                    tris[t++] = rB + i;
                    tris[t++] = rB + i + 1;
                    tris[t++] = rA + i + 1;
                }
            }

            var mesh = new Mesh { hideFlags = HideFlags.DontSave };
            mesh.indexFormat = verts.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
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
