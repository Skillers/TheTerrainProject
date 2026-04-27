using System.Collections.Generic;
using Terrain.Biomes;
using Terrain.Core;
using Terrain.Data;
using Terrain.Systems;
using UnityEngine;

namespace Terrain.DebugViews
{
    // Attach to a Quad/Plane with a material. Right-click component header → Regenerate.
    [RequireComponent(typeof(MeshRenderer))]
    public class RegionDebugView : MonoBehaviour
    {
        public enum ColorMode { Biome, RegionId }

        public WorldConfig config;
        public bool regenerateOnStart = true;
        public bool advanceSeedEachRegenerate = false;

        [Header("Biomes")]
        public BiomeProfile[] biomePool;
        public ColorMode colorMode = ColorMode.Biome;

        [Header("Seed dots")]
        public bool showSeeds = true;
        public float seedDotWorldSize = 0.15f;
        public Color seedDotColor = Color.white;

        [Header("Edges (one GameObject per region pair)")]
        public bool showEdges = true;
        public Color edgeColor = new Color(1f, 0.55f, 0.05f, 1f);
        [Min(1)] public int edgeWidthPx = 2; // Each Bresenham step emits a (w×w) grid-tile quad — width in pixels.
        public bool showGeometricLine = true;
        public Color geometricLineColor = Color.white;

        [Header("Boundary endpoints (where an edge meets the map boundary)")]
        public bool showBoundaryEndpoints = true;
        public float boundaryDotWorldSize = 0.18f;
        public Color boundaryDotColor = new Color(1f, 0.35f, 0.05f, 1f);

        [Header("Corners (Voronoi vertices)")]
        public bool showCorners = true;
        public float cornerDotWorldSize = 0.20f;
        public Color cornerDotColor = Color.yellow;

        private Texture2D _tex;
        private Transform _seedsRoot;
        private Transform _edgesRoot;
        private Transform _boundaryDotsRoot;
        private Transform _cornersRoot;
        private Material _seedMat;
        private Material _edgeBaseMat;
        private Material _geoLineMat;
        private Material _boundaryDotMat;
        private Material _cornerMat;

        private void Start()
        {
            if (regenerateOnStart) Regenerate();
        }

