using System.Collections.Generic;
using Terrain.Algorithms;
using Terrain.Data;
using UnityEngine;

namespace Terrain.Systems
{
    // Rasterizes a wing for each region pair as a thick band of integer corner cells
    // around the line from CornerA to CornerB. Two-phase build:
    //
    //   Phase A — identify cells (no heightmap reads):
    //     For each candidate corner (px, py), check
    //       in-bounds              0 ≤ px < W,  0 ≤ py < H
    //       owned by the pair      result.PixelOwners[p] ∈ {aId, bId}
    //       in the band            s ∈ [0, L]  and  |d| ≤ maxDepth
    //     where  s = (p - c1) · edgeDir  and  d = (p - c1) · perp.
    //     Cells failing any of those drop out — wings can't bleed past world edges or
    //     into a third region's territory just because the seam line is straight.
    //
    //   Phase B — sample heights:
    //     With every pair's cells already identified, walk the pending lists and
    //     sample baseHeight = avg(regionA, regionB) + regionHeight = side's region.
    //     Final Height = lerp(baseHeight, regionHeight, curve.Evaluate(|d|/maxDepth)).
    //     Doing every read after phase A is finished means any future heightmap
    //     write-back can't contaminate the avg-pixel snapshot of a neighbouring pair.
    //
    // Same-biome pairs share continuous noise across the seam, so we skip them entirely
    // (no blending needed) — matches the filter in ExtractSeamEndpoints.
    public static class EdgeWingBuilder
    {
        public static List<EdgeWingPair> Build(
            RegionBuilder.Result result,
            Dictionary<long, List<Vector2Int>> pairToCorners,
            int maxDepth)
        {
            if (maxDepth < 1) maxDepth = 1;

            // Phase A: identify cells across all pairs — no heightmap reads yet.
            var pending = new List<PendingPair>(pairToCorners.Count);
            foreach (var kv in pairToCorners)
            {
                var p = IdentifyCells(result, kv.Key, kv.Value, maxDepth);
                if (p != null) pending.Add(p);
            }

            // Phase B: now sample heights and compute blends. All edges are known so
            // the avg-pixel reads happen against unmodified heightmaps regardless of
            // later write-back stages.
            var pairs = new List<EdgeWingPair>(pending.Count);
            for (int i = 0; i < pending.Count; i++)
            {
                var ewp = ComputeHeights(pending[i], maxDepth);
                if (ewp != null) pairs.Add(ewp);
            }
            return pairs;
        }

