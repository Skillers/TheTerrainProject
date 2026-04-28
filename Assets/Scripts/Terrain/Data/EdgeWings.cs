using System.Collections.Generic;
using UnityEngine;

namespace Terrain.Data
{
    // Per-pixel metadata stored in the deduplicated collection.
    public struct WingPixel
    {
        public float Height;
        public byte Side;   // 0 = on the seam line, 1 = +perp side, 2 = -perp side
        public byte Depth;  // perpendicular distance in pixels, clamped to [0, maxDepth]
    }

    // One region pair's edge data, rasterized cell-by-cell over a band of half-width
    // maxDepth around the line from CornerA to CornerB. The Collection is keyed on
    // integer corner positions — exactly the same grid the heightmap mesh samples —
    // so a wing pixel and a terrain pixel at the same (x, y) line up perfectly.
    public class EdgeWingPair
    {
        public int RegionA;
        public int RegionB;

        public Vector2Int CornerA; // chosen extreme corner in corner coords
        public Vector2Int CornerB; // the other extreme

        public Vector2 PerpUnit;   // unit perpendicular to (CornerB - CornerA)

        public Dictionary<Vector2Int, WingPixel> Collection;
    }
}
