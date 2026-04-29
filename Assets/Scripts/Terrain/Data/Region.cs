using System.Collections.Generic;
using Terrain.Biomes;
using UnityEngine;

namespace Terrain.Data
{
    public sealed class Region
    {
        public int Id { get; }
        public RectInt BoundsPx { get; }
        // Canonical seed pixel for this region — the lowest-index original Voronoi
        // seed that mapped to this region (after seedMergeDistance unions). Used by
        // post-Voronoi local re-rasterization, e.g. corner-merge pixel reshape.
        public Vector2Int SeedPx { get; set; }
        public BiomeProfile Biome { get; set; }            // assigned in M2
        public float[,] HeightMap { get; private set; }    // populated in M3
        public bool[,]  HeightLock { get; private set; }   // pixels that later passes must not overwrite
        public float[,] Sdf { get; private set; }          // populated in M4
        public byte[,] BiomeWeightsPacked { get; private set; } // populated in M4 blend

        private readonly List<int> _neighbors = new();
        public IReadOnlyList<int> Neighbors => _neighbors;

        public Region(int id, RectInt boundsPx)
        {
            Id = id;
            BoundsPx = boundsPx;
        }

        public void AllocateHeightMap() =>
            HeightMap = new float[BoundsPx.width, BoundsPx.height];

        public void AllocateHeightLock() =>
            HeightLock = new bool[BoundsPx.width, BoundsPx.height];

        public void AllocateSdf() =>
            Sdf = new float[BoundsPx.width, BoundsPx.height];

        public void AllocateBiomeWeights() =>
            BiomeWeightsPacked = new byte[BoundsPx.width, BoundsPx.height];

        public void AddNeighbor(int neighborId)
        {
            if (neighborId == Id) return;
            if (!_neighbors.Contains(neighborId)) _neighbors.Add(neighborId);
        }
    }
}