        [ContextMenu("Regenerate")]
        public void Regenerate()
        {
            if (config == null)
            {
                Debug.LogError("[RegionDebugView] Assign a WorldConfig asset.", this);
                return;
            }

            if (advanceSeedEachRegenerate) config.seed++;

            var result = RegionBuilder.Build(config);
            BiomeAssigner.Assign(result.Graph, biomePool, config.seed);

            var regionColor = new Color32[result.Graph.Count];
            for (int r = 0; r < result.Graph.Count; r++)
            {
                var reg = result.Graph.Get(r);
                bool useBiome = colorMode == ColorMode.Biome && reg.Biome != null;
                regionColor[r] = useBiome ? (Color32)reg.Biome.debugColor : ColorForRegion(r);
            }

            if (_tex == null || _tex.width != result.Width || _tex.height != result.Height)
            {
                if (_tex != null) DestroyImmediate(_tex);
                _tex = new Texture2D(result.Width, result.Height, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }

            var pixels = new Color32[result.Width * result.Height];
            for (int i = 0; i < pixels.Length; i++)
            {
                int r = result.PixelOwners[i];
                pixels[i] = r >= 0 ? regionColor[r] : new Color32(0, 0, 0, 255);
            }

            _tex.SetPixels32(pixels);
            _tex.Apply(false, false);

            var mr = GetComponent<MeshRenderer>();
            var mat = Application.isPlaying ? mr.material : mr.sharedMaterial;
            mat.mainTexture = _tex;

            var corners = DetectCorners(result);

            UpdateSeedDots(result);
            UpdateEdgeMeshes(result, corners.PairToCorners);
            UpdateCornerDots(result, corners.Interior);

            int poolSize = biomePool != null ? biomePool.Length : 0;
            Debug.Log($"[RegionDebugView] seed={config.seed}  regions={result.Graph.Count}  biomes={poolSize}  mode={colorMode}  pixels={result.Width}x{result.Height}", this);
        }

        private void UpdateSeedDots(RegionBuilder.Result result)
        {
            EnsureSeedsRoot();
            ClearSeedDots();
            if (!showSeeds) return;

            var mat = GetOrCreateSeedMat();
            for (int i = 0; i < result.SeedPixels.Length; i++)
            {
                float u = (result.SeedPixels[i].x + 0.5f) / result.Width;
                float v = (result.SeedPixels[i].y + 0.5f) / result.Height;
                Vector3 worldPos = transform.TransformPoint(new Vector3(u - 0.5f, v - 0.5f, 0.02f));

                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = $"Seed_{i}";
                sphere.transform.SetParent(_seedsRoot, true);
                sphere.transform.position = worldPos;
                sphere.transform.localScale = Vector3.one * seedDotWorldSize;

                var col = sphere.GetComponent<Collider>();
                if (col != null) SafeDestroy(col);

                var mr = sphere.GetComponent<MeshRenderer>();
                if (mr != null) mr.sharedMaterial = mat;
            }
        }

        private void EnsureSeedsRoot()
        {
            if (_seedsRoot != null) return;
            string rootName = $"SeedDots ({gameObject.name})";
            var found = GameObject.Find(rootName);
            if (found != null) { _seedsRoot = found.transform; return; }
            var go = new GameObject(rootName);
            _seedsRoot = go.transform;
        }

        private void ClearSeedDots()
        {
            for (int i = _seedsRoot.childCount - 1; i >= 0; i--)
                SafeDestroy(_seedsRoot.GetChild(i).gameObject);
        }

        private Material GetOrCreateSeedMat()
        {
            if (_seedMat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                _seedMat = new Material(shader) { hideFlags = HideFlags.DontSave };
            }
            if (_seedMat.HasProperty("_BaseColor")) _seedMat.SetColor("_BaseColor", seedDotColor);
            else _seedMat.color = seedDotColor;
            return _seedMat;
        }

        private struct CornerData
        {
            public List<Vector2Int> Interior;                         // grid coords of Voronoi vertices (3+ regions)
            public Dictionary<long, List<Vector2Int>> PairToCorners;  // (a,b) → endpoint grid coords (interior + boundary)
        }

        // Single source of truth for "where do A↔B seams start and end". Scans every 2×2 block
        // for Voronoi vertices (≥3 distinct regions); for each one, registers the corner under
        // every region pair it touches. Then walks the four map-edge rows/cols and registers a
        // boundary corner wherever the boundary crosses between two regions — those are the
        // seam terminations on the map edge. The result: pairToCorners[(a,b)] is the full set
        // of seam endpoints for that pair, in grid-corner coords aligned with the yellow orbs.
        private static CornerData DetectCorners(RegionBuilder.Result result)
        {
            int W = result.Width, H = result.Height;
            var data = new CornerData
            {
                Interior = new List<Vector2Int>(),
                PairToCorners = new Dictionary<long, List<Vector2Int>>(),
            };

            var regs = new List<int>(4);
            for (int y = 0; y < H - 1; y++)
            for (int x = 0; x < W - 1; x++)
            {
                int a = result.PixelOwners[y * W + x];
                int b = result.PixelOwners[y * W + x + 1];
                int c = result.PixelOwners[(y + 1) * W + x];
                int d = result.PixelOwners[(y + 1) * W + x + 1];

                regs.Clear();
                if (a >= 0)                                      regs.Add(a);
                if (b >= 0 && !regs.Contains(b))                 regs.Add(b);
                if (c >= 0 && !regs.Contains(c))                 regs.Add(c);
                if (d >= 0 && !regs.Contains(d))                 regs.Add(d);
                if (regs.Count < 3) continue;

                var pos = new Vector2Int(x + 1, y + 1);
                for (int i = 0; i < regs.Count; i++)
                for (int j = i + 1; j < regs.Count; j++)
                    AddPairCorner(data.PairToCorners, regs[i], regs[j], pos);

                // Skip the orb when every incident region maps to the same biome type — every
                // pair gets biome-filtered in UpdateEdgeMeshes, so no seam lines meet here.
                if (!AllSameBiome(result.Graph, regs))
                    data.Interior.Add(pos);
            }

            AddBoundaryRowCorners(result, data.PairToCorners, y: 0,     gy: 0);
            AddBoundaryRowCorners(result, data.PairToCorners, y: H - 1, gy: H);
            AddBoundaryColCorners(result, data.PairToCorners, x: 0,     gx: 0);
            AddBoundaryColCorners(result, data.PairToCorners, x: W - 1, gx: W);

            return data;
        }

        private static void AddBoundaryRowCorners(RegionBuilder.Result result, Dictionary<long, List<Vector2Int>> dict, int y, int gy)
        {
            int W = result.Width;
            for (int x = 0; x < W - 1; x++)
            {
                int a = result.PixelOwners[y * W + x];
                int b = result.PixelOwners[y * W + x + 1];
                if (a < 0 || b < 0 || a == b) continue;
                AddPairCorner(dict, a, b, new Vector2Int(x + 1, gy));
            }
        }

        private static void AddBoundaryColCorners(RegionBuilder.Result result, Dictionary<long, List<Vector2Int>> dict, int x, int gx)
        {
            int W = result.Width, H = result.Height;
            for (int y = 0; y < H - 1; y++)
            {
                int a = result.PixelOwners[y * W + x];
                int b = result.PixelOwners[(y + 1) * W + x];
                if (a < 0 || b < 0 || a == b) continue;
                AddPairCorner(dict, a, b, new Vector2Int(gx, y + 1));
            }
        }

        // Returns true iff every region in `regs` has a non-null biome and they all share the
        // same BiomeProfile.type. A null biome counts as "different" (it can still pair with
        // anything in UpdateEdgeMeshes, so the corner has potential seams).
        private static bool AllSameBiome(RegionGraph graph, List<int> regs)
        {
            BiomeProfile first = null;
            for (int i = 0; i < regs.Count; i++)
            {
                var biome = graph.Get(regs[i]).Biome;
                if (biome == null) return false;
                if (first == null) first = biome;
                else if (biome.type != first.type) return false;
            }
            return true;
        }

        private static void AddPairCorner(Dictionary<long, List<Vector2Int>> dict, int a, int b, Vector2Int pos)
        {
            long key = PairKey(a, b);
            if (!dict.TryGetValue(key, out var list))
            {
                list = new List<Vector2Int>();
                dict[key] = list;
            }
            list.Add(pos);
        }

        // For every region pair (A, B) that has at least 2 endpoint corners:
        //   1. pick the two extremes (diameter approximation across the corner set),
        //   2. supercover-rasterize a line in grid-corner coordinate space,
        //   3. build a polyline mesh through those grid coords.
        // Pairs whose biomes share the same BiomeProfile.type are skipped — visually they
        // merge into one biome, so a seam line would be misleading.
        private void UpdateEdgeMeshes(RegionBuilder.Result result, Dictionary<long, List<Vector2Int>> pairToCorners)
        {
            EnsureEdgesRoot();
            ClearEdgeMeshes();
            EnsureBoundaryDotsRoot();
            ClearBoundaryDots();
            if (!showEdges) return;

            int W = result.Width, H = result.Height;
            var baseMat = GetOrCreateEdgeBaseMat();

            foreach (var kv in pairToCorners)
            {
                int a = (int)(kv.Key >> 32);
                int b = (int)(kv.Key & 0xFFFFFFFFL);

                var biomeA = result.Graph.Get(a).Biome;
                var biomeB = result.Graph.Get(b).Biome;
                if (biomeA != null && biomeB != null && biomeA.type == biomeB.type) continue;

                var endpoints = kv.Value;
                if (endpoints.Count < 2) continue;

                var p1 = FindFurthest(endpoints, endpoints[0]);
                var p2 = FindFurthest(endpoints, p1);
                if (p1 == p2) continue;

                var line = Supercover(p1, p2);
                if (line.Count < 2) continue;

                // Each Bresenham step is a "tile". Instead of a zero-width line through that
                // tile's center grid corner, emit the full quad: 4 grid corners of a
                // (edgeWidthPx × edgeWidthPx) square centered on the step. Adjacent steps
                // overlap, so the resulting strip naturally fills the staircase noise between
                // the straight Bresenham path and the actual JFA-rasterized seam.
                float halfW = edgeWidthPx * 0.5f;
                var verts = new Vector3[line.Count * 4];
                // 12 indices per tile: 2 triangles for front, 2 with reversed winding for back —
                // makes the band visible regardless of which side the host quad faces.
                var indices = new int[line.Count * 12];
                for (int i = 0; i < line.Count; i++)
                {
                    int v = i * 4;
                    int t = i * 12;
                    Vector2Int p = line[i];
                    verts[v + 0] = QuadGridCornerFloatToWorld(p.x - halfW, p.y - halfW, W, H);
                    verts[v + 1] = QuadGridCornerFloatToWorld(p.x + halfW, p.y - halfW, W, H);
                    verts[v + 2] = QuadGridCornerFloatToWorld(p.x - halfW, p.y + halfW, W, H);
                    verts[v + 3] = QuadGridCornerFloatToWorld(p.x + halfW, p.y + halfW, W, H);
                    indices[t + 0]  = v + 0; indices[t + 1]  = v + 1; indices[t + 2]  = v + 2;
                    indices[t + 3]  = v + 2; indices[t + 4]  = v + 1; indices[t + 5]  = v + 3;
                    indices[t + 6]  = v + 0; indices[t + 7]  = v + 2; indices[t + 8]  = v + 1;
                    indices[t + 9]  = v + 2; indices[t + 10] = v + 3; indices[t + 11] = v + 1;
                }

                var mesh = new Mesh { hideFlags = HideFlags.DontSave };
                mesh.SetVertices(verts);
                mesh.SetIndices(indices, MeshTopology.Triangles, 0);
                mesh.RecalculateBounds();

                var go = new GameObject($"Edge_{a}_{b}");
                go.transform.SetParent(_edgesRoot, true);
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = baseMat;

                if (showGeometricLine)
                {
                    var lineMesh = new Mesh { hideFlags = HideFlags.DontSave };
                    lineMesh.SetVertices(new[] { QuadGridCornerToWorld(p1, W, H), QuadGridCornerToWorld(p2, W, H) });
                    lineMesh.SetIndices(new[] { 0, 1 }, MeshTopology.Lines, 0);
                    lineMesh.RecalculateBounds();

                    var goLine = new GameObject($"EdgeLine_{a}_{b}");
                    goLine.transform.SetParent(_edgesRoot, true);
                    var mfL = goLine.AddComponent<MeshFilter>();
                    mfL.sharedMesh = lineMesh;
                    var mrL = goLine.AddComponent<MeshRenderer>();
                    mrL.sharedMaterial = GetOrCreateGeoLineMat();
                }

                if (showBoundaryEndpoints)
                {
                    if (IsOnBoundary(p1, W, H)) SpawnBoundaryDot(p1, W, H, $"BoundaryEnd_{a}_{b}_p1");
                    if (IsOnBoundary(p2, W, H)) SpawnBoundaryDot(p2, W, H, $"BoundaryEnd_{a}_{b}_p2");
                }
            }
        }

        private Vector3 QuadGridCornerToWorld(Vector2Int gp, int W, int H)
            => QuadGridCornerFloatToWorld(gp.x, gp.y, W, H);

        private Vector3 QuadGridCornerFloatToWorld(float gx, float gy, int W, int H)
        {
            float lx = gx / W - 0.5f;
            float ly = gy / H - 0.5f;
            Vector3 wp = transform.TransformPoint(new Vector3(lx, ly, 0f));
            wp.y += 0.1f;
            return wp;
        }

        private static bool IsOnBoundary(Vector2Int gp, int W, int H)
            => gp.x == 0 || gp.x == W || gp.y == 0 || gp.y == H;

        private void SpawnBoundaryDot(Vector2Int gp, int W, int H, string name)
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = name;
            sphere.transform.SetParent(_boundaryDotsRoot, true);
            sphere.transform.position = QuadGridCornerToWorld(gp, W, H);
            sphere.transform.localScale = Vector3.one * boundaryDotWorldSize;
            var col = sphere.GetComponent<Collider>();
            if (col != null) SafeDestroy(col);
            var mr = sphere.GetComponent<MeshRenderer>();
            if (mr != null) mr.sharedMaterial = GetOrCreateBoundaryDotMat();
        }

