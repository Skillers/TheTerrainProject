using Terrain.Biomes;
using Terrain.Core;
using Terrain.Data;

namespace Terrain.Systems
{
    // Phase B — assigns a BiomeProfile to each region. For now: uniform random from the pool.
    // Deterministic for a given (worldSeed, pool) pair. Elevation-banded / Whittaker strategies
    // can be added later without changing the call site.
    public static class BiomeAssigner
    {
        public static void Assign(RegionGraph graph, BiomeProfile[] pool, int worldSeed)
        {
            if (graph == null || pool == null || pool.Length == 0) return;
            var rng = SeededRandom.ForSubsystem((uint)worldSeed, "biomes");
            for (int i = 0; i < graph.Count; i++)
            {
                int pick = rng.NextInt(0, pool.Length);
                graph.Get(i).Biome = pool[pick];
            }
        }
    }
}
