using System.Collections.Generic;
using Terrain.Algorithms;
using Terrain.Biomes;
using Terrain.Core;
using Terrain.Data;
using Terrain.Systems;
using UnityEngine;

namespace Terrain.DebugViews
{
    // Single source of truth for all debug views.
    // Runs the full pipeline (RegionBuilder → BiomeAssigner → HeightmapBuilder → edge detection)
    // and stores the result in Data. Both RegionDebugView and HeightmapDebugView reference this
    // component and call Regenerate() to refresh everything.
    public class TerrainDataSource : MonoBehaviour
    {
        public static TerrainDataSource Instance { get; private set; }

        public WorldConfig config;
        public BiomeProfile[] biomePool;
        public bool regenerateOnStart = true;
        public bool advanceSeedEachRegenerate = false;

        public TerrainData Data { get; private set; }

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            if (regenerateOnStart) Regenerate();
        }

        [ContextMenu("Regenerate")]
        public void Regenerate()
        {
            if (config == null)
            {
                Debug.LogError("[TerrainDataSource] Assign a WorldConfig asset.", this);
                return;
            }
            config.ResolveSeed();
            if (advanceSeedEachRegenerate) config.seed++;

            var buildResult = RegionBuilder.Build(config);
            BiomeAssigner.Assign(buildResult.Graph, biomePool, config.seed);
            HeightmapBuilder.BuildAll(buildResult.Graph, config);

            List<Vector2Int> interiorCorners;
            Dictionary<long, List<Vector2Int>> pairToCorners;
            DetectCorners(buildResult, out pairToCorners, out interiorCorners);
            var seamEndpoints = ExtractSeamEndpoints(buildResult, pairToCorners);

            Data = new TerrainData
            {
                BuildResult     = buildResult,
                InteriorCorners = interiorCorners,
                PairToCorners   = pairToCorners,
                SeamEndpoints   = seamEndpoints,
            };

            Debug.Log($"[TerrainDataSource] seed={config.seed}  regions={buildResult.Graph.Count}  seams={seamEndpoints.Count}  pixels={buildResult.Width}x{buildResult.Height}", this);
        }

        // ── Corner / edge detection ────────────────────────────────────────────────

        private static void DetectCorners(RegionBuilder.Result result,
            out Dictionary<long, List<Vector2Int>> pairToCorners, out List<Vector2Int> interior)
        {
            int W = result.Width, H = result.Height;
            var dict = new Dictionary<long, List<Vector2Int>>();
            interior  = new List<Vector2Int>();
            var regs  = new List<int>(4);

            for (int y = 0; y < H - 1; y++)
            for (int x = 0; x < W - 1; x++)
            {
                int a = result.PixelOwners[y * W + x];
                int b = result.PixelOwners[y * W + x + 1];
                int c = result.PixelOwners[(y + 1) * W + x];
                int d = result.PixelOwners[(y + 1) * W + x + 1];

                regs.Clear();
                if (a >= 0)                        regs.Add(a);
                if (b >= 0 && !regs.Contains(b))   regs.Add(b);
                if (c >= 0 && !regs.Contains(c))   regs.Add(c);
                if (d >= 0 && !regs.Contains(d))   regs.Add(d);
                if (regs.Count < 3) continue;

                var pos = new Vector2Int(x + 1, y + 1);
                for (int i = 0; i < regs.Count; i++)
                for (int j = i + 1; j < regs.Count; j++)
                    AddPairCorner(dict, regs[i], regs[j], pos);

                if (!AllSameBiome(result.Graph, regs))
                    interior.Add(pos);
            }

            AddBoundaryRowCorners(result, dict, y: 0,     gy: 0);
            AddBoundaryRowCorners(result, dict, y: H - 1, gy: H);
            AddBoundaryColCorners(result, dict, x: 0,     gx: 0);
            AddBoundaryColCorners(result, dict, x: W - 1, gx: W);

            pairToCorners = dict;
        }

        private static void AddBoundaryRowCorners(RegionBuilder.Result result,
            Dictionary<long, List<Vector2Int>> dict, int y, int gy)
        {
            int W = result.Width;
            for (int x = 0; x < W - 1; x++)
            {
                int a = result.PixelOwners[y * W + x];
                int b = result.PixelOwners[y * W + x + 1];
                if (a < 0 || b < 0 || a == b) continue;
                AddPairCorner(dict, a, b, new Vector2Int(x + 1, gy));
            }
        }

        private static void AddBoundaryColCorners(RegionBuilder.Result result,
            Dictionary<long, List<Vector2Int>> dict, int x, int gx)
        {
            int W = result.Width, H = result.Height;
            for (int y = 0; y < H - 1; y++)
            {
                int a = result.PixelOwners[y * W + x];
                int b = result.PixelOwners[(y + 1) * W + x];
                if (a < 0 || b < 0 || a == b) continue;
                AddPairCorner(dict, a, b, new Vector2Int(gx, y + 1));
            }
        }

        private static List<SeamLine> ExtractSeamEndpoints(
            RegionBuilder.Result result, Dictionary<long, List<Vector2Int>> pairToCorners)
        {
            var list = new List<SeamLine>();
            foreach (var kv in pairToCorners)
            {
                int a = (int)(kv.Key >> 32);
                int b = (int)(kv.Key & 0xFFFFFFFFL);
                var biomeA = result.Graph.Get(a).Biome;
                var biomeB = result.Graph.Get(b).Biome;
                if (biomeA != null && biomeB != null && biomeA.type == biomeB.type) continue;

                var pts = kv.Value;
                if (pts.Count < 2) continue;
                var p1 = FindFurthest(pts, pts[0]);
                var p2 = FindFurthest(pts, p1);
                if (p1 == p2) continue;
                list.Add(new SeamLine(p1, p2));
            }
            return list;
        }

        private static void AddPairCorner(Dictionary<long, List<Vector2Int>> dict,
            int a, int b, Vector2Int pos)
        {
            long key = PairKey(a, b);
            if (!dict.TryGetValue(key, out var list))
                dict[key] = list = new List<Vector2Int>();
            list.Add(pos);
        }

        // ── Shared algorithms (public so RegionDebugView can call them) ───────────

        public static bool AllSameBiome(RegionGraph graph, List<int> regs)
        {
            BiomeProfile first = null;
            for (int i = 0; i < regs.Count; i++)
            {
                var biome = graph.Get(regs[i]).Biome;
                if (biome == null) return false;
                if (first == null) first = biome;
                else if (biome.type != first.type) return false;
            }
            return true;
        }

        public static long PairKey(int a, int b)
            => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        public static Vector2Int FindFurthest(List<Vector2Int> pts, Vector2Int from)
            => LineRaster.FindFurthest(pts, from);

        public static List<Vector2Int> Supercover(Vector2Int p0, Vector2Int p1)
            => LineRaster.Supercover(p0, p1);
    }
}