        private static PendingPair IdentifyCells(RegionBuilder.Result result, long key,
            List<Vector2Int> corners, int maxDepth)
        {
            int aId = (int)(key >> 32);
            int bId = (int)(key & 0xFFFFFFFFL);

            var regionA = result.Graph.Get(aId);
            var regionB = result.Graph.Get(bId);
            if (regionA?.Biome != null && regionB?.Biome != null
                && regionA.Biome.type == regionB.Biome.type) return null;

            if (corners.Count < 2) return null;
            var c1 = LineRaster.FindFurthest(corners, corners[0]);
            var c2 = LineRaster.FindFurthest(corners, c1);
            if (c1 == c2) return null;

            Vector2 edgeVec = new Vector2(c2.x - c1.x, c2.y - c1.y);
            float L = edgeVec.magnitude;
            if (L < 1e-4f) return null;
            Vector2 edgeDir = edgeVec / L;
            Vector2 perp = new Vector2(-edgeDir.y, edgeDir.x);

            // Resolve which actual region sits on each perpendicular side. Walk integer
            // pixels outward from the midpoint along ±perpStep until we hit a pair-owned
            // pixel. perpStep here is just a coarse direction for the walk.
            var mid = new Vector2Int((c1.x + c2.x) / 2, (c1.y + c2.y) / 2);
            var perpStep = new Vector2Int(Mathf.RoundToInt(perp.x), Mathf.RoundToInt(perp.y));
            if (perpStep == Vector2Int.zero)
                perpStep = new Vector2Int(perp.x >= 0f ? 1 : -1, perp.y >= 0f ? 1 : -1);
            ResolveSideRegions(result, mid, perpStep, aId, bId, regionA, regionB,
                out var regionPos, out var regionNeg);

            // AABB of the cell band — clamp to world bounds so we don't loop over
            // out-of-world coords.
            Vector2 c1f = new Vector2(c1.x, c1.y);
            Vector2 c2f = new Vector2(c2.x, c2.y);
            Vector2[] bandCorners =
            {
                c1f + perp * maxDepth,
                c1f - perp * maxDepth,
                c2f + perp * maxDepth,
                c2f - perp * maxDepth,
            };
            int xMin = int.MaxValue, yMin = int.MaxValue;
            int xMax = int.MinValue, yMax = int.MinValue;
            for (int i = 0; i < 4; i++)
            {
                int bx = Mathf.FloorToInt(bandCorners[i].x) - 1;
                int by = Mathf.FloorToInt(bandCorners[i].y) - 1;
                int Bx = Mathf.CeilToInt(bandCorners[i].x) + 1;
                int By = Mathf.CeilToInt(bandCorners[i].y) + 1;
                if (bx < xMin) xMin = bx;
                if (by < yMin) yMin = by;
                if (Bx > xMax) xMax = Bx;
                if (By > yMax) yMax = By;
            }
            int W = result.Width, H = result.Height;
            xMin = Mathf.Max(xMin, 0);
            yMin = Mathf.Max(yMin, 0);
            xMax = Mathf.Min(xMax, W - 1);
            yMax = Mathf.Min(yMax, H - 1);
            if (xMax < xMin || yMax < yMin) return null;

            var curvePos = regionPos?.Biome?.edgeTransitionCurve;
            var curveNeg = regionNeg?.Biome?.edgeTransitionCurve;
            var cells = new List<CellInfo>(Mathf.Max(64, (xMax - xMin + 1)));

            for (int py = yMin; py <= yMax; py++)
            for (int px = xMin; px <= xMax; px++)
            {
                // Owner check: cell must belong to one of the pair's regions, otherwise
                // the wing would bleed into a third Voronoi region whose heights have
                // nothing to do with this seam.
                int owner = result.PixelOwners[py * W + px];
                if (owner != aId && owner != bId) continue;

                // Band check.
                float vx = px - c1.x;
                float vy = py - c1.y;
                float s = vx * edgeDir.x + vy * edgeDir.y;
                if (s < 0f || s > L) continue;
                float d = vx * perp.x + vy * perp.y;
                float absD = Mathf.Abs(d);
                if (absD > maxDepth) continue;

                byte side;
                Region sideRegion;
                AnimationCurve curve;
                if (d > 0.5f)        { side = 1; sideRegion = regionPos; curve = curvePos; }
                else if (d < -0.5f)  { side = 2; sideRegion = regionNeg; curve = curveNeg; }
                else                 { side = 0; sideRegion = null;      curve = null;     }

                cells.Add(new CellInfo
                {
                    P = new Vector2Int(px, py),
                    Side = side,
                    Depth = (byte)Mathf.Clamp(Mathf.RoundToInt(absD), 0, maxDepth),
                    W = absD / maxDepth,                  // 0 at seam, 1 at outer edge
                    SideRegion = sideRegion,
                    Curve = curve,
                });
            }

            if (cells.Count == 0) return null;

            return new PendingPair
            {
                AId = aId, BId = bId,
                C1 = c1, C2 = c2,
                Perp = perp,
                RegionA = regionA, RegionB = regionB,
                Cells = cells,
            };
        }

