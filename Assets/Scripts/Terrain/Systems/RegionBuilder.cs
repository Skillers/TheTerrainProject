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
        // A pixel has 4 edge-neighbours and so borders at most 4 distinct regions —
        // its own and up to 3 others. We store ownership as a symmetric 4-slot strip
        // per pixel: every region touching the pixel goes into the same array, with
        // no preferred order or "primary" designation. Equal owners.
        public const int OwnerSlots = 4;

        public readonly struct Result
        {
            public readonly RegionGraph Graph;

            // Symmetric multi-ownership. AllOwners[4*i + 0..3] hold every region that
            // touches pixel i (including its own Voronoi cell). Slots are filled left
            // to right; unused slots are -1. No slot is special — interior pixels
            // happen to have only slot 0 set, and edge pixels fill 2-4 slots, all
            // equally valid owners. -1 in slot 0 means the pixel is unassigned.
            public readonly int[] AllOwners; // length 4*Width*Height

            // Convenience view for single-owner consumers (mesh assignment, bounds, etc.).
            // PixelOwners[i] == AllOwners[4*i]; it's exactly slot 0, NOT a designated
            // primary. For edge pixels every entry in AllOwners[4*i..4*i+3] is equally
            // an owner — new code that does generation/blending should read AllOwners.
            public readonly int[] PixelOwners; // length Width*Height; -1 = unassigned

            public readonly Vector2Int[] SeedPixels; // original jittered seed positions (pre-merge)
            public readonly int Width;
            public readonly int Height;

            public Result(RegionGraph g, int[] allOwners, int[] pixelOwners,
                Vector2Int[] seeds, int w, int h)
            {
                Graph = g; AllOwners = allOwners; PixelOwners = pixelOwners;
                SeedPixels = seeds; Width = w; Height = h;
            }

            // Total number of regions touching pixel index i. 0 if unassigned.
            public int OwnerCount(int i)
            {
                int b = OwnerSlots * i, n = 0;
                if (AllOwners[b]     >= 0) n++;
                if (AllOwners[b + 1] >= 0) n++;
                if (AllOwners[b + 2] >= 0) n++;
                if (AllOwners[b + 3] >= 0) n++;
                return n;
            }

            public bool PixelTouchesRegion(int i, int regionId)
            {
                int b = OwnerSlots * i;
                return AllOwners[b]     == regionId
                    || AllOwners[b + 1] == regionId
                    || AllOwners[b + 2] == regionId
                    || AllOwners[b + 3] == regionId;
            }

            public bool IsEdgePixel(int i) => AllOwners[OwnerSlots * i + 1] >= 0;
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

            // 4b. Multi-ownership pass. For each pixel, list every region touching it
            // through a shared edge (4-conn) — its own Voronoi cell goes in slot 0, then
            // any distinct neighbouring regions fill the next slots. All slots are equal
            // owners; slot 0 is just where the own region happens to land, not a primary.
            // Capped at 4 slots per pixel since a pixel has 4 edges (4 distinct regions max).
            int[] allOwners = new int[OwnerSlots * W * H];
            for (int i = 0; i < allOwners.Length; i++) allOwners[i] = -1;

            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int idx  = y * W + x;
                int self = pixelOwners[idx];
                if (self < 0) continue;

                int slotBase = OwnerSlots * idx;
                allOwners[slotBase] = self;
                int slot = 1;

                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    // 4-conn: skip self (0,0) and diagonals (|dx|==|dy|==1).
                    if ((dx == 0) == (dy == 0)) continue;
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)W || (uint)ny >= (uint)H) continue;
                    int cand = pixelOwners[ny * W + nx];
                    if (cand < 0 || cand == self) continue;
                    bool dup = false;
                    for (int k = 1; k < slot; k++) if (allOwners[slotBase + k] == cand) { dup = true; break; }
                    if (!dup && slot < OwnerSlots) allOwners[slotBase + slot++] = cand;
                }
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
                int minX = mins[r].x - pad;
                int minY = mins[r].y - pad;
                int maxX = maxs[r].x + pad;
                int maxY = maxs[r].y + pad;
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

            return new Result(graph, allOwners, pixelOwners, seedPixels, W, H);
        }
    }
}
