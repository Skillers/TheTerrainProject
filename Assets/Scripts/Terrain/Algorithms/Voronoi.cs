using Unity.Collections;
using Unity.Mathematics;

namespace Terrain.Algorithms
{
    // Jump Flood Algorithm: computes, for every pixel, the index of its nearest seed.
    // O(W*H * log(max(W,H))). Not Burst-wrapped yet — that's a later optimization.
    public static class Voronoi
    {
        // owner[]: length W*H, row-major. On input, seed pixels must hold their seed index;
        //          all other pixels must hold -1. On output, every pixel holds its owner's index.
        // seedPositions[seedId]: pixel coord of that seed.
        public static void ComputeOwnership(
            NativeArray<int> owner,
            NativeArray<int2> seedPositions,
            int width, int height)
        {
            int maxDim = math.max(width, height);
            int step = 1;
            while (step * 2 < maxDim) step *= 2;

            var scratch = new NativeArray<int>(owner.Length, Allocator.Temp);

            while (step >= 1)
            {
                scratch.CopyFrom(owner);
                JfaStep(owner, scratch, seedPositions, width, height, step);
                step /= 2;
            }

            scratch.Dispose();
        }

        private static void JfaStep(
            NativeArray<int> output,
            NativeArray<int> input,
            NativeArray<int2> seeds,
            int width, int height, int step)
        {
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width;  x++)
            {
                int2 here = new int2(x, y);
                int bestOwner = input[y * width + x];
                int bestDist  = bestOwner >= 0 ? DistSq(here, seeds[bestOwner]) : int.MaxValue;

                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx * step;
                    int ny = y + dy * step;
                    if ((uint)nx >= (uint)width || (uint)ny >= (uint)height) continue;

                    int cand = input[ny * width + nx];
                    if (cand < 0) continue;

                    int d = DistSq(here, seeds[cand]);
                    if (d < bestDist)
                    {
                        bestDist  = d;
                        bestOwner = cand;
                    }
                }

                output[y * width + x] = bestOwner;
            }
        }

        private static int DistSq(int2 a, int2 b)
        {
            int dx = a.x - b.x;
            int dy = a.y - b.y;
            return dx * dx + dy * dy;
        }
    }
}
