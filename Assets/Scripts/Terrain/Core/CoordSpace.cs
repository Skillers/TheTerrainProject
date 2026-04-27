using Unity.Mathematics;

namespace Terrain.Core
{
    // Three coordinate spaces exist in this system:
    //   world units    — float, what Unity sees (Transform.position)
    //   world pixels   — int, heightmap grid across the whole world
    //   region pixels  — int, heightmap grid local to one region (origin = region bounds min)
    public static class CoordSpace
    {
        public static int2 WorldToPixel(float2 worldUnits, int pixelsPerUnit)
        {
            return new int2(
                (int)math.floor(worldUnits.x * pixelsPerUnit),
                (int)math.floor(worldUnits.y * pixelsPerUnit)
            );
        }

        public static float2 PixelToWorld(int2 worldPixel, int pixelsPerUnit)
        {
            float inv = 1f / pixelsPerUnit;
            return new float2(worldPixel.x * inv, worldPixel.y * inv);
        }

        public static int2 WorldPixelToRegionLocal(int2 worldPixel, int2 regionMinPixel)
        {
            return worldPixel - regionMinPixel;
        }

        public static int2 RegionLocalToWorldPixel(int2 regionLocal, int2 regionMinPixel)
        {
            return regionLocal + regionMinPixel;
        }

        public static float2 RegionLocalToWorld(int2 regionLocal, int2 regionMinPixel, int pixelsPerUnit)
        {
            return PixelToWorld(RegionLocalToWorldPixel(regionLocal, regionMinPixel), pixelsPerUnit);
        }
    }
}