        private void EnsureBoundaryDotsRoot()
        {
            if (_boundaryDotsRoot != null) return;
            string rootName = $"BoundaryDots ({gameObject.name})";
            var found = GameObject.Find(rootName);
            if (found != null) { _boundaryDotsRoot = found.transform; return; }
            var go = new GameObject(rootName);
            _boundaryDotsRoot = go.transform;
        }

        private void ClearBoundaryDots()
        {
            for (int i = _boundaryDotsRoot.childCount - 1; i >= 0; i--)
                SafeDestroy(_boundaryDotsRoot.GetChild(i).gameObject);
        }

        private Material GetOrCreateGeoLineMat()
        {
            if (_geoLineMat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                _geoLineMat = new Material(shader)
                {
                    hideFlags   = HideFlags.DontSave,
                    renderQueue = 4001, // one above the supercover line so it draws on top.
                };
                if (_geoLineMat.HasProperty("_ZTest"))
                    _geoLineMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                if (_geoLineMat.HasProperty("_ZWrite"))
                    _geoLineMat.SetInt("_ZWrite", 0);
            }
            if (_geoLineMat.HasProperty("_BaseColor")) _geoLineMat.SetColor("_BaseColor", geometricLineColor);
            else                                       _geoLineMat.color = geometricLineColor;
            return _geoLineMat;
        }

