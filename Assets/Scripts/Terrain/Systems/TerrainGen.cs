using System;
using System.Collections.Generic;
using Terrain.Algorithms;
using Terrain.Biomes;
using Terrain.Core;
using Terrain.Data;
using UnityEngine;

namespace Terrain.Systems
{
    // Pipeline owner. Runs the full terrain build sequentially in one synchronous pass —
    //   RegionBuilder → BiomeAssigner → HeightmapBuilder → DetectCorners
    //                 → EdgeWingBuilder.Build → Smooth
    //                 → SeamHeightApplier → EdgeWingApplier
    //                 → seam endpoints / supercover / boundary walls
    // and stores the result in Data, then fires OnRegenerated. Views subscribe to that
    // event and rebuild their visuals from the published Data — they never trigger the
    // pipeline themselves, so each Regenerate runs exactly once and every view sees the
    // same fully-finished state.
    public class TerrainGen : MonoBehaviour
    {
        public static TerrainGen Instance { get; private set; }

        public WorldConfig config;
        public BiomeProfile[] biomePool;
        public bool regenerateOnStart = true;
        public bool advanceSeedEachRegenerate = false;

        [Header("Wings")]
        [Tooltip("Half-width of each pair's perpendicular blend band, in pixels. Built BEFORE SeamHeightApplier so the avg-pixel reads see raw biome heights.")]
        [Min(1)] public int wingMaxDepth = 2;
        [Tooltip("Per-pass Jacobi smoothing strength on the wing collection — 0.1 means each cell pulls 10% of the height diff from each 4-neighbour. Set to 0 to disable.")]
        [Range(0f, 0.25f)] public float wingSmoothStrength = 0.1f;
        [Min(0)] public int wingSmoothPasses = 1;
        [Tooltip("Stamp wing heights into the region heightmaps after the wings are built — terrain mesh picks them up directly.")]
        public bool applyWingsToHeightmap = true;

        public TerrainData Data { get; private set; }

        // Fired AFTER Data is fully populated. Views subscribe in OnEnable and rebuild
        // their meshes/gizmos from Data when this fires. The pipeline is finished by the
        // time any subscriber runs.
        public event Action OnRegenerated;

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
                Debug.LogError("[TerrainGen] Assign a WorldConfig asset.", this);
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

            // Collapse close-by corner detections (multiple 2x2 blocks around a single
            // Voronoi vertex) into one centroid each, so every pair that shares a vertex
            // sees the same merged position downstream. Phase 2 re-rasterizes the pixel
            // ownership in each merged cluster's window so the actual region boundaries
            // honor the merge — thin spurs caused by close-but-distinct vertices get
            // absorbed into the closest neighbor's seed.
            float cornerMergePx = config.cornerMergeDistance * config.pixelsPerUnit;
            var cornerClusters = MergeCloseCorners(pairToCorners, interiorCorners, cornerMergePx);
            ReshapePixelsAroundClusters(buildResult, cornerClusters);

            // Wings sample the heightmaps for their avg-pixel snapshot. They MUST run
            // before SeamHeightApplier overwrites multi-owner pixels with the seam avg —
            // otherwise the side-blend reads the locked avg instead of the side region's
            // raw FBM noise.
            var wingPairs = EdgeWingBuilder.Build(buildResult, pairToCorners, wingMaxDepth);
            EdgeWingBuilder.Smooth(wingPairs, wingSmoothStrength, wingSmoothPasses);

            SeamHeightApplier.Apply(buildResult);

            // Now stamp the (possibly-smoothed) wing heights into the regions' heightmaps,
            // overwriting whatever was there — no blending. The terrain mesh will pick
            // these up directly on its next sample.
            if (applyWingsToHeightmap)
                EdgeWingApplier.Apply(wingPairs, buildResult);

            var seamEndpoints   = ExtractSeamEndpoints(buildResult, pairToCorners);
            var pairToSupercov  = BuildSupercoverPixels(pairToCorners);
            var pairToWalls     = BuildBoundaryWalls(buildResult);

            Data = new TerrainData
            {
                BuildResult            = buildResult,
                InteriorCorners        = interiorCorners,
                PairToCorners          = pairToCorners,
                SeamEndpoints          = seamEndpoints,
                PairToSupercoverPixels = pairToSupercov,
                PairToBoundaryWalls    = pairToWalls,
                WingPairs              = wingPairs,
            };

            Debug.Log($"[TerrainGen] seed={config.seed}  regions={buildResult.Graph.Count}  seams={seamEndpoints.Count}  wings={wingPairs.Count}  pixels={buildResult.Width}x{buildResult.Height}", this);

            OnRegenerated?.Invoke();
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

