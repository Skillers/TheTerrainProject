using System.Collections.Generic;
using Terrain.Data;
using UnityEngine;

namespace Terrain.Systems
{
    // Stamps wing heights into region heightmaps. Two phases so every region touching a
    // given cell gets the same height — fixes the "same corner, different height in
    // different quads" tearing that happens when each pair writes only to its own A/B
    // pair last-write-wins style.
    //
    //   1. Merge:  walk every wing, accumulate one height per cell. If multiple wings
    //              cover the same cell (cells inside two seams' bands at once), keep the
    //              value from the wing with the smallest Depth — that's the wing whose
    //              seam is closest to the cell, so its value is the more authoritative
    //              one.
    //   2. Apply:  for each merged cell, write the height to every region whose AllOwners
    //              entry includes the cell. This includes single-owner cells (the cell's
    //              one Voronoi region) and multi-owner seam/junction cells (every region
    //              touching the seam). Pixels locked by SeamHeightApplier are skipped —
    //              those already agree across all touching regions, no need to overwrite.
    public static class EdgeWingApplier
    {
        public static void Apply(List<EdgeWingPair> pairs, RegionBuilder.Result result)
        {
            if (pairs == null) return;

            // Phase 1: merge per-cell heights from all wings.
            var merged = new Dictionary<Vector2Int, (float Height, byte Depth)>();
            for (int i = 0; i < pairs.Count; i++)
            {
                var pair = pairs[i];
                if (pair?.Collection == null) continue;
                foreach (var kv in pair.Collection)
                {
                    var p = kv.Key;
                    var entry = kv.Value;
                    if (merged.TryGetValue(p, out var existing))
                    {
                        if (entry.Depth < existing.Depth)
                            merged[p] = (entry.Height, entry.Depth);
                    }
                    else
                    {
                        merged[p] = (entry.Height, entry.Depth);
                    }
                }
            }

            // Phase 2: apply each merged height to every region that touches the cell.
            int W = result.Width;
            foreach (var kv in merged)
            {
                var p = kv.Key;
                float h = kv.Value.Height;
                int idx = p.y * W + p.x;
                int b = RegionBuilder.OwnerSlots * idx;
                for (int s = 0; s < RegionBuilder.OwnerSlots; s++)
                {
                    int oId = result.AllOwners[b + s];
                    if (oId < 0) continue;
                    Write(result.Graph.Get(oId), p, h);
                }
            }
        }

        private static void Write(Region r, Vector2Int p, float h)
        {
            if (r == null || r.HeightMap == null) return;
            var bounds = r.BoundsPx;
            int lx = p.x - bounds.xMin;
            int ly = p.y - bounds.yMin;
            if (lx < 0 || lx >= bounds.width || ly < 0 || ly >= bounds.height) return;
            if (r.HeightLock != null && r.HeightLock[lx, ly]) return;
            r.HeightMap[lx, ly] = h;
        }
    }
}
