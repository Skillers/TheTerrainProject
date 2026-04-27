using UnityEngine;

namespace Terrain.Biomes
{
    [CreateAssetMenu(menuName = "Terrain/Biome Pair Blend", fileName = "BiomePairBlend")]
    public sealed class BiomePairBlend : ScriptableObject
    {
        public BiomeType a;
        public BiomeType b;

        [Min(0f)] public float blendRadius = 4f;
        [Min(0f)] public float warpAmplitude = 1f;

        public bool Matches(BiomeType x, BiomeType y)
            => (x == a && y == b) || (x == b && y == a);
    }
}
