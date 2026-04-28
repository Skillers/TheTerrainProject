using System.Collections.Generic;
using Terrain.Algorithms;
using Terrain.Data;
using UnityEngine;

namespace Terrain.Systems
{
    // For each region pair, produces three Bresenham edges:
    //   depth 0  base line  — corner-to-corner straight line (tag side 0)
    //   depth 1  side A     — base shifted by +PerpStep, snapped to pixels (tag side 1)
    //   depth 1  side B     — base shifted by -PerpStep, snapped to pixels (tag side 2)
    // Pixels are deduplicated across the three lines via a per-pair dictionary keyed on
    // (x, y). First write wins, so a depth-1 pixel that lands on an existing depth-0
    // pixel is skipped. Depth-1 pixels copy their height from the nearest base pixel
    // (Euclidean), depth-0 pixels take the average of the two regions' heightmaps.
    public static class EdgeWingBuilder
    {
        public static List<EdgeWingPair> Build(
            RegionBuilder.Result result,
            Dictionary<long, List<Vector2Int>> pairToCorners)
        {
            var pairs = new List<EdgeWingPair>(pairToCorners.Count);
            foreach (var kv in pairToCorners)
            {
                var p = BuildPair(result, kv.Key, kv.Value);
                if (p != null) pairs.Add(p);
            }
            return pairs;
        }

        private static EdgeWingPair BuildPair(RegionBuilder.Result result, long key, List<Vector2Int> corners)
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

            // Three Bresenham edges. Same dx/dy → same pixel count for all three.
            var baseLine  = LineRaster.Bresenham(c1, c2);
            var sideALine = LineRaster.Bresenham(c1 + perpStep, c2 + perpStep);
            var sideBLine = LineRaster.Bresenham(c1 - perpStep, c2 - perpStep);

            var regionA = result.Graph.Get(aId);
            var regionB = result.Graph.Get(bId);

            int totalCap = baseLine.Count + sideALine.Count + sideBLine.Count;
            var collection = new Dictionary<Vector2Int, WingPixel>(totalCap);

            // Step 1: base line. Height = average of the two regions' heightmaps.
            for (int i = 0; i < baseLine.Count; i++)
            {
                var p = baseLine[i];
                if (collection.ContainsKey(p)) continue;
                float h = SampleAvgHeight(regionA, regionB, p);
                collection[p] = new WingPixel { Height = h, Side = 0, Depth = 0 };
            }

            // Step 2: side A (toward region A's perpendicular direction).
            AddDepth1(sideALine, side: 1, baseLine, collection);

            // Step 3: side B (opposite direction).
            AddDepth1(sideBLine, side: 2, baseLine, collection);

            return new EdgeWingPair
            {
                RegionA = aId,
                RegionB = bId,
                CornerA = c1,
                CornerB = c2,
                PerpUnit = perp,
                PerpStep = perpStep,
                BaseLine = baseLine,
                SideALine = sideALine,
                SideBLine = sideBLine,
                Collection = collection,
            };
        }

        private static void AddDepth1(
            List<Vector2Int> line, byte side,
            List<Vector2Int> baseLine,
            Dictionary<Vector2Int, WingPixel> collection)
        {
            for (int i = 0; i < line.Count; i++)
            {
                var p = line[i];
                if (collection.ContainsKey(p)) continue;
                float h = NearestDepth0Height(p, baseLine, collection);
                collection[p] = new WingPixel { Height = h, Side = side, Depth = 1 };
            }
        }

        private static float NearestDepth0Height(
            Vector2Int p,
            List<Vector2Int> baseLine,
            Dictionary<Vector2Int, WingPixel> collection)
        {
            float bestDistSq = float.MaxValue;
            float bestH = 0f;
            for (int i = 0; i < baseLine.Count; i++)
            {
                var bp = baseLine[i];
                int dx = p.x - bp.x, dy = p.y - bp.y;
                float d = dx * dx + dy * dy;
                if (d >= bestDistSq) continue;
                if (!collection.TryGetValue(bp, out var entry) || entry.Depth != 0) continue;
                bestDistSq = d;
                bestH = entry.Height;
            }
            return bestH;
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