        // One merge cluster's worth of state. Centroid + radius defines the reshape window;
        // Regions is the union of pair regions across every original corner that fell into
        // this cluster (from pairToCorners), so the local re-Voronoi knows exactly which
        // seeds to consider when reassigning pixels in the window.
        private class CornerCluster
        {
            public Vector2Int Centroid;
            public int RadiusPx;
            public int MemberCount;
            public HashSet<int> Regions = new HashSet<int>();
        }

        // Phase 1: single-link clustering of close corners. Any two within `mergeRadiusPx`
        // collapse, chained transitively. Each cluster's int-rounded centroid replaces every
        // member position in pairToCorners and interiorCorners (per-pair lists deduped),
        // so duplicate detections of one Voronoi vertex stop confusing `FindFurthest`
        // downstream. Returns one CornerCluster per multi-member cluster — those are the
        // ones the pixel reshape pass needs to act on.
        private static List<CornerCluster> MergeCloseCorners(
            Dictionary<long, List<Vector2Int>> pairToCorners,
            List<Vector2Int> interior,
            float mergeRadiusPx)
        {
            if (mergeRadiusPx <= 0f) return null;

            // Every interior corner also lives in pairToCorners (added in the same loop),
            // so enumerating pair lists picks up all positions we need to cluster.
            var posIndex = new Dictionary<Vector2Int, int>();
            var positions = new List<Vector2Int>();
            foreach (var list in pairToCorners.Values)
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (posIndex.ContainsKey(p)) continue;
                posIndex[p] = positions.Count;
                positions.Add(p);
            }
            if (positions.Count < 2) return null;

            var uf = new UnionFind(positions.Count);
            float radiusSq = mergeRadiusPx * mergeRadiusPx;
            for (int i = 0; i < positions.Count; i++)
            for (int j = i + 1; j < positions.Count; j++)
            {
                int dx = positions[i].x - positions[j].x;
                int dy = positions[i].y - positions[j].y;
                if (dx * dx + dy * dy <= radiusSq) uf.Union(i, j);
            }

            // Centroid + member count per root.
            var sumByRoot = new Dictionary<int, (long sx, long sy, int n)>();
            for (int i = 0; i < positions.Count; i++)
            {
                int r = uf.Find(i);
                if (sumByRoot.TryGetValue(r, out var s))
                    sumByRoot[r] = (s.sx + positions[i].x, s.sy + positions[i].y, s.n + 1);
                else
                    sumByRoot[r] = (positions[i].x, positions[i].y, 1);
            }

            var rootToCluster = new Dictionary<int, CornerCluster>(sumByRoot.Count);
            foreach (var kv in sumByRoot)
            {
                int n = kv.Value.n;
                int cx = (int)((kv.Value.sx + n / 2) / n);
                int cy = (int)((kv.Value.sy + n / 2) / n);
                rootToCluster[kv.Key] = new CornerCluster
                {
                    Centroid = new Vector2Int(cx, cy),
                    MemberCount = n,
                };
            }

            // Per-cluster radius = max chebyshev-rounded euclid from centroid to any member.
            for (int i = 0; i < positions.Count; i++)
            {
                int r = uf.Find(i);
                var c = rootToCluster[r];
                int dx = positions[i].x - c.Centroid.x;
                int dy = positions[i].y - c.Centroid.y;
                int dsq = dx * dx + dy * dy;
                int rad = (int)System.Math.Ceiling(System.Math.Sqrt(dsq));
                if (rad > c.RadiusPx) c.RadiusPx = rad;
            }

            // Per-cluster region set — built BEFORE we rewrite pairToCorners. A 3-region
            // 2x2 block contributes the same position to AB, AC, BC, so walking pair keys
            // and their corner lists picks up every region that touches each cluster.
            foreach (var kv in pairToCorners)
            {
                int a = (int)(kv.Key >> 32);
                int b = (int)(kv.Key & 0xFFFFFFFFL);
                var list = kv.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    int posIdx = posIndex[list[i]];
                    var c = rootToCluster[uf.Find(posIdx)];
                    c.Regions.Add(a);
                    c.Regions.Add(b);
                }
            }

            // Map every original position → its cluster centroid.
            var posToCentroid = new Dictionary<Vector2Int, Vector2Int>(positions.Count);
            for (int i = 0; i < positions.Count; i++)
                posToCentroid[positions[i]] = rootToCluster[uf.Find(i)].Centroid;

            var keys = new List<long>(pairToCorners.Keys);
            for (int k = 0; k < keys.Count; k++)
            {
                var list = pairToCorners[keys[k]];
                var seen = new HashSet<Vector2Int>();
                var newList = new List<Vector2Int>(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    var merged = posToCentroid[list[i]];
                    if (seen.Add(merged)) newList.Add(merged);
                }
                pairToCorners[keys[k]] = newList;
            }

