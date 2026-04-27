using Unity.Mathematics;

namespace Terrain.Algorithms
{
    // Fractal Brownian Motion — sum of Perlin noise octaves. Each octave doubles frequency
    // (lacunarity) and halves amplitude (persistence) by default, yielding natural-looking terrain.
    public static class FbmNoise
    {
        // Returns roughly [-1, 1].
        public static float Sample(float x, float y, int octaves, float scale, float persistence, float lacunarity, float2 offset)
        {
            float sum   = 0f;
            float amp   = 1f;
            float freq  = scale;
            float ampSum = 0f;

            for (int i = 0; i < octaves; i++)
            {
                float sx = (x + offset.x) * freq;
                float sy = (y + offset.y) * freq;
                sum    += noise.snoise(new float2(sx, sy)) * amp;
                ampSum += amp;
                amp    *= persistence;
                freq   *= lacunarity;
            }

            return ampSum > 0f ? sum / ampSum : 0f;
        }

        // Returns [0, 1]. Convenient for driving BiomeProfile.heightCurve.Evaluate(...).
        public static float Sample01(float x, float y, int octaves, float scale, float persistence, float lacunarity, float2 offset)
        {
            return math.saturate(0.5f + 0.5f * Sample(x, y, octaves, scale, persistence, lacunarity, offset));
        }
    }
}
