using System.Collections.Generic;
using Terrain.Data;
using Terrain.Systems;
using UnityEngine;

namespace Terrain.DebugViews
{
    public struct SeamLine
    {
        public Vector2Int p1;
        public Vector2Int p2;
        public SeamLine(Vector2Int p1, Vector2Int p2) { this.p1 = p1; this.p2 = p2; }
    }

    // Shared pipeline output consumed by all debug views.
    public class TerrainData
    {
        public RegionBuilder.Result BuildResult;

        // Grid-corner coords of every Voronoi vertex where ≥3 regions meet and at least
        // one biome boundary is present. Used by RegionDebugView for corner dots.
        public List<Vector2Int> InteriorCorners;

        // All endpoint sets per region pair in grid-corner coords.
        // Key = PairKey(a, b). Used by RegionDebugView for edge rendering.
        public Dictionary<long, List<Vector2Int>> PairToCorners;

        // Supercover endpoints for every different-biome region pair.
        // Used by HeightmapDebugView to build its Y=0 seam mesh.
        public List<SeamLine> SeamEndpoints;

        // Pixel chain produced by Bresenham/supercover between the two extreme corner
        // points of each pair. Straight-line approximation of the seam.
        public Dictionary<long, List<Vector2Int>> PairToSupercoverPixels;

        // Real jagged seam: each entry is a unit-length wall segment in grid-corner coords
        // where two 4-adjacent pixels belong to different regions of the pair.
        public Dictionary<long, List<SeamLine>> PairToBoundaryWalls;

        // Per-pair perpendicular blend bands. Built BEFORE SeamHeightApplier modifies the
        // heightmaps so each cell's avg-pixel sample is the raw biome FBM output, not the
        // already-locked seam value.
        public List<EdgeWingPair> WingPairs;
    }
}
