using System.Collections.Generic;
using Terrain.Biomes;
using Terrain.Data;
using Terrain.Systems;
using UnityEngine;

namespace Terrain.DebugViews
{
    // One mesh per region — each quad samples its owning region's HeightMap for all four corners,
    // giving a hard cliff at every region boundary. Bresenham edge overlays are drawn on top of the
    // terrain surface using the same pairToCorners data as the quad (RegionDebugView) edges.
    public class HeightmapDebugView : MonoBehaviour
    {
        public bool regenerateOnStart = true;

        [Header("Rendering")]
        public Material sharedMaterial;

        [Header("Edges")]
        public bool showEdges = true;
        public Color edgeColor = new Color(1f, 0.55f, 0.05f, 1f);
        [Min(1)] public int edgeWidthPx = 2;
        public float edgeHeight = 0f;

        private Transform _meshesRoot;
        private Transform _edgesRoot;
        private Material _fallbackMat;
        private Material _edgeMat;

        private void Start()
        {
            if (TerrainDataSource.Instance != null && TerrainDataSource.Instance.regenerateOnStart)
                Regenerate();
        }

        [ContextMenu("Regenerate")]
        public void Regenerate()
        {
            if (TerrainDataSource.Instance == null)
            {
                Debug.LogError("[HeightmapDebugView] No TerrainDataSource found in scene.", this);
                return;
            }
            TerrainDataSource.Instance.Regenerate();
            Rebuild();
        }

