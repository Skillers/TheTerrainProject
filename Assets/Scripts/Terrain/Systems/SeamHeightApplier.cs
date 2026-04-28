using Terrain.Data;
using UnityEngine;

namespace Terrain.Systems
{
    // Walks the raw multi-owner seam — every pixel where two or more regions meet, as
    // recorded in RegionBuilder's AllOwners — and replaces each touching region's
    // heightmap value at that pixel with the average across all owners. The pixel is
    // also locked so subsequent passes (EdgeWing perpendicular blending, etc.) skip it.
    //
    // After this pass, every seam pixel reads the same height from any region that
    // owns it: the seam is "agreed upon" and frozen in the regions themselves, so no
    // overlay or coordination is needed downstream.
    public static class SeamHeightApplier
    {
        public static void Apply(RegionBuilder.Result result)
        {
            int W = result.Width, H = result.Height;
            var graph = result.Graph;

            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                if (!result.IsEdgePixel(i)) continue;

                int b = RegionBuilder.OwnerSlots * i;
                int o0 = result.AllOwners[b];
                int o1 = result.AllOwners[b + 1];
                int o2 = result.AllOwners[b + 2];
                int o3 = result.AllOwners[b + 3];

                float sum = 0f; int n = 0;
                if (o0 >= 0) { sum += SampleHeight(graph.Get(o0), x, y); n++; }
                if (o1 >= 0) { sum += SampleHeight(graph.Get(o1), x, y); n++; }
                if (o2 >= 0) { sum += SampleHeight(graph.Get(o2), x, y); n++; }
                if (o3 >= 0) { sum += SampleHeight(graph.Get(o3), x, y); n++; }
                if (n == 0) continue;
                float avg = sum / n;

                if (o0 >= 0) WriteAndLock(graph.Get(o0), x, y, avg);
                if (o1 >= 0) WriteAndLock(graph.Get(o1), x, y, avg);
                if (o2 >= 0) WriteAndLock(graph.Get(o2), x, y, avg);
                if (o3 >= 0) WriteAndLock(graph.Get(o3), x, y, avg);
            }
        }

        private static float SampleHeight(Region r, int wx, int wy)
        {
            if (r == null || r.HeightMap == null) return 0f;
            var bounds = r.BoundsPx;
            int lx = wx - bounds.xMin, ly = wy - bounds.yMin;
            if (lx < 0 || lx >= bounds.width || ly < 0 || ly >= bounds.height) return 0f;
            return r.HeightMap[lx, ly];
        }

        private static void WriteAndLock(Region r, int wx, int wy, float h)
        {
            if (r == null || r.HeightMap == null) return;
            var bounds = r.BoundsPx;
            int lx = wx - bounds.xMin, ly = wy - bounds.yMin;
            if (lx < 0 || lx >= bounds.width || ly < 0 || ly >= bounds.height) return;
            if (r.HeightLock == null) r.AllocateHeightLock();
            r.HeightMap[lx, ly] = h;
            r.HeightLock[lx, ly] = true;
        }
    }
}
