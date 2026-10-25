using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // M5c-1, task 2 (Docs/M5_MacroLayer_Plan.md, E2): the erosion graph, pinned before any erosion
    // runs on it, because the corner and reversal cases are where it would go wrong. Small top
    // levels, so the file runs in well under a second outside the Editor. The Burst build is held to
    // this one in MacroMapsTests.
    public class ErosionGraphTests
    {
        internal sealed class Case
        {
            public string       Name;
            public SurfaceDef   Surface;
            public RootQuad[]   Roots;
            public MacroLayout  Layout;
            public ErosionGraph Graph;
            public int          N => Graph.Cells;
        }

        internal static Case Make(string name, in SurfaceDef s, int cells0, int levels)
        {
            var roots  = SurfaceMath.BuildRoots(s);
            var layout = new MacroLayout(cells0, levels, s.QuadCount);
            return new Case
            {
                Name = name, Surface = s, Roots = roots, Layout = layout,
                Graph = ErosionGraph.Build(s, roots, layout),
            };
        }

        // A cube sphere (every quad corner a cube corner, 8 reversed links), a ring of one row (open
        // rims), a cylinder of 8 x 4 (interior corners where four quads meet), a cylinder of one
        // column (its East edge is its own West) and a plane grid (outer corners and edges).
        internal static Case[] Cases() => new[]
        {
            Make("sphere",     SurfaceDef.CubeSphere(6371000.0),               8, 2),
            Make("ring",       SurfaceDef.Cylinder(1592750.0, 1250000.0, 8, 1), 4, 2),
            Make("terrarium",  SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4),     4, 1),
            // 16 texels round and 32 along, about square: 2 pi 1000 / 16 = 393 m against 12566 / 32.
            Make("one column", SurfaceDef.Cylinder(1000.0, 12566.0, 1, 2),      16, 1),
            Make("plane",      SurfaceDef.PlaneGrid(1000.0, 2, 3),              4, 2),
        };

        static IEnumerable<int> NeighboursOf(ErosionGraph g, int node)
        {
            for (int k = 0; k < ErosionGraph.MaxNeighbours; k++)
            {
                int m = g.Neighbours[node * ErosionGraph.MaxNeighbours + k];
                if (m >= 0) yield return m;
            }
        }

        static int Degree(ErosionGraph g, int node)
        {
            int d = 0;
            foreach (int _ in NeighboursOf(g, node)) d++;
            return d;
        }

        [Test]
        public void NodeCountsMatchTheSurface()
        {
            foreach (var c in Cases())
            {
                var  s = c.Surface;
                long n = c.N, expected = s.Type switch
                {
                    SurfaceType.CubeSphere => 6 * n * n + 2,
                    SurfaceType.Cylinder   => s.Cols * n * (s.Rows * n + 1),
                    _                      => (s.Cols * n + 1) * (s.Rows * n + 1),
                };
                Assert.AreEqual(expected, c.Graph.NodeCount, c.Name);

                // Every node is its owner's texel, and every texel maps to a node.
                for (int node = 0; node < c.Graph.NodeCount; node++)
                    Assert.AreEqual(node, c.Graph.NodeOf[c.Graph.NodeTexel[node]], $"{c.Name}: node {node}");
                foreach (int node in c.Graph.NodeOf)
                    Assert.That(node, Is.InRange(0, c.Graph.NodeCount - 1), c.Name);
            }
        }

        [Test]
        public void NeighboursAreSymmetricDistinctAndNeverSelf()
        {
            foreach (var c in Cases())
            {
                var g = c.Graph;
                for (int a = 0; a < g.NodeCount; a++)
                {
                    var  seen   = new HashSet<int>();
                    bool padded = false;
                    for (int k = 0; k < ErosionGraph.MaxNeighbours; k++)
                    {
                        int b = g.Neighbours[a * ErosionGraph.MaxNeighbours + k];
                        if (b < 0) { padded = true; continue; }
                        Assert.IsFalse(padded, $"{c.Name}: node {a}'s neighbours are not packed first");
                        Assert.AreNotEqual(a, b, $"{c.Name}: node {a} is its own neighbour");
                        Assert.IsTrue(seen.Add(b), $"{c.Name}: node {a} lists {b} twice");

                        int back = Array.IndexOf(g.Neighbours, a, b * ErosionGraph.MaxNeighbours, ErosionGraph.MaxNeighbours);
                        Assert.GreaterOrEqual(back, 0, $"{c.Name}: {b} is a neighbour of {a} but not the reverse");
                        Assert.AreEqual(g.Lengths[a * ErosionGraph.MaxNeighbours + k], g.Lengths[back],
                                        $"{c.Name}: the link {a}-{b} has two lengths");
                    }
                }
            }
        }

        // Interior nodes, cube edges included, have all eight. A cube corner has six: three along
        // the cube's edges and the diagonal on each face, its two orthogonal steps off the owner's
        // face landing on one node. A rim node has five, and a plane grid's outer corner three.
        [Test]
        public void DegreesFollowTheTopology()
        {
            foreach (var c in Cases())
            {
                var s = c.Surface;
                int n = c.N;
                var count = new Dictionary<int, int>();
                for (int node = 0; node < c.Graph.NodeCount; node++)
                {
                    int d = Degree(c.Graph, node);
                    count[d] = count.TryGetValue(d, out int k) ? k + 1 : 1;
                }

                var expected = new Dictionary<int, int>();
                if (s.Type == SurfaceType.CubeSphere)
                    expected[6] = 8;
                else if (s.Type == SurfaceType.Cylinder)
                    expected[5] = 2 * s.Cols * n;
                else
                {
                    expected[3] = 4;
                    expected[5] = 2 * (s.Cols * n - 1) + 2 * (s.Rows * n - 1);
                }
                int rest = c.Graph.NodeCount;
                foreach (int v in expected.Values) rest -= v;
                expected[8] = rest;

                CollectionAssert.AreEquivalent(expected, count, c.Name);
            }
        }

        // Every link is the chord between its nodes' points, and no longer than a diagonal at the
        // cube face's 1.6:1 spread: a crossing that ignored a link's reversal lands texels across
        // the quad, which this catches.
        [Test]
        public void LengthsAreChordsWithinTheSpread()
        {
            foreach (var c in Cases())
            {
                var    g       = c.Graph;
                double nominal = math.sqrt(ContinentParams.SurfaceArea(c.Surface) / g.NodeCount);
                for (int a = 0; a < g.NodeCount; a++)
                for (int k = 0; k < ErosionGraph.MaxNeighbours; k++)
                {
                    int b = g.Neighbours[a * ErosionGraph.MaxNeighbours + k];
                    if (b < 0)
                    {
                        Assert.AreEqual(0f, g.Lengths[a * ErosionGraph.MaxNeighbours + k], $"{c.Name}: an empty slot");
                        continue;
                    }
                    double chord = math.length(g.Points[g.NodeTexel[a]] - g.Points[g.NodeTexel[b]]);
                    float  len   = g.Lengths[a * ErosionGraph.MaxNeighbours + k];
                    Assert.AreEqual((float)chord, len, $"{c.Name}: link {a}-{b}");
                    Assert.That(len / nominal, Is.InRange(1.0 / 1.7, 1.7 * math.SQRT2_DBL),
                                $"{c.Name}: link {a}-{b} is {len / nominal:F3} nominal spacings");
                }
            }
        }

        // Across every linked edge, each edge texel's node has the neighbour's texel one in at the
        // linked position, reversed where the link is, and not at the unreversed position.
        [Test]
        public void CrossingsFollowEveryLinkAndItsReversal()
        {
            int reversed = 0;
            foreach (var c in Cases())
            {
                var g = c.Graph;
                int n = c.N;
                for (int q = 0; q < c.Surface.QuadCount; q++)
                foreach (Edge e in new[] { Edge.South, Edge.East, Edge.North, Edge.West })
                {
                    if (!SurfaceMath.QuadEdgeLink(c.Surface, q, e, out EdgeLink link)) continue;
                    if (link.Reversed) reversed++;

                    for (int along = 1; along < n; along++)
                    {
                        EdgeTexel(e, along, 0, n, out int i, out int j);
                        int node = g.NodeOf[ErosionGraph.Texel(n, q, i, j)];

                        int a2 = link.Reversed ? n - along : along;
                        EdgeTexel(link.Edge, a2, 1, n, out int ni, out int nj);
                        int across = g.NodeOf[ErosionGraph.Texel(n, link.Face, ni, nj)];
                        CollectionAssert.Contains(NeighboursOf(g, node), across,
                            $"{c.Name}: quad {q} {e} at {along} misses quad {link.Face}'s texel one in");

                        if (!link.Reversed || a2 == along || Math.Abs(a2 - along) < 2) continue;
                        EdgeTexel(link.Edge, along, 1, n, out int wi, out int wj);
                        int wrong = g.NodeOf[ErosionGraph.Texel(n, link.Face, wi, wj)];
                        CollectionAssert.DoesNotContain(NeighboursOf(g, node), wrong,
                            $"{c.Name}: quad {q} {e} at {along} is linked as if unreversed");
                    }
                }
            }
            Assert.Greater(reversed, 0, "no reversed link was exercised");
        }

        // The texel `along` an edge row, `depth` texels in from it.
        static void EdgeTexel(Edge e, int along, int depth, int n, out int i, out int j)
        {
            switch (e)
            {
                case Edge.South: i = along;     j = depth;     return;
                case Edge.North: i = along;     j = n - depth; return;
                case Edge.West:  i = depth;     j = along;     return;
                default:         i = n - depth; j = along;     return;
            }
        }

        // Each node's area is a quarter of each cell it is a corner of, so the areas sum to the
        // surface's: exactly on a plane, and within the chords' error on the curved surfaces.
        [Test]
        public void AreasSumToTheSurface()
        {
            foreach (var c in Cases())
            {
                double sum = 0.0;
                foreach (double a in c.Graph.Areas)
                {
                    Assert.Greater(a, 0.0, c.Name);
                    sum += a;
                }
                double surface = ContinentParams.SurfaceArea(c.Surface);
                double tol     = c.Surface.Type == SurfaceType.PlaneGrid ? 1e-12 : 1e-2;
                Assert.AreEqual(1.0, sum / surface, tol, c.Name);
            }
        }

        [Test]
        public void TwoBuildsAreIdentical()
        {
            foreach (var c in Cases())
            {
                var other = ErosionGraph.Build(c.Surface, c.Roots, c.Layout);
                CollectionAssert.AreEqual(c.Graph.NodeOf,     other.NodeOf,     c.Name);
                CollectionAssert.AreEqual(c.Graph.Neighbours, other.Neighbours, c.Name);
                CollectionAssert.AreEqual(c.Graph.Lengths,    other.Lengths,    c.Name);
                CollectionAssert.AreEqual(c.Graph.Areas,      other.Areas,      c.Name);
            }
        }
    }
}