        public void Rebuild()
        {
            if (TerrainDataSource.Instance?.Data == null) return;
            var data     = TerrainDataSource.Instance.Data;
            var result   = data.BuildResult;
            float invPpu = 1f / TerrainDataSource.Instance.config.pixelsPerUnit;
            int W = result.Width, H = result.Height;


            // ── Region terrain meshes ─────────────────────────────────────────────

            var regionQuads = new Dictionary<int, List<Vector2Int>>();
            for (int wy = 0; wy < H - 1; wy++)
            for (int wx = 0; wx < W - 1; wx++)
            {
                int r00 = result.PixelOwners[ wy      * W + wx    ];
                int r10 = result.PixelOwners[ wy      * W + wx + 1];
                int r01 = result.PixelOwners[(wy + 1) * W + wx    ];
                int r11 = result.PixelOwners[(wy + 1) * W + wx + 1];

                int dominant = DominantRegion(r00, r10, r01, r11);
                if (dominant < 0) continue;

                if (!regionQuads.TryGetValue(dominant, out var list))
                    regionQuads[dominant] = list = new List<Vector2Int>();
                list.Add(new Vector2Int(wx, wy));
            }

            EnsureMeshesRoot();
            ClearMeshes();

            foreach (var kv in regionQuads)
            {
                int regionId = kv.Key;
                var region   = result.Graph.Get(regionId);
                if (region.Biome == null || region.HeightMap == null) continue;

                var quads  = kv.Value;
                var bounds = region.BoundsPx;

                var verts = new Vector3[quads.Count * 6];
                var tris  = new int[quads.Count * 6];

                for (int i = 0; i < quads.Count; i++)
                {
                    int qx = quads[i].x, qy = quads[i].y;

                    float h00 = SampleHeight(region, bounds, qx,     qy    );
                    float h10 = SampleHeight(region, bounds, qx + 1, qy    );
                    float h01 = SampleHeight(region, bounds, qx,     qy + 1);
                    float h11 = SampleHeight(region, bounds, qx + 1, qy + 1);

                    var v00 = new Vector3( qx      * invPpu, h00,  qy      * invPpu);
                    var v10 = new Vector3((qx + 1) * invPpu, h10,  qy      * invPpu);
                    var v01 = new Vector3( qx      * invPpu, h01, (qy + 1) * invPpu);
                    var v11 = new Vector3((qx + 1) * invPpu, h11, (qy + 1) * invPpu);

                    int b = i * 6;
                    verts[b]     = v00; tris[b]     = b;
                    verts[b + 1] = v01; tris[b + 1] = b + 1;
                    verts[b + 2] = v10; tris[b + 2] = b + 2;
                    verts[b + 3] = v10; tris[b + 3] = b + 3;
                    verts[b + 4] = v01; tris[b + 4] = b + 4;
                    verts[b + 5] = v11; tris[b + 5] = b + 5;
                }

                var mesh = new Mesh { hideFlags = HideFlags.DontSave };
                mesh.indexFormat = verts.Length > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16;
                mesh.SetVertices(verts);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                var go = new GameObject($"HeightRegion_{regionId}_{region.Biome.displayName}");
                go.transform.SetParent(_meshesRoot, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = ResolveMaterial(region.Biome.debugColor);
            }

            // ── Edge overlays ─────────────────────────────────────────────────────

            EnsureEdgesRoot();
            ClearEdgeMeshes();
            if (showEdges)
                BuildEdgeMeshes(result, data.PairToCorners, invPpu, W, H);

            Debug.Log($"[HeightmapDebugView] seed={TerrainDataSource.Instance.config.seed}  regions={regionQuads.Count}  pixels={W}x{H}", this);
        }

        // ── Edge mesh building ────────────────────────────────────────────────────

        private void BuildEdgeMeshes(RegionBuilder.Result result,
            Dictionary<long, List<Vector2Int>> pairToCorners,
            float invPpu, int W, int H)
        {
            float halfW  = edgeWidthPx * invPpu * 0.5f;
            var   mat    = GetOrCreateEdgeMat();

            foreach (var kv in pairToCorners)
            {
                int a = (int)(kv.Key >> 32);
                int b = (int)(kv.Key & 0xFFFFFFFFL);

                var biomeA = result.Graph.Get(a).Biome;
                var biomeB = result.Graph.Get(b).Biome;
                if (biomeA != null && biomeB != null && biomeA.type == biomeB.type) continue;

                var endpoints = kv.Value;
                if (endpoints.Count < 2) continue;
                var p1 = TerrainDataSource.FindFurthest(endpoints, endpoints[0]);
                var p2 = TerrainDataSource.FindFurthest(endpoints, p1);
                if (p1 == p2) continue;

                var line = TerrainDataSource.Supercover(p1, p2);
                if (line.Count < 2) continue;

                var verts   = new Vector3[line.Count * 4];
                var indices = new int[line.Count * 12];

                for (int i = 0; i < line.Count; i++)
                {
                    int gx = line[i].x, gy = line[i].y;
                    float cx = gx * invPpu, cz = gy * invPpu;

                    int v = i * 4, t = i * 12;
                    verts[v + 0] = new Vector3(cx - halfW, edgeHeight, cz - halfW);
                    verts[v + 1] = new Vector3(cx + halfW, edgeHeight, cz - halfW);
                    verts[v + 2] = new Vector3(cx - halfW, edgeHeight, cz + halfW);
                    verts[v + 3] = new Vector3(cx + halfW, edgeHeight, cz + halfW);

                    // two-sided so it's visible from all camera angles
                    indices[t]      = v;     indices[t + 1]  = v + 1; indices[t + 2]  = v + 2;
                    indices[t + 3]  = v + 2; indices[t + 4]  = v + 1; indices[t + 5]  = v + 3;
                    indices[t + 6]  = v;     indices[t + 7]  = v + 2; indices[t + 8]  = v + 1;
                    indices[t + 9]  = v + 2; indices[t + 10] = v + 3; indices[t + 11] = v + 1;
                }

                var mesh = new Mesh { hideFlags = HideFlags.DontSave };
                mesh.indexFormat = verts.Length > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16;
                mesh.SetVertices(verts);
                mesh.SetIndices(indices, MeshTopology.Triangles, 0);
                mesh.RecalculateBounds();

                var go = new GameObject($"Edge_{a}_{b}");
                go.transform.SetParent(_edgesRoot, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static float SampleHeight(Region region, RectInt bounds, int wx, int wy)
        {
            int lx = wx - bounds.xMin;
            int ly = wy - bounds.yMin;
            if (lx < 0 || lx >= bounds.width || ly < 0 || ly >= bounds.height) return 0f;
            return region.HeightMap[lx, ly];
        }

        private static int DominantRegion(int r00, int r10, int r01, int r11)
        {
            int best = -1, bestCount = 0;
            int[] c = { r00, r10, r01, r11 };
            for (int i = 0; i < 4; i++)
            {
                if (c[i] < 0) continue;
                int count = 0;
                for (int j = 0; j < 4; j++) if (c[j] == c[i]) count++;
                if (count > bestCount) { bestCount = count; best = c[i]; }
            }
            return best;
        }

        // ── Material helpers ──────────────────────────────────────────────────────

        private Material GetOrCreateEdgeMat()
        {
            if (_edgeMat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                _edgeMat = new Material(shader)
                {
                    hideFlags   = HideFlags.DontSave,
                    renderQueue = 4000,
                };
                if (_edgeMat.HasProperty("_ZTest"))
                    _edgeMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                if (_edgeMat.HasProperty("_ZWrite"))
                    _edgeMat.SetInt("_ZWrite", 0);
                if (_edgeMat.HasProperty("_Cull"))
                    _edgeMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            }
            if (_edgeMat.HasProperty("_BaseColor")) _edgeMat.SetColor("_BaseColor", edgeColor);
            else                                    _edgeMat.color = edgeColor;
            return _edgeMat;
        }

        private Material ResolveMaterial(Color tint)
        {
            if (sharedMaterial != null)
            {
                var inst = new Material(sharedMaterial) { hideFlags = HideFlags.DontSave };
                ApplyColor(inst, tint);
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
            ApplyColor(mat, tint);
            return mat;
        }

        private static void ApplyColor(Material mat, Color c)
        {
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            else mat.color = c;
        }

        // ── Scene object management ───────────────────────────────────────────────

        private void EnsureMeshesRoot()
        {
            if (_meshesRoot != null) return;
            string rootName = $"HeightMeshes ({gameObject.name})";
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

        private void EnsureEdgesRoot()
        {
            if (_edgesRoot != null) return;
            string rootName = $"HeightEdges ({gameObject.name})";
            var found = transform.Find(rootName);
            if (found != null) { _edgesRoot = found; return; }
            var go = new GameObject(rootName);
            go.transform.SetParent(transform, false);
            _edgesRoot = go.transform;
        }

        private void ClearEdgeMeshes()
        {
            for (int i = _edgesRoot.childCount - 1; i >= 0; i--)
            {
                var child = _edgesRoot.GetChild(i);
                var mf = child.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) SafeDestroy(mf.sharedMesh);
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
