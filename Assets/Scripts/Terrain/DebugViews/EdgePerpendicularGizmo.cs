using System.Collections.Generic;
using Terrain.Data;
using Terrain.Systems;
using UnityEngine;

namespace Terrain.DebugViews
{
    // Per Voronoi-edge debug: at the midpoint of each region-pair's geometric line (p1→p2),
    // draws the perpendicular and tags which side belongs to which region by sampling
    // PixelOwners along the perpendicular until it hits one of the pair's owners.
    //
    // CachedEntries is populated by OnDrawGizmos and read by the Editor companion to render
    // the region-id labels via Handles.Label.
    [ExecuteAlways]
    public class EdgePerpendicularGizmo : MonoBehaviour
    {
        public bool show = true;

        [Header("Perpendicular")]
        [Min(1)] public int perpLengthPx = 16;
        [Min(1)] public int sideSampleMaxPx = 64;
        public Color lineColor = new Color(0f, 1f, 1f, 1f);
        public Color midDotColor = Color.yellow;
        public float midDotRadius = 0.06f;

        [Header("Placement")]
        public bool snapToHeightmap = true;
        public float worldYOffset = 0.5f;

        [Header("Filtering")]
        public bool skipSameBiomePairs = true;

        public struct Entry
        {
            public Vector3 worldMid;
            public Vector3 worldPosEnd;   // end of +perpendicular
            public Vector3 worldNegEnd;   // end of -perpendicular
            public int posRegionId;       // -1 if no pair owner found within sideSampleMaxPx
            public int negRegionId;
        }

        public readonly List<Entry> CachedEntries = new();

        private void OnDrawGizmos()
        {
            CachedEntries.Clear();
            if (!show) return;

            var src = TerrainGen.Instance;
            if (src == null || src.config == null || src.Data == null) return;
            var data = src.Data;
            if (data.PairToCorners == null) return;

            var result = data.BuildResult;
            float invPpu = 1f / src.config.pixelsPerUnit;
            int W = result.Width, H = result.Height;

            foreach (var kv in data.PairToCorners)
            {
                int aId = (int)(kv.Key >> 32);
                int bId = (int)(kv.Key & 0xFFFFFFFFL);

                var regionA = result.Graph.Get(aId);
                var regionB = result.Graph.Get(bId);

                if (skipSameBiomePairs &&
                    regionA.Biome != null && regionB.Biome != null &&
                    regionA.Biome.type == regionB.Biome.type) continue;

                var pts = kv.Value;
                if (pts.Count < 2) continue;
                var p1 = TerrainGen.FindFurthest(pts, pts[0]);
                var p2 = TerrainGen.FindFurthest(pts, p1);
                if (p1 == p2) continue;

                Vector2 dir = new Vector2(p2.x - p1.x, p2.y - p1.y);
                if (dir.sqrMagnitude < 1e-6f) continue;
                dir.Normalize();
                Vector2 perp = new Vector2(-dir.y, dir.x);

                Vector2 mid = new Vector2((p1.x + p2.x) * 0.5f, (p1.y + p2.y) * 0.5f);

                int posRegion = SampleNearestPairOwner(result, mid,  perp, aId, bId, W, H, sideSampleMaxPx);
                int negRegion = SampleNearestPairOwner(result, mid, -perp, aId, bId, W, H, sideSampleMaxPx);

                Vector2 posPx = mid + perp * perpLengthPx;
                Vector2 negPx = mid - perp * perpLengthPx;

                float midY    = ResolveY(regionA, regionB, mid.x, mid.y);
                float posEndY = ResolveY(regionA, regionB, posPx.x, posPx.y);
                float negEndY = ResolveY(regionA, regionB, negPx.x, negPx.y);

                Vector3 worldMid    = new Vector3(mid.x   * invPpu, midY,    mid.y   * invPpu);
                Vector3 worldPosEnd = new Vector3(posPx.x * invPpu, posEndY, posPx.y * invPpu);
                Vector3 worldNegEnd = new Vector3(negPx.x * invPpu, negEndY, negPx.y * invPpu);

                Gizmos.color = lineColor;
                Gizmos.DrawLine(worldNegEnd, worldPosEnd);
                Gizmos.color = midDotColor;
                Gizmos.DrawSphere(worldMid, midDotRadius);

                CachedEntries.Add(new Entry
                {
                    worldMid     = worldMid,
                    worldPosEnd  = worldPosEnd,
                    worldNegEnd  = worldNegEnd,
                    posRegionId  = posRegion,
                    negRegionId  = negRegion,
                });
            }
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

        // Walks one pixel at a time along dirUnit until it lands on a pixel owned by either
        // member of the pair. Returns that owner id, or -1 if no hit within maxStep.
        private static int SampleNearestPairOwner(RegionBuilder.Result result, Vector2 mid,
            Vector2 dirUnit, int a, int b, int W, int H, int maxStep)
        {
            for (int step = 1; step <= maxStep; step++)
            {
                int sx = Mathf.RoundToInt(mid.x + dirUnit.x * step);
                int sy = Mathf.RoundToInt(mid.y + dirUnit.y * step);
                if (sx < 0 || sx >= W || sy < 0 || sy >= H) return -1;
                int owner = result.PixelOwners[sy * W + sx];
                if (owner == a || owner == b) return owner;
            }
            return -1;
        }
    }
}
