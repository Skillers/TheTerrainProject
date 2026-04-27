using System.Collections.Generic;
using Terrain.Biomes;

namespace Terrain.Data
{
    public sealed class World
    {
        public RegionGraph Graph { get; }
        public IReadOnlyList<Region> Regions => Graph.Regions;

        public World(RegionGraph graph)
        {
            Graph = graph;
        }

        // Filled in at M6 once WorldGenerator builds a pixel→region lookup.
        public float SampleHeight(float worldX, float worldZ) => 0f;
        public BiomeType SampleBiome(float worldX, float worldZ) => default;
    }
}
