using System.Collections.Generic;
using Terrain.Algorithms;
using Terrain.Core;
using Terrain.Data;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Terrain.Systems
{
    public static class RegionBuilder
    {
        public readonly struct Result
        {
            public readonly RegionGraph Graph;
            public readonly int[] PixelOwners; // row-major, length Width*Height; -1 = unassigned
            public readonly Vector2Int[] SeedPixels; // original jittered seed positions (pre-merge)
            public readonly int Width;
            public readonly int Height;

            public Result(RegionGraph g, int[] owners, Vector2Int[] seeds, int w, int h)
            {
                Graph = g; PixelOwners = owners; SeedPixels = seeds; Width = w; Height = h;
            }
        }

        public static Result Build(WorldConfig config)
        {
            int W = config.WorldWidthPx;
            int H = config.WorldHeightPx;
            int biomeSize = config.biomeSize;

            var rng = SeededRandom.ForSubsystem((uint)config.seed, "regions");

            // 1. Scatter one jittered seed per biome cell (with fill-rate dropout).
            var seedList = new List<int2>();
            for (int gy = 0; gy < config.worldSizeInBiomes.y; gy++)
            for (int gx = 0; gx < config.worldSizeInBiomes.x; gx++)
            {
                if (rng.NextFloat() > config.biomeSeedFillRate) continue;
                int minX = gx * biomeSize + 1;
                int minY = gy * biomeSize + 1;
                int maxX = minX + biomeSize - 2;
                int maxY = minY + biomeSize - 2;
                int sx = rng.NextInt(minX, maxX);
                int sy = rng.NextInt(minY, maxY);
                seedList.Add(new int2(sx, sy));
            }

            int numSeeds = seedList.Count;
            if (numSeeds == 0)
            {
                // Edge case: fill rate was so low nothing got placed. Add a single center seed.
                seedList.Add(new int2(W / 2, H / 2));
                numSeeds = 1;
            }

            var seeds = new NativeArray<int2>(numSeeds, Allocator.Temp);
            for (int i = 0; i < numSeeds; i++) seeds[i] = seedList[i];

            // 2. Run Jump Flood: each pixel gets the index of its nearest seed.
            var owner = new NativeArray<int>(W * H, Allocator.Temp);
            for (int i = 0; i < owner.Length; i++) owner[i] = -1;
            for (int i = 0; i < numSeeds; i++)
            {
                int2 p = seeds[i];
                owner[p.y * W + p.x] = i;
            }
            Voronoi.ComputeOwnership(owner, seeds, W, H);

            // 3. Union-find merge of seeds that are within seedMergeDistance.
            var uf = new UnionFind(numSeeds);
            if (config.seedMergeDistance > 0f)
            {
                float mergePx = config.seedMergeDistance * config.pixelsPerUnit;
                float mergeSq = mergePx * mergePx;
                for (int i = 0; i < numSeeds; i++)
                for (int j = i + 1; j < numSeeds; j++)
                {
                    int2 d = seeds[i] - seeds[j];
                    if (d.x * d.x + d.y * d.y < mergeSq) uf.Union(i, j);
                }
            }

            // 4. Compact root ids → 0..numRegions.
            var rootToRegion = new Dictionary<int, int>();
            var seedToRegion = new int[numSeeds];
            for (int i = 0; i < numSeeds; i++)
            {
                int root = uf.Find(i);
                if (!rootToRegion.TryGetValue(root, out int rid))
                {
                    rid = rootToRegion.Count;
                    rootToRegion[root] = rid;
                }
                seedToRegion[i] = rid;
            }
            int numRegions = rootToRegion.Count;

            int[] pixelOwners = new int[W * H];
            for (int i = 0; i < pixelOwners.Length; i++)
            {
                int s = owner[i];
                pixelOwners[i] = s >= 0 ? seedToRegion[s] : -1;
            }

            // 5. Compute axis-aligned bounds per region.
            var mins = new int2[numRegions];
            var maxs = new int2[numRegions];
            for (int r = 0; r < numRegions; r++)
            {
                mins[r] = new int2(int.MaxValue, int.MaxValue);
                maxs[r] = new int2(int.MinValue, int.MinValue);
            }
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int r = pixelOwners[y * W + x];
                if (r < 0) continue;
                if (x < mins[r].x) mins[r].x = x;
                if (y < mins[r].y) mins[r].y = y;
                if (x > maxs[r].x) maxs[r].x = x;
                if (y > maxs[r].y) maxs[r].y = y;
            }

            // Pad region bounds beyond the Voronoi cell so M4 blending has valid neighbor data
            // at the seams (avoids corner bugs and edge clipping). Pixel ownership is unchanged,
            // so the visual stays identical — adjacent regions' padded bounds simply overlap.
            int pad = config.HeightmapPaddingPx;
            var graph = new RegionGraph();
            for (int r = 0; r < numRegions; r++)
            {
                int minX = math.max(0,     mins[r].x - pad);
                int minY = math.max(0,     mins[r].y - pad);
                int maxX = math.min(W - 1, maxs[r].x + pad);
                int maxY = math.min(H - 1, maxs[r].y + pad);
                var rect = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
                graph.Add(new Region(r, rect));
            }

            // 6. Build adjacency by scanning right+down neighbors.
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int here = pixelOwners[y * W + x];
                if (here < 0) continue;
                if (x + 1 < W)
                {
                    int right = pixelOwners[y * W + x + 1];
                    if (right >= 0 && right != here) graph.AddNeighbor(here, right);
                }
                if (y + 1 < H)
                {
                    int down = pixelOwners[(y + 1) * W + x];
                    if (down >= 0 && down != here) graph.AddNeighbor(here, down);
                }
            }

            var seedPixels = new Vector2Int[numSeeds];
            for (int i = 0; i < numSeeds; i++) seedPixels[i] = new Vector2Int(seeds[i].x, seeds[i].y);

            seeds.Dispose();
            owner.Dispose();

            return new Result(graph, pixelOwners, seedPixels, W, H);
        }
    }
}
