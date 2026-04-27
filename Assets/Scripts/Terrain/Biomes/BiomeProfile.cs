using UnityEngine;

namespace Terrain.Biomes
{
    [CreateAssetMenu(menuName = "Terrain/Biome Profile", fileName = "BiomeProfile")]
    public sealed class BiomeProfile : ScriptableObject
    {
        [Header("Identity")]
        public string displayName = "Unnamed";
        public BiomeType type = BiomeType.Grassland;
        public Color debugColor = Color.green;

        [Header("Height")]
        public float baseHeight = 8f;
        public float heightAmplitude = 5f;
        public AnimationCurve heightCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        [Header("Noise")]
        [Min(0.0001f)] public float noiseScale = 0.05f;
        [Range(1, 8)]  public int   octaves = 4;
        [Range(0f, 1f)] public float persistence = 0.5f;
        [Min(1f)]      public float lacunarity = 2f;
        public Vector2 noiseOffset = Vector2.zero;

        [Header("Assignment bias")]
        [Range(0f, 1f)] public float preferredElevation = 0.5f;

        [Header("Rendering (wired in later milestones)")]
        public Material material;
    }
}
