using System.Collections.Generic;

namespace Terrain.Data
{
    public sealed class RegionGraph
    {
        private readonly List<Region> _regions = new();
        public IReadOnlyList<Region> Regions => _regions;
        public int Count => _regions.Count;

        public Region Add(Region r)
        {
            _regions.Add(r);
            return r;
        }

        public Region Get(int id) => _regions[id];

        public void AddNeighbor(int a, int b)
        {
            if (a == b) return;
            _regions[a].AddNeighbor(b);
            _regions[b].AddNeighbor(a);
        }
    }
}
