using System;
using System.Collections.Generic;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // A uniform grid of buckets over the surface's bounding box, one micro spacing a side, which
    // the nearest-seed and class-distance queries walk. Blittable, for the jobs.
    public struct ContinentGrid
    {
        public double3 Origin;
        public double  Cell;
        public int3    Dims;

        public int Count => Dims.x * Dims.y * Dims.z;

        // The bucket a point falls in, unclamped: a point off the box (a border texel, a warped
        // point) lies in a bucket outside it, and the queries skip buckets outside the grid, which
        // hold no seed.
        public int3 CellOf(double3 p) => (int3)math.floor((p - Origin) / Cell);

        public bool Contains(int3 c) => c.x >= 0 && c.y >= 0 && c.z >= 0 && c.x < Dims.x && c.y < Dims.y && c.z < Dims.z;

        public int Flat(int3 c) => (c.z * Dims.y + c.y) * Dims.x + c.x;

        // A seed's bucket, clamped in: a seed on the box's far face, or past it by a rounding,
        // belongs to the last bucket rather than none.
        public int SeedCell(double3 p) => Flat(math.clamp(CellOf(p), 0, Dims - 1));

        // The surface's box: a sphere's cube, a cylinder's radius across and its length along Z, a
        // plane grid's tiles in XZ. Every dimension at least one bucket, so a plane's flat Y works.
        public static ContinentGrid For(in SurfaceDef s, double cell)
        {
            double3 lo, hi;
            switch (s.Type)
            {
                case SurfaceType.CubeSphere:
                    lo = -s.Radius; hi = s.Radius; break;
                case SurfaceType.Cylinder:
                    lo = new double3(-s.Radius, -s.Radius, 0.0); hi = new double3(s.Radius, s.Radius, s.Length); break;
                default:
                {
                    double hx = s.Cols * s.TileSize * 0.5, hz = s.Rows * s.TileSize * 0.5;
                    lo = new double3(-hx, 0.0, -hz); hi = new double3(hx, 0.0, hz); break;
                }
            }
            int3 dims = (int3)math.floor((hi - lo) / cell) + 1;
            return new ContinentGrid { Origin = lo, Cell = cell, Dims = math.max(dims, 1) };
        }
    }

    // The continent map's tables, read through a generic constraint as IMacroTexels is, so the
    // managed reference and the Burst jobs run one code path: ContinentMap's arrays here, the
    // jobs' NativeArrays in ContinentJobs.
    public interface IContinentTables
    {
        ContinentGrid Grid { get; }
        int     MacroCount { get; }
        double3 MacroSeed(int i);
        double  MacroElevation(int i);
        double  BlendRadius2 { get; }
        double3 MicroSeed(int i);
        bool    MicroLand(int i);
        int     CellStart(int cell);   // the bucket's seeds are Item(CellStart(c)) .. Item(CellStart(c + 1) - 1)
        int     Item(int k);
    }

    // What the texel scan finds at one stored texel of the top level: its nearest macro and micro
    // seeds, unwarped, and the squared distances to them.
    public struct ContinentTexel
    {
        public int    Macro, Micro;
        public double MacroD2, MicroD2;
    }

    public interface IContinentScan
    {
        ContinentTexel Get(int k);
    }

    // What the build reports (Docs/Continents_Plan.md, K10).
    public struct ContinentStats
    {
        public int    MacroCells, MicroCells, LandCells;
        public double LandFraction;           // the map's land share by area
        public double CoverageRadius;         // metres: the farthest any stored top-level texel is from its macro seed
        public double SaturationRadius;       // micro spacings: the farthest any texel inside a quad is from its micro seed, unwarped
        public double WarpedSaturation;       // micro spacings: the farthest any warped field point inside a quad is from its own class, where the other is capped
        public double StageAMilliseconds, StageBMilliseconds, StageCMilliseconds;
    }

    // Stage A of the continents (Docs/Continents_Plan.md, K3): the Voronoi continent map, built once
    // at startup. Macro seeds and micro seeds are spread over the surface by best-candidate blue
    // noise; the top macro level's texels give the macro cells' adjacency and the micro cells' areas;
    // a breadth-first spread from a few continent centres gives each macro cell an elevation; and
    // each micro cell is land or ocean by its parent's elevation plus noise, against the threshold
    // whose land share by area is LandFraction.
    //
    // Deterministic: one seeded generator drawn in a fixed order, every tie to the lower index, and
    // no iteration over a hash container. The per-texel scan is the one part worth a Burst job
    // (ContinentTexelScanJob); it calls ScanTexel, as the managed loop here does, and the reductions
    // over its output run here, in texel order, for both. UnityEngine-free, for the harness.
    public sealed class ContinentMap
    {
        public const int Candidates     = 8;
        const int        BruteForceUpTo = 128;   // seeds placed before the bucket grid pays

        public double3[] MacroSeeds;
        public double[]  MacroElevation;     // BFS: 1 at a centre, decaying outwards, 0 where it never reached
        public bool[]    MacroCentre;
        public int[][]   Adjacency;          // ascending
        public double3[] MicroSeeds;
        public int[]     MicroParent;
        public double[]  MicroScore;
        public byte[]    MicroLand;          // 1 land, 0 ocean
        public double[]  MicroArea;          // m^2, from the texels each is nearest
        public ContinentGrid Grid;
        public int[]     CellStart;          // CSR over Grid's buckets, Count + 1 entries
        public int[]     Items;
        public double    BlendRadius2;       // E's kernel radius squared: (1.5 x CoverageRadius)^2
        public ContinentStats Stats;

        Unity.Mathematics.Random _random;    // continues from the placement into the BFS

        // ── The whole of Stage A, managed ────────────────────────────────────

        // The managed reference: placement, the texel scan in managed code, then Finish.
        public static ContinentMap Build(in SurfaceDef s, RootQuad[] roots, in MacroLayout layout, in ContinentParams cp)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var map   = Place(s, roots, cp);
            map.Finish(s, roots, layout, cp, new ArrayContinentScan(map.Scan(s, roots, layout)));
            map.Stats.StageAMilliseconds = clock.Elapsed.TotalMilliseconds;
            return map;
        }

        // ── Seeds ────────────────────────────────────────────────────────────

        // Both seed sets and the micro seeds' bucket grid; the scan and Finish follow.
        public static ContinentMap Place(in SurfaceDef s, RootQuad[] roots, in ContinentParams cp)
        {
            if (!cp.Enabled) throw new ArgumentException("ContinentMap: no continents to place.", nameof(cp));

            var map = new ContinentMap
            {
                // CreateFromIndex hashes the index into a valid state; a zero state is caught only
                // with the collections checks on, and in a player the sphere's rejection loop would
                // never end.
                _random = Unity.Mathematics.Random.CreateFromIndex(cp.PlacementSeed),
                Grid    = ContinentGrid.For(s, cp.MicroSpacing),
            };
            map.MacroSeeds = PlaceSeeds(s, roots, cp.MacroCells, ref map._random, default, out _, out _);
            map.MicroSeeds = PlaceSeeds(s, roots, cp.MicroCells, ref map._random, map.Grid, out map.CellStart, out map.Items);
            map.MacroElevation = new double[cp.MacroCells];
            map.MicroLand      = new byte[cp.MicroCells];
            return map;
        }

        // A point uniform over the surface's area: rejection in the cube on a sphere (sqrt only),
        // a uniform quad and (u, v) on a cylinder or plane grid, whose quads are congruent and whose
        // parameterisations are linear in area.
        static double3 RandomPoint(in SurfaceDef s, RootQuad[] roots, ref Unity.Mathematics.Random r)
        {
            if (s.Type == SurfaceType.CubeSphere)
            {
                while (true)
                {
                    double3 v  = r.NextDouble3(-1.0, 1.0);
                    double  l2 = math.dot(v, v);
                    if (l2 > 1e-6 && l2 <= 1.0) return v * (s.Radius / math.sqrt(l2));
                }
            }
            int    q = r.NextInt(roots.Length);
            double u = r.NextDouble(), w = r.NextDouble();
            return SurfaceMath.SurfacePoint(s, roots[q], u, w);
        }

        // Best-candidate blue noise: each seed is the candidate farthest from those placed, of
        // Candidates drawn, every one of them drawn whatever the distances so the stream does not
        // depend on them. Brute force for the first BruteForceUpTo, then the bucket grid, built as
        // linked lists as it goes and returned as CSR when grid has buckets.
        static double3[] PlaceSeeds(in SurfaceDef s, RootQuad[] roots, int count, ref Unity.Mathematics.Random r,
                                    ContinentGrid grid, out int[] cellStart, out int[] items)
        {
            var  seeds   = new double3[count];
            bool useGrid = grid.Cell > 0.0;
            int[] head   = useGrid ? new int[grid.Count] : null;
            int[] next   = useGrid ? new int[count] : null;
            if (useGrid) for (int c = 0; c < head.Length; c++) head[c] = -1;

            for (int k = 0; k < count; k++)
            {
                double3 best = default;
                double  bestD2 = -1.0;
                for (int c = 0; c < Candidates; c++)
                {
                    double3 x  = RandomPoint(s, roots, ref r);
                    double  d2 = k == 0 ? double.MaxValue
                               : !useGrid || k <= BruteForceUpTo ? NearestPlaced(seeds, k, x)
                               : NearestLinked(grid, head, next, seeds, x);
                    if (d2 > bestD2) { bestD2 = d2; best = x; }
                }
                seeds[k] = best;
                if (useGrid)
                {
                    int cell = grid.SeedCell(best);
                    next[k]    = head[cell];
                    head[cell] = k;
                }
            }

            cellStart = null; items = null;
            if (useGrid)
            {
                // CSR, each bucket's seeds in ascending index.
                cellStart = new int[grid.Count + 1];
                for (int k = 0; k < count; k++) cellStart[grid.SeedCell(seeds[k]) + 1]++;
                for (int c = 0; c < grid.Count; c++) cellStart[c + 1] += cellStart[c];
                items = new int[count];
                var fill = (int[])cellStart.Clone();
                for (int k = 0; k < count; k++) items[fill[grid.SeedCell(seeds[k])]++] = k;
            }
            return seeds;
        }

        static double NearestPlaced(double3[] seeds, int placed, double3 x)
        {
            double best = double.MaxValue;
            for (int i = 0; i < placed; i++) best = math.min(best, math.lengthsq(x - seeds[i]));
            return best;
        }

        // The nearest placed seed through the linked buckets, ring by ring outwards until the rings
        // searched cover the best distance found.
        static double NearestLinked(in ContinentGrid g, int[] head, int[] next, double3[] seeds, double3 x)
        {
            int3   c    = g.CellOf(x);
            int    rMax = RingLimit(g, c);
            double best = double.MaxValue;
            for (int r = 0; r <= rMax; r++)
            {
                for (int dz = -r; dz <= r; dz++)
                for (int dy = -r; dy <= r; dy++)
                {
                    int step = r == 0 || math.abs(dz) == r || math.abs(dy) == r ? 1 : 2 * r;
                    for (int dx = -r; dx <= r; dx += step)
                    {
                        int3 b = c + new int3(dx, dy, dz);
                        if (!g.Contains(b)) continue;
                        for (int i = head[g.Flat(b)]; i >= 0; i = next[i])
                            best = math.min(best, math.lengthsq(x - seeds[i]));
                    }
                }
                double reach = r * g.Cell;
                if (best <= reach * reach) break;
            }
            return best;
        }

        // The ring radius past which every bucket of the grid has been searched.
        static int RingLimit(in ContinentGrid g, int3 c)
            => math.cmax(math.max(c, g.Dims - 1 - c));

        // ── Queries, on any tables ───────────────────────────────────────────

        // The nearest macro seed by brute force: a few dozen. Ties to the lower index.
        public static int NearestMacro<T>(T t, double3 p, out double d2) where T : struct, IContinentTables
        {
            int best = 0;
            d2 = double.MaxValue;
            for (int i = 0; i < t.MacroCount; i++)
            {
                double e = math.lengthsq(p - t.MacroSeed(i));
                if (e < d2) { d2 = e; best = i; }
            }
            return best;
        }

        // The nearest micro seed through the CSR buckets, ring by ring outwards until the rings
        // searched cover the best distance. Ties to the lower index.
        public static int NearestMicro<T>(T t, double3 p, out double d2) where T : struct, IContinentTables
        {
            var  g    = t.Grid;
            int3 c    = g.CellOf(p);
            int  rMax = RingLimit(g, c);
            int  best = -1;
            d2 = double.MaxValue;
            for (int r = 0; r <= rMax; r++)
            {
                for (int dz = -r; dz <= r; dz++)
                for (int dy = -r; dy <= r; dy++)
                {
                    int step = r == 0 || math.abs(dz) == r || math.abs(dy) == r ? 1 : 2 * r;
                    for (int dx = -r; dx <= r; dx += step)
                    {
                        int3 b = c + new int3(dx, dy, dz);
                        if (!g.Contains(b)) continue;
                        int f = g.Flat(b);
                        for (int k = t.CellStart(f), end = t.CellStart(f + 1); k < end; k++)
                        {
                            int    i = t.Item(k);
                            double e = math.lengthsq(p - t.MicroSeed(i));
                            if (e < d2 || (e == d2 && i < best)) { d2 = e; best = i; }
                        }
                    }
                }
                double reach = r * g.Cell;
                if (best >= 0 && d2 <= reach * reach) break;
            }
            return best;
        }

        // The squared distances from p to the nearest land seed and the nearest ocean seed, over
        // the 5 x 5 x 5 buckets around p: exact within two micro spacings, which is the field's cap,
        // and double.MaxValue for a class with no seed in reach. The caller caps them.
        public static void ClassDistances<T>(T t, double3 p, out double land2, out double ocean2)
            where T : struct, IContinentTables
        {
            var  g = t.Grid;
            int3 c = g.CellOf(p);
            land2 = ocean2 = double.MaxValue;
            for (int dz = -2; dz <= 2; dz++)
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int3 b = c + new int3(dx, dy, dz);
                if (!g.Contains(b)) continue;
                int f = g.Flat(b);
                for (int k = t.CellStart(f), end = t.CellStart(f + 1); k < end; k++)
                {
                    int    i = t.Item(k);
                    double e = math.lengthsq(p - t.MicroSeed(i));
                    if (t.MicroLand(i)) land2 = math.min(land2, e);
                    else                ocean2 = math.min(ocean2, e);
                }
            }
        }

        // ── The texel scan ───────────────────────────────────────────────────

        // One stored texel of the top level, at the point MacroGenerator.Texel evaluates it.
        public static ContinentTexel ScanTexel<T>(in SurfaceDef s, in RootQuad q, int n, int i, int j, T t)
            where T : struct, IContinentTables
        {
            double  cells = n;
            double3 p     = SurfaceMath.SurfacePoint(s, q, i / cells, j / cells);
            var o = new ContinentTexel();
            o.Macro = NearestMacro(t, p, out o.MacroD2);
            o.Micro = NearestMicro(t, p, out o.MicroD2);
            return o;
        }

        // Every stored texel of the top level, borders included, in the order Finish reads:
        // quad, then row, then column, ScanIndex.
        public ContinentTexel[] Scan(in SurfaceDef s, RootQuad[] roots, in MacroLayout layout)
        {
            int top = layout.LevelCount - 1, n = layout.Cells(top), w = layout.Width(top);
            var tables = new ArrayContinentTables(this);
            var scan   = new ContinentTexel[layout.QuadCount * w * w];
            for (int k = 0; k < scan.Length; k++)
            {
                ScanCoords(k, w, out int q, out int i, out int j);
                scan[k] = ScanTexel(s, roots[q], n, i, j, tables);
            }
            return scan;
        }

        // The scan's flat index and the texel it is, top level only.
        public static int ScanIndex(int quad, int i, int j, int w)
            => (quad * w + j + MacroLayout.Border) * w + i + MacroLayout.Border;

        public static void ScanCoords(int k, int w, out int quad, out int i, out int j)
        {
            quad = k / (w * w);
            int r = k - quad * w * w;
            j = r / w - MacroLayout.Border;
            i = r - (r / w) * w - MacroLayout.Border;
        }

        // The texel area table of one quad, |dp/du x dp/dv| / n^2 by central differences at a
        // hundredth of a cell. Root quads are congruent, so quad 0's serves all of them; computed
        // here, in managed code, so no area -- which decides the land threshold -- comes from Burst's
        // trigonometry.
        public static double[] AreaTable(in SurfaceDef s, in RootQuad q, int n)
        {
            var    area = new double[(n + 1) * (n + 1)];
            double h    = 0.01 / n;
            for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                double  u  = i / (double)n, v = j / (double)n;
                double3 du = (SurfaceMath.SurfacePoint(s, q, u + h, v) - SurfaceMath.SurfacePoint(s, q, u - h, v)) / (2.0 * h);
                double3 dv = (SurfaceMath.SurfacePoint(s, q, u, v + h) - SurfaceMath.SurfacePoint(s, q, u, v - h)) / (2.0 * h);
                area[j * (n + 1) + i] = math.length(math.cross(du, dv)) / ((double)n * n);
            }
            return area;
        }

        // ── The rest of Stage A, from the scan ───────────────────────────────

        // Adjacency, areas and radii from the scan, then the BFS, the scores and the threshold.
        // Everything here runs in texel order or seed order, so a Burst scan and a managed one give
        // the same tables.
        public void Finish<TScan>(in SurfaceDef s, RootQuad[] roots, in MacroLayout layout, in ContinentParams cp, TScan scan)
            where TScan : struct, IContinentScan
        {
            int top = layout.LevelCount - 1, n = layout.Cells(top), w = layout.Width(top), b = MacroLayout.Border;
            int macroCount = MacroSeeds.Length, microCount = MicroSeeds.Length;

            double[] area = AreaTable(s, roots[0], n);
            var adjacent  = new bool[macroCount * macroCount];
            MicroArea     = new double[microCount];
            double cover2 = 0.0, sat2 = 0.0, total = 0.0;

            for (int q = 0; q < layout.QuadCount; q++)
            for (int j = -b; j <= n + b; j++)
            for (int i = -b; i <= n + b; i++)
            {
                int k = ScanIndex(q, i, j, w);
                ContinentTexel t = scan.Get(k);
                // Over the border too: Stage B evaluates the field there, and its blend needs a
                // macro seed within the kernel radius wherever it does.
                cover2 = math.max(cover2, t.MacroD2);
                if (i < 0 || j < 0 || i > n || j > n) continue;

                // The plateau only inside the quads. A border texel's value reaches a drawn texel
                // only through the filter's outer taps before the halo replaces it, and past a
                // cylinder's rim it is off the surface altogether, far from every seed.
                sat2 = math.max(sat2, t.MicroD2);

                // Two macro cells touch where neighbouring texels of one quad take different
                // seeds. Edge rows are stored by both quads, so links across quad edges come free.
                if (i < n) Touch(adjacent, macroCount, t.Macro, scan.Get(k + 1).Macro);
                if (j < n) Touch(adjacent, macroCount, t.Macro, scan.Get(k + w).Macro);

                // Shared edge texels count once, for their owner.
                if (!layout.EdgeSource(s, q, top, i, j, out _, out _, out _))
                {
                    double a = area[j * (n + 1) + i];
                    MicroArea[t.Micro] += a;
                    total += a;
                }
            }

            double coverage = math.sqrt(cover2);
            BlendRadius2 = (1.5 * coverage) * (1.5 * coverage);
            double saturation = math.sqrt(sat2) / cp.MicroSpacing;
            // Inside a region the capped class distance dominates and the profile is flat, so long
            // as every point is close enough to a seed of its own class (K2). Stage B checks that at
            // its warped points (ContinentParams.PlateauLimit2); this is the same bound unwarped,
            // and the cheaper failure.
            if (sat2 > cp.PlateauLimit2)
                throw new InvalidOperationException(
                    $"ContinentMap: a texel is {saturation:F2} micro spacings from its nearest micro seed, too far for " +
                    "the profile to reach its plateau inside a cell. Raise MicroCells, or narrow CoastWidth.");

            Adjacency = new int[macroCount][];
            var list = new List<int>();
            for (int a = 0; a < macroCount; a++)
            {
                list.Clear();
                for (int c = 0; c < macroCount; c++) if (adjacent[a * macroCount + c]) list.Add(c);
                Adjacency[a] = list.ToArray();
            }

            Spread(cp);
            Classify(cp, total);

            Stats.MacroCells       = macroCount;
            Stats.MicroCells       = microCount;
            Stats.CoverageRadius   = coverage;
            Stats.SaturationRadius = saturation;
        }

        static void Touch(bool[] adjacent, int count, int a, int c)
        {
            if (a == c) return;
            adjacent[a * count + c] = true;
            adjacent[c * count + a] = true;
        }

        // The concept's spread: ContinentCount centres, drawn by a partial Fisher-Yates shuffle, at
        // elevation 1, then a FIFO outwards, each step multiplying by a decay drawn in
        // [DecayMin, DecayMax). A neighbour keeps a new elevation that beats its own and the floor.
        // One draw per neighbour visited, kept or not, so the stream is fixed by the graph.
        void Spread(in ContinentParams cp)
        {
            int count = MacroSeeds.Length;
            MacroCentre = new bool[count];
            var order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;

            var queue = new Queue<int>();
            for (int c = 0; c < cp.ContinentCount; c++)
            {
                int pick = c + _random.NextInt(count - c);
                (order[c], order[pick]) = (order[pick], order[c]);
                MacroElevation[order[c]] = 1.0;
                MacroCentre[order[c]]    = true;
                queue.Enqueue(order[c]);
            }

            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                foreach (int nb in Adjacency[cur])
                {
                    double e = MacroElevation[cur] * _random.NextDouble(cp.DecayMin, cp.DecayMax);
                    if (e > MacroElevation[nb] && e > cp.ElevationFloor)
                    {
                        MacroElevation[nb] = e;
                        queue.Enqueue(nb);
                    }
                }
            }
        }

        // Each micro cell's score is its parent macro cell's elevation plus noise at its own seed;
        // the highest-scoring cells are land until their area reaches LandFraction of the whole.
        // By rank, scores descending and ties to the lower index, rather than by comparing with the
        // last cell's score, which would also take every later cell that ties it.
        void Classify(in ContinentParams cp, double totalArea)
        {
            var tables = new ArrayContinentTables(this);
            int count  = MicroSeeds.Length;
            MicroParent = new int[count];
            MicroScore  = new double[count];
            for (int i = 0; i < count; i++)
            {
                MicroParent[i] = NearestMacro(tables, MicroSeeds[i], out _);
                MicroScore[i]  = MacroElevation[MicroParent[i]]
                               + cp.ScoreNoise * LatticeNoise.Fbm(MicroSeeds[i] / cp.MacroSpacing, 3, 0.5f, 2f, cp.ScoreSeed);
            }

            var order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;
            double[] score = MicroScore;
            Array.Sort(order, (a, c) => score[a] != score[c] ? score[c].CompareTo(score[a]) : a.CompareTo(c));

            double target = cp.LandFraction * totalArea, land = 0.0;
            int    cells  = 0;
            for (int r = 0; r < count && land < target; r++)
            {
                MicroLand[order[r]] = 1;
                land += MicroArea[order[r]];
                cells++;
            }

            Stats.LandCells    = cells;
            Stats.LandFraction = land / totalArea;
        }
    }

    // ContinentMap's arrays as tables.
    public readonly struct ArrayContinentTables : IContinentTables
    {
        readonly ContinentMap _m;

        public ArrayContinentTables(ContinentMap m) { _m = m; }

        public ContinentGrid Grid         => _m.Grid;
        public int           MacroCount   => _m.MacroSeeds.Length;
        public double        BlendRadius2 => _m.BlendRadius2;
        public double3 MacroSeed(int i)      => _m.MacroSeeds[i];
        public double  MacroElevation(int i) => _m.MacroElevation[i];
        public double3 MicroSeed(int i)      => _m.MicroSeeds[i];
        public bool    MicroLand(int i)      => _m.MicroLand[i] != 0;
        public int     CellStart(int cell)   => _m.CellStart[cell];
        public int     Item(int k)           => _m.Items[k];
    }

    public readonly struct ArrayContinentScan : IContinentScan
    {
        readonly ContinentTexel[] _a;
        public ArrayContinentScan(ContinentTexel[] a) { _a = a; }
        public ContinentTexel Get(int k) => _a[k];
    }
}
