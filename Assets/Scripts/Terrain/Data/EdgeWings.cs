using System.Collections.Generic;
using UnityEngine;

namespace Terrain.Data
{
    // Per-pixel metadata stored in the deduplicated collection.
    public struct WingPixel
    {
        public float Height;
        public byte Side;   // 0 = base line, 1 = side A, 2 = side B
        public byte Depth;  // 0 = base, 1 = first ring of perpendicular shifts
    }

    // One region pair's edge data: the corner-to-corner straight line rasterized via
    // Bresenham (BaseLine, depth 0), plus two more Bresenham edges shifted one
    // perpendicular unit into each region (SideALine and SideBLine, depth 1). The
    // Collection holds one entry per UNIQUE pixel across all three lines — first
    // write wins, so a depth-1 pixel that lands on a depth-0 pixel is skipped.
    public class EdgeWingPair
    {
        public int RegionA;
        public int RegionB;

        public Vector2Int CornerA; // chosen extreme corner in corner coords
        public Vector2Int CornerB; // the other extreme

        public Vector2 PerpUnit;   // unit perpendicular to (CornerB - CornerA)
        public Vector2Int PerpStep; // PerpUnit snapped to integer step (RoundToInt)

        public List<Vector2Int> BaseLine;   // Bresenham(CornerA, CornerB)
        public List<Vector2Int> SideALine;  // Bresenham(CornerA + PerpStep, CornerB + PerpStep)
        public List<Vector2Int> SideBLine;  // Bresenham(CornerA - PerpStep, CornerB - PerpStep)

        public Dictionary<Vector2Int, WingPixel> Collection;
    }
}
