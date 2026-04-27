using System.Collections.Generic;
using Terrain.Biomes;
using Terrain.Core;
using Terrain.Data;
using Terrain.Systems;
using UnityEngine;

namespace Terrain.DebugViews
{
    // M3 visual exit criterion: one flat-shaded mesh per region, vertices elevated by HeightMap[x,y].
    // No blending across boundaries — adjacent biomes will show hard seams. That's correct for M3;
    // M4's EdgeBlender is what makes them disappear. Right-click component header → Regenerate.
    //
    // Vertices are placed in world units: X = pixel_x / pixelsPerUnit,  Y = pixel_y / pixelsPerUnit,
    // Z = raw height value.  Place this GameObject at the world origin (0,0,0) with identity rotation
    // and scale so the mesh sits in the same coordinate space as any other world-unit debug geometry.
    public class HeightmapDebugView : MonoBehaviour
    {
        public WorldConfig config;
        public BiomeProfile[] biomePool;
        public bool regenerateOnStart = true;
        public bool advanceSeedEachRegenerate = false;

        [Header("Rendering")]
        public Material sharedMaterial;
        public bool flatShaded = true;

        private Transform _meshesRoot;
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
                Debug.LogError("[HeightmapDebugView] Assign a WorldConfig asset.", this);
                return;
            }
            if (advanceSeedEachRegenerate) config.seed++;

            var result = RegionBuilder.Build(config);
            BiomeAssigner.Assign(result.Graph, biomePool, config.seed);
            HeightmapBuilder.BuildAll(result.Graph, config);

            EnsureMeshesRoot();
            ClearMeshes();

            for (int r = 0; r < result.Graph.Count; r++)
            {
                var region = result.Graph.Get(r);
                if (region.HeightMap == null) continue;
                BuildRegionMesh(region, result.PixelOwners, result.Width, result.Height);
            }

            Debug.Log($"[HeightmapDebugView] seed={config.seed}  regions={result.Graph.Count}  pixels={result.Width}x{result.Height}", this);
        }

        private void BuildRegionMesh(Region region, int[] pixelOwners, int worldWidth, int worldHeight)
        {
            float invPpu = 1f / config.pixelsPerUnit;
            var bounds = region.BoundsPx;
            int rw = bounds.width;
            int rh = bounds.height;

            var vertexIndex = new int[rw * rh];
            for (int i = 0; i < vertexIndex.Length; i++) vertexIndex[i] = -1;

            var verts = new List<Vector3>();
            for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int wx = bounds.xMin + x;
                int wy = bounds.yMin + y;
                if (pixelOwners[wy * worldWidth + wx] != region.Id) continue;

                vertexIndex[y * rw + x] = verts.Count;
                verts.Add(new Vector3(wx * invPpu, region.HeightMap[x, y], wy * invPpu));
            }

            if (verts.Count == 0) return;

            var tris = new List<int>();
            for (int y = 0; y < rh - 1; y++)
            for (int x = 0; x < rw - 1; x++)
            {
                int i00 = vertexIndex[y * rw + x];
                int i10 = vertexIndex[y * rw + x + 1];
                int i01 = vertexIndex[(y + 1) * rw + x];
                int i11 = vertexIndex[(y + 1) * rw + x + 1];
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

            var go = new GameObject($"HeightRegion_{region.Id}");
            go.transform.SetParent(_meshesRoot, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = ResolveMaterial(region.Biome);
        }

        private Material ResolveMaterial(BiomeProfile biome)
        {
            if (sharedMaterial != null) return sharedMaterial;
            if (_fallbackMat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Standard");
                _fallbackMat = new Material(shader) { hideFlags = HideFlags.DontSave };
            }
            var mat = new Material(_fallbackMat) { hideFlags = HideFlags.DontSave };
            Color c = biome != null ? biome.debugColor : Color.gray;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            else mat.color = c;
            return mat;
        }

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

        private static void SafeDestroy(Object obj)
        {
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }
    }
}
