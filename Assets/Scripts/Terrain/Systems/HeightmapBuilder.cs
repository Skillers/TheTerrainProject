using Terrain.Algorithms;
using Terrain.Core;
using Terrain.Data;
using Unity.Mathematics;

namespace Terrain.Systems
{
    // M3 — fills each region's HeightMap over its (padded) BoundsPx. Two contributions per pixel:
    //   1. Per-biome FBM, sampled in world coords → biome.heightCurve → biome.heightAmplitude + biome.baseHeight.
    //   2. Shared world-base FBM (low frequency, single global offset for the seed) → worldBaseAmplitude.
    // Sampling in world coords means two regions of the same biome produce continuous patterns
    // where their padded boxes overlap — that's the M4 input. No blending here; seams at biome
    // transitions are intentional and load-bearing for the M3 visual exit criterion.
    public static class HeightmapBuilder
    {
        public static void Build(Region region, float2 worldBaseOffset, WorldConfig config)
        {
            if (region.Biome == null) return;
            region.AllocateHeightMap();

            var biome = region.Biome;
            var bounds = region.BoundsPx;
            float invPpu = 1f / config.pixelsPerUnit;
            float2 biomeOffset = new float2(biome.noiseOffset.x, biome.noiseOffset.y);

            for (int y = 0; y < bounds.height; y++)
            for (int x = 0; x < bounds.width; x++)
            {
                float wx = (bounds.xMin + x) * invPpu;
                float wy = (bounds.yMin + y) * invPpu;

                float n01 = FbmNoise.Sample01(wx, wy, biome.octaves, biome.noiseScale,
                                              biome.persistence, biome.lacunarity, biomeOffset);
                float biomeHeight = biome.baseHeight + biome.heightCurve.Evaluate(n01) * biome.heightAmplitude;

                float worldBase = FbmNoise.Sample(wx, wy, 4, config.worldBaseScale,
                                                  0.5f, 2f, worldBaseOffset) * config.worldBaseAmplitude;

                region.HeightMap[x, y] = biomeHeight + worldBase;
            }
        }

        public static void BuildAll(RegionGraph graph, WorldConfig config)
        {
            var rng = SeededRandom.ForSubsystem((uint)config.seed, "heightmap");
            float2 worldBaseOffset = new float2(rng.NextFloat() * 1000f, rng.NextFloat() * 1000f);
            for (int i = 0; i < graph.Count; i++)
                Build(graph.Get(i), worldBaseOffset, config);
        }
    }
}
