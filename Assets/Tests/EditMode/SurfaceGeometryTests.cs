using System;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // Geometric properties of the surface evaluation: sphere conformance, the effect of
    // the tangent adjustment, node sizing, and the cylinder's inward normal convention.
    public class SurfaceGeometryTests
    {
        // MaxNodeSizeSpread is a hand-entered table, so it is re-derived here numerically --
        // the same treatment as the cube adjacency table and the handedness switch.
        //
        // It matters because LodMath uses it to bound MorphStartFraction: a coarse leaf's
        // patch reaches past its own bounding-sphere distance by roughly this factor, and
        // the morph range has to start beyond that or LOD boundaries crack. A table entry
        // that is too small is therefore a seam, not a rounding error, so each entry must be
        // an upper bound on what the geometry actually does.
        [Test]
        public void NodeSizeSpreadMatchesTheTable()
        {
            var surfaces = new[]
            {
                SurfaceDef.CubeSphere(6371000.0, true),
                SurfaceDef.CubeSphere(6371000.0, false),
                SurfaceDef.PlaneGrid(200000.0, 4, 4),
                SurfaceDef.Cylinder(5000.0, 32000.0, 8),
            };

            foreach (var s in surfaces)
            {
                var    roots = SurfaceMath.BuildRoots(s);
                double s0    = SurfaceMath.NodeWorldSize(s, roots[0], new NodeId(0, 0, 0, 0));
                double worst = 1.0;

                // The ratio climbs with depth and converges; depth 8 is within 0.2% of the
                // limit for every topology here.
                for (int d = 1; d <= 8; d++)
                {
                    int    span    = 1 << d;
                    double nominal = s0 / (double)(1L << d);
                    for (int x = 0; x < span; x++)
                    for (int y = 0; y < span; y++)
                        worst = math.max(worst,
                            SurfaceMath.NodeWorldSize(s, roots[0], new NodeId(0, d, x, y)) / nominal);
                }

                double tabled = SurfaceMath.MaxNodeSizeSpread(s);
                Assert.GreaterOrEqual(tabled, worst,
                    $"{s.Type} (tangent adjust {s.TangentAdjust}): the tabled spread {tabled:F5} " +
                    $"is below the measured {worst:F5}, which would let MorphStartFraction sit " +
                    "above what the geometry allows");
                Assert.Less(tabled, worst * 1.02,
                    $"{s.Type} (tangent adjust {s.TangentAdjust}): the tabled spread {tabled:F5} " +
                    $"is needlessly far above the measured {worst:F5}, which costs morph window " +
                    "for nothing");
            }

            // The limits are exact: the tangent adjustment converges on pi/2, and the raw
            // cube-sphere parameterisation on sqrt(3).
            Assert.AreEqual(math.PI_DBL / 2.0, SurfaceMath.MaxNodeSizeSpread(SurfaceDef.CubeSphere(1.0, true)), 0.0);
            Assert.AreEqual(math.sqrt(3.0),    SurfaceMath.MaxNodeSizeSpread(SurfaceDef.CubeSphere(1.0, false)), 0.0);
            Assert.AreEqual(1.0,               SurfaceMath.MaxNodeSizeSpread(SurfaceDef.PlaneGrid(1.0, 1, 1)), 0.0);
        }

        [Test]
        public void CubeSpherePointsLieOnSphere()
        {
            const double radius = 6371000.0;

            foreach (bool tangentAdjust in new[] { true, false })
            {
                var s     = SurfaceDef.CubeSphere(radius, tangentAdjust);
                var roots = SurfaceMath.BuildRoots(s);
                var rng   = new Unity.Mathematics.Random(12345);   // fixed seed: failures reproduce

                for (int i = 0; i < 2000; i++)
                {
                    int    face = rng.NextInt(0, 6);
                    double u    = rng.NextDouble();
                    double v    = rng.NextDouble();

                    SurfaceMath.SurfaceFrame(s, roots[face], u, v, out double3 p, out float3 n);

                    Assert.AreEqual(radius, math.length(p), 1e-9 * radius,
                        $"tangentAdjust={tangentAdjust} face {face} ({u:F4},{v:F4}) is off the sphere");

                    // On a sphere the outward normal is the normalised position.
                    Assert.AreEqual(1.0, math.length(n), 1e-6, "normal is not unit length");
                    double3 expected = p / radius;
                    Assert.Less(math.distance((double3)n, expected), 1e-6,
                        "normal does not point radially outward");
                }
            }
        }

        [Test]
        public void TangentAdjustmentImprovesSpacing()
        {
            const int cells = 16;
            double Ratio(bool tangentAdjust)
            {
                var s    = SurfaceDef.CubeSphere(1000.0, tangentAdjust);
                var root = SurfaceMath.BuildRoots(s)[4];      // +Z face
                double lo = double.MaxValue, hi = 0.0;

                for (int i = 0; i < cells; i++)
                for (int j = 0; j < cells; j++)
                {
                    double3 a = SurfaceMath.SurfacePoint(s, root, i       / (double)cells, j       / (double)cells);
                    double3 b = SurfaceMath.SurfacePoint(s, root, (i + 1) / (double)cells, (j + 1) / (double)cells);
                    double d = math.distance(a, b);
                    lo = math.min(lo, d);
                    hi = math.max(hi, d);
                }
                return hi / lo;
            }

            double adjusted   = Ratio(true);
            double unadjusted = Ratio(false);

            // Measured: 1.621 adjusted, 2.733 unadjusted. Both bounds are asserted so that
            // silently disabling the adjustment fails loudly rather than degrading quietly.
            Assert.Less(adjusted, 1.7,
                $"tangent-adjusted spacing ratio {adjusted:F4} is worse than expected (~1.62)");
            Assert.Greater(unadjusted, 2.5,
                $"unadjusted spacing ratio {unadjusted:F4} is unexpectedly good (~2.73 expected) - is the flag wired up?");
            Assert.Less(adjusted, unadjusted, "the tangent adjustment should improve spacing, not worsen it");
        }

        [Test]
        public void NodeWorldSizeHalvesWithDepth()
        {
            var cases = new[]
            {
                (s: SurfaceDef.CubeSphere(1000.0),                 quad: 4),
                (s: SurfaceDef.CubeSphere(1000.0, false),          quad: 2),
                (s: SurfaceDef.Cylinder(400.0, 1000.0, 8, 4),      quad: 5),
                (s: SurfaceDef.PlaneGrid(1000.0, 4, 3),            quad: 4),
            };

            foreach (var (s, quad) in cases)
            {
                var root = SurfaceMath.BuildRoots(s)[quad];

                for (int depth = 0; depth <= 4; depth++)
                {
                    int span = 1 << depth;
                    for (int x = 0; x < span; x++)
                    for (int y = 0; y < span; y++)
                    {
                        var parent     = new NodeId(quad, depth, x, y);
                        double parentSize = SurfaceMath.NodeWorldSize(s, root, parent);

                        for (int i = 0; i < 4; i++)
                        {
                            double childSize = SurfaceMath.NodeWorldSize(s, root, parent.Child(i));
                            double ratio     = childSize / parentSize;

                            // Exactly 0.5 on a plane. Curvature plus the tangent adjustment
                            // spread it; measured range over depths 0-5 is 0.467 .. 0.633.
                            Assert.That(ratio, Is.InRange(0.45, 0.65),
                                $"{s.Type} {parent}.Child({i}): child/parent size ratio {ratio:F4}");
                        }
                    }
                }
            }
        }

        // NodeWorldSize takes both a RootQuad and a NodeId that already carries its quad, so
        // the two can disagree. BuildRoots stamps Index on every topology to make the
        // mismatch detectable.
        [Test]
        public void BuildRootsStampsQuadIndex()
        {
            var surfaces = new[]
            {
                SurfaceDef.CubeSphere(1000.0),
                SurfaceDef.Cylinder(400.0, 1000.0, 8, 4),
                SurfaceDef.PlaneGrid(1000.0, 4, 3),
            };

            foreach (var s in surfaces)
            {
                var roots = SurfaceMath.BuildRoots(s);
                Assert.AreEqual(s.QuadCount, roots.Length, $"{s.Type}: QuadCount disagrees with BuildRoots");

                for (int i = 0; i < roots.Length; i++)
                    Assert.AreEqual(i, roots[i].Index,
                        $"{s.Type}: roots[{i}].Index must equal its position in the array");
            }
        }

        [Test]
        public void NodeWorldSizeRejectsMismatchedRoot()
        {
            var s     = SurfaceDef.CubeSphere(1000.0);
            var roots = SurfaceMath.BuildRoots(s);

            // Pairing a root with a node from another quad evaluates the node's UV range
            // against the wrong basis and returns a wrong-but-plausible size, which would
            // bias LOD decisions with nothing asserting.
            Assert.Throws<ArgumentException>(
                () => SurfaceMath.NodeWorldSize(s, roots[0], new NodeId(1, 2, 0, 0)),
                "root of quad 0 paired with a node on quad 1 must throw");

            Assert.DoesNotThrow(
                () => SurfaceMath.NodeWorldSize(s, roots[1], new NodeId(1, 2, 0, 0)),
                "the matching root must still be accepted");
        }

        [Test]
        public void CylinderNormalsPointInward()
        {
            var s     = SurfaceDef.Cylinder(400.0, 1000.0, 8, 4);
            var roots = SurfaceMath.BuildRoots(s);
            var rng   = new Unity.Mathematics.Random(99);

            for (int i = 0; i < 500; i++)
            {
                int quad = rng.NextInt(0, roots.Length);
                double u = rng.NextDouble();
                double v = rng.NextDouble();

                SurfaceMath.SurfaceFrame(s, roots[quad], u, v, out double3 p, out float3 n);

                // Radial component of the position, with the axial (Z) part removed.
                double3 radial = new double3(p.x, p.y, 0.0);
                Assert.Greater(math.length(radial), 1e-6, "sample landed on the axis");
                radial = radial / math.length(radial);

                Assert.Less(math.dot((double3)n, radial), -0.99,
                    $"quad {quad} ({u:F3},{v:F3}): cylinder normal should point inward toward the axis");

                // The surface itself sits on the wall.
                Assert.AreEqual(s.Radius, math.length(new double3(p.x, p.y, 0.0)), 1e-9 * s.Radius,
                    "cylinder point is not on the wall");

                // And within the axial extent.
                Assert.That(p.z, Is.InRange(-1e-9, s.Length + 1e-9), "cylinder point is outside the axial range");
            }
        }
    }
}
