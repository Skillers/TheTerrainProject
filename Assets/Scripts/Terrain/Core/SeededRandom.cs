using Unity.Mathematics;

namespace Terrain.Core
{
    public struct SeededRandom
    {
        private Random _rng;

        public SeededRandom(uint seed)
        {
            _rng = new Random(seed == 0u ? 1u : seed);
        }

        public static SeededRandom ForSubsystem(uint worldSeed, string subsystem)
        {
            return new SeededRandom(Hash(worldSeed, subsystem));
        }

        public uint NextUInt() => _rng.NextUInt();
        public int NextInt() => _rng.NextInt();
        public int NextInt(int min, int max) => _rng.NextInt(min, max);
        public float NextFloat() => _rng.NextFloat();
        public float NextFloat(float min, float max) => _rng.NextFloat(min, max);
        public float2 NextFloat2() => _rng.NextFloat2();
        public float2 NextFloat2(float2 min, float2 max) => _rng.NextFloat2(min, max);

        // FNV-1a style mix. Deterministic, no dependency on string hashing (which varies across runtimes).
        public static uint Hash(uint worldSeed, string subsystem)
        {
            uint h = 2166136261u;
            h ^= worldSeed;
            h *= 16777619u;
            for (int i = 0; i < subsystem.Length; i++)
            {
                h ^= subsystem[i];
                h *= 16777619u;
            }
            return h == 0u ? 1u : h;
        }
    }
}
