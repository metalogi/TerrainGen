using System;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // MacroPyramid: the halo rule, the filter and the filter-down that derive a macro channel's
    // levels from its top (Docs/Continents_Plan.md, K5; the machinery M5c's E4 needs as well).
    //
    // UnityEngine-free, so it runs in the out-of-Editor harness. The layouts are MacroTests' small
    // ones, eight cells at level 0 and three levels, on every topology MacroTests covers: the rules
    // are generic in the cell count, and EdgeDisagreements, reused here, assumes that size.
    public class MacroPyramidTests
    {
        const int Cells0     = 8;
        const int LevelCount = 3;

        static float[] RandomTop(in MacroLayout layout, uint seed, float fillBelow = float.NaN)
        {
            var ch  = new float[layout.TexelCount];
            var rng = new Unity.Mathematics.Random(seed);
            int top = layout.LevelCount - 1;
            for (int t = 0; t < ch.Length; t++)
            {
                layout.Decode(t, out _, out int l, out _, out _);
                ch[t] = l == top ? rng.NextFloat(-1000f, 1000f) : fillBelow;
            }
            return ch;
        }

        static float4[] AsMaps(float[] ch)
        {
            var maps = new float4[ch.Length];
            for (int t = 0; t < ch.Length; t++) maps[t] = new float4(ch[t], 0f, 0f, 0f);
            return maps;
        }

        // Every border texel of every quad and level is filled exactly once, from a texel inside
        // [0, n] of some quad that its own quad owns -- so the fill is one gather in any order,
        // and it reads the bits the edge copies will leave on both sides.
        [Test]
        public void HaloFillsEveryBorderTexelFromAnOwnedTexel()
        {
            foreach (var (name, s) in MacroTests.Surfaces)
            {
                var layout = new MacroLayout(Cells0, LevelCount, s.QuadCount);
                for (int l = 0; l < LevelCount; l++)
                {
                    int n    = layout.Cells(l);
                    int side = n + 1 + 2 * MacroLayout.Border;
                    var halo = layout.HaloSources(s, l);
                    Assert.AreEqual(s.QuadCount * (side * side - (n + 1) * (n + 1)), halo.Length,
                        $"{name}, level {l}: halo size");

                    var dst = new System.Collections.Generic.HashSet<int>();
                    foreach (int2 h in halo)
                    {
                        Assert.IsTrue(dst.Add(h.x), $"{name}, level {l}: texel {h.x} is filled twice");
                        layout.Decode(h.x, out _, out int dl, out int di, out int dj);
                        Assert.AreEqual(l, dl);
                        Assert.IsTrue(di < 0 || di > n || dj < 0 || dj > n, $"{name}: a halo destination inside the quad");

                        layout.Decode(h.y, out int sq, out int sl, out int si, out int sj);
                        Assert.AreEqual(l, sl, $"{name}: a halo source on another level");
                        Assert.IsTrue(si >= 0 && si <= n && sj >= 0 && sj <= n,
                            $"{name}, level {l}: halo source ({sq}, {si}, {sj}) is outside its quad");
                        Assert.IsFalse(layout.EdgeSource(s, sq, l, si, sj, out _, out _, out _),
                            $"{name}, level {l}: halo source ({sq}, {si}, {sj}) is an edge copy, not its owner");
                    }
                }
            }
        }

        // The halo reads the neighbour's texel where the border texel continues to. Distances are
        // in texels per texel past the edge, from a border texel's own continued surface point to
        // its source's point. Measured 2026-10-23 at 8 and 32 cells:
        //
        //   * Within a cylinder or a plane grid the quads continue into each other exactly: 0.
        //   * Across a cube edge the two faces' grid lines are different great circles off the edge
        //     line, so the neighbour's texel is beside the continued point, not on it: 0 at an
        //     edge's midpoint, at most 0.567 over its middle half, and exactly 1.0 at its ends, one
        //     texel from the cube corner, where three faces meet at 120 degrees each.
        //   * Corner blocks and rims have no counterpart and stay within one texel per texel out
        //     (a rim repeats its edge texel, which is exactly that).
        //
        // For a derived channel nothing is defined at the continued point, so the halo only has to
        // read the neighbour's region. The mutation is the one a halo rule is likeliest to get
        // wrong, ignoring a link's reversal: on the cube's reversed links it lands most side
        // texels more than two texels per texel away, up to 25.
        [Test]
        public void HaloReadsTheNeighboursOverlappingTexel()
        {
            foreach (int cells0 in new[] { Cells0, 32 })
            foreach (var (name, s) in MacroTests.Surfaces)
            {
                double worstSide = 0, worstOther = 0, worstMiddle = 0;
                int mirroredFar = 0, mirrored = 0;
                string worstAt = "";
                var roots  = SurfaceMath.BuildRoots(s);
                var layout = new MacroLayout(cells0, cells0 == Cells0 ? LevelCount : 1, s.QuadCount);
                for (int l = 0; l < layout.LevelCount; l++)
                {
                    int n = layout.Cells(l);
                    foreach (int2 h in layout.HaloSources(s, l))
                    {
                        layout.Decode(h.x, out int q, out _, out int i, out int j);
                        layout.Decode(h.y, out int sq, out _, out int si, out int sj);
                        int outI = Math.Abs(i - math.clamp(i, 0, n)), outJ = Math.Abs(j - math.clamp(j, 0, n));
                        double spacing = Spacing(s, roots, sq, n, si, sj);
                        double miss    = math.distance(Point(s, roots, q, n, i, j), Point(s, roots, sq, n, si, sj)) / spacing;

                        bool corner = outI > 0 && outJ > 0;
                        Edge edge   = outJ > 0 ? (j < 0 ? Edge.South : Edge.North) : (i < 0 ? Edge.West : Edge.East);
                        bool linked = SurfaceMath.QuadEdgeLink(s, q, edge, out EdgeLink link);
                        if (!corner && linked)
                        {
                            if (miss / (outI + outJ) > worstSide)
                                worstAt = $"level {l}, ({q}, {i}, {j}) <- ({sq}, {si}, {sj}), n {n}, miss {miss:F3}";
                            worstSide = Math.Max(worstSide, miss / (outI + outJ));
                            int alongAt = outJ > 0 ? i : j;
                            if (alongAt >= n / 4 && alongAt <= 3 * n / 4)
                                worstMiddle = Math.Max(worstMiddle, miss / (outI + outJ));
                            if (link.Reversed)
                            {
                                // The same texel with the reversal ignored.
                                int along = outJ > 0 ? i : j, depth = outI + outJ;
                                Mirror(link.Edge, along, depth, n, out int mi, out int mj);
                                double m = math.distance(Point(s, roots, q, n, i, j), Point(s, roots, link.Face, n, mi, mj)) / spacing;
                                mirrored++;
                                if (m / depth > 2.0) mirroredFar++;
                            }
                        }
                        else worstOther = Math.Max(worstOther, miss / (outI + outJ));
                    }
                }
                TestContext.WriteLine($"{name}, {cells0} cells: worst side miss {worstSide:F4} texels per texel past the edge " +
                                      $"(middle half {worstMiddle:F4}) at {worstAt}; corners and rims {worstOther:F3}; " +
                                      $"reversal ignored: {mirroredFar} of {mirrored} far");

                string at = $"{name}, {cells0} cells";
                Assert.LessOrEqual(worstOther, 1.0 + 1e-6, $"{at}: a corner or rim texel strays more than a texel per texel out");
                if (s.Type == SurfaceType.CubeSphere)
                {
                    Assert.LessOrEqual(worstSide, 1.0 + 1e-6, $"{at}: worst side texel, {worstAt}");
                    Assert.Less(worstMiddle, 0.6, $"{at}: a side texel in the middle half of a cube edge misses by {worstMiddle:F3}");
                    Assert.Greater(mirrored, 0, $"{at}: no reversed link was exercised");
                    Assert.Greater(mirroredFar, mirrored / 2,
                        $"{at}: ignoring a link's reversal should land most of its side texels far away; if it " +
                        "does not, the test cannot tell the reversal is honoured");
                }
                else
                    Assert.Less(worstSide, 1e-6, $"{at}: quads that continue into each other exactly, but {worstAt}");
            }
        }

        static void Mirror(Edge e, int along, int depth, int n, out int i, out int j)
        {
            switch (e)
            {
                case Edge.South: i = along;     j = depth;     return;
                case Edge.North: i = along;     j = n - depth; return;
                case Edge.West:  i = depth;     j = along;     return;
                default:         i = n - depth; j = along;     return;   // East
            }
        }

        static double3 Point(in SurfaceDef s, RootQuad[] roots, int q, int n, int i, int j)
            => SurfaceMath.SurfacePoint(s, roots[q], i / (double)n, j / (double)n);

        // The local texel spacing at a texel inside its quad: the larger of its two axes.
        static double Spacing(in SurfaceDef s, RootQuad[] roots, int q, int n, int i, int j)
        {
            int i1 = i < n ? i + 1 : i - 1, j1 = j < n ? j + 1 : j - 1;
            double3 p = Point(s, roots, q, n, i, j);
            return Math.Max(math.distance(p, Point(s, roots, q, n, i1, j)), math.distance(p, Point(s, roots, q, n, i, j1)));
        }

        // A constant goes through every step exactly -- the filter's weights are integers divided
        // once by a power of two -- and every texel of every level below the top is written: the
        // levels start as NaN, and none survives. Random values never leave the top level's range,
        // since the weights are positive and rounding is monotone. Both with and without the top
        // level's own filter.
        [Test]
        public void ConstantsPassExactlyAndNothingLeavesTheRange()
        {
            foreach (var (name, s) in MacroTests.Surfaces)
            {
                var layout = new MacroLayout(Cells0, LevelCount, s.QuadCount);
                var plan   = MacroPyramidPlan.Create(s, layout);
                foreach (bool filterTop in new[] { true, false })
                {
                    var constant = RandomTop(layout, 1u);
                    int top = LevelCount - 1;
                    for (int t = 0; t < constant.Length; t++)
                    {
                        layout.Decode(t, out _, out int l, out _, out _);
                        if (l == top) constant[t] = 1234.5678f;
                    }
                    MacroPyramid.Run(constant, layout, plan, filterTop);
                    for (int t = 0; t < constant.Length; t++)
                        Assert.AreEqual(1234.5678f, constant[t], $"{name}, filterTop {filterTop}: texel {t} after the pyramid");

                    var random = RandomTop(layout, 2u);
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (int t = 0; t < random.Length; t++)
                        if (!float.IsNaN(random[t])) { lo = Math.Min(lo, random[t]); hi = Math.Max(hi, random[t]); }
                    MacroPyramid.Run(random, layout, plan, filterTop);
                    for (int t = 0; t < random.Length; t++)
                        Assert.IsTrue(random[t] >= lo && random[t] <= hi,
                            $"{name}, filterTop {filterTop}: texel {t} is {random[t]}, outside [{lo}, {hi}]");
                }
            }
        }

        // C2 on a derived channel: after the pyramid, a sample at a shared edge point is
        // bit-identical from both quads at every level, on every topology -- the same check
        // MacroEdgesAgreeAcrossEveryLink makes of generated maps, reversed links to depth 24.
        //
        // The mutation: derive the levels with the halo but without the edge copies below the top,
        // and the two sides disagree on every topology (measured 2026-10-23: 60 to 672 samples).
        [Test]
        public void DerivedEdgesAgreeAcrossEveryLink()
        {
            foreach (var (name, s) in MacroTests.Surfaces)
            {
                var layout = new MacroLayout(Cells0, LevelCount, s.QuadCount);
                var plan   = MacroPyramidPlan.Create(s, layout);

                var ch = RandomTop(layout, 3u);
                MacroPyramid.Run(ch, layout, plan, filterTop: true);
                foreach (int2 c in layout.EdgeCopies(s))
                    Assert.AreEqual(ch[c.y], ch[c.x], $"{name}: edge copy {c.x} <- {c.y} differs after the pyramid");
                int bad = MacroTests.EdgeDisagreements(s, layout, AsMaps(ch), MacroTests.Real, out _, out int samples);
                Assert.AreEqual(0, bad, $"{name}: {bad} of {samples} edge samples disagree across a link");

                // Without the copies below the top.
                var raw     = RandomTop(layout, 3u);
                var arr     = new MacroPyramid.ArrayScalars(raw);
                var scratch = new MacroPyramid.ArrayScalars(new float[raw.Length]);
                var halo    = new MacroPyramid.ArrayPairs(plan.Halo);
                var copies  = new MacroPyramid.ArrayPairs(plan.Copies);
                var ranges  = new MacroPyramid.ArrayPairs(plan.Ranges);
                int top     = LevelCount - 1;
                MacroPyramid.FilterLevel(ref arr, ref scratch, layout, top);
                MacroPyramid.CopyLevel(ref scratch, ref arr, layout, top);
                MacroPyramid.FinishLevel(ref arr, halo, copies, ranges, top);
                for (int l = top - 1; l >= 0; l--)
                {
                    MacroPyramid.Decimate(ref arr, layout, l);
                    MacroPyramid.Gather(ref arr, halo, plan.Ranges[l].x, plan.Ranges[l + 1].x);
                }
                int broken = MacroTests.EdgeDisagreements(s, layout, AsMaps(raw), MacroTests.Real, out _, out _);
                Assert.Greater(broken, 0,
                    $"{name}: without the edge copies below the top the edges should disagree; if they " +
                    "do not, the test is not reading the derived levels' edge rows");
            }
        }

        // How much of a derived level's energy is at wavelengths under four texels, the share C1's
        // truncation would leave at zero: measured on white noise at the top of a single plane tile
        // (no links, so nothing but the filter shapes it), over the interior rows of the level
        // below, against white noise's own share, one half. The binomial is a filter, not a hard
        // cut, so this is a measurement rather than a zero: 25.7% against 50.4% (2026-10-23),
        // close to the 24% its response cos^4 predicts. A smooth field such as the continent
        // height has far less there to begin with. If ringing shows in the bicubic read, the
        // 7-tap binomial is the next step (Docs/Continents_Plan.md, K5).
        [Test]
        public void DerivedLevelsCarryLittleBelowFourTexels()
        {
            var s      = SurfaceDef.PlaneGrid(1000.0, 1, 1);
            var layout = new MacroLayout(32, 3, 1);
            var plan   = MacroPyramidPlan.Create(s, layout);
            var ch     = RandomTop(layout, 4u);

            double white = ShortShare(ch, layout, 2);
            MacroPyramid.Run(ch, layout, plan, filterTop: false);
            double derived = ShortShare(ch, layout, 1);

            TestContext.WriteLine($"energy under four texels: white noise {white:P1}, the level below {derived:P1}");
            Assert.That(white, Is.InRange(0.40, 0.60), "the white-noise reference is not white");
            Assert.Less(derived, 0.30, "the filter-down leaves too much energy under four texels per wavelength");
        }

        // The share of the power at frequencies above a quarter cycle per texel, by DFT along each
        // row of [0, n], over the rows away from the tile's rims.
        static double ShortShare(float[] ch, in MacroLayout layout, int level)
        {
            int n = layout.Cells(level), len = n + 1;
            double shortPower = 0, total = 0;
            for (int j = 4; j <= n - 4; j++)
            {
                double mean = 0;
                for (int i = 0; i <= n; i++) mean += ch[layout.Index(0, level, i, j)];
                mean /= len;
                for (int k = 1; k <= len / 2; k++)
                {
                    double re = 0, im = 0;
                    for (int i = 0; i <= n; i++)
                    {
                        double v = ch[layout.Index(0, level, i, j)] - mean, a = 2 * Math.PI * k * i / len;
                        re += v * Math.Cos(a); im -= v * Math.Sin(a);
                    }
                    double p = re * re + im * im;
                    total += p;
                    if (k / (double)len > 0.25) shortPower += p;
                }
            }
            return shortPower / total;
        }
    }
}
