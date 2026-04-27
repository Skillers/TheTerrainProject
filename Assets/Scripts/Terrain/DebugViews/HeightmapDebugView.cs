using System.Collections.Generic;
using Terrain.Algorithms;
using Terrain.Biomes;
using Terrain.Data;
using Terrain.Systems;
using UnityEngine;

namespace Terrain.DebugViews
{
    // One mesh per region — each quad samples its owning region's HeightMap for all four corners,
    // giving a hard cliff at every region boundary. Edge overlays are continuous ribbon meshes whose
    // height at each point is the average of both adjacent regions' padded HeightMap samples.
    public class HeightmapDebugView : MonoBehaviour
    {
        public bool regenerateOnStart = true;

        [Header("Rendering")]
        public Material sharedMaterial;

        [Header("Edges")]
        public bool showEdges = true;
        public Color edgeColor = new Color(1f, 0.55f, 0.05f, 1f);
        [Range(0f, 1f)] public float bandFractionOfBiome = 0.15f;

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
                BuildEdgeMeshes(result, data.PairToCorners, invPpu);

            Debug.Log($"[HeightmapDebugView] seed={TerrainDataSource.Instance.config.seed}  regions={regionQuads.Count}  pixels={W}x{H}", this);
        }

        // ── Edge ribbon building ──────────────────────────────────────────────────

        private void BuildEdgeMeshes(RegionBuilder.Result result,
            Dictionary<long, List<Vector2Int>> pairToCorners, float invPpu)
        {
            int biomeSize = TerrainDataSource.Instance.config.biomeSize;
            int N = Mathf.Max(0, Mathf.RoundToInt(biomeSize * bandFractionOfBiome));
            if (N == 0) return;
            int crossCount = 2 * N + 1; // -N..-1, 0, +1..+N
            var mat = GetOrCreateEdgeMat();

            foreach (var kv in pairToCorners)
            {
                int aId = (int)(kv.Key >> 32);
                int bId = (int)(kv.Key & 0xFFFFFFFFL);

                var regionA = result.Graph.Get(aId);
                var regionB = result.Graph.Get(bId);
                if (regionA.Biome != null && regionB.Biome != null &&
                    regionA.Biome.type == regionB.Biome.type) continue;

                var endpoints = kv.Value;
                if (endpoints.Count < 2) continue;
                var p1 = LineRaster.FindFurthest(endpoints, endpoints[0]);
                var p2 = LineRaster.FindFurthest(endpoints, p1);
                if (p1 == p2) continue;

                var line = LineRaster.Supercover(p1, p2);
                if (line.Count < 2) continue;

                // Edge tangent + perpendicular are constant along this straight supercover line.
                Vector2 dir = new Vector2(p2.x - p1.x, p2.y - p1.y);
                if (dir.sqrMagnitude < 1e-6f) continue;
                dir.Normalize();
                Vector2 perp = new Vector2(-dir.y, dir.x);

                // Pre-walk the perpendicular as supercover and capture the first N step pixels
                // on each side. Same offsets get reused for every centerline pixel.
                var posSteps = SupercoverSteps(perp,  N);
                var negSteps = SupercoverSteps(-perp, N);

                int n = line.Count;
                var verts = new Vector3[n * crossCount];
                var tris  = new List<int>((n - 1) * (crossCount - 1) * 12);

                for (int i = 0; i < n; i++)
                {
                    int ex = line[i].x, ey = line[i].y;
                    float hA = SampleRegionAt(regionA, ex, ey);
                    float hB = SampleRegionAt(regionB, ex, ey);
                    float h  = (hA + hB) * 0.5f;
                    int rowBase = i * crossCount;

                    // -N .. -1 (negative side, outermost first)
                    for (int s = N; s >= 1; s--)
                    {
                        Vector2Int off = negSteps[s - 1];
                        verts[rowBase + (N - s)] =
                            new Vector3((ex + off.x) * invPpu, h, (ey + off.y) * invPpu);
                    }

                    // 0 (centerline)
                    verts[rowBase + N] = new Vector3(ex * invPpu, h, ey * invPpu);

                    // +1 .. +N
                    for (int s = 1; s <= N; s++)
                    {
                        Vector2Int off = posSteps[s - 1];
                        verts[rowBase + (N + s)] =
                            new Vector3((ex + off.x) * invPpu, h, (ey + off.y) * invPpu);
                    }
                }

                for (int i = 0; i < n - 1; i++)
                for (int j = 0; j < crossCount - 1; j++)
                {
                    int v00 = i       * crossCount + j;
                    int v10 = i       * crossCount + j + 1;
                    int v01 = (i + 1) * crossCount + j;
                    int v11 = (i + 1) * crossCount + j + 1;
                    // front
                    tris.Add(v00); tris.Add(v01); tris.Add(v10);
                    tris.Add(v10); tris.Add(v01); tris.Add(v11);
                    // back (two-sided)
                    tris.Add(v00); tris.Add(v10); tris.Add(v01);
                    tris.Add(v10); tris.Add(v11); tris.Add(v01);
                }

                var mesh = new Mesh { hideFlags = HideFlags.DontSave };
                mesh.indexFormat = verts.Length > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16;
                mesh.SetVertices(verts);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                var go = new GameObject($"Edge_{aId}_{bId}");
                go.transform.SetParent(_edgesRoot, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            }
        }

        // Walks supercover from (0,0) along dirUnit and returns the first n step pixels
        // (excluding origin). If the synthetic target doesn't yield enough pixels, pads
        // with the last produced offset.
        private static Vector2Int[] SupercoverSteps(Vector2 dirUnit, int n)
        {
            var steps = new Vector2Int[n];
            if (n <= 0) return steps;
            int reach = n + 4;
            var origin = Vector2Int.zero;
            var target = new Vector2Int(
                Mathf.RoundToInt(dirUnit.x * reach),
                Mathf.RoundToInt(dirUnit.y * reach));
            if (target == origin)
            {
                for (int i = 0; i < n; i++) steps[i] = origin;
                return steps;
            }
            var pixels = LineRaster.Supercover(origin, target);
            int idx = 0;
            for (int i = 1; i < pixels.Count && idx < n; i++)
                steps[idx++] = pixels[i];
            for (; idx < n; idx++)
                steps[idx] = idx > 0 ? steps[idx - 1] : origin;
            return steps;
        }

        // Samples a region's padded HeightMap at world pixel (wx, wy).
        // Both adjacent regions have valid data at the boundary thanks to the padding.
        private static float SampleRegionAt(Region region, int wx, int wy)
        {
            if (region.HeightMap == null) return 0f;
            var bounds = region.BoundsPx;
            int lx = wx - bounds.xMin;
            int ly = wy - bounds.yMin;
            if (lx < 0 || lx >= bounds.width || ly < 0 || ly >= bounds.height) return 0f;
            return region.HeightMap[lx, ly];
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