        private static EdgeWingPair ComputeHeights(PendingPair pp, int maxDepth)
        {
            var collection = new Dictionary<Vector2Int, WingPixel>(pp.Cells.Count);
            for (int i = 0; i < pp.Cells.Count; i++)
            {
                var c = pp.Cells[i];
                float wEased = c.Curve != null ? c.Curve.Evaluate(c.W) : c.W;

                // Avg pixel: midpoint of the two regions' heightmaps at this corner.
                // Sampled here, after every pair's cells have been identified.
                float baseHeight = SampleAvgHeight(pp.RegionA, pp.RegionB, c.P);
                float regionHeight = c.SideRegion != null ? SampleHeight(c.SideRegion, c.P) : baseHeight;
                float blended = baseHeight * (1f - wEased) + regionHeight * wEased;

                collection[c.P] = new WingPixel
                {
                    Height = blended,
                    Side = c.Side,
                    Depth = c.Depth,
                };
            }

            if (collection.Count == 0) return null;

            return new EdgeWingPair
            {
                RegionA = pp.AId,
                RegionB = pp.BId,
                CornerA = pp.C1,
                CornerB = pp.C2,
                PerpUnit = pp.Perp,
                Collection = collection,
            };
        }

        // Walk one pixel at a time outward from `mid` along ±perpStep until we hit a
        // pixel owned by the pair, to figure out which actual region lives on each side.
        // If sampling fails, infer the missing side from the other; if both fail (rare,
        // mid sits on the world boundary), fall back to (aId, bId) as an arbitrary guess.
        private static void ResolveSideRegions(
            RegionBuilder.Result result, Vector2Int mid, Vector2Int perpStep,
            int aId, int bId, Region regionA, Region regionB,
            out Region regionPos, out Region regionNeg)
        {
            bool foundPos = TryFindPairOwner(result, mid, perpStep, aId, bId, out int posOwner);
            bool foundNeg = TryFindPairOwner(result, mid,
                new Vector2Int(-perpStep.x, -perpStep.y), aId, bId, out int negOwner);

            if (!foundPos && foundNeg) posOwner = (negOwner == aId) ? bId : aId;
            else if (!foundNeg && foundPos) negOwner = (posOwner == aId) ? bId : aId;
            else if (!foundPos && !foundNeg) { posOwner = aId; negOwner = bId; }

            regionPos = posOwner == aId ? regionA : regionB;
            regionNeg = negOwner == aId ? regionA : regionB;
        }

        private static bool TryFindPairOwner(
            RegionBuilder.Result result, Vector2Int from, Vector2Int dir,
            int a, int b, out int owner)
        {
            int W = result.Width, H = result.Height;
            const int maxSteps = 64;
            for (int step = 1; step <= maxSteps; step++)
            {
                int sx = from.x + dir.x * step;
                int sy = from.y + dir.y * step;
                if ((uint)sx >= (uint)W || (uint)sy >= (uint)H) break;
                int o = result.PixelOwners[sy * W + sx];
                if (o == a || o == b) { owner = o; return true; }
            }
            owner = -1;
            return false;
        }

        private static float SampleAvgHeight(Region a, Region b, Vector2Int p)
            => 0.5f * (SampleHeight(a, p) + SampleHeight(b, p));

        private static float SampleHeight(Region r, Vector2Int p)
        {
            if (r == null || r.HeightMap == null) return 0f;
            var bounds = r.BoundsPx;
            int lx = p.x - bounds.xMin;
            int ly = p.y - bounds.yMin;
            if (lx < 0 || lx >= bounds.width || ly < 0 || ly >= bounds.height) return 0f;
            return r.HeightMap[lx, ly];
        }

        // Phase-A intermediate: per-pair identified cells, no heights sampled yet.
        private class PendingPair
        {
            public int AId, BId;
            public Vector2Int C1, C2;
            public Vector2 Perp;
            public Region RegionA, RegionB;
            public List<CellInfo> Cells;
        }

        private struct CellInfo
        {
            public Vector2Int P;
            public byte Side;
            public byte Depth;
            public float W;
            public Region SideRegion;
            public AnimationCurve Curve;
        }
    }
}
