using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // M5c-1, task 3 (Docs/M5_MacroLayer_Plan.md, E3): the stream-power pass on the erosion graph.
    // Routing reaches an outlet downhill from everywhere, drainage is right where it is known
    // analytically, incision only lowers, diffusion keeps the range and is refused when unstable, and
    // the pass is deterministic. The Burst job is held to it in MacroMapsTests.
    public class ErosionPassTests
    {
        internal static ErosionParams Params(int iterations = 1, double kdt = 0.0, double kappa = 0.0,
                                             double baseLevel = 0.0, int mBits = 512) => new ErosionParams
        {
            Enabled = true, Iterations = iterations, AreaExponentBits = mBits, AreaExponent = mBits / 1024.0,
            BaseLevel = baseLevel, QuadArea = 1.0, KDt = kdt, DiffusionKDt = kappa,
        };

        static double3 Point(ErosionGraph g, int node) => g.Points[g.NodeTexel[node]];

        // A broad trend and a rough, pitted surface on top of it: every node of the noise a pit or
        // a peak somewhere, so the flood has lakes to fill and the routing has ties to break.
        internal static double[] PittedHeights(ErosionGraphTests.Case c, uint seed = 0x9E3779B9u)
        {
            var g   = c.Graph;
            var rng = new Unity.Mathematics.Random(seed);
            var h   = new double[g.NodeCount];
            double size = math.sqrt(ContinentParams.SurfaceArea(c.Surface));
            for (int node = 0; node < g.NodeCount; node++)
            {
                double3 p = Point(g, node);
                double trend = c.Surface.Type == SurfaceType.CubeSphere ? p.y / c.Surface.Radius
                             : c.Surface.Type == SurfaceType.Cylinder   ? p.z / c.Surface.Length - 0.3
                             : p.z / size - 0.3;
                h[node] = 2000.0 * trend + rng.NextDouble(-300.0, 300.0);
            }
            return h;
        }

        static IEnumerable<(int m, float len)> Links(ErosionGraph g, int node)
        {
            for (int k = 0; k < ErosionGraph.MaxNeighbours; k++)
            {
                int m = g.Neighbours[node * ErosionGraph.MaxNeighbours + k];
                if (m >= 0) yield return (m, g.Lengths[node * ErosionGraph.MaxNeighbours + k]);
            }
        }

        static bool IsNeighbour(ErosionGraph g, int a, int b)
        {
            foreach (var (m, _) in Links(g, a)) if (m == b) return true;
            return false;
        }

        // From every node the receivers lead downhill on the filled surface, strictly, to an outlet:
        // a node at or below the base level, or the lowest node where there is none. The stack puts
        // every receiver before its donors, the filled surface is never below the ground, and all the
        // area drains out.
        [Test]
        public void RoutingReachesAnOutletDownhill()
        {
            foreach (var c in ErosionGraphTests.Cases())
            foreach (double baseLevel in new[] { 0.0, -1e9 })
            {
                var g = c.Graph;
                var h = PittedHeights(c);
                var r = ErosionPass.Run(Params(baseLevel: baseLevel), g, h);
                string name = $"{c.Name}, base level {baseLevel}";

                int lowest = 0;
                for (int i = 1; i < g.NodeCount; i++) if (h[i] < h[lowest]) lowest = i;

                var position = new int[g.NodeCount];
                for (int i = 0; i < g.NodeCount; i++) position[i] = -1;
                for (int s = 0; s < g.NodeCount; s++)
                {
                    Assert.AreEqual(-1, position[r.Stack[s]], $"{name}: the stack lists {r.Stack[s]} twice");
                    position[r.Stack[s]] = s;
                }

                double outletArea = 0.0, total = 0.0;
                for (int i = 0; i < g.NodeCount; i++)
                {
                    total += g.Areas[i];
                    Assert.GreaterOrEqual(r.Filled[i], h[i], $"{name}: node {i} filled below its ground");
                    int rec = r.Receivers[i];
                    if (rec == i)
                    {
                        Assert.IsTrue(h[i] <= baseLevel || (baseLevel < -1e8 && i == lowest),
                                      $"{name}: node {i} drains itself but is no outlet");
                        Assert.AreEqual(h[i], r.Filled[i], $"{name}: outlet {i} is filled");
                        outletArea += r.Drainage[i];
                        continue;
                    }
                    Assert.IsTrue(IsNeighbour(g, i, rec), $"{name}: node {i}'s receiver is not a neighbour");
                    Assert.Less(r.Filled[rec], r.Filled[i], $"{name}: node {i} drains uphill");
                    Assert.Less(position[rec], position[i], $"{name}: node {i} is stacked before its receiver");
                }
                Assert.AreEqual(1.0, outletArea / total, 1e-12, $"{name}: area lost or made");
                if (baseLevel < -1e8)
                    Assert.AreEqual(lowest, r.Receivers[lowest], $"{name}: the lowest node is not the outlet");
            }
        }

        // A tilted plane with its low row as the outlets: the steepest descent is straight down the
        // tilt (an orthogonal step falls 1 per metre, a diagonal 1 / sqrt 2), so each node drains
        // exactly the column above it.
        [Test]
        public void ATiltedPlaneDrainsItsColumns()
        {
            var c = ErosionGraphTests.Make("plane", SurfaceDef.PlaneGrid(1000.0, 2, 3), 4, 2);
            var g = c.Graph;
            // The grid is centred on the origin, so the tilt is measured from its low row.
            double low = double.MaxValue;
            for (int i = 0; i < g.NodeCount; i++) low = math.min(low, Point(g, i).z);
            var h = new double[g.NodeCount];
            for (int i = 0; i < g.NodeCount; i++) h[i] = Point(g, i).z - low;
            var r = ErosionPass.Run(Params(), g, h);

            for (int i = 0; i < g.NodeCount; i++)
            {
                double3 p = Point(g, i);
                double upslope = 0.0;
                for (int j = 0; j < g.NodeCount; j++)
                {
                    double3 q = Point(g, j);
                    if (q.x == p.x && q.z >= p.z) upslope += g.Areas[j];
                }
                Assert.AreEqual(1.0, r.Drainage[i] / upslope, 1e-12, $"node {i} at {p}");
                if (h[i] > 0.0)
                    Assert.AreEqual(p.x, Point(g, r.Receivers[i]).x, $"node {i} does not drain straight down");
            }
        }

        // A cone with its apex the one outlet: everything drains to the apex, and no node drains
        // more than the area above its own height, which is all that can reach it.
        [Test]
        public void AConeDrainsToItsApex()
        {
            var c    = ErosionGraphTests.Make("plane", SurfaceDef.PlaneGrid(1000.0, 2, 2), 8, 2);
            var g    = c.Graph;
            var apex = double3.zero;   // the grid's centre, where its four quads meet
            var h    = new double[g.NodeCount];
            for (int i = 0; i < g.NodeCount; i++) h[i] = math.length(Point(g, i) - apex);
            var r = ErosionPass.Run(Params(), g, h);

            double total = 0.0;
            foreach (double a in g.Areas) total += a;
            int outlets = 0;
            for (int i = 0; i < g.NodeCount; i++)
            {
                if (r.Receivers[i] == i)
                {
                    outlets++;
                    Assert.AreEqual(0.0, h[i], "the outlet is not the apex");
                    Assert.AreEqual(1.0, r.Drainage[i] / total, 1e-12, "the apex does not drain everything");
                    continue;
                }
                double above = 0.0;
                for (int j = 0; j < g.NodeCount; j++) if (h[j] >= h[i]) above += g.Areas[j];
                Assert.GreaterOrEqual(r.Drainage[i], g.Areas[i], $"node {i}");
                Assert.LessOrEqual(r.Drainage[i], above * (1 + 1e-12), $"node {i} drains more than is above it");
            }
            Assert.AreEqual(1, outlets);
        }

        // With diffusion off, incision only ever lowers ground (D <= 0), outlets do not move, and a
        // pass with nothing to do leaves the heights as they were, to the bit.
        [Test]
        public void IncisionOnlyLowers()
        {
            foreach (var c in ErosionGraphTests.Cases())
            {
                var h = PittedHeights(c);
                var r = ErosionPass.Run(Params(iterations: 10, kdt: 0.05), c.Graph, h);
                int lowered = 0;
                for (int i = 0; i < h.Length; i++)
                {
                    Assert.LessOrEqual(r.Heights[i], h[i], $"{c.Name}: node {i} rose");
                    if (h[i] <= 0.0) Assert.AreEqual(h[i], r.Heights[i], $"{c.Name}: outlet {i} moved");
                    if (r.Heights[i] < h[i]) lowered++;
                }
                Assert.Greater(lowered, h.Length / 10, $"{c.Name}: almost nothing was cut");

                var none = ErosionPass.Run(Params(iterations: 3), c.Graph, h);
                CollectionAssert.AreEqual(h, none.Heights, $"{c.Name}: no incision and no diffusion changed the heights");
            }
        }

        // Within one step, cutting harder lowers every node at least as far: each node's new height
        // rises with its receiver's and falls with F, and the receivers are updated first.
        [Test]
        public void HarderIncisionCutsDeeper()
        {
            foreach (var c in ErosionGraphTests.Cases())
            {
                var h    = PittedHeights(c);
                var soft = ErosionPass.Run(Params(kdt: 0.01), c.Graph, h);
                var hard = ErosionPass.Run(Params(kdt: 0.1),  c.Graph, h);
                for (int i = 0; i < h.Length; i++)
                    Assert.LessOrEqual(hard.Heights[i], soft.Heights[i], $"{c.Name}: node {i}");
            }
        }

        // Diffusion within its stability limit is a convex blend of each node and its neighbours, and
        // incision lands each node between itself and its receiver, so the result stays within the
        // range it started in; a lone spike spreads.
        [Test]
        public void DiffusionKeepsTheRangeAndSpreadsASpike()
        {
            foreach (var c in ErosionGraphTests.Cases())
            {
                var    g     = c.Graph;
                var    h     = PittedHeights(c);
                double unit  = ErosionPass.DiffusionNumber(1.0, g.NodeCount, new ErosionGraph.Floats(g.Lengths));
                var    r     = ErosionPass.Run(Params(iterations: 5, kdt: 0.02, kappa: 0.9 / unit), g, h);
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (double x in h) { lo = math.min(lo, x); hi = math.max(hi, x); }
                foreach (double x in r.Heights) Assert.That(x, Is.InRange(lo, hi), c.Name);
            }

            var flat = ErosionGraphTests.Make("plane", SurfaceDef.PlaneGrid(1000.0, 2, 2), 8, 1);
            var fg   = flat.Graph;
            var fh   = new double[fg.NodeCount];
            for (int i = 0; i < fh.Length; i++) fh[i] = 1.0;
            int spike = fg.NodeOf[ErosionGraph.Texel(fg.Cells, 0, 4, 4)];
            fh[spike] = 101.0;
            double u2  = ErosionPass.DiffusionNumber(1.0, fg.NodeCount, new ErosionGraph.Floats(fg.Lengths));
            var    out1 = ErosionPass.Run(Params(kappa: 0.5 / u2), fg, fh);
            Assert.Less(out1.Heights[spike], 101.0, "the spike did not fall");
            foreach (var (m, _) in Links(fg, spike))
                Assert.Greater(out1.Heights[m], 1.0, $"neighbour {m} did not rise");
        }

        [Test]
        public void UnstableDiffusionIsRefused()
        {
            var c    = ErosionGraphTests.Cases()[0];
            var g    = c.Graph;
            double u = ErosionPass.DiffusionNumber(1.0, g.NodeCount, new ErosionGraph.Floats(g.Lengths));
            var h    = PittedHeights(c);
            Assert.DoesNotThrow(() => ErosionPass.Run(Params(kappa: 1.0 / u), g, h), "at the limit");
            Assert.Throws<ArgumentOutOfRangeException>(() => ErosionPass.Run(Params(kappa: 1.01 / u), g, h), "past it");
            Assert.Throws<ArgumentException>(() => ErosionPass.Run(ErosionParams.None, g, h), "no erosion");
            Assert.Throws<ArgumentException>(() => ErosionPass.Run(Params(), g, new double[3]), "wrong length");
        }

        // A plane rising from a sea row at the base level, with a pit far below it in the middle:
        // the shape of the ring's inland hollows. Node heights by texel; the pit is 2 x 2 nodes.
        static (ErosionGraph g, double[] h, bool[] pit, bool[] sea) PitAndSea()
        {
            var c   = ErosionGraphTests.Make("plane", SurfaceDef.PlaneGrid(1000.0, 1, 1), 8, 2);
            var g   = c.Graph;
            var h   = new double[g.NodeCount];
            var pit = new bool[g.NodeCount];
            var sea = new bool[g.NodeCount];
            for (int node = 0; node < g.NodeCount; node++)
            {
                ErosionGraph.Coords(g.Cells, g.NodeTexel[node], out _, out int i, out int j);
                sea[node] = j == 0;
                pit[node] = (i == 8 || i == 9) && (j == 8 || j == 9);
                h[node]   = sea[node] ? 0.0 : pit[node] ? -1000.0 : 100.0 + 10.0 * j + 3.0 * math.abs(i - 8);
            }
            return (g, h, pit, sea);
        }

        static double AreaOf(ErosionGraph g, bool[] which)
        {
            double a = 0.0;
            for (int i = 0; i < which.Length; i++) if (which[i]) a += g.Areas[i];
            return a;
        }

        // A water body under MinOutletArea is a hollow: its nodes are not outlets, and the land that
        // drained into it drains on through it to the sea. At 0 every body is an outlet, as before.
        // Above every body, the largest is still the sea.
        [Test]
        public void OnlyLargeWaterBodiesAreSeas()
        {
            var (g, h, pit, sea) = PitAndSea();
            double pitArea = AreaOf(g, pit), seaArea = AreaOf(g, sea);
            Assert.Less(pitArea, seaArea, "the test's pit is smaller than its sea");

            foreach (var (threshold, pitIsSea) in new[] { (0.0, true), (0.5 * (pitArea + seaArea), false), (10.0 * seaArea, false) })
            {
                var p = Params();
                p.MinOutletArea = threshold;
                var r = ErosionPass.Run(p, g, h);
                for (int i = 0; i < g.NodeCount; i++)
                {
                    if (sea[i]) Assert.AreEqual(i, r.Receivers[i], $"threshold {threshold}: sea node {i} is not an outlet");
                    if (pit[i]) Assert.AreEqual(pitIsSea, r.Receivers[i] == i, $"threshold {threshold}: pit node {i}");
                    // Every node's water ends in an outlet, and past the threshold, in the sea row.
                    int x = i;
                    while (r.Receivers[x] != x) x = r.Receivers[x];
                    if (!pitIsSea) Assert.IsTrue(sea[x], $"threshold {threshold}: node {i} drains to {x}, not the sea");
                }
            }
        }

        // A river entering a lake is cut towards the lake's surface, never its floor. With incision
        // strong enough to bring a node all the way to its target in one step, nothing outside the
        // pit may end below the base level: the sea row is at it, and the pit's surface is its spill
        // point, on land above it. Cut to the pit's floor, the nodes around it would fall towards
        // -1,000 m.
        [Test]
        public void IncisionStopsAtTheLakeSurface()
        {
            var (g, h, pit, sea) = PitAndSea();
            var p = Params(iterations: 1, kdt: 1e9);
            p.MinOutletArea = 0.5 * (AreaOf(g, pit) + AreaOf(g, sea));
            var r = ErosionPass.Run(p, g, h);

            int cut = 0;
            for (int i = 0; i < g.NodeCount; i++)
            {
                if (pit[i]) continue;
                Assert.GreaterOrEqual(r.Heights[i], 0.0, $"node {i} was cut below the lake's surface");
                if (r.Heights[i] < h[i]) cut++;
            }
            Assert.Greater(cut, g.NodeCount / 4, "the incision did not run");
        }

        [Test]
        public void TwoRunsAreIdentical()
        {
            foreach (var c in ErosionGraphTests.Cases())
            {
                var    h     = PittedHeights(c);
                double u     = ErosionPass.DiffusionNumber(1.0, c.Graph.NodeCount, new ErosionGraph.Floats(c.Graph.Lengths));
                var    p     = Params(iterations: 4, kdt: 0.05, kappa: 0.5 / u, mBits: 461);
                var    a     = ErosionPass.Run(p, c.Graph, h);
                var    b     = ErosionPass.Run(p, c.Graph, h);
                CollectionAssert.AreEqual(a.Heights,   b.Heights,   c.Name);
                CollectionAssert.AreEqual(a.Drainage,  b.Drainage,  c.Name);
                CollectionAssert.AreEqual(a.Receivers, b.Receivers, c.Name);
                CollectionAssert.AreEqual(a.Stack,     b.Stack,     c.Name);
            }
        }
    }
}
