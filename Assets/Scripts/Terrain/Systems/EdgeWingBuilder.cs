using System.Collections.Generic;
using Terrain.Algorithms;
using Terrain.Data;
using UnityEngine;

namespace Terrain.Systems
{
    // For each region pair, produces a base Bresenham edge plus N depth layers on
    // each side:
    //   depth 0  base line  — corner-to-corner straight line (tag side 0)
    //   depth k  side A     — base shifted by +k*PerpStep, snapped to pixels (tag side 1)
    //   depth k  side B     — base shifted by -k*PerpStep, snapped to pixels (tag side 2)
    // Pixels are deduplicated across all lines via a per-pair dictionary keyed on
    // (x, y). First write wins, so a deeper pixel that lands on an existing one is
    // skipped. Depth-N pixels copy their height from the base pixel at the same
    // index (which is the Euclidean-nearest base pixel for parallel translated
    // Bresenham lines). Depth-0 pixels take the average of the two regions' heightmaps.
    public static class EdgeWingBuilder
    {
        public static List<EdgeWingPair> Build(
            RegionBuilder.Result result,
            Dictionary<long, List<Vector2Int>> pairToCorners,
            int maxDepth)
        {
            if (maxDepth < 1) maxDepth = 1;
            var pairs = new List<EdgeWingPair>(pairToCorners.Count);
            foreach (var kv in pairToCorners)
            {
                var p = BuildPair(result, kv.Key, kv.Value, maxDepth);
                if (p != null) pairs.Add(p);
            }
            return pairs;
        }

        private static EdgeWingPair BuildPair(RegionBuilder.Result result, long key,
            List<Vector2Int> corners, int maxDepth)
        {
            int aId = (int)(key >> 32);
            int bId = (int)(key & 0xFFFFFFFFL);

            if (corners.Count < 2) return null;
            var c1 = LineRaster.FindFurthest(corners, corners[0]);
            var c2 = LineRaster.FindFurthest(corners, c1);
            if (c1 == c2) return null;

            Vector2 edgeDir = new Vector2(c2.x - c1.x, c2.y - c1.y);
            if (edgeDir.sqrMagnitude < 1e-6f) return null;
            edgeDir.Normalize();
            Vector2 perp = new Vector2(-edgeDir.y, edgeDir.x);
            var perpStep = new Vector2Int(Mathf.RoundToInt(perp.x), Mathf.RoundToInt(perp.y));
            if (perpStep == Vector2Int.zero) return null;

            // Base line + depth layers. Same dx/dy across all → same pixel count, so
            // index correspondence is exact.
            var baseLine = LineRaster.Bresenham(c1, c2);

            var regionA = result.Graph.Get(aId);
            var regionB = result.Graph.Get(bId);

            // Determine which actual region sits on each perpendicular side. The blend
            // at depth k pulls toward the heightmap of THAT side's region — not "region
            // A" by ID, since side A in our naming is just the +PerpStep side and which
            // real region lives there depends on c1/c2 ordering.
            var mid = new Vector2Int((c1.x + c2.x) / 2, (c1.y + c2.y) / 2);
            ResolveSideRegions(result, mid, perpStep, aId, bId, regionA, regionB,
                out var regionPos, out var regionNeg);

            int totalCap = baseLine.Count * (1 + 2 * maxDepth);
            var collection = new Dictionary<Vector2Int, WingPixel>(totalCap);

            // Step 1: base line. Height = average of the two regions' heightmaps. The
            // central area "doesn't change" — depth 0 stays at this seam-blend value.
            for (int i = 0; i < baseLine.Count; i++)
            {
                var p = baseLine[i];
                if (collection.ContainsKey(p)) continue;
                float h = SampleAvgHeight(regionA, regionB, p);
                collection[p] = new WingPixel { Height = h, Side = 0, Depth = 0 };
            }

            // Steps 2..N: side A and side B at each depth k. Height blends from the
            // depth-0 base height toward that side's actual region height with t = k/maxDepth.
            // depth = maxDepth → fully the region's heightmap; depth 0 → unchanged.
            var sideALayers = new List<List<Vector2Int>>(maxDepth);
            var sideBLayers = new List<List<Vector2Int>>(maxDepth);
            for (int depth = 1; depth <= maxDepth; depth++)
            {
                var shift = new Vector2Int(perpStep.x * depth, perpStep.y * depth);
                var sideALine = LineRaster.Bresenham(c1 + shift, c2 + shift);
                var sideBLine = LineRaster.Bresenham(c1 - shift, c2 - shift);

                AddDepthLayer(sideALine, side: 1, depth: (byte)depth, baseLine, regionPos, maxDepth, collection);
                AddDepthLayer(sideBLine, side: 2, depth: (byte)depth, baseLine, regionNeg, maxDepth, collection);

                sideALayers.Add(sideALine);
                sideBLayers.Add(sideBLine);
            }

            return new EdgeWingPair
            {
                RegionA = aId,
                RegionB = bId,
                CornerA = c1,
                CornerB = c2,
                PerpUnit = perp,
                PerpStep = perpStep,
                BaseLine = baseLine,
                SideALayers = sideALayers,
                SideBLayers = sideBLayers,
                Collection = collection,
            };
        }

        private static void AddDepthLayer(
            List<Vector2Int> line, byte side, byte depth,
            List<Vector2Int> baseLine, Region region, int maxDepth,
            Dictionary<Vector2Int, WingPixel> collection)
        {
            int baseN = baseLine.Count;
            float t = (float)depth / maxDepth; // 0 → all base, 1 → all region
            for (int i = 0; i < line.Count; i++)
            {
                var p = line[i];
                if (collection.ContainsKey(p)) continue;
                var basePixel = baseLine[Mathf.Min(i, baseN - 1)];
                float baseHeight = collection[basePixel].Height;
                float regionHeight = SampleHeight(region, p);
                float blended = baseHeight * (1f - t) + regionHeight * t;
                collection[p] = new WingPixel { Height = blended, Side = side, Depth = depth };
            }
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
    }
}