        private Material GetOrCreateBoundaryDotMat()
        {
            if (_boundaryDotMat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                _boundaryDotMat = new Material(shader) { hideFlags = HideFlags.DontSave };
            }
            if (_boundaryDotMat.HasProperty("_BaseColor")) _boundaryDotMat.SetColor("_BaseColor", boundaryDotColor);
            else                                            _boundaryDotMat.color = boundaryDotColor;
            return _boundaryDotMat;
        }

        // Supercover line algorithm (Eugen Dedu's variant). Standard Bresenham picks one
        // pixel per dominant-axis step; on diagonals it can leave thin gaps where the line
        // grazes a pixel corner. Supercover walks the line orthogonally — every step is
        // either a horizontal or a vertical move into the next touched pixel — so any pixel
        // the geometric line intersects (overlap > 0) is claimed. Returns pixels in line
        // order; consecutive pixels differ by exactly one axis, so they form a clean polyline.
        private static List<Vector2Int> Supercover(Vector2Int p0, Vector2Int p1)
        {
            var result = new List<Vector2Int>();
            int dx = p1.x - p0.x;
            int dy = p1.y - p0.y;
            int absDx = Mathf.Abs(dx);
            int absDy = Mathf.Abs(dy);
            int sx = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
            int sy = dy > 0 ? 1 : (dy < 0 ? -1 : 0);

            int x = p0.x, y = p0.y;
            result.Add(new Vector2Int(x, y));

            int errX = absDy * 2;
            int errY = absDx * 2;
            int err  = absDx - absDy;
            int n    = absDx + absDy;

            while (n-- > 0)
            {
                if (err > 0)
                {
                    x   += sx;
                    err -= errX;
                }
                else if (err < 0)
                {
                    y   += sy;
                    err += errY;
                }
                else
                {
                    // Exact corner crossing: emit the intermediate pixel (x+sx, y), then
                    // step both axes diagonally. Consumes two of the n steps.
                    result.Add(new Vector2Int(x + sx, y));
                    x   += sx;
                    y   += sy;
                    err += errY - errX;
                    n--;
                }
                result.Add(new Vector2Int(x, y));
            }
            return result;
        }

