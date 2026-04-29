using System.Collections.Generic;
using Terrain.Biomes;
using Terrain.Core;
using Terrain.Systems;
using UnityEngine;

namespace Terrain.DebugViews
{
    // Attach to a Quad/Plane with a material. Right-click component header → Regenerate.
    [RequireComponent(typeof(MeshRenderer))]
    public class RegionDebugView : MonoBehaviour
    {
        public enum ColorMode { Biome, RegionId }

        [Header("Biomes")]
        public ColorMode colorMode = ColorMode.Biome;

        [Header("Seed dots")]
        public bool showSeeds = true;
        public float seedDotWorldSize = 0.15f;
        public Color seedDotColor = Color.white;

        [Header("Edges (one GameObject per region pair)")]
        public bool showEdges = true;
        public Color edgeColor = new Color(1f, 0.55f, 0.05f, 1f);
        [Min(1)] public int edgeWidthPx = 2;
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
            if (TerrainGen.Instance == null) return;
            TerrainGen.Instance.OnRegenerated += Rebuild;
            if (TerrainGen.Instance.Data != null) Rebuild();
        }

        private void OnDestroy()
        {
            if (TerrainGen.Instance != null)
                TerrainGen.Instance.OnRegenerated -= Rebuild;
        }

        [ContextMenu("Regenerate")]
        public void Regenerate()
        {
            if (TerrainGen.Instance == null)
            {
                Debug.LogError("[RegionDebugView] No TerrainGen found in scene.", this);
                return;
            }

            TerrainGen.Instance.Regenerate();
        }

        // Rebuilds visuals from the current TerrainGen.Instance.Data without re-running the pipeline.
        public void Rebuild()
        {
            if (TerrainGen.Instance?.Data == null) return;
            var data = TerrainGen.Instance.Data;
            var result = data.BuildResult;

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
                    wrapMode   = TextureWrapMode.Clamp,
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

            UpdateSeedDots(result);
            UpdateEdgeMeshes(result, data.PairToCorners);
            UpdateCornerDots(result, data.InteriorCorners);

            int poolSize = TerrainGen.Instance.biomePool != null ? TerrainGen.Instance.biomePool.Length : 0;
            Debug.Log($"[RegionDebugView] seed={TerrainGen.Instance.config.seed}  regions={result.Graph.Count}  biomes={poolSize}  mode={colorMode}  pixels={result.Width}x{result.Height}", this);
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
            _seedsRoot = new GameObject(rootName).transform;
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

        private void UpdateEdgeMeshes(RegionBuilder.Result result,
            Dictionary<long, List<Vector2Int>> pairToCorners)
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

                var p1 = TerrainGen.FindFurthest(endpoints, endpoints[0]);
                var p2 = TerrainGen.FindFurthest(endpoints, p1);
                if (p1 == p2) continue;

                var line = TerrainGen.Supercover(p1, p2);
                if (line.Count < 2) continue;

                float halfW = edgeWidthPx * 0.5f;
                var verts   = new Vector3[line.Count * 4];
                var indices = new int[line.Count * 12];
                for (int i = 0; i < line.Count; i++)
                {
                    int v = i * 4, t = i * 12;
                    Vector2Int p = line[i];
                    verts[v + 0] = QuadGridCornerFloatToWorld(p.x - halfW, p.y - halfW, W, H);
                    verts[v + 1] = QuadGridCornerFloatToWorld(p.x + halfW, p.y - halfW, W, H);
                    verts[v + 2] = QuadGridCornerFloatToWorld(p.x - halfW, p.y + halfW, W, H);
                    verts[v + 3] = QuadGridCornerFloatToWorld(p.x + halfW, p.y + halfW, W, H);
                    indices[t + 0]  = v;     indices[t + 1]  = v + 1; indices[t + 2]  = v + 2;
                    indices[t + 3]  = v + 2; indices[t + 4]  = v + 1; indices[t + 5]  = v + 3;
                    indices[t + 6]  = v;     indices[t + 7]  = v + 2; indices[t + 8]  = v + 1;
                    indices[t + 9]  = v + 2; indices[t + 10] = v + 3; indices[t + 11] = v + 1;
                }

                var mesh = new Mesh { hideFlags = HideFlags.DontSave };
                mesh.SetVertices(verts);
                mesh.SetIndices(indices, MeshTopology.Triangles, 0);
                mesh.RecalculateBounds();

                var go = new GameObject($"Edge_{a}_{b}");
                go.transform.SetParent(_edgesRoot, true);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = baseMat;

                if (showGeometricLine)
                {
                    var lineMesh = new Mesh { hideFlags = HideFlags.DontSave };
                    lineMesh.SetVertices(new[] { QuadGridCornerToWorld(p1, W, H), QuadGridCornerToWorld(p2, W, H) });
                    lineMesh.SetIndices(new[] { 0, 1 }, MeshTopology.Lines, 0);
                    lineMesh.RecalculateBounds();
                    var goLine = new GameObject($"EdgeLine_{a}_{b}");
                    goLine.transform.SetParent(_edgesRoot, true);
                    goLine.AddComponent<MeshFilter>().sharedMesh = lineMesh;
                    goLine.AddComponent<MeshRenderer>().sharedMaterial = GetOrCreateGeoLineMat();
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
            _boundaryDotsRoot = new GameObject(rootName).transform;
        }

        private void ClearBoundaryDots()
        {
            for (int i = _boundaryDotsRoot.childCount - 1; i >= 0; i--)
                SafeDestroy(_boundaryDotsRoot.GetChild(i).gameObject);
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
                worldPos.y += 0.2f;

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
            _cornersRoot = new GameObject(rootName).transform;
        }

        private void ClearCornerDots()
        {
            for (int i = _cornersRoot.childCount - 1; i >= 0; i--)
                SafeDestroy(_cornersRoot.GetChild(i).gameObject);
        }

        // ── Material helpers ──────────────────────────────────────────────────────

        private Material GetOrCreateEdgeBaseMat()
        {
            if (_edgeBaseMat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                _edgeBaseMat = new Material(shader)
                {
                    hideFlags   = HideFlags.DontSave,
                    renderQueue = 4000,
                };
                if (_edgeBaseMat.HasProperty("_ZTest"))
                    _edgeBaseMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                if (_edgeBaseMat.HasProperty("_ZWrite"))
                    _edgeBaseMat.SetInt("_ZWrite", 0);
                if (_edgeBaseMat.HasProperty("_Cull"))
                    _edgeBaseMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            }
            if (_edgeBaseMat.HasProperty("_BaseColor")) _edgeBaseMat.SetColor("_BaseColor", edgeColor);
            else                                        _edgeBaseMat.color = edgeColor;
            return _edgeBaseMat;
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
                    renderQueue = 4001,
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

        private void EnsureEdgesRoot()
        {
            if (_edgesRoot != null) return;
            string rootName = $"Edges ({gameObject.name})";
            var found = GameObject.Find(rootName);
            if (found != null) { _edgesRoot = found.transform; return; }
            _edgesRoot = new GameObject(rootName).transform;
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
