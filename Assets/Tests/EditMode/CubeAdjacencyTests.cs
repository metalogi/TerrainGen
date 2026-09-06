using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // Guards the cube-sphere face basis and the 24-entry edge adjacency table.
    // The table is hand-entered in SurfaceMath.CubeEdgeLink; these tests re-derive it
    // from geometry so a transcription error cannot survive.
    public class CubeAdjacencyTests
    {
        static readonly Edge[] AllEdges = { Edge.South, Edge.East, Edge.North, Edge.West };

        // Corner of a face at (a, b) in [-1,1]^2, on the unit cube.
        // tan(+-pi/4) == +-1, so face corners are cube corners with or without
        // the tangent adjustment, which is what lets us match edges exactly.
        static double3 Corner(int face, double a, double b)
            => SurfaceMath.FaceCentre(face) + a * SurfaceMath.FaceRight(face) + b * SurfaceMath.FaceUp(face);

        // Edge endpoints ordered by increasing along-edge parameter.
        static void EdgeEnds(int face, Edge e, out double3 start, out double3 end)
        {
            switch (e)
            {
                case Edge.South: start = Corner(face, -1, -1); end = Corner(face,  1, -1); return;
                case Edge.East:  start = Corner(face,  1, -1); end = Corner(face,  1,  1); return;
                case Edge.North: start = Corner(face, -1,  1); end = Corner(face,  1,  1); return;
                default:         start = Corner(face, -1, -1); end = Corner(face, -1,  1); return;
            }
        }

        static bool Same(double3 a, double3 b) => math.distance(a, b) < 1e-12;

        // Parameter t along an edge, as (u, v) on that face.
        static void EdgeUv(Edge e, double t, out double u, out double v)
        {
            switch (e)
            {
                case Edge.South: u = t;   v = 0.0; return;
                case Edge.East:  u = 1.0; v = t;   return;
                case Edge.North: u = t;   v = 1.0; return;
                default:         u = 0.0; v = t;   return;
            }
        }

        [Test]
        public void FaceBasisIsRightHanded()
        {
            for (int f = 0; f < 6; f++)
            {
                double3 expected = SurfaceMath.FaceCentre(f);
                double3 actual   = math.cross(SurfaceMath.FaceRight(f), SurfaceMath.FaceUp(f));
                Assert.IsTrue(Same(expected, actual),
                    $"face {f}: right x up == {actual}, expected centre {expected}");
            }
        }

        [Test]
        public void AdjacencyTableMatchesGeometricDerivation()
        {
            for (int f = 0; f < 6; f++)
            foreach (Edge e in AllEdges)
            {
                EdgeEnds(f, e, out double3 p0, out double3 p1);

                int  foundFace = -1, matches = 0;
                Edge foundEdge = Edge.South;
                bool foundReversed = false;

                for (int f2 = 0; f2 < 6; f2++)
                {
                    if (f2 == f) continue;
                    foreach (Edge e2 in AllEdges)
                    {
                        EdgeEnds(f2, e2, out double3 q0, out double3 q1);
                        bool sameSet = (Same(p0, q0) && Same(p1, q1))
                                    || (Same(p0, q1) && Same(p1, q0));
                        if (!sameSet) continue;

                        matches++;
                        foundFace     = f2;
                        foundEdge     = e2;
                        foundReversed = !Same(p0, q0);   // start corners differ => parameter runs backwards
                    }
                }

                Assert.AreEqual(1, matches, $"face {f} edge {e} matched {matches} candidate edges, expected exactly 1");

                EdgeLink link = SurfaceMath.CubeEdgeLink(f, e);
                Assert.AreEqual(foundFace,     link.Face,     $"face {f} edge {e}: wrong neighbour face");
                Assert.AreEqual(foundEdge,     link.Edge,     $"face {f} edge {e}: wrong neighbour edge");
                Assert.AreEqual(foundReversed, link.Reversed, $"face {f} edge {e}: wrong reversed flag");
            }
        }

        [Test]
        public void EdgeLinksRoundTrip()
        {
            int reversedCount = 0;

            for (int f = 0; f < 6; f++)
            foreach (Edge e in AllEdges)
            {
                EdgeLink a = SurfaceMath.CubeEdgeLink(f, e);
                EdgeLink b = SurfaceMath.CubeEdgeLink(a.Face, a.Edge);

                Assert.AreEqual(f, b.Face, $"face {f} edge {e}: return link lands on face {b.Face}");
                Assert.AreEqual(e, b.Edge, $"face {f} edge {e}: return link arrives on edge {b.Edge}");
                Assert.AreEqual(a.Reversed, b.Reversed, $"face {f} edge {e}: reversed flag is asymmetric");

                if (a.Reversed) reversedCount++;
            }

            // Eight of the 24 links reverse the along-edge parameter. This is a property of
            // the cube, not an accident of the table; if it changes, the basis changed too.
            Assert.AreEqual(8, reversedCount, "expected exactly 8 reversed links out of 24");
        }

        [Test]
        public void SharedEdgePointsCoincide()
        {
            const double radius = 6371000.0;   // planetary scale, to exercise magnitude
            const int    samples = 33;

            foreach (bool tangentAdjust in new[] { true, false })
            {
                var s     = SurfaceDef.CubeSphere(radius, tangentAdjust);
                var roots = SurfaceMath.BuildRoots(s);
                double worst = 0.0;

                for (int f = 0; f < 6; f++)
                foreach (Edge e in AllEdges)
                {
                    EdgeLink link = SurfaceMath.CubeEdgeLink(f, e);

                    for (int i = 0; i < samples; i++)
                    {
                        double t = i / (double)(samples - 1);
                        EdgeUv(e, t, out double u, out double v);
                        double3 p = SurfaceMath.SurfacePoint(s, roots[f], u, v);

                        double t2 = link.Reversed ? 1.0 - t : t;
                        EdgeUv(link.Edge, t2, out double u2, out double v2);
                        double3 q = SurfaceMath.SurfacePoint(s, roots[link.Face], u2, v2);

                        worst = math.max(worst, math.distance(p, q));
                    }
                }

                // Measured worst case is 2.07e-16 relative, i.e. machine precision.
                // 1e-9 relative leaves six orders of magnitude of headroom while still
                // catching any genuine mapping error.
                Assert.Less(worst, 1e-9 * radius,
                    $"tangentAdjust={tangentAdjust}: worst shared-edge mismatch {worst} m at radius {radius} m");
            }
        }
    }
}
