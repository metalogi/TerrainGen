using System;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // Guards SurfaceMath.Neighbour across all three topologies, including the cross-quad
    // cases where the cube reverses the along-edge parameter.
    public class NeighbourTests
    {
        static readonly Edge[] AllEdges = { Edge.South, Edge.East, Edge.North, Edge.West };

        // World-space endpoints of one edge of a node.
        static void NodeEdgeEnds(in SurfaceDef s, in RootQuad q, NodeId n, Edge e,
                                 out double3 a, out double3 b)
        {
            switch (e)
            {
                case Edge.South:
                    a = SurfaceMath.SurfacePoint(s, q, n.UMin, n.VMin);
                    b = SurfaceMath.SurfacePoint(s, q, n.UMax, n.VMin); return;
                case Edge.East:
                    a = SurfaceMath.SurfacePoint(s, q, n.UMax, n.VMin);
                    b = SurfaceMath.SurfacePoint(s, q, n.UMax, n.VMax); return;
                case Edge.North:
                    a = SurfaceMath.SurfacePoint(s, q, n.UMin, n.VMax);
                    b = SurfaceMath.SurfacePoint(s, q, n.UMax, n.VMax); return;
                default:
                    a = SurfaceMath.SurfacePoint(s, q, n.UMin, n.VMin);
                    b = SurfaceMath.SurfacePoint(s, q, n.UMin, n.VMax); return;
            }
        }

        [Test]
        public void CubeNeighbourRoundTripAllDepths()
        {
            var s = SurfaceDef.CubeSphere(1000.0);
            int checkedCases = 0;

            for (int depth = 0; depth <= 4; depth++)
            {
                int span = 1 << depth;
                for (int face = 0; face < 6; face++)
                for (int x = 0; x < span; x++)
                for (int y = 0; y < span; y++)
                foreach (Edge e in AllEdges)
                {
                    var n   = new NodeId(face, depth, x, y);
                    var hop = SurfaceMath.Neighbour(s, n, e);

                    // A closed surface has a neighbour across every edge.
                    Assert.IsTrue(hop.Exists, $"{n} edge {e}: no neighbour on a closed cube-sphere");

                    var back = SurfaceMath.Neighbour(s, hop.Node, hop.ArrivalEdge);
                    Assert.IsTrue(back.Exists, $"{n} edge {e}: return hop missing");
                    Assert.AreEqual(n, back.Node,
                        $"{n} edge {e} -> {hop.Node} (arrived {hop.ArrivalEdge}) -> {back.Node}");

                    checkedCases++;
                }
            }

            // 6 faces * sum(4^d for d in 0..4) * 4 edges = 6 * 341 * 4 = 8184.
            // Stated so a wrong loop bound is visible rather than silently reducing coverage.
            Assert.AreEqual(8184, checkedCases, "unexpected number of cases checked");
        }

        // The round-trip test above cannot catch a reversal that is dropped consistently:
        // the return hop makes the same mistake and lands back where it started. This test
        // checks the property that actually matters -- the node we get back physically
        // shares the edge we crossed -- so a wrong-but-self-consistent mapping fails here.
        [Test]
        public void NeighboursAreGeometricallyAdjacent()
        {
            var cases = new[]
            {
                (s: SurfaceDef.CubeSphere(1000.0),            tol: 1e-9 * 1000.0),
                (s: SurfaceDef.CubeSphere(1000.0, false),     tol: 1e-9 * 1000.0),
                (s: SurfaceDef.Cylinder(400.0, 1000.0, 8, 4), tol: 1e-9 * 1000.0),
                (s: SurfaceDef.PlaneGrid(1000.0, 4, 3),       tol: 1e-9 * 1000.0),
            };

            foreach (var (s, tol) in cases)
            {
                var roots = SurfaceMath.BuildRoots(s);

                for (int depth = 1; depth <= 3; depth++)
                {
                    int span = 1 << depth;
                    for (int quad = 0; quad < roots.Length; quad++)
                    for (int x = 0; x < span; x++)
                    for (int y = 0; y < span; y++)
                    foreach (Edge e in AllEdges)
                    {
                        var n   = new NodeId(quad, depth, x, y);
                        var hop = SurfaceMath.Neighbour(s, n, e);
                        if (!hop.Exists) continue;

                        NodeEdgeEnds(s, roots[quad],          n,        e,               out double3 a0, out double3 a1);
                        NodeEdgeEnds(s, roots[hop.Node.Quad], hop.Node, hop.ArrivalEdge, out double3 b0, out double3 b1);

                        // The shared edge is the same segment; orientation may differ.
                        bool aligned  = math.distance(a0, b0) < tol && math.distance(a1, b1) < tol;
                        bool flipped  = math.distance(a0, b1) < tol && math.distance(a1, b0) < tol;

                        Assert.IsTrue(aligned || flipped,
                            $"{s.Type} {n} edge {e} -> {hop.Node} arriving {hop.ArrivalEdge}: " +
                            $"edges do not coincide. ours [{a0} .. {a1}] theirs [{b0} .. {b1}]");
                    }
                }
            }
        }

        [Test]
        public void CylinderWrapsInColumnsNotRows()
        {
            const int cols = 8, rows = 4, depth = 2;
            var s = SurfaceDef.Cylinder(400.0, 1000.0, cols, rows);
            int span = 1 << depth;

            // Every column has an east and west neighbour, including across the seam.
            for (int col = 0; col < cols; col++)
            {
                var n = new NodeId(col * rows + 1, depth, span - 1, 0);
                var east = SurfaceMath.Neighbour(s, n, Edge.East);
                Assert.IsTrue(east.Exists, $"column {col}: east neighbour missing");
                Assert.AreEqual(Edge.West, east.ArrivalEdge);
                Assert.AreEqual(0, east.Node.X, "east crossing should land on the neighbour's west column");
            }

            // The seam specifically: last column east wraps to column 0.
            var seam = SurfaceMath.Neighbour(s, new NodeId((cols - 1) * rows + 0, depth, span - 1, 0), Edge.East);
            Assert.IsTrue(seam.Exists);
            Assert.AreEqual(0, seam.Node.Quad / rows, "east from the last column should wrap to column 0");

            var seamBack = SurfaceMath.Neighbour(s, new NodeId(0 * rows + 0, depth, 0, 0), Edge.West);
            Assert.IsTrue(seamBack.Exists);
            Assert.AreEqual(cols - 1, seamBack.Node.Quad / rows, "west from column 0 should wrap to the last column");

            // The ends are open: no caps, so no neighbour beyond the first and last row.
            var offBottom = SurfaceMath.Neighbour(s, new NodeId(0 * rows + 0, depth, 0, 0), Edge.South);
            Assert.IsFalse(offBottom.Exists, "south of row 0 should not exist on an open cylinder");

            var offTop = SurfaceMath.Neighbour(s, new NodeId(0 * rows + (rows - 1), depth, 0, span - 1), Edge.North);
            Assert.IsFalse(offTop.Exists, "north of the last row should not exist on an open cylinder");
        }

        [Test]
        public void PlaneGridDoesNotWrap()
        {
            const int cols = 4, rows = 3, depth = 2;
            var s = SurfaceDef.PlaneGrid(1000.0, cols, rows);
            int span = 1 << depth;

            // Corner tile (col 0, row 0): west and south leave the grid.
            var sw = new NodeId(0, depth, 0, 0);
            Assert.IsFalse(SurfaceMath.Neighbour(s, sw, Edge.West).Exists,  "plane grid must not wrap west");
            Assert.IsFalse(SurfaceMath.Neighbour(s, sw, Edge.South).Exists, "plane grid must not wrap south");

            // Opposite corner: east and north leave the grid.
            var ne = new NodeId((cols - 1) * rows + (rows - 1), depth, span - 1, span - 1);
            Assert.IsFalse(SurfaceMath.Neighbour(s, ne, Edge.East).Exists,  "plane grid must not wrap east");
            Assert.IsFalse(SurfaceMath.Neighbour(s, ne, Edge.North).Exists, "plane grid must not wrap north");

            // An interior crossing still round-trips.
            var interior = new NodeId(1 * rows + 1, depth, span - 1, 0);
            var hop = SurfaceMath.Neighbour(s, interior, Edge.East);
            Assert.IsTrue(hop.Exists);
            var back = SurfaceMath.Neighbour(s, hop.Node, hop.ArrivalEdge);
            Assert.IsTrue(back.Exists);
            Assert.AreEqual(interior, back.Node);
        }

        // A malformed address used to answer with a plausible-looking neighbour instead of
        // failing: an out-of-range Quad fell through the face switches' catch-all and got
        // face 5's basis paired with face 0's adjacency.
        [Test]
        public void NeighbourRejectsMalformedNodeIds()
        {
            var cube  = SurfaceDef.CubeSphere(1000.0);
            var plane = SurfaceDef.PlaneGrid(1000.0, 4, 3);   // 12 root quads

            // Quad outside the surface's root count.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SurfaceMath.Neighbour(cube, new NodeId(6, 0, 0, 0), Edge.South),
                "quad 6 does not exist on a six-face cube-sphere");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SurfaceMath.Neighbour(cube, new NodeId(-1, 0, 0, 0), Edge.South));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SurfaceMath.Neighbour(plane, new NodeId(12, 0, 0, 0), Edge.East),
                "quad 12 is one past the last tile of a 4x3 grid");

            // X or Y outside the node grid at this depth (span 2 at depth 1).
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SurfaceMath.Neighbour(cube, new NodeId(0, 1, 2, 0), Edge.East));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SurfaceMath.Neighbour(cube, new NodeId(0, 1, 0, -1), Edge.North));

            // Depth past 30, where C# shift masking would wrap Span negative.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SurfaceMath.Neighbour(cube, new NodeId(0, 31, 0, 0), Edge.South));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SurfaceMath.Neighbour(cube, new NodeId(0, -1, 0, 0), Edge.South));

            // Well-formed addresses are untouched.
            Assert.DoesNotThrow(() => SurfaceMath.Neighbour(cube, new NodeId(5, 3, 7, 7), Edge.North));
            Assert.DoesNotThrow(() => SurfaceMath.Neighbour(plane, new NodeId(11, 2, 3, 3), Edge.West));
        }

        [Test]
        public void NeighbourIsAlwaysSameDepth()
        {
            var surfaces = new[]
            {
                SurfaceDef.CubeSphere(1000.0),
                SurfaceDef.Cylinder(400.0, 1000.0, 8, 4),
                SurfaceDef.PlaneGrid(1000.0, 4, 3),
            };

            foreach (var s in surfaces)
            for (int depth = 0; depth <= 3; depth++)
            {
                int span = 1 << depth;
                for (int quad = 0; quad < s.QuadCount; quad++)
                for (int x = 0; x < span; x++)
                for (int y = 0; y < span; y++)
                foreach (Edge e in AllEdges)
                {
                    var n   = new NodeId(quad, depth, x, y);
                    var hop = SurfaceMath.Neighbour(s, n, e);
                    if (!hop.Exists) continue;

                    Assert.AreEqual(depth, hop.Node.Depth, $"{s.Type} {n} edge {e}: depth changed");
                    Assert.IsTrue(hop.Node.X >= 0 && hop.Node.X < span, $"{s.Type} {n} edge {e}: X out of range");
                    Assert.IsTrue(hop.Node.Y >= 0 && hop.Node.Y < span, $"{s.Type} {n} edge {e}: Y out of range");
                    Assert.IsTrue(hop.Node.Quad >= 0 && hop.Node.Quad < s.QuadCount,
                        $"{s.Type} {n} edge {e}: quad {hop.Node.Quad} out of range");
                }
            }
        }
    }
}