            var interiorSeen = new HashSet<Vector2Int>();
            var newInterior = new List<Vector2Int>(interior.Count);
            for (int i = 0; i < interior.Count; i++)
            {
                if (!posToCentroid.TryGetValue(interior[i], out var merged)) merged = interior[i];
                if (interiorSeen.Add(merged)) newInterior.Add(merged);
            }
            interior.Clear();
            interior.AddRange(newInterior);

            // Only multi-member clusters need reshape — single-member clusters had nothing
            // to merge, so their underlying pixel ownership is already consistent.
            var multiMember = new List<CornerCluster>();
            foreach (var kv in rootToCluster)
                if (kv.Value.MemberCount > 1 && kv.Value.Regions.Count >= 2)
                    multiMember.Add(kv.Value);
            return multiMember;
        }

        // Phase 2: local re-Voronoi inside each merged cluster's window. For every pixel
        // in (centroid ± radius+buffer), if the pixel is currently owned by a region in
        // the cluster's region set, reassign it to the closest involved region's seed.
        // Pixels owned by regions OUTSIDE the cluster set are left alone — those aren't
        // ours to touch. After reassignment, AllOwners is recomputed for the changed
        // pixels and their 4-neighbors (since AllOwners depends on neighbor PixelOwners).
        //
        // Effect: the thin "isthmus" pixels that produced the duplicate corner detections
        // get absorbed into whichever neighbor's seed is closer, so the regions actually
        // meet at the merged centroid instead of just claiming to.
        private const int CornerReshapeBufferPx = 2;
        private static void ReshapePixelsAroundClusters(
            RegionBuilder.Result result, List<CornerCluster> clusters)
        {
            if (clusters == null || clusters.Count == 0) return;

            int W = result.Width, H = result.Height;
            var changed = new HashSet<int>();

            for (int ci = 0; ci < clusters.Count; ci++)
            {
                var cluster = clusters[ci];
                if (cluster.Regions.Count < 2) continue;

                // Snapshot involved seed positions once per cluster.
                var seedRegion = new List<int>(cluster.Regions.Count);
                var seedX = new List<int>(cluster.Regions.Count);
                var seedY = new List<int>(cluster.Regions.Count);
                foreach (int rid in cluster.Regions)
                {
                    var region = result.Graph.Get(rid);
                    if (region == null) continue;
                    seedRegion.Add(rid);
                    seedX.Add(region.SeedPx.x);
                    seedY.Add(region.SeedPx.y);
                }
                if (seedRegion.Count < 2) continue;

                int radius = cluster.RadiusPx + CornerReshapeBufferPx;
                int xMin = Mathf.Max(0, cluster.Centroid.x - radius);
                int yMin = Mathf.Max(0, cluster.Centroid.y - radius);
                int xMax = Mathf.Min(W - 1, cluster.Centroid.x + radius);
                int yMax = Mathf.Min(H - 1, cluster.Centroid.y + radius);

                for (int py = yMin; py <= yMax; py++)
                for (int px = xMin; px <= xMax; px++)
                {
                    int idx = py * W + px;
                    int curOwner = result.PixelOwners[idx];
                    if (curOwner < 0 || !cluster.Regions.Contains(curOwner)) continue;

                    int bestRid = curOwner;
                    long bestDistSq = long.MaxValue;
                    for (int s = 0; s < seedRegion.Count; s++)
                    {
                        long dx = px - seedX[s];
                        long dy = py - seedY[s];
                        long d = dx * dx + dy * dy;
                        if (d < bestDistSq) { bestDistSq = d; bestRid = seedRegion[s]; }
                    }

                    if (bestRid != curOwner)
                    {
                        result.PixelOwners[idx] = bestRid;
                        changed.Add(idx);
                    }
                }
            }

            if (changed.Count == 0) return;

            // Patch AllOwners for changed pixels AND their 4-neighbors — neighbors' slots
            // could include the old owner that no longer borders them, or be missing the
            // new owner that now does.
            var toPatch = new HashSet<int>(changed);
            foreach (int idx in changed)
            {
                int px = idx % W, py = idx / W;
                if (px > 0)     toPatch.Add(idx - 1);
                if (px < W - 1) toPatch.Add(idx + 1);
                if (py > 0)     toPatch.Add(idx - W);
                if (py < H - 1) toPatch.Add(idx + W);
            }

            foreach (int idx in toPatch)
            {
                int px = idx % W, py = idx / W;
                int self = result.PixelOwners[idx];
                int slotBase = RegionBuilder.OwnerSlots * idx;
                for (int k = 0; k < RegionBuilder.OwnerSlots; k++) result.AllOwners[slotBase + k] = -1;
                if (self < 0) continue;
                result.AllOwners[slotBase] = self;
                int slot = 1;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx == 0) == (dy == 0)) continue;
                    int nx = px + dx, ny = py + dy;
                    if ((uint)nx >= (uint)W || (uint)ny >= (uint)H) continue;
                    int cand = result.PixelOwners[ny * W + nx];
                    if (cand < 0 || cand == self) continue;
                    bool dup = false;
                    for (int k = 1; k < slot; k++)
                        if (result.AllOwners[slotBase + k] == cand) { dup = true; break; }
                    if (!dup && slot < RegionBuilder.OwnerSlots)
                        result.AllOwners[slotBase + slot++] = cand;
                }
            }
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

        private static Dictionary<long, List<Vector2Int>> BuildSupercoverPixels(
            Dictionary<long, List<Vector2Int>> pairToCorners)
        {
            var dict = new Dictionary<long, List<Vector2Int>>();
            foreach (var kv in pairToCorners)
            {
                var pts = kv.Value;
                if (pts.Count < 2) continue;
                var p1 = LineRaster.FindFurthest(pts, pts[0]);
                var p2 = LineRaster.FindFurthest(pts, p1);
                if (p1 == p2) continue;
                dict[kv.Key] = LineRaster.Supercover(p1, p2);
            }
            return dict;
        }

        // Per pair, collect unit-length wall segments in corner coords along the same
        // boundaries seen in HeightmapDebugView. The terrain mesh colours each quad
        // (qx, qy) by the dominant region of its 4 corner pixels, so the visible seam
        // sits between adjacent quads whose dominants differ — NOT between adjacent
        // pixels. Compute dominants once into a (W-1) x (H-1) grid, then add a wall
        // wherever two 4-neighbour quads disagree.
        private static Dictionary<long, List<SeamLine>> BuildBoundaryWalls(RegionBuilder.Result result)
        {
            int W = result.Width, H = result.Height;
            int QW = W - 1, QH = H - 1;
            if (QW <= 0 || QH <= 0) return new Dictionary<long, List<SeamLine>>();

            var dominant = new int[QW * QH];
            for (int qy = 0; qy < QH; qy++)
            for (int qx = 0; qx < QW; qx++)
            {
                int r00 = result.PixelOwners[ qy      * W + qx    ];
                int r10 = result.PixelOwners[ qy      * W + qx + 1];
                int r01 = result.PixelOwners[(qy + 1) * W + qx    ];
                int r11 = result.PixelOwners[(qy + 1) * W + qx + 1];
                dominant[qy * QW + qx] = QuadDominant(r00, r10, r01, r11);
            }

            var dict = new Dictionary<long, List<SeamLine>>();
            for (int qy = 0; qy < QH; qy++)
            for (int qx = 0; qx < QW; qx++)
            {
                int self = dominant[qy * QW + qx];
                if (self < 0) continue;

                if (qx + 1 < QW)
                {
                    int e = dominant[qy * QW + qx + 1];
                    if (e >= 0 && e != self)
                        AddWall(dict, self, e,
                            new Vector2Int(qx + 1, qy), new Vector2Int(qx + 1, qy + 1));
                }
                if (qy + 1 < QH)
                {
                    int n = dominant[(qy + 1) * QW + qx];
                    if (n >= 0 && n != self)
                        AddWall(dict, self, n,
                            new Vector2Int(qx, qy + 1), new Vector2Int(qx + 1, qy + 1));
                }
            }
            return dict;
        }

        private static int QuadDominant(int r00, int r10, int r01, int r11)
        {
            int best = -1, bestCount = 0;
            int c0 = r00, c1 = r10, c2 = r01, c3 = r11;
            for (int i = 0; i < 4; i++)
            {
                int ci = i == 0 ? c0 : i == 1 ? c1 : i == 2 ? c2 : c3;
                if (ci < 0) continue;
                int count = 0;
                if (c0 == ci) count++;
                if (c1 == ci) count++;
                if (c2 == ci) count++;
                if (c3 == ci) count++;
                if (count > bestCount) { bestCount = count; best = ci; }
            }
            return best;
        }

        private static void AddWall(Dictionary<long, List<SeamLine>> dict,
            int a, int b, Vector2Int p1, Vector2Int p2)
        {
            long key = PairKey(a, b);
            if (!dict.TryGetValue(key, out var list))
                dict[key] = list = new List<SeamLine>();
            list.Add(new SeamLine(p1, p2));
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

        // ── Shared algorithms (public so debug views can call them) ───────────────

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
