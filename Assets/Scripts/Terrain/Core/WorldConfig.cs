using UnityEngine;

namespace Terrain.Core
{
    [CreateAssetMenu(menuName = "Terrain/World Config", fileName = "WorldConfig")]
    public sealed class WorldConfig : ScriptableObject
    {
        [Header("Seed")]
        [Tooltip("Type a number or any text. Same input always produces the same map. " +
                 "Leave empty to use the numeric seed below directly.")]
        public string seedString = "";
        public int seed = 0;

        // Applies seedString → seed if seedString is set. Call before every build.
        public void ResolveSeed()
        {
            if (string.IsNullOrWhiteSpace(seedString)) return;
            seed = int.TryParse(seedString, out int n) ? n : HashString(seedString);
        }

        private static int HashString(string s)
        {
            // FNV-1a — deterministic, not locale-sensitive, works identically on all platforms.
            uint hash = 2166136261u;
            for (int i = 0; i < s.Length; i++)
            {
                hash ^= s[i];
                hash *= 16777619u;
            }
            return (int)hash;
        }

        [Header("World size")]
        public Vector2Int worldSizeInBiomes = new(16, 16);
        [Min(4)] public int biomeSize = 128;
        [Min(1)] public int pixelsPerUnit = 2;

        [Header("Voronoi")]
        [Range(0f, 1f)] public float biomeSeedFillRate = 0.9f;
        [Min(0f)] public float seedMergeDistance = 0f;

        [Header("World base layer")]
        [Min(0.0001f)] public float worldBaseScale = 0.005f;
        public float worldBaseAmplitude = 2f;

        [Header("Heightmap allocation")]
        // Each region's heightmap is allocated over a bounding box larger than its Voronoi cell,
        // so neighbors can sample into its data during the M4 blend pass. Expressed as a fraction of
        // biomeSize applied per side. 0 = no padding (strict Voronoi bounds).
        [Range(0f, 1f)] public float heightmapPaddingFraction = 0.3f;

        public int HeightmapPaddingPx => Mathf.RoundToInt(biomeSize * heightmapPaddingFraction);

        public int WorldWidthPx  => worldSizeInBiomes.x * biomeSize;
        public int WorldHeightPx => worldSizeInBiomes.y * biomeSize;
    }
}
