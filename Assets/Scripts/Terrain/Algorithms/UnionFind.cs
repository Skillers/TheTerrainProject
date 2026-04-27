namespace Terrain.Algorithms
{
    // Weighted quick-union with path halving. ~O(α(n)) per op.
    // Used to merge Voronoi seeds that are closer than WorldConfig.seedMergeDistance.
    public struct UnionFind
    {
        private readonly int[] _parent;
        private readonly byte[] _rank;

        public int Count => _parent.Length;

        public UnionFind(int count)
        {
            _parent = new int[count];
            _rank   = new byte[count];
            for (int i = 0; i < count; i++) _parent[i] = i;
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]]; // path halving
                x = _parent[x];
            }
            return x;
        }

        public bool Union(int a, int b)
        {
            int ra = Find(a);
            int rb = Find(b);
            if (ra == rb) return false;
            if (_rank[ra] < _rank[rb]) (ra, rb) = (rb, ra);
            _parent[rb] = ra;
            if (_rank[ra] == _rank[rb]) _rank[ra]++;
            return true;
        }
    }
}