        private static Vector2Int FindFurthest(List<Vector2Int> pixels, Vector2Int from)
        {
            Vector2Int best = pixels[0];
            int bestSq = -1;
            for (int i = 0; i < pixels.Count; i++)
            {
                int dx = pixels[i].x - from.x;
                int dy = pixels[i].y - from.y;
                int d  = dx * dx + dy * dy;
                if (d > bestSq) { bestSq = d; best = pixels[i]; }
            }
            return best;
        }

        private static long PairKey(int a, int b)
            => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        private void EnsureEdgesRoot()
        {
            if (_edgesRoot != null) return;
            string rootName = $"Edges ({gameObject.name})";
            var found = GameObject.Find(rootName);
            if (found != null) { _edgesRoot = found.transform; return; }
            var go = new GameObject(rootName);
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

        private Material GetOrCreateEdgeBaseMat()
        {
            if (_edgeBaseMat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                _edgeBaseMat = new Material(shader)
                {
                    hideFlags   = HideFlags.DontSave,
                    renderQueue = 4000, // Overlay — drawn after opaque geometry.
                };
                // ZTest Always so edges stay visible on top of the quad regardless of depth.
                if (_edgeBaseMat.HasProperty("_ZTest"))
                    _edgeBaseMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                if (_edgeBaseMat.HasProperty("_ZWrite"))
                    _edgeBaseMat.SetInt("_ZWrite", 0);
                // Cull off so the thick triangle quads are visible from either side of the host quad.
                if (_edgeBaseMat.HasProperty("_Cull"))
                    _edgeBaseMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            }
            if (_edgeBaseMat.HasProperty("_BaseColor")) _edgeBaseMat.SetColor("_BaseColor", edgeColor);
            else                                       _edgeBaseMat.color = edgeColor;
            return _edgeBaseMat;
        }

        private void UpdateCornerDots(RegionBuilder.Result result, List<Vector2Int> corners)
        {
            EnsureCornersRoot();
            ClearCornerDots();
            if (!showCorners) return;

            var mat = GetOrCreateCornerMat();
            int W = result.Width, H = result.Height;
            for (int i = 0; i < corners.Count; i++)
            {
                var gp = corners[i];
                float u = (float)gp.x / W;
                float v = (float)gp.y / H;
                Vector3 worldPos = transform.TransformPoint(new Vector3(u - 0.5f, v - 0.5f, 0f));
                worldPos.y += 0.2f; // sit above the edge mesh (edges are at +0.1).

                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = $"Corner_{i}";
                sphere.transform.SetParent(_cornersRoot, true);
                sphere.transform.position = worldPos;
                sphere.transform.localScale = Vector3.one * cornerDotWorldSize;

                var col = sphere.GetComponent<Collider>();
                if (col != null) SafeDestroy(col);
                var mr = sphere.GetComponent<MeshRenderer>();
                if (mr != null) mr.sharedMaterial = mat;
            }
        }

        private void EnsureCornersRoot()
        {
            if (_cornersRoot != null) return;
            string rootName = $"CornerDots ({gameObject.name})";
            var found = GameObject.Find(rootName);
            if (found != null) { _cornersRoot = found.transform; return; }
            var go = new GameObject(rootName);
            _cornersRoot = go.transform;
        }

        private void ClearCornerDots()
        {
            for (int i = _cornersRoot.childCount - 1; i >= 0; i--)
                SafeDestroy(_cornersRoot.GetChild(i).gameObject);
        }

        private Material GetOrCreateCornerMat()
        {
            if (_cornerMat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                _cornerMat = new Material(shader) { hideFlags = HideFlags.DontSave };
            }
            if (_cornerMat.HasProperty("_BaseColor")) _cornerMat.SetColor("_BaseColor", cornerDotColor);
            else _cornerMat.color = cornerDotColor;
            return _cornerMat;
        }

        private static void SafeDestroy(Object obj)
        {
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }

        // Distinct color per region id via integer hash.
        private static Color32 ColorForRegion(int id)
        {
            uint h = ((uint)id + 1u) * 2654435761u;
            return new Color32((byte)((h >> 16) | 0x40),
                               (byte)((h >>  8) | 0x40),
                               (byte)( h         | 0x40),
                               255);
        }
    }
}
