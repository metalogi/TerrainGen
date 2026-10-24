using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // Stage A of the continents (Docs/Continents_Plan.md, K3, Task 3): the continent map is
    // deterministic, its land share is LandFraction by area, its macro graph is sound, its seeds are
    // spread, and its grid queries are exact against brute force. Small maps -- resolution 9, three
    // levels, a few hundred micro cells, each at least four top-level texels across -- so the whole
    // file runs in well under a second outside the Editor.
    public class ContinentMapTests
    {
        internal sealed class Case
        {
            public string          Name;
            public SurfaceDef      Surface;
            public RootQuad[]      Roots;
            public MacroParams     Macro;
            public ContinentParams Continents;
            public MacroConfig     Config;
            public MacroLayout     Layout => Macro.Layout;
        }

        internal static ContinentConfig SmallConfig(int macro, int micro, int continents) => new ContinentConfig
        {
            Seed = 3u, MacroCells = macro, MicroCells = micro, ContinentCount = continents,
            DecayMin = 0.5f, DecayMax = 0.75f, ElevationFloor = 0.1f, ScoreNoise = 0.25f, LandFraction = 0.3f,
            WarpAmplitude = 0.5f, WarpWavelength = 2f, CoastWidth = 1f,
            LandHeight = 2000f, OceanDepth = 6000f, InteriorRise = 0.8f,
        };

        internal static Case Make(string name, in SurfaceDef s, in ContinentConfig k, int resolution = 9, int levels = 3)
        {
            var hp = HeightParams.Create(s, resolution, 0.0, 12, 100f, 0.5f, 2f, 42u);
            var c  = new MacroConfig
            {
                LevelCount = levels, Wavelength0 = 0.0, Persistence = 0.5f, BaseHeightScale = 900f,
                EquatorTemperature = 30f, PoleTemperature = -30f, LapseRate = 6.5f, TemperatureNoise = 5f,
                MoistureNoise = 1.5f, RuggednessNoise = 1.5f, Seed = 7u, Continents = k,
            };
            var mp = MacroParams.Create(s, hp, c);
            return new Case
            {
                Name = name, Surface = s, Roots = SurfaceMath.BuildRoots(s), Macro = mp, Config = c,
                Continents = ContinentParams.Create(s, mp, c),
            };
        }

        // A sphere, the ring, and a plane grid for the topology switch. Each micro cell is about
        // 4.7, 5.2 and 6.4 top-level texels across.
        internal static Case[] Cases() => new[]
        {
            Make("sphere", SurfaceDef.CubeSphere(6371000.0),           SmallConfig(8, 60, 2)),
            Make("ring",   SurfaceDef.Cylinder(1592750.0, 1250000.0, 8, 1), SmallConfig(12, 300, 3)),
            Make("plane",  SurfaceDef.PlaneGrid(1000.0, 2, 2),        SmallConfig(6, 100, 2)),
        };

        internal static ContinentMap Build(Case c) => ContinentMap.Build(c.Surface, c.Roots, c.Layout, c.Continents);

        [Test]
        public void TwoBuildsAreIdentical()
        {
            foreach (var c in Cases())
            {
                ContinentMap a = Build(c), b = Build(c);
                AssertSame(a.MacroSeeds, b.MacroSeeds, c.Name + ": macro seeds");
                AssertSame(a.MicroSeeds, b.MicroSeeds, c.Name + ": micro seeds");
                CollectionAssert.AreEqual(a.MacroElevation, b.MacroElevation, c.Name + ": elevations");
                CollectionAssert.AreEqual(a.MicroLand,      b.MicroLand,      c.Name + ": land");
                CollectionAssert.AreEqual(a.MicroArea,      b.MicroArea,      c.Name + ": areas");
                CollectionAssert.AreEqual(a.MicroScore,     b.MicroScore,     c.Name + ": scores");
                CollectionAssert.AreEqual(a.CellStart,      b.CellStart,      c.Name + ": buckets");
                CollectionAssert.AreEqual(a.Items,          b.Items,          c.Name + ": bucket items");
                for (int m = 0; m < a.Adjacency.Length; m++)
                    CollectionAssert.AreEqual(a.Adjacency[m], b.Adjacency[m], $"{c.Name}: adjacency of {m}");
                Assert.AreEqual(a.BlendRadius2, b.BlendRadius2, c.Name + ": blend radius");
            }
        }

        static void AssertSame(double3[] a, double3[] b, string what)
        {
            Assert.AreEqual(a.Length, b.Length, what);
            for (int i = 0; i < a.Length; i++)
                Assert.IsTrue(math.asulong(a[i].x) == math.asulong(b[i].x) && math.asulong(a[i].y) == math.asulong(b[i].y) &&
                              math.asulong(a[i].z) == math.asulong(b[i].z), $"{what}: {i} differs");
        }

        // The land is the highest-scoring cells, by rank, up to the first whose area takes the
        // land share to LandFraction: over it by less than that last cell's area. And the areas are
        // the surface's, counted once per shared texel.
        [Test]
        public void LandShareIsTheLandFraction()
        {
            foreach (var c in Cases())
            {
                var    map   = Build(c);
                double total = 0.0, land = 0.0, lowestLand = double.MaxValue, highestOcean = double.MinValue;
                int    lowestLandCell = -1, cells = 0;
                for (int i = 0; i < map.MicroSeeds.Length; i++)
                {
                    total += map.MicroArea[i];
                    if (map.MicroLand[i] != 0)
                    {
                        land += map.MicroArea[i];
                        cells++;
                        if (map.MicroScore[i] < lowestLand || (map.MicroScore[i] == lowestLand && i > lowestLandCell))
                        { lowestLand = map.MicroScore[i]; lowestLandCell = i; }
                    }
                    else highestOcean = math.max(highestOcean, map.MicroScore[i]);
                }

                double target = c.Continents.LandFraction * total;
                Assert.GreaterOrEqual(land, target, $"{c.Name}: land short of LandFraction");
                Assert.Less(land - map.MicroArea[lowestLandCell], target,
                    $"{c.Name}: land over LandFraction by more than its last cell");
                Assert.GreaterOrEqual(lowestLand, highestOcean, $"{c.Name}: an ocean cell outscores a land cell");
                Assert.AreEqual(land / total, map.Stats.LandFraction, 1e-12, $"{c.Name}: the reported land fraction");
                Assert.AreEqual(cells, map.Stats.LandCells, $"{c.Name}: the reported land cells");

                double area = ContinentParams.SurfaceArea(c.Surface);
                Assert.AreEqual(1.0, total / area, 0.05, $"{c.Name}: the texel areas sum to {total:E3} m^2 of {area:E3}");
            }
        }

        // Adjacency is symmetric and never a cell with itself; every cell has a neighbour; a
        // sphere's cells are one graph. The spread starts at ContinentCount centres at 1 and decays
        // from there, never above 1 and never kept below the floor.
        [Test]
        public void MacroGraphIsSound()
        {
            foreach (var c in Cases())
            {
                var map = Build(c);
                int n   = map.MacroSeeds.Length;
                for (int a = 0; a < n; a++)
                {
                    Assert.Greater(map.Adjacency[a].Length, 0, $"{c.Name}: macro cell {a} has no neighbour");
                    for (int k = 0; k < map.Adjacency[a].Length; k++)
                    {
                        int b = map.Adjacency[a][k];
                        Assert.AreNotEqual(a, b, $"{c.Name}: {a} is its own neighbour");
                        if (k > 0) Assert.Greater(b, map.Adjacency[a][k - 1], $"{c.Name}: adjacency of {a} not ascending");
                        CollectionAssert.Contains(map.Adjacency[b], a, $"{c.Name}: {a} -> {b} but not back");
                    }
                }

                if (c.Surface.Type == SurfaceType.CubeSphere)
                {
                    var seen  = new bool[n];
                    var queue = new Queue<int>();
                    queue.Enqueue(0); seen[0] = true;
                    int reached = 1;
                    while (queue.Count > 0)
                        foreach (int b in map.Adjacency[queue.Dequeue()])
                            if (!seen[b]) { seen[b] = true; reached++; queue.Enqueue(b); }
                    Assert.AreEqual(n, reached, $"{c.Name}: the macro graph is not connected");
                }

                int centres = 0;
                for (int a = 0; a < n; a++)
                {
                    double e = map.MacroElevation[a];
                    if (map.MacroCentre[a]) { centres++; Assert.AreEqual(1.0, e, $"{c.Name}: centre {a}"); }
                    else Assert.IsTrue(e == 0.0 || (e > c.Continents.ElevationFloor && e < 1.0),
                        $"{c.Name}: macro cell {a} at elevation {e}");
                }
                Assert.AreEqual(c.Continents.ContinentCount, centres, $"{c.Name}: continent centres");
            }
        }

        // Best-candidate placement keeps seeds apart without guaranteeing a minimum (8 candidates
        // late in placement all land near a seed about 6% of the time), so this pins what it
        // measures: the closest pair of micro seeds, in micro spacings, was 0.540 on the sphere,
        // 0.408 on the ring and 0.557 on the plane (2026-10-24), and a change that brings any under
        // 0.4 has changed the placement.
        [Test]
        public void MicroSeedsAreSpread()
        {
            foreach (var c in Cases())
            {
                var    map  = Build(c);
                double min2 = double.MaxValue;
                for (int i = 0; i < map.MicroSeeds.Length; i++)
                for (int j = i + 1; j < map.MicroSeeds.Length; j++)
                    min2 = math.min(min2, math.lengthsq(map.MicroSeeds[i] - map.MicroSeeds[j]));
                double closest = math.sqrt(min2) / c.Continents.MicroSpacing;
                TestContext.WriteLine($"{c.Name}: closest micro seeds {closest:F3} D apart");
                Assert.Greater(closest, 0.4, $"{c.Name}: two micro seeds are {closest:F3} micro spacings apart");
            }
        }

        // The scan's nearest seeds, and the radii Stage A reports from them, against brute force at
        // every stored texel of the top level.
        [Test]
        public void ScanMatchesBruteForce()
        {
            foreach (var c in Cases())
            {
                var map    = Build(c);
                var layout = c.Layout;
                int top = layout.LevelCount - 1, n = layout.Cells(top), w = layout.Width(top);
                ContinentTexel[] scan = map.Scan(ContinentMap.TopPoints(c.Surface, c.Roots, layout));
                double cover2 = 0.0, sat2 = 0.0;
                for (int k = 0; k < scan.Length; k++)
                {
                    ContinentMap.ScanCoords(k, w, out int q, out int i, out int j);
                    Assert.AreEqual(k, ContinentMap.ScanIndex(q, i, j, w), "ScanIndex inverts ScanCoords");
                    double3 p = SurfaceMath.SurfacePoint(c.Surface, c.Roots[q], i / (double)n, j / (double)n);
                    int macro = Brute(map.MacroSeeds, p, out double macro2);
                    int micro = Brute(map.MicroSeeds, p, out double micro2);
                    Assert.AreEqual(macro, scan[k].Macro, $"{c.Name}: nearest macro seed at ({q}, {i}, {j})");
                    Assert.AreEqual(micro, scan[k].Micro, $"{c.Name}: nearest micro seed at ({q}, {i}, {j})");
                    Assert.AreEqual(micro2, scan[k].MicroD2, $"{c.Name}: micro distance at ({q}, {i}, {j})");
                    cover2 = math.max(cover2, macro2);
                    if (i >= 0 && j >= 0 && i <= n && j <= n) sat2 = math.max(sat2, micro2);
                }
                Assert.AreEqual(math.sqrt(cover2), map.Stats.CoverageRadius, $"{c.Name}: coverage radius");
                Assert.AreEqual(math.sqrt(sat2) / c.Continents.MicroSpacing, map.Stats.SaturationRadius,
                    $"{c.Name}: saturation radius");
                Assert.AreEqual(2.25 * cover2, map.BlendRadius2, 1e-9 * cover2, $"{c.Name}: blend radius");
                Assert.Less(map.Stats.SaturationRadius, math.SQRT2, $"{c.Name}: saturation");
                TestContext.WriteLine($"{c.Name}: coverage {map.Stats.CoverageRadius / c.Continents.MacroSpacing:F3} " +
                                      $"macro spacings, saturation {map.Stats.SaturationRadius:F3} micro spacings");
            }
        }

        static int Brute(double3[] seeds, double3 p, out double d2)
        {
            int best = 0;
            d2 = double.MaxValue;
            for (int i = 0; i < seeds.Length; i++)
            {
                double e = math.lengthsq(p - seeds[i]);
                if (e < d2) { d2 = e; best = i; }
            }
            return best;
        }

        // The class distances are exact within the cap, against brute force, on points on the
        // surface, off it by up to a micro spacing as a warped point is, and past the grid's box.
        [Test]
        public void ClassDistancesMatchBruteForce()
        {
            foreach (var c in Cases())
            {
                var    map    = Build(c);
                var    tables = new ArrayContinentTables(map);
                double d      = c.Continents.MicroSpacing, cap2 = c.Continents.Cap2;
                var    rng    = new Unity.Mathematics.Random(0xD15u);
                int    outside = 0;
                for (int k = 0; k < 4000; k++)
                {
                    var q = c.Roots[rng.NextInt(c.Roots.Length)];
                    // u and v a little past the quad, so cylinder rims and plane edges are crossed.
                    SurfaceMath.SurfaceFrame(c.Surface, q, rng.NextDouble(-0.05, 1.05), rng.NextDouble(-0.05, 1.05),
                                             out double3 p, out float3 normal);
                    p += (double3)normal * rng.NextDouble(-d, d) + rng.NextDouble3(-0.5 * d, 0.5 * d);
                    if (!map.Grid.Contains(map.Grid.CellOf(p))) outside++;

                    ContinentMap.ClassDistances(tables, p, out double land2, out double ocean2);
                    double bl = double.MaxValue, bo = double.MaxValue;
                    for (int i = 0; i < map.MicroSeeds.Length; i++)
                    {
                        double e = math.lengthsq(p - map.MicroSeeds[i]);
                        if (map.MicroLand[i] != 0) bl = math.min(bl, e); else bo = math.min(bo, e);
                    }
                    Assert.AreEqual(math.min(bl, cap2), math.min(land2, cap2), $"{c.Name}: land distance at {p}");
                    Assert.AreEqual(math.min(bo, cap2), math.min(ocean2, cap2), $"{c.Name}: ocean distance at {p}");

                    ContinentMap.NearestMicro(tables, p, out double near2);
                    Assert.AreEqual(math.min(bl, bo), near2, $"{c.Name}: nearest micro seed at {p}");
                }
                if (c.Surface.Type != SurfaceType.CubeSphere)
                    Assert.Greater(outside, 0, $"{c.Name}: no point fell outside the grid's box");
            }
        }

        // Each configuration K9 and the review refuse, refused; and the macro parameters must come
        // from the same configuration as the continents.
        [Test]
        public void ParamsRefuseInvalidConfigurations()
        {
            var good = SmallConfig(8, 60, 2);
            var bad  = new List<(string, ContinentConfig)>();
            void Add(string what, Func<ContinentConfig, ContinentConfig> f) => bad.Add((what, f(good)));
            Add("no macro cells",           k => { k.MacroCells = 0; return k; });
            Add("no micro cells",           k => { k.MicroCells = 0; return k; });
            Add("no continents",            k => { k.ContinentCount = 0; return k; });
            Add("more continents than cells", k => { k.ContinentCount = 9; return k; });
            Add("decay min over max",       k => { k.DecayMin = 0.8f; k.DecayMax = 0.7f; return k; });
            Add("decay min zero",           k => { k.DecayMin = 0f; return k; });
            Add("decay max over one",       k => { k.DecayMax = 1.1f; return k; });
            Add("negative floor",           k => { k.ElevationFloor = -0.1f; return k; });
            Add("land fraction 0",          k => { k.LandFraction = 0f; return k; });
            Add("land fraction 1",          k => { k.LandFraction = 1f; return k; });
            Add("negative warp",            k => { k.WarpAmplitude = -1f; return k; });
            Add("zero warp wavelength",     k => { k.WarpWavelength = 0f; return k; });
            Add("zero coast width",         k => { k.CoastWidth = 0f; return k; });
            Add("coast width at the cap",   k => { k.CoastWidth = 2f; return k; });
            Add("negative land height",     k => { k.LandHeight = -1f; return k; });
            Add("negative ocean depth",     k => { k.OceanDepth = -1f; return k; });
            Add("interior rise over 2",     k => { k.InteriorRise = 2.5f; return k; });
            Add("negative interior rise",   k => { k.InteriorRise = -0.1f; return k; });
            Add("negative cutoff",          k => { k.DetailCutoff = -1.0; return k; });
            Add("negative coarse amplitude", k => { k.CoarseOctaveAmplitude = -1f; return k; });
            Add("NaN score noise",          k => { k.ScoreNoise = float.NaN; return k; });
            Add("infinite land height",     k => { k.LandHeight = float.PositiveInfinity; return k; });

            var s = SurfaceDef.CubeSphere(6371000.0);
            foreach (var (what, k) in bad)
                Assert.Throws<ArgumentOutOfRangeException>(() => Make("bad", s, k), what);
            Assert.DoesNotThrow(() => Make("good", s, good), "the good configuration");

            // One configuration for both: a bound from one and a field from another would leave the
            // envelope not bounding the field.
            var c     = Make("good", s, good);
            var other = c.Config; other.Continents = null;
            Assert.Throws<ArgumentException>(() => ContinentParams.Create(s, c.Macro, other), "continents in the macro only");
            var hp     = HeightParams.Create(s, 9, 0.0, 12, 100f, 0.5f, 2f, 42u);
            var plain  = MacroParams.Create(s, hp, other);
            Assert.Throws<ArgumentException>(() => ContinentParams.Create(s, plain, c.Config), "continents in the configuration only");
            var deeper = c.Config; var kk = good; kk.OceanDepth = 9000f; deeper.Continents = kk;
            Assert.Throws<ArgumentException>(() => ContinentParams.Create(s, c.Macro, deeper), "a different bound");
            Assert.IsFalse(ContinentParams.Create(s, plain, other).Enabled, "no continents is None");

            Assert.AreEqual(math.max(2000.0 * (1.0 + 0.8f / 2.0), 6000.0), c.Macro.ContinentBound, "the bound");
            Assert.AreEqual(0.0, plain.ContinentBound, "no continents, no bound");
            Assert.IsFalse(plain.HasContinents, "no continents");
        }
    }
}
