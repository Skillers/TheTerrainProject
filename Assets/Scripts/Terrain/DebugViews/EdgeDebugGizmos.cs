using Terrain.Algorithms;
using Terrain.Data;
using Terrain.Systems;
using UnityEngine;

namespace Terrain.DebugViews
{
    // Per-pair edge debug overlay drawn entirely with Gizmos. Three independently toggleable
    // line types so we can compare the different "edge" interpretations side by side:
    //   1. Real boundary  — actual jagged seam between the two regions (PixelOwners flips)
    //   2. Supercover     — Bresenham-stepped line connecting the two extreme corners
    //   3. Corner-to-corner — straight line between the two extreme corners (the ideal)
    [ExecuteAlways]
    public class EdgeDebugGizmos : MonoBehaviour
    {
        public bool show = true;

        [Header("Lines")]
        public bool showRealBoundary    = true;
        public bool showSupercover      = true;
        public bool showCornerToCorner  = true;
        public Color realBoundaryColor   = new Color(0.10f, 0.80f, 1.00f, 1f); // cyan
        public Color supercoverColor     = new Color(1.00f, 0.85f, 0.10f, 1f); // yellow
        public Color cornerToCornerColor = new Color(1.00f, 0.20f, 0.80f, 1f); // magenta

        [Header("Filtering")]
        public bool skipSameBiomePairs = true;

        [Header("Placement")]
        public bool snapToHeightmap = true;
        public float worldYOffset   = 0.5f;

        private void OnDrawGizmos()
        {
            if (!show) return;

            var src = TerrainGen.Instance;
            if (src?.config == null || src.Data == null) return;
            var data   = src.Data;
            var result = data.BuildResult;
            if (data.PairToCorners == null) return;

            float invPpu = 1f / src.config.pixelsPerUnit;

            foreach (var kv in data.PairToCorners)
            {
                int aId = (int)(kv.Key >> 32);
                int bId = (int)(kv.Key & 0xFFFFFFFFL);

                var regionA = result.Graph.Get(aId);
                var regionB = result.Graph.Get(bId);
                if (skipSameBiomePairs &&
                    regionA.Biome != null && regionB.Biome != null &&
                    regionA.Biome.type == regionB.Biome.type) continue;

                if (showRealBoundary &&
                    data.PairToBoundaryWalls != null &&
                    data.PairToBoundaryWalls.TryGetValue(kv.Key, out var walls))
                {
                    Gizmos.color = realBoundaryColor;
                    for (int i = 0; i < walls.Count; i++)
                    {
                        var seg = walls[i];
                        Gizmos.DrawLine(
                            ToWorld(regionA, regionB, seg.p1.x, seg.p1.y, invPpu),
                            ToWorld(regionA, regionB, seg.p2.x, seg.p2.y, invPpu));
                    }
                }

                if (showSupercover &&
                    data.PairToSupercoverPixels != null &&
                    data.PairToSupercoverPixels.TryGetValue(kv.Key, out var pixels) &&
                    pixels.Count >= 2)
                {
                    // Supercover here is fed corner-coord endpoints (from PairToCorners), so its
                    // output integer points are corner coords too — same lattice as magenta. No
                    // half-pixel shift; just connect them directly.
                    Gizmos.color = supercoverColor;
                    for (int i = 0; i < pixels.Count - 1; i++)
                    {
                        var p = pixels[i];
                        var q = pixels[i + 1];
                        Gizmos.DrawLine(
                            ToWorld(regionA, regionB, p.x, p.y, invPpu),
                            ToWorld(regionA, regionB, q.x, q.y, invPpu));
                    }
                }

                if (showCornerToCorner)
                {
                    var pts = kv.Value;
                    if (pts.Count >= 2)
                    {
                        var p1 = LineRaster.FindFurthest(pts, pts[0]);
                        var p2 = LineRaster.FindFurthest(pts, p1);
                        if (p1 != p2)
                        {
                            Gizmos.color = cornerToCornerColor;
                            Gizmos.DrawLine(
                                ToWorld(regionA, regionB, p1.x, p1.y, invPpu),
                                ToWorld(regionA, regionB, p2.x, p2.y, invPpu));
                        }
                    }
                }
            }
        }

        private Vector3 ToWorld(Region a, Region b, float gx, float gy, float invPpu)
        {
            float y = ResolveY(a, b, gx, gy);
            return new Vector3(gx * invPpu, y, gy * invPpu);
        }

        private float ResolveY(Region a, Region b, float gx, float gy)
        {
            if (!snapToHeightmap) return worldYOffset;
            float hA = SampleHeight(a, gx, gy);
            float hB = SampleHeight(b, gx, gy);
            return 0.5f * (hA + hB) + worldYOffset;
        }

        private static float SampleHeight(Region r, float gx, float gy)
        {
            if (r == null || r.HeightMap == null) return 0f;
            var bounds = r.BoundsPx;
            int lx = Mathf.RoundToInt(gx) - bounds.xMin;
            int ly = Mathf.RoundToInt(gy) - bounds.yMin;
            if (lx < 0 || lx >= bounds.width || ly < 0 || ly >= bounds.height) return 0f;
            return r.HeightMap[lx, ly];
        }
    }
}
