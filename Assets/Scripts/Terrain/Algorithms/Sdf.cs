using Unity.Collections;
using Unity.Mathematics;

namespace Terrain.Algorithms
{
    // Chamfer distance transform (3-4 approximation of Euclidean distance).
    // Caller initializes: 0 at "edge" pixels, float.PositiveInfinity elsewhere.
    // After Compute(): every pixel holds its approx distance (in pixels) to the nearest edge,
    // clamped at maxDistance.
    public static class Sdf
    {
        private const float D_ORTHO = 3f;
        private const float D_DIAG  = 4f;
        private const float UNIT    = 3f;

        public static void Compute(NativeArray<float> distances, int width, int height, float maxDistance)
        {
            float scaledMax = maxDistance * UNIT;

            // Forward pass: top-left → bottom-right, read N/NW/W/NE neighbors.
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width;  x++)
            {
                int i = y * width + x;
                float best = distances[i];
                if (y > 0)
                {
                    best = math.min(best, distances[i - width] + D_ORTHO);
                    if (x > 0)         best = math.min(best, distances[i - width - 1] + D_DIAG);
                    if (x + 1 < width) best = math.min(best, distances[i - width + 1] + D_DIAG);
                }
                if (x > 0) best = math.min(best, distances[i - 1] + D_ORTHO);
                distances[i] = math.min(best, scaledMax);
            }

            // Backward pass: bottom-right → top-left, read S/SE/E/SW neighbors.
            for (int y = height - 1; y >= 0; y--)
            for (int x = width  - 1; x >= 0; x--)
            {
                int i = y * width + x;
                float best = distances[i];
                if (y + 1 < height)
                {
                    best = math.min(best, distances[i + width] + D_ORTHO);
                    if (x > 0)         best = math.min(best, distances[i + width - 1] + D_DIAG);
                    if (x + 1 < width) best = math.min(best, distances[i + width + 1] + D_DIAG);
                }
                if (x + 1 < width) best = math.min(best, distances[i + 1] + D_ORTHO);
                distances[i] = math.min(best, scaledMax);
            }

            // Normalize chamfer units (3,4) back to pixel distance.
            for (int i = 0; i < distances.Length; i++)
                distances[i] /= UNIT;
        }
    }
}
