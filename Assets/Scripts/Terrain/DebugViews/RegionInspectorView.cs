using System.Collections.Generic;
using Terrain.Biomes;
using Terrain.Core;
using Terrain.Data;
using Terrain.Systems;
using UnityEngine;

namespace Terrain.DebugViews
{
    // Single-region heightmap inspector. Picks one region by index and renders its full padded
    // BoundsPx — including the area OUTSIDE the Voronoi cell (the duplicate-bigger-version data
    // that M4's EdgeBlender will sample for cross-region blending). Splits the result into two
    // submeshes so cell vs. padding is visually obvious:
    //   - Cell mesh:    pixels owned by this region. Tinted by biome color.
    //   - Padding mesh: pixels owned by other regions but inside this region's BoundsPx. Tinted
    //                   by `paddingColor` so you can see exactly how far the heightmap extends.
    //
    // Vertices are in world units (same coordinate space as HeightmapDebugView).
    // Place this GameObject at the world origin with identity transform.
    public class RegionInspectorView : MonoBehaviour
    {
        public WorldConfig config;
        public BiomeProfile[] biomePool;
        [Min(0)] public int regionIndex = 0;
        public bool regenerateOnStart = true;
        public bool advanceSeedEachRegenerate = false;

        [Header("Rendering")]
        public bool flatShaded = true;
        public Material sharedMaterial;
        public Color paddingColor = new Color(0.35f, 0.35f, 0.35f, 1f);

        private Transform _meshRoot;
        private Material _fallbackMat;

        private void Start()
        {
            if (regenerateOnStart) Regenerate();
        }

        [ContextMenu("Regenerate")]
        public void Regenerate()
        {
            if (config == null)
            {
                Debug.LogError("[RegionInspectorView] Assign a WorldConfig asset.", this);
                return;
            }
            if (advanceSeedEachRegenerate) config.seed++;

            var result = RegionBuilder.Build(config);
            BiomeAssigner.Assign(result.Graph, biomePool, config.seed);
            HeightmapBuilder.BuildAll(result.Graph, config);

            EnsureMeshRoot();
            ClearMeshRoot();

            int count = result.Graph.Count;
            if (count == 0)
            {
                Debug.LogWarning("[RegionInspectorView] No regions in graph.", this);
                return;
            }
            int idx = Mathf.Clamp(regionIndex, 0, count - 1);
            var region = result.Graph.Get(idx);
            if (region.HeightMap == null)
            {
                Debug.LogWarning($"[RegionInspectorView] Region {idx} has no heightmap (biome unassigned?).", this);
                return;
            }

            Color biomeColor = region.Biome != null ? region.Biome.debugColor : Color.gray;
            BuildSubmesh(region, result.PixelOwners, result.Width, includeOwned: true,  $"Region{idx}_Cell",    biomeColor);
            BuildSubmesh(region, result.PixelOwners, result.Width, includeOwned: false, $"Region{idx}_Padding", paddingColor);

            Debug.Log($"[RegionInspectorView] region={idx}/{count - 1}  biome={(region.Biome != null ? region.Biome.displayName : "<none>")}  bounds={region.BoundsPx}", this);
        }

        private void BuildSubmesh(Region region, int[] pixelOwners, int worldWidth,
                                  bool includeOwned, string name, Color tint)
        {
            float invPpu = 1f / config.pixelsPerUnit;
            var bounds = region.BoundsPx;
            int rw = bounds.width;
            int rh = bounds.height;

            var indexMap = new int[rw * rh];
            for (int i = 0; i < indexMap.Length; i++) indexMap[i] = -1;

            var verts = new List<Vector3>();
            for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int wx = bounds.xMin + x;
                int wy = bounds.yMin + y;
                bool owned = pixelOwners[wy * worldWidth + wx] == region.Id;
                if (owned != includeOwned) continue;

                indexMap[y * rw + x] = verts.Count;
                verts.Add(new Vector3(wx * invPpu, region.HeightMap[x, y], wy * invPpu));
            }
            if (verts.Count == 0) return;

            var tris = new List<int>();
            for (int y = 0; y < rh - 1; y++)
            for (int x = 0; x < rw - 1; x++)
            {
                int i00 = indexMap[y * rw + x];
                int i10 = indexMap[y * rw + x + 1];
                int i01 = indexMap[(y + 1) * rw + x];
                int i11 = indexMap[(y + 1) * rw + x + 1];
                if (i00 < 0 || i10 < 0 || i01 < 0 || i11 < 0) continue;
                tris.Add(i00); tris.Add(i01); tris.Add(i10);
                tris.Add(i10); tris.Add(i01); tris.Add(i11);
            }
            if (tris.Count == 0) return;

            Mesh mesh;
            if (flatShaded)
            {
                var flatVerts = new Vector3[tris.Count];
                var flatTris  = new int[tris.Count];
                for (int i = 0; i < tris.Count; i++)
                {
                    flatVerts[i] = verts[tris[i]];
                    flatTris[i]  = i;
                }
                mesh = new Mesh { hideFlags = HideFlags.DontSave };
                mesh.indexFormat = flatVerts.Length > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
                mesh.SetVertices(flatVerts);
                mesh.SetTriangles(flatTris, 0);
            }
            else
            {
                mesh = new Mesh { hideFlags = HideFlags.DontSave };
                mesh.indexFormat = verts.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
                mesh.SetVertices(verts);
                mesh.SetTriangles(tris, 0);
            }
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject(name);
            go.transform.SetParent(_meshRoot, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = ResolveMaterial(tint);
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

        private void EnsureMeshRoot()
        {
            if (_meshRoot != null) return;
            string rootName = $"InspectedRegion ({gameObject.name})";
            var found = transform.Find(rootName);
            if (found != null) { _meshRoot = found; return; }
            var go = new GameObject(rootName);
            go.transform.SetParent(transform, false);
            _meshRoot = go.transform;
        }

        private void ClearMeshRoot()
        {
            for (int i = _meshRoot.childCount - 1; i >= 0; i--)
            {
                var child = _meshRoot.GetChild(i);
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
