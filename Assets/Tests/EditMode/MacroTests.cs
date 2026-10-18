using System;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The macro layer's pure core: the pyramid's levels, the edge rule and the bicubic read.
    //
    // UnityEngine-free, so it runs in the out-of-Editor harness. The maps here are small --
    // resolution 9, three levels -- because every rule is generic in the cell count and a full
    // Earth pyramid is half a million texels, which the Editor's Mono would take seconds over.
    // MacroMapsTests holds the Burst build to MacroGenerator.Build at a real size.
    public class MacroTests
    {
        const int Resolution = 9;
        const int Cells0     = Resolution - 1;
        const int LevelCount = 3;

        // Every topology, and every kind of corner: three faces on a cube, four roots at the
        // terrarium's interior corners, two at a ring's open ends, a one-column cylinder whose
        // East edge is its own West, and a plane grid with outer edges that link nowhere.
        static readonly (string Name, SurfaceDef Surface)[] Surfaces =
        {
            ("cube sphere",         SurfaceDef.CubeSphere(6371000.0)),
            ("ring",                SurfaceDef.Cylinder(1592750.0, 1250000.0, 8, 1)),
            ("terrarium",           SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4)),
            ("one-column cylinder", SurfaceDef.Cylinder(1000.0, 3000.0, 1, 3)),
            ("plane grid",          SurfaceDef.PlaneGrid(1000.0, 3, 2)),
        };

        // ── The stress scenes' macro layers ──────────────────────────────────
        //
        // Hand copies of each stress scene's MacroLayerSettings asset, held to the files by
        // MacroMapsTests.StressScenesRunTheirMacroLayers. SampleScene has none (ground rule 1).
        //
        // Base heights are about a third of each planet's or ring's detail HeightScale, and equal
        // to the terrarium's, whose 10 km world wants hundreds of metres. They are sized against
        // the relief each scene's LOD allowance has to cover. Measured on 300 random nodes per
        // depth (600 in the terrarium), 17 x 17 vertices each, the roughest node's C rises with
        // the macro from 6.23 to 6.65 on Earth at depth 19, 5.24 to 5.69 on the small planet at
        // depth 17, 5.80 to 6.00 on the ring at 16 and 3.62 to 4.03 in the terrarium at 9 (its
        // worst, 4.63, is at depth 6), all well inside the pinned C of 15, 15, 15 and 9
        // (2026-10-18). The terrarium at 300 m reached 5.74, too close to 9 for an estimate.
        // The selector's end-of-session report of the roughest chunk is the arbiter.
        internal const double MacroWavelength0  = 0.0;
        internal const float  MacroPersistence  = 0.5f;
        internal const float  PoleTemperature   = -30f;
        internal const float  LapseRate         = 6.5f;
        internal const float  TemperatureNoise  = 5f;
        internal const float  MoistureNoise     = 1.5f;
        internal const float  RuggednessNoise   = 1.5f;
        internal const uint   MacroSeed         = 7u;

        internal sealed class SceneMacro
        {
            public string     Scene, Settings, Asset;
            public SurfaceDef Surface;
            public int        Levels;
            public float      BaseHeightScale, EquatorTemperature;

            public MacroConfig Config => new MacroConfig
            {
                LevelCount = Levels, Wavelength0 = MacroWavelength0, Persistence = MacroPersistence,
                BaseHeightScale = BaseHeightScale, EquatorTemperature = EquatorTemperature,
                PoleTemperature = PoleTemperature, LapseRate = LapseRate, TemperatureNoise = TemperatureNoise,
                MoistureNoise = MoistureNoise, RuggednessNoise = RuggednessNoise, Seed = MacroSeed,
            };
        }

        // The cylinders have no poles, so EquatorTemperature is their temperature everywhere at
        // zero base height: temperate rather than tropical. The terrarium stops at three levels,
        // because its 32 roots would make four 47 MB, and level 2's 60 m texels are already
        // finer than anything a macro layer is for in a 10 km world.
        internal static readonly SceneMacro[] StressMacros =
        {
            new SceneMacro { Scene = "EarthStress", Settings = "Assets/Settings/EarthTerrainSettings.asset",
                             Asset = "Assets/Settings/EarthMacro.asset", Surface = EarthScaleTests.Earth,
                             Levels = 4, BaseHeightScale = 3000f, EquatorTemperature = 30f },
            new SceneMacro { Scene = "SmallPlanetStress", Settings = StressSceneTests.SmallPlanet.Asset,
                             Asset = "Assets/Settings/SmallPlanetMacro.asset", Surface = StressSceneTests.SmallPlanet.Surface,
                             Levels = 4, BaseHeightScale = 2000f, EquatorTemperature = 30f },
            new SceneMacro { Scene = "RingworldStress", Settings = StressSceneTests.Ringworld.Asset,
                             Asset = "Assets/Settings/RingworldMacro.asset", Surface = StressSceneTests.Ringworld.Surface,
                             Levels = 4, BaseHeightScale = 1500f, EquatorTemperature = 18f },
            new SceneMacro { Scene = "TerrariumStress", Settings = StressSceneTests.Terrarium.Asset,
                             Asset = "Assets/Settings/TerrariumMacro.asset", Surface = StressSceneTests.Terrarium.Surface,
                             Levels = 3, BaseHeightScale = 200f, EquatorTemperature = 18f },
        };

        // Each stress scene's macro layer is a valid configuration, small enough to build at
        // startup without thought, and carries continental octaves at its top level.
        [Test]
        public void StressSceneMacroLayersAreValidAndSmall()
        {
            foreach (var m in StressMacros)
            {
                var hp = HeightParams.Create(m.Surface, 33, 0.0, 20, 1000f, 0.5f, 2f, 42u);
                var p  = MacroParams.Create(m.Surface, hp, m.Config);
                double mb = p.Layout.TexelCount * 16.0 / (1024.0 * 1024.0);
                Assert.LessOrEqual(mb, 16.0, $"{m.Scene}: {p.Layout.TexelCount:N0} texels, {mb:F1} MB");
                Assert.AreEqual(3, p.K0, $"{m.Scene}: macro octave 0 at S0 gives octaves 0-3 at level 0");
                Assert.AreEqual(m.Surface.Type == SurfaceType.CubeSphere, p.HasLatitude, $"{m.Scene}: latitude term");
            }
        }

        // C8: what TerrainRoot.HeightAt reads -- the quad SurfaceMath.Locate names, at MaxDepth's
        // level -- is what the deepest chunks read at their own vertices, including on shared
        // edges and corners, where Locate may name the other quad. A tool path, so to a
        // tolerance: Locate's u and v carry about 1e-15 of rounding.
        [Test]
        public void HeightAtReadsWhatTheChunksRead()
        {
            foreach (var s in new[] { SurfaceDef.CubeSphere(6371000.0), SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4) })
            {
                var roots = SurfaceMath.BuildRoots(s);
                var p     = MacroParams.Create(s, Params(s), Config());
                var maps  = new ArrayTexels(MacroGenerator.Build(s, roots, p));
                var rng   = new Unity.Mathematics.Random(77u);
                const int MaxDepth = 12;
                int level = p.Layout.MacroLevel(MaxDepth);

                for (int k = 0; k < 2000; k++)
                {
                    int q = rng.NextInt(0, roots.Length);
                    // Every eighth sample on an edge or corner of the quad.
                    double u = (k & 7) == 0 ? rng.NextInt(0, 2) : rng.NextDouble();
                    double v = (k & 15) == 0 ? rng.NextInt(0, 2) : rng.NextDouble();

                    double3 point = SurfaceMath.SurfacePoint(s, roots[q], u, v);
                    var chunk = MacroSampler.Sample(maps, p.Layout, q, level, u, v);

                    SurfaceMath.Locate(s, point, out int lq, out double lu, out double lv);
                    var tool = MacroSampler.Sample(maps, p.Layout, lq, level, lu, lv);

                    Assert.AreEqual(chunk.BaseHeight, tool.BaseHeight, 1e-6,
                        $"{s.Type} quad {q} ({u}, {v}), located on {lq} ({lu}, {lv})");
                }
            }
        }

        internal readonly struct ArrayTexels : IMacroTexels
        {
            readonly float4[] _texels;
            public ArrayTexels(float4[] texels) { _texels = texels; }
            public float4 Texel(int index) => _texels[index];
        }

        static MacroConfig Config(int levelCount = LevelCount) => new MacroConfig
        {
            LevelCount = levelCount, Wavelength0 = 0.0, Persistence = 0.5f, BaseHeightScale = 3000f,
            EquatorTemperature = 30f, PoleTemperature = -30f, LapseRate = 6.5f, TemperatureNoise = 5f,
            MoistureNoise = 1.5f, RuggednessNoise = 1.5f, Seed = 7u,
        };

        static HeightParams Params(in SurfaceDef s, int resolution = Resolution) =>
            HeightParams.Create(s, resolution, 0.0, 20, 200f, 0.5f, 2f, 42u);

        // Independent values in every texel, then the edge copies. Stronger than the generator for
        // the edge rule, which must hold whatever fills the maps -- erosion will not keep two
        // faces' edge rows equal by accident -- and a smooth field would let a wrong copy pass
        // within rounding.
        static float4[] RandomMaps(in MacroLayout layout, in SurfaceDef s, bool applyEdgeCopies = true)
        {
            var texels = new float4[layout.TexelCount];
            var rng    = new Unity.Mathematics.Random(0x9E3779B9u);
            for (int t = 0; t < texels.Length; t++)
                texels[t] = rng.NextFloat4(-1000f, 1000f);
            if (applyEdgeCopies)
                foreach (int2 c in layout.EdgeCopies(s))
                    texels[c.x] = texels[c.y];
            return texels;
        }

        static void OnEdge(Edge e, double t, out double u, out double v)
        {
            switch (e)
            {
                case Edge.South: u = t;   v = 0.0; return;
                case Edge.East:  u = 1.0; v = t;   return;
                case Edge.North: u = t;   v = 1.0; return;
                default:         u = 0.0; v = t;   return;   // West
            }
        }

        static bool Same(in MacroSample a, in MacroSample b) =>
            a.BaseHeight == b.BaseHeight && a.Temperature == b.Temperature &&
            a.Moisture == b.Moisture && a.Ruggedness == b.Ruggedness;

        delegate MacroSample Sampler(float4[] texels, in MacroLayout layout, int quad, int level,
                                     double u, double v);

        static MacroSample Real(float4[] texels, in MacroLayout layout, int quad, int level,
                                double u, double v)
            => MacroSampler.Sample(new ArrayTexels(texels), layout, quad, level, u, v);

        // The read as anyone would first write it: the textbook Catmull-Rom weights, summed left to
        // right. Algebraically identical to MacroSampler's; the mutation the edge test must catch.
        static MacroSample Textbook(float4[] texels, in MacroLayout layout, int quad, int level,
                                    double u, double v)
        {
            int n = layout.Cells(level);
            Weights(u * n, n, out int ci, out double4 wu);
            Weights(v * n, n, out int cj, out double4 wv);
            int w  = layout.Width(level);
            int at = layout.Index(quad, level, ci - 1, cj - 1);

            double4 m = 0.0;
            for (int r = 0; r < 4; r++)
            {
                double4 row = 0.0;
                for (int k = 0; k < 4; k++) row += wu[k] * (double4)texels[at + r * w + k];
                m += wv[r] * row;
            }
            return new MacroSample { BaseHeight = m.x, Temperature = (float)m.y,
                                     Moisture = (float)m.z, Ruggedness = (float)m.w };

            static void Weights(double x, int n, out int cell, out double4 wt)
            {
                cell = math.clamp((int)math.floor(x), -1, n);
                double t = x - cell, t2 = t * t, t3 = t2 * t;
                wt = new double4((-t3 + 2.0 * t2 - t) * 0.5, (3.0 * t3 - 5.0 * t2 + 2.0) * 0.5,
                                 (-3.0 * t3 + 4.0 * t2 + t) * 0.5, (t3 - t2) * 0.5);
            }
        }

        // Samples both sides of every link at chunk vertices along the edge, at every depth from
        // 0 to 24 -- on texels, between them, and in the last cell at each corner -- and counts
        // the points where the two disagree. Returns the count, and how many of those were on
        // reversed links.
        //
        // The deep depths are not optional. Up to a few depths past the top level the cell
        // fraction t has only a few bits, so every weight and every product of a weight and a
        // float texel is exact in double and the order of the sum cannot matter: the textbook
        // read passes there. A real chunk is deeper -- Earth's depth-19 vertices read level 3 with
        // a 16-bit t -- and the products round. Shallow depths take every vertex; deep ones the
        // four at each end of the edge and a fixed random sample between.
        //
        // Only a link that reverses needs them. On one that does not, both sides do the same
        // arithmetic on the same texels, so all a sample can catch there is a wrong copy, and two
        // depths past the top level already read every edge-row texel of every level.
        const int DeepestDepth  = 24;
        const int FullEdgeLimit = 1024;
        const int DeepSamples   = 128;

        static int EdgeDisagreements(in SurfaceDef s, in MacroLayout layout, float4[] texels,
                                     Sampler sample, out int onReversed, out int samples)
        {
            int bad = 0;
            onReversed = 0;
            samples = 0;
            var rng = new Unity.Mathematics.Random(12345u);
            for (int q = 0; q < s.QuadCount; q++)
            for (int e = 0; e < 4; e++)
            {
                if (!SurfaceMath.QuadEdgeLink(s, q, (Edge)e, out EdgeLink link)) continue;

                int deepest = link.Reversed ? DeepestDepth : layout.LevelCount + 1;
                for (int depth = 0; depth <= deepest; depth++)
                {
                    int level    = layout.MacroLevel(depth);
                    int vertices = Cells0 << depth;   // vertex intervals along the edge at this depth
                    bool all     = vertices <= FullEdgeLimit;
                    int  count   = all ? vertices + 1 : 8 + DeepSamples;
                    for (int c = 0; c < count; c++)
                    {
                        int k = all ? c
                              : c < 4 ? c
                              : c < 8 ? vertices - (c - 4)
                              : rng.NextInt(4, vertices - 3);

                        // Exactly the (u, v) ChunkGrid.VertexUV gives an edge vertex at this depth:
                        // k / (Cells0 2^d), a dyadic, and its reverse is exact.
                        double t = k / (double)vertices;
                        OnEdge((Edge)e, t, out double ua, out double va);
                        OnEdge(link.Edge, link.Reversed ? 1.0 - t : t, out double ub, out double vb);

                        var a = sample(texels, layout, q,         level, ua, va);
                        var b = sample(texels, layout, link.Face, level, ub, vb);
                        samples++;
                        if (Same(a, b)) continue;
                        bad++;
                        if (link.Reversed) onReversed++;
                    }
                }
            }
            return bad;
        }

        // C2: a macro sample at a shared edge point is bit-identical from both quads, at every
        // level, on every topology. The base height is kilometres, so anything less than
        // identical along a cube edge is a crack in the ground rather than in the detail.
        //
        // Two mutation checks keep the test honest, the way RebaseIsExactAfterAThousandShifts was
        // checked against position -= shift: the textbook read must fail it on the cube's eight
        // reversed links (and only there, because on a non-reversed link both sides do the same
        // arithmetic), and maps without the edge copies must fail it everywhere.
        [Test]
        public void MacroEdgesAgreeAcrossEveryLink()
        {
            foreach (var (name, s) in Surfaces)
            {
                var layout = new MacroLayout(Cells0, LevelCount, s.QuadCount);
                var maps   = RandomMaps(layout, s);

                int bad = EdgeDisagreements(s, layout, maps, Real, out _, out int samples);
                Assert.AreEqual(0, bad, $"{name}: {bad} of {samples} edge samples disagree across a link");

                int unshared = EdgeDisagreements(s, layout, RandomMaps(layout, s, applyEdgeCopies: false),
                                                 Real, out _, out _);
                Assert.Greater(unshared, samples / 2,
                    $"{name}: without the edge copies the two sides should disagree almost everywhere; " +
                    "the test is not reading the edge rows");

                int textbook = EdgeDisagreements(s, layout, maps, Textbook, out int reversed, out _);
                if (s.Type == SurfaceType.CubeSphere)
                    Assert.Greater(reversed, 0,
                        "the textbook weights summed left to right should disagree on a reversed cube " +
                        "link; if they do not, the test is not sampling enough of the eight");
                Assert.AreEqual(textbook, reversed,
                    $"{name}: the textbook read disagrees on a link that is not reversed, where both " +
                    "sides do the same arithmetic");
            }
        }

        // The same agreement on maps the real generator filled, Earth's sphere and the terrarium.
        [Test]
        public void GeneratedMacroEdgesAgreeAcrossEveryLink()
        {
            foreach (var s in new[] { SurfaceDef.CubeSphere(6371000.0), SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4) })
            {
                var p    = MacroParams.Create(s, Params(s), Config());
                var maps = MacroGenerator.Build(s, SurfaceMath.BuildRoots(s), p);

                int bad = EdgeDisagreements(s, p.Layout, maps, Real, out _, out int samples);
                Assert.AreEqual(0, bad, $"{s.Type}: {bad} of {samples} edge samples disagree");
            }
        }

        // Every texel on a shared edge row is a copy of a texel nothing copies into, so the copies
        // can be applied once, in any order. If a source were itself a copy, the result would
        // depend on the order MacroMaps happened to apply them in.
        [Test]
        public void EdgeCopiesReadOnlyOwnedTexels()
        {
            foreach (var (name, s) in Surfaces)
            {
                var layout = new MacroLayout(Cells0, LevelCount, s.QuadCount);
                var copies = layout.EdgeCopies(s);
                var dst    = new System.Collections.Generic.HashSet<int>();
                foreach (int2 c in copies)
                    Assert.IsTrue(dst.Add(c.x), $"{name}: texel {c.x} is copied into twice");
                foreach (int2 c in copies)
                    Assert.IsFalse(dst.Contains(c.y), $"{name}: texel {c.y} is a source and a copy");
            }
        }

        // The corner rule: three quads at a cube corner, four at the terrarium's, two at a ring's
        // open end, and all of them read the lowest-indexed quad's corner texel.
        [Test]
        public void CornersComeFromTheLowestQuad()
        {
            var cube = SurfaceDef.CubeSphere(1.0);
            var cl   = new MacroLayout(Cells0, 1, 6);
            int n    = Cells0;
            var sources = new System.Collections.Generic.Dictionary<(int, int, int), int>();
            for (int q = 0; q < 6; q++)
            foreach (var (i, j) in new[] { (0, 0), (n, 0), (0, n), (n, n) })
            {
                cl.EdgeSource(cube, q, 0, i, j, out int sq, out int si, out int sj);
                Assert.IsTrue((si == 0 || si == n) && (sj == 0 || sj == n), "a corner's source is a corner");
                Assert.LessOrEqual(sq, q);
                // The source is its own source: a corner nothing copies into.
                Assert.IsFalse(cl.EdgeSource(cube, sq, 0, si, sj, out _, out _, out _));
                sources.TryGetValue((sq, si, sj), out int readers);
                sources[(sq, si, sj)] = readers + 1;
            }
            // Eight cube corners, each read by its three faces from one texel.
            Assert.AreEqual(8, sources.Count);
            foreach (var kv in sources) Assert.AreEqual(3, kv.Value, $"corner source {kv.Key}");

            var terrarium = SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4);
            var tl        = new MacroLayout(Cells0, 1, terrarium.QuadCount);
            // Quad (col 1, row 1) = 5, its South-West corner: shared with (0, 0) = 0, (0, 1) = 1
            // and (1, 0) = 4. Quad 0 owns it, at its own North-East corner.
            Assert.IsTrue(tl.EdgeSource(terrarium, 5, 0, 0, 0, out int tq, out int ti, out int tj));
            Assert.AreEqual((0, n, n), (tq, ti, tj));

            var ring = SurfaceDef.Cylinder(1592750.0, 1250000.0, 8, 1);
            var rl   = new MacroLayout(Cells0, 1, ring.QuadCount);
            // Quad 7's South-East corner is quad 0's South-West, across the closing seam.
            Assert.IsTrue(rl.EdgeSource(ring, 7, 0, n, 0, out int rq, out int ri, out int rj));
            Assert.AreEqual((0, 0, 0), (rq, ri, rj));
            Assert.IsFalse(rl.EdgeSource(ring, 0, 0, 0, 0, out _, out _, out _));
        }

        // C1: at every depth up to the top level a chunk's vertices land on texels, and the read
        // returns the stored texel exactly -- weights (0, 1, 0, 0) on both axes, no interpolation.
        // The coarse job's even vertices, read at the parent's level, land on that level's
        // texels too. Border vertices included.
        [Test]
        public void MacroReadIsExactOnTexels()
        {
            var s     = SurfaceDef.CubeSphere(6371000.0);
            var p     = MacroParams.Create(s, Params(s), Config());
            var maps  = MacroGenerator.Build(s, SurfaceMath.BuildRoots(s), p);
            var texel = new ArrayTexels(maps);

            for (int q = 0; q < 6; q++)
            for (int depth = 0; depth < LevelCount; depth++)
            {
                int span = 1 << depth;
                for (int x = 0; x < span; x++)
                for (int y = 0; y < span; y++)
                {
                    var node = new NodeId(q, depth, x, y);
                    for (int j = -1; j <= Resolution; j++)
                    for (int i = -1; i <= Resolution; i++)
                    {
                        ChunkGrid.VertexUV(node, i, j, Resolution, out double u, out double v);
                        ExpectTexel(maps, texel, p.Layout, q, p.Layout.MacroLevel(depth),
                                    x * Cells0 + i, y * Cells0 + j, u, v, node, i, j);

                        // The coarse job's even vertices, at the parent's level.
                        if (depth == 0 || ((i | j) & 1) != 0) continue;
                        int parent = p.Layout.MacroLevel(depth - 1);
                        ExpectTexel(maps, texel, p.Layout, q, parent,
                                    (x * Cells0 + i) / 2, (y * Cells0 + j) / 2, u, v, node, i, j);
                    }
                }
            }
        }

        static void ExpectTexel(float4[] maps, ArrayTexels texel, in MacroLayout layout, int q,
                                int level, int ti, int tj, double u, double v, NodeId node, int i, int j)
        {
            float4 want = maps[layout.Index(q, level, ti, tj)];
            var    got  = MacroSampler.Sample(texel, layout, q, level, u, v);
            Assert.IsTrue(got.BaseHeight == want.x && got.Temperature == want.y &&
                          got.Moisture == want.z && got.Ruggedness == want.w,
                $"{node} vertex ({i}, {j}) at level {level}: read ({got.BaseHeight}, {got.Temperature}, " +
                $"{got.Moisture}, {got.Ruggedness}) where texel ({ti}, {tj}) holds {want}");
        }

        // C1: the level is depth clamped to the pyramid, a function of depth alone, so same-depth
        // neighbours read the same level whatever their measured sizes. Up to the top level its
        // texel spacing is exactly the depth's vertex spacing; it is never finer, which is what
        // keeps a sample job's one-vertex border within the one-texel reach the stored border
        // allows for (C2).
        [Test]
        public void MacroLevelIsDepthOnly()
        {
            foreach (int levels in new[] { 1, 3, 4, 6 })
            {
                var layout = new MacroLayout(32, levels, 6);
                for (int depth = -1; depth <= 30; depth++)
                {
                    int level = layout.MacroLevel(depth);
                    Assert.AreEqual(math.clamp(depth, 0, levels - 1), level, $"{levels} levels, depth {depth}");
                    Assert.LessOrEqual(level, math.max(depth, 0), "a level finer than its depth's vertex spacing");
                    if (depth >= 0 && depth < levels)
                        Assert.AreEqual(32 << depth, layout.Cells(level),
                            $"{levels} levels, depth {depth}: texel spacing differs from vertex spacing");
                }
            }
        }

        // C1: each level carries every macro octave of at least four texel spacings, and no
        // finer one. On Earth at resolution 33 with octave 0 at S0 the ratio is exactly 8, so a
        // limit read through log2 rather than the exponent would be one octave short here.
        [Test]
        public void MacroLevelsAreBandLimited()
        {
            var earth = SurfaceDef.CubeSphere(6371000.0);
            var cases = new[]
            {
                (s: earth, r: 33, lambda: 0.0),
                (s: earth, r: 65, lambda: 0.0),
                (s: earth, r: 33, lambda: 2.0e6),
                (s: SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4), r: 33, lambda: 12345.0),
                (s: SurfaceDef.PlaneGrid(1000.0, 2, 2), r: 9, lambda: 300.0),
            };
            foreach (var (s, r, lambda) in cases)
            {
                var hp = Params(s, r);
                var c  = Config(4);
                c.Wavelength0 = lambda;
                var p  = MacroParams.Create(s, hp, c);

                for (int level = 0; level < 4; level++)
                {
                    int    k       = p.MaxOctave(level);
                    double spacing = hp.S0 / p.Layout.Cells(level);
                    string at      = $"{s.Type} R {r} lambda {lambda} level {level} octave {k}";
                    Assert.GreaterOrEqual(p.Wavelength0 / math.pow(2.0, k), 4.0 * spacing,
                        $"{at}: the finest octave is shorter than four texels");
                    Assert.Less(p.Wavelength0 / math.pow(2.0, k + 1), 4.0 * spacing,
                        $"{at}: a further octave would still fit, so the level is band-limited short");
                }
            }

            var earth33 = MacroParams.Create(earth, Params(earth, 33), Config(4));
            Assert.AreEqual(new[] { 3, 4, 5, 6 },
                new[] { earth33.MaxOctave(0), earth33.MaxOctave(1), earth33.MaxOctave(2), earth33.MaxOctave(3) },
                "Earth at resolution 33: octaves 0-3 at level 0 up to 0-6 at level 3, as the plan's table says");
        }

        // Configuration is validated once, in MacroParams.Create.
        [Test]
        public void MacroParamsRefuseUnusableConfigurations()
        {
            var s  = SurfaceDef.CubeSphere(6371000.0);
            var hp = Params(s, 33);

            var c = Config(); c.LevelCount = 0;
            Assert.Throws<ArgumentOutOfRangeException>(() => MacroParams.Create(s, hp, c), "no levels");
            c = Config(); c.LevelCount = 20;
            Assert.Throws<ArgumentOutOfRangeException>(() => MacroParams.Create(s, hp, c), "too many texels");
            c = Config(); c.Persistence = 0f;
            Assert.Throws<ArgumentOutOfRangeException>(() => MacroParams.Create(s, hp, c), "persistence 0");
            c = Config(); c.BaseHeightScale = float.NaN;
            Assert.Throws<ArgumentOutOfRangeException>(() => MacroParams.Create(s, hp, c), "NaN amplitude");
            c = Config(); c.Wavelength0 = -1.0;
            Assert.Throws<ArgumentOutOfRangeException>(() => MacroParams.Create(s, hp, c), "negative wavelength");
            c = Config(); c.Wavelength0 = 1.0;   // 1 m against a 10,000 km face: every level flat
            Assert.Throws<ArgumentOutOfRangeException>(() => MacroParams.Create(s, hp, c), "flat everywhere");

            Assert.IsFalse(MacroParams.Disabled.Enabled);
            Assert.IsTrue(MacroParams.Create(s, hp, Config()).Enabled);
        }

        // The layout addresses every texel exactly once: Decode inverts Index across every
        // quad, level and border texel, and the levels tile the array with no gap.
        [Test]
        public void LayoutDecodeInvertsIndex()
        {
            var layout = new MacroLayout(Cells0, LevelCount, 6);
            var seen   = new bool[layout.TexelCount];
            for (int q = 0; q < 6; q++)
            for (int l = 0; l < LevelCount; l++)
            {
                int n = layout.Cells(l);
                for (int j = -MacroLayout.Border; j <= n + MacroLayout.Border; j++)
                for (int i = -MacroLayout.Border; i <= n + MacroLayout.Border; i++)
                {
                    int at = layout.Index(q, l, i, j);
                    Assert.IsFalse(seen[at], $"texel ({q}, {l}, {i}, {j}) shares index {at}");
                    seen[at] = true;
                    layout.Decode(at, out int dq, out int dl, out int di, out int dj);
                    Assert.AreEqual((q, l, i, j), (dq, dl, di, dj));
                }
            }
            Assert.IsTrue(Array.TrueForAll(seen, b => b), "the layout leaves texels nothing addresses");
        }
    }
}
