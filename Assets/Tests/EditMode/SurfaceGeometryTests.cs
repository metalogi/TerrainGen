using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // Geometric properties of the surface evaluation: sphere conformance, the effect of
    // the tangent adjustment, node sizing, and the cylinder's inward normal convention.
    public class SurfaceGeometryTests
    {
        [Test]
        public void CubeSpherePointsLieOnSphere()
        {
            const double radius = 6371000.0;

            foreach (bool tangentAdjust in new[] { true, false })
            {
                var s     = SurfaceDef.CubeSphere(radius, tangentAdjust);
                var roots = Surface.BuildRoots(s);
                var rng   = new Unity.Mathematics.Random(12345);   // fixed seed: failures reproduce

                for (int i = 0; i < 2000; i++)
                {
                    int    face = rng.NextInt(0, 6);
                    double u    = rng.NextDouble();
                    double v    = rng.NextDouble();

                    Surface.SurfaceFrame(s, roots[face], u, v, out double3 p, out float3 n);

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
                var root = Surface.BuildRoots(s)[4];      // +Z face
                double lo = double.MaxValue, hi = 0.0;

                for (int i = 0; i < cells; i++)
                for (int j = 0; j < cells; j++)
                {
                    double3 a = Surface.SurfacePoint(s, root, i       / (double)cells, j       / (double)cells);
                    double3 b = Surface.SurfacePoint(s, root, (i + 1) / (double)cells, (j + 1) / (double)cells);
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
                var root = Surface.BuildRoots(s)[quad];

                for (int depth = 0; depth <= 4; depth++)
                {
                    int span = 1 << depth;
                    for (int x = 0; x < span; x++)
                    for (int y = 0; y < span; y++)
                    {
                        var parent     = new NodeId(quad, depth, x, y);
                        double parentSize = Surface.NodeWorldSize(s, root, parent);

                        for (int i = 0; i < 4; i++)
                        {
                            double childSize = Surface.NodeWorldSize(s, root, parent.Child(i));
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

        [Test]
        public void CylinderNormalsPointInward()
        {
            var s     = SurfaceDef.Cylinder(400.0, 1000.0, 8, 4);
            var roots = Surface.BuildRoots(s);
            var rng   = new Unity.Mathematics.Random(99);

            for (int i = 0; i < 500; i++)
            {
                int quad = rng.NextInt(0, roots.Length);
                double u = rng.NextDouble();
                double v = rng.NextDouble();

                Surface.SurfaceFrame(s, roots[quad], u, v, out double3 p, out float3 n);

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
