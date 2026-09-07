using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The seam and band-limit guarantees. These are the properties that let M3 drop
    // stitching entirely, so they are asserted at the strongest level each one supports:
    // bit-identical inside a root quad, bounded across a cube edge.
    public class HeightFunctionTests
    {
        const double EarthRadius = 6371000.0;
        const int    Resolution  = 33;
        const int    OctaveCount = 20;
        const float  HeightScale = 200f;

        static SurfaceDef Sphere() => SurfaceDef.CubeSphere(EarthRadius);

        static HeightParams Params(double octaveWavelength0 = 0.0) =>
            HeightParams.Create(Sphere(), Resolution, octaveWavelength0, OctaveCount,
                                HeightScale, 0.5f, 2f, 42u);

        // (u, v) of the point a fraction t along one edge of a root quad.
        static void EdgeUV(Edge e, double t, out double u, out double v)
        {
            switch (e)
            {
                case Edge.South: u = t;   v = 0.0; return;
                case Edge.East:  u = 1.0; v = t;   return;
                case Edge.North: u = t;   v = 1.0; return;
                default:         u = 0.0; v = t;   return;   // West
            }
        }

        [Test]
        public void BandLimitIsDepthOnly()
        {
            var p = Params();

            // Reference config: lambda0 = S0 with R = 33 makes the ratio exactly 16, so K0
            // lands on a power of two. This is why HeightParams reads the IEEE exponent
            // rather than calling log2 -- a library log returning 3.9999999999999996 here
            // would silently drop an octave from every chunk in the world.
            Assert.AreEqual(10403799.4342, p.S0, 1e-3, "unexpected depth-0 node size");
            Assert.AreEqual(4, p.K0, "K0 for the reference config should be exactly 4");

            for (int d = 0; d < 24; d++)
                Assert.AreEqual(math.clamp(d + p.K0, 0, OctaveCount - 1), p.MaxOctave(d),
                    $"band limit at depth {d} is not clamp(d + K0)");

            var s     = Sphere();
            var roots = SurfaceMath.BuildRoots(s);

            // That MaxOctave cannot vary within a depth is structural -- it takes an int and
            // has nothing else to read. What is worth testing is that the alternative really
            // would vary, i.e. that the rule is load-bearing rather than a stylistic choice.
            //
            // Node size varies across a cube face by up to 1.33:1 at a fixed depth, which
            // is less than an octave but more than enough to straddle a floor(log2(...))
            // boundary for many configurations -- 13 of 32 sampled lambda0 values do so by
            // depth 8. At lambda0 = 1.25 * S0 the split appears at depth 3: two same-depth
            // neighbours would take different octave counts, evaluate different functions,
            // and crack along the edge they share.
            var    pq        = Params(1.25 * p.S0);
            var    distinctK = new System.Collections.Generic.HashSet<int>();
            const int depth  = 3;
            int    span      = 1 << depth;
            for (int x = 0; x < span; x++)
            for (int y = 0; y < span; y++)
            {
                double measured = SurfaceMath.NodeWorldSize(s, roots[0], new NodeId(0, depth, x, y))
                                / (Resolution - 1);
                distinctK.Add((int)math.floor(math.log2(pq.OctaveWavelength0 / (2.0 * measured))));
            }

            Assert.Greater(distinctK.Count, 1,
                "the size-derived band limit no longer varies within a depth, so this test has " +
                "stopped demonstrating why MaxOctave must take a depth. Pick another lambda0.");
        }

        [Test]
        public void SameQuadSharedVerticesAreBitIdentical()
        {
            var s     = Sphere();
            var roots = SurfaceMath.BuildRoots(s);
            var p     = Params();

            for (int depth = 0; depth <= 4; depth++)
            {
                int span = 1 << depth;
                if (span < 2) continue;

                for (int x = 0; x + 1 < span; x++)
                {
                    var a = new NodeId(0, depth, x,     1 % span);
                    var b = new NodeId(0, depth, x + 1, 1 % span);
                    int oct = p.MaxOctave(depth);

                    for (int j = 0; j < Resolution; j++)
                    {
                        // A's East column is B's West column.
                        ChunkGrid.VertexUV(a, Resolution - 1, j, Resolution, out double ua, out double va);
                        ChunkGrid.VertexUV(b, 0,              j, Resolution, out double ub, out double vb);

                        Assert.AreEqual(math.asulong(ua), math.asulong(ub),
                            $"depth {depth} x={x} j={j}: shared u differs in bits ({ua:R} vs {ub:R})");
                        Assert.AreEqual(math.asulong(va), math.asulong(vb),
                            $"depth {depth} x={x} j={j}: shared v differs in bits");

                        double3 pa = SurfaceMath.SurfacePoint(s, roots[0], ua, va);
                        double3 pb = SurfaceMath.SurfacePoint(s, roots[0], ub, vb);

                        float ha = TerrainHeightFunction.Height(pa, oct, MacroSample.Zero, p);
                        float hb = TerrainHeightFunction.Height(pb, oct, MacroSample.Zero, p);

                        // Bit-identical, not close. This is the guarantee that replaces seam
                        // stitching inside a root quad; anything weaker is a crack waiting
                        // for a wide enough height scale.
                        Assert.AreEqual(math.asuint(ha), math.asuint(hb),
                            $"depth {depth} x={x} j={j}: shared height differs in bits ({ha:R} vs {hb:R})");
                    }
                }
            }
        }

        [Test]
        public void CrossQuadSharedVerticesAgreeToDoublePrecision()
        {
            var s     = Sphere();
            var roots = SurfaceMath.BuildRoots(s);
            var p     = Params();
            int oct   = p.MaxOctave(0);

            double worstPos = 0.0;
            double worstH   = 0.0;

            for (int face = 0; face < 6; face++)
            for (int e = 0; e < 4; e++)
            {
                var link = SurfaceMath.CubeEdgeLink(face, (Edge)e);

                for (int i = 0; i < Resolution; i++)
                {
                    double t  = i / (double)(Resolution - 1);
                    double t2 = link.Reversed ? 1.0 - t : t;

                    EdgeUV((Edge)e,   t,  out double ua, out double va);
                    EdgeUV(link.Edge, t2, out double ub, out double vb);

                    double3 pa = SurfaceMath.SurfacePoint(s, roots[face],      ua, va);
                    double3 pb = SurfaceMath.SurfacePoint(s, roots[link.Face], ub, vb);

                    worstPos = math.max(worstPos, math.distance(pa, pb));
                    worstH   = math.max(worstH, math.abs(
                        TerrainHeightFunction.Height(pa, oct, MacroSample.Zero, p) -
                        TerrainHeightFunction.Height(pb, oct, MacroSample.Zero, p)));
                }
            }

            // These are *not* bit-identical and cannot be: the two faces reach the shared
            // edge by different parameterisations, so the double3 they produce differs in
            // the last bit or two. SonomaRevisedPlan.md section 5 asks M2 for bit-identical
            // heights "including across a cube edge"; that half of the requirement is
            // unachievable, and this bounded form replaces it.
            //
            // Measured worst-case position discrepancy is 2.067e-16 relative -- 1.32 nm at
            // Earth radius, five orders below a micrometre, and covered by skirts regardless.
            Assert.Less(worstPos, 1e-9 * EarthRadius,
                $"cross-face shared points diverged by {worstPos:E3} m");

            // The height tolerance is looser than the position one on purpose: the noise
            // accumulates in float, so two inputs differing in their last double bits can
            // round differently at ~1e-7 relative per octave. 1e-5 of HeightScale is 2 mm
            // here -- far above float rounding, far below anything visible.
            Assert.Less(worstH, HeightScale * 1e-5,
                $"cross-face shared heights diverged by {worstH:E3} m");
        }

        // Chunk vertex (i, j) of a node in world space, exactly as HeightSampleJob and
        // ChunkMeshJob.Local build it. i and j may be -1 or Resolution: the border samples.
        static double3 VertexPos(in SurfaceDef s, RootQuad[] roots, NodeId n, int i, int j,
                                 int oct, in HeightParams hp)
        {
            ChunkGrid.VertexUV(n, i, j, Resolution, out double u, out double v);
            SurfaceMath.SurfaceFrame(s, roots[n.Quad], u, v, out double3 p, out float3 nrm);
            return p + (double3)(nrm * TerrainHeightFunction.Height(p, oct, MacroSample.Zero, hp));
        }

        // The vertex normal ChunkMeshJob computes: central difference over the border-extended
        // grid, oriented against the base surface normal.
        static double3 VertexNormal(in SurfaceDef s, RootQuad[] roots, NodeId n, int i, int j,
                                    int oct, in HeightParams hp)
        {
            double3 dU = VertexPos(s, roots, n, i + 1, j, oct, hp) - VertexPos(s, roots, n, i - 1, j, oct, hp);
            double3 dV = VertexPos(s, roots, n, i, j + 1, oct, hp) - VertexPos(s, roots, n, i, j - 1, oct, hp);
            double3 nn = math.cross(dV, dU);

            ChunkGrid.VertexUV(n, i, j, Resolution, out double u, out double v);
            float3 baseN = SurfaceMath.SurfaceNormal(s, roots[n.Quad], u, v);
            if (math.dot(nn, (double3)baseN) < 0.0) nn = -nn;
            return math.normalize(nn);
        }

        static void EdgeVertex(Edge e, int k, out int i, out int j)
        {
            switch (e)
            {
                case Edge.South: i = k;              j = 0;              return;
                case Edge.East:  i = Resolution - 1; j = k;              return;
                case Edge.North: i = k;              j = Resolution - 1; return;
                default:         i = 0;              j = k;              return;   // West
            }
        }

        // The other half of the seam guarantee. Positions are covered above; this is what the
        // shader actually reads, and the two regimes are not the same.
        //
        // Inside a root quad the border sample is bit-identical to the neighbour's own vertex,
        // so the normals match to rounding. Across a cube face the border continues this face's
        // tangent-adjusted parameterisation past its own edge rather than crossing onto the
        // neighbour's, so the central difference is not centred and the two sides disagree.
        // That is a shading discontinuity along the 12 cube edges, not a crack -- the positions
        // still agree to 1.3 nm.
        [Test]
        public void CrossFaceEdgeNormalsAgreeToABoundedAngle()
        {
            var s     = Sphere();
            var roots = SurfaceMath.BuildRoots(s);
            var p     = Params();

            double Angle(double3 a, double3 b) =>
                math.degrees(math.acos(math.clamp(math.dot(a, b), -1.0, 1.0)));

            // Control: two nodes inside one root quad, sharing a column of vertices.
            {
                int oct = p.MaxOctave(1);
                var a   = new NodeId(0, 1, 0, 0);
                var b   = new NodeId(0, 1, 1, 0);
                double worst = 0.0;

                for (int j = 0; j < Resolution; j++)
                    worst = math.max(worst, Angle(VertexNormal(s, roots, a, Resolution - 1, j, oct, p),
                                                  VertexNormal(s, roots, b, 0,              j, oct, p)));

                // Measured 1.9e-6 degrees -- pure float rounding in the height sum.
                Assert.Less(worst, 1e-4,
                    $"same-quad seam normals diverged by {worst:E3} deg; inside a root quad the " +
                    "border sample is bit-identical to the neighbour's vertex and this must be exact");
            }

            // Every cube edge, at root depth.
            {
                int    oct   = p.MaxOctave(0);
                double worst = 0.0;
                string where = "";

                for (int face = 0; face < 6; face++)
                for (int e = 0; e < 4; e++)
                {
                    var link = SurfaceMath.CubeEdgeLink(face, (Edge)e);
                    var na   = new NodeId(face,      0, 0, 0);
                    var nb   = new NodeId(link.Face, 0, 0, 0);

                    for (int k = 0; k < Resolution; k++)
                    {
                        int k2 = link.Reversed ? Resolution - 1 - k : k;
                        EdgeVertex((Edge)e,   k,  out int ia, out int ja);
                        EdgeVertex(link.Edge, k2, out int ib, out int jb);

                        double d = Angle(VertexNormal(s, roots, na, ia, ja, oct, p),
                                         VertexNormal(s, roots, nb, ib, jb, oct, p));
                        if (d > worst) { worst = d; where = $"face {face} {(Edge)e} -> face {link.Face} {link.Edge}"; }
                    }
                }

                // Measured 0.1510 degrees. The bound is deliberately close to the measurement:
                // this is a known, geometric artefact -- at HeightScale 0, on a perfect sphere,
                // it is still 0.1503 degrees -- and the point of the test is that it stays this
                // size. If a change pushes it up, that is a real regression in edge shading and
                // should be looked at, not absorbed by widening the tolerance.
                Assert.Less(worst, 0.25,
                    $"cross-face edge normals diverged by {worst:F4} deg at {where}");
            }
        }

        [Test]
        public void HeightHasNoConstantBias()
        {
            // Deliberately not "mean height over the sphere with the default wavelength".
            // At lambda0 = S0 there are only a handful of independent octave-0 cells on the
            // whole planet, so that mean is dominated by low-frequency structure and a
            // tight threshold would fail for reasons unrelated to bias. Shrink lambda0 so
            // the samples span millions of cells, and the mean becomes a real estimate.
            var s     = Sphere();
            var roots = SurfaceMath.BuildRoots(s);
            var p     = Params(EarthRadius / 1000.0);

            var    rng = new Unity.Mathematics.Random(67890);
            double sum = 0.0;
            const int n = 50000;

            for (int i = 0; i < n; i++)
            {
                int face = rng.NextInt(0, 6);
                double3 pt = SurfaceMath.SurfacePoint(s, roots[face], rng.NextDouble(), rng.NextDouble());
                sum += TerrainHeightFunction.Height(pt, p.MaxOctave(0), MacroSample.Zero, p);
            }

            double mean = sum / n;

            // A reintroduced (noise * 0.5 + 0.5) would land near +0.67 * HeightScale.
            Assert.Less(math.abs(mean), 0.02 * HeightScale,
                $"mean height over {n} samples was {mean:F4} m; expected ~0");
        }
    }
}
