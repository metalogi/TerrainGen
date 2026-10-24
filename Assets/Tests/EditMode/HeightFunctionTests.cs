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

        // The noise's fractional cell coordinate must not be float. It is in [0,1), so a float
        // holds it to 2^-24 of a cell -- but octave 0's cell at Earth radius is 10,400 km, and
        // 2^-24 of that is 0.62 m. Octave 0 then rises in 0.62 m steps along every world axis,
        // octave 1 in 0.31 m steps, and so on; the risers are about a millimetre, and on ground
        // as smooth as Earth's at metre scale the shading draws them as long straight lines
        // along the x, y and z lattice planes. That is what EarthStress showed flying low.
        //
        // Heights along each world axis, 5 cm apart over 6 m, at four places on EarthStress's
        // sphere, the first its start. The second difference of smooth terrain is tens of
        // microns at that spacing; what is left above it is the float height's own rounding,
        // allowed for as two ULPs of |height|. Measured with the float fraction: 0.98 to 5.7 mm,
        // failing on every axis at every point. With the noise in double: 0.019 mm at the start
        // (-15 m), and exactly one height ULP elsewhere (0.06 mm at 817 m, 0.49 mm at 5.1 km).
        //
        // Mutation check: narrowing f = p - fl to float in LatticeNoise.Gradient3D fails this.
        [Test]
        public void EarthScaleHeightHasNoAxisAlignedSteps()
        {
            var p = HeightParams.Create(Sphere(), Resolution, 0.0, 24, 9000f, 0.5f, 2f, 42u);
            const int    MaxOctave = 23;      // depth 19's band limit on the Earth configuration
            const double Step      = 0.05;

            var points = new[]
            {
                new double3(-0.1545, 0.6691, -0.7269),   // EarthStress's start, near sea level
                new double3( 0.31,   0.52,    0.79),
                new double3( 0.9,   -0.3,     0.2),
                new double3(-0.5,   -0.5,     0.7),
            };

            foreach (var dir in points)
            {
                double3 origin = math.normalize(dir) * EarthRadius;
                float   h      = TerrainHeightFunction.Height(origin, MaxOctave, MacroSample.Zero, p);
                float   mag    = math.abs(h);
                double  ulp    = math.asfloat(math.asuint(mag) + 1u) - mag;
                double  limit  = 2.0 * ulp + 5e-5;

                for (int axis = 0; axis < 3; axis++)
                {
                    var    a     = axis == 0 ? new double3(1, 0, 0) : axis == 1 ? new double3(0, 1, 0) : new double3(0, 0, 1);
                    double worst = 0.0;
                    for (int i = 1; i < 120; i++)
                    {
                        double h0 = TerrainHeightFunction.Height(origin + a * ((i - 1) * Step), MaxOctave, MacroSample.Zero, p);
                        double h1 = TerrainHeightFunction.Height(origin + a * (i * Step),       MaxOctave, MacroSample.Zero, p);
                        double h2 = TerrainHeightFunction.Height(origin + a * ((i + 1) * Step), MaxOctave, MacroSample.Zero, p);
                        worst = math.max(worst, math.abs(h2 - 2.0 * h1 + h0));
                    }

                    Assert.LessOrEqual(worst, limit,
                        $"at {dir} (height {h:F1} m) along {"xyz"[axis]}: a second difference of " +
                        $"{worst * 1000:F3} mm over {Step} m, against {limit * 1000:F3} mm allowed. The " +
                        "noise is stepping along a lattice axis -- check its fraction is double.");
                }
            }
        }

        // TerrainHeightFunction.Height and LatticeNoise.Fbm exactly as M4 shipped them, copied
        // verbatim, with M4's float MacroSample.Zero.BaseHeight written as the 0f it was. Not
        // to be tidied or kept in step with the real ones: being frozen is the point.
        static float M4Height(in double3 surfacePos, int maxOctave, in HeightParams p)
        {
            double3 n = surfacePos / p.OctaveWavelength0;

            double fbm = M4Fbm(n, maxOctave + 1, p.Persistence, p.Lacunarity, p.Seed);

            return (float)(p.HeightScale * fbm + 0f);
        }

        static double M4Fbm(double3 p, int octaveCount, float persistence, float lacunarity, uint seed)
        {
            double lac = lacunarity;
            double3 q  = p;
            double sum = 0.0;
            double amp = 1.0;

            for (int k = 0; k < octaveCount; k++)
            {
                sum += amp * LatticeNoise.Gradient3D(q, seed + (uint)k);
                amp *= persistence;
                q   *= lac;
            }

            return sum;
        }

        // Ground rule 1 of M5 as an assertion: with no macro layer -- default(MacroSample), which
        // is what every scene without one passes -- the height is M4's to the bit, on SampleScene's
        // configuration and Earth's, at every depth. That is what keeps SampleScene unchanged and
        // every relief and working-set figure pinned against it valid without re-measurement.
        //
        // The trap it guards is MacroSample's default. A field stored as a multiplier defaults to
        // zero and flattens the world on any path that forgets to set it; stored relative to one,
        // as M5b's band gains are, the default is the identity.
        [Test]
        public void HeightIsBitIdenticalWithoutAMacroLayer()
        {
            var configs = new[]
            {
                (name: "SampleScene", s: SurfaceDef.CubeSphere(TerrainReliefTests.Radius),
                 p: HeightParams.Create(SurfaceDef.CubeSphere(TerrainReliefTests.Radius),
                        TerrainReliefTests.Resolution, 0.0, TerrainReliefTests.OctaveCount,
                        TerrainReliefTests.HeightScale, TerrainReliefTests.Persistence,
                        TerrainReliefTests.Lacunarity, TerrainReliefTests.Seed),
                 maxDepth: TerrainReliefTests.MaxDepth),
                (name: "EarthStress", s: EarthScaleTests.Earth,
                 p: EarthScaleTests.Params(EarthScaleTests.OctaveCount),
                 maxDepth: EarthScaleTests.MaxDepth),
            };
            var rng = new Unity.Mathematics.Random(0x5EEDu);

            foreach (var (name, s, p, maxDepth) in configs)
            {
                var roots = SurfaceMath.BuildRoots(s);
                for (int k = 0; k < 400; k++)
                {
                    var     q     = roots[rng.NextInt(0, roots.Length)];
                    double3 point = SurfaceMath.SurfacePoint(s, q, rng.NextDouble(), rng.NextDouble());
                    int     depth = rng.NextInt(0, maxDepth + 1);
                    int     oct   = p.MaxOctave(depth);

                    float now = TerrainHeightFunction.Height(point, oct, default(MacroSample), p);
                    float m4  = M4Height(point, oct, p);
                    Assert.AreEqual(math.asuint(m4), math.asuint(now),
                        $"{name} at {point}, depth {depth}: {now} where M4 gave {m4}");
                    Assert.AreEqual(math.asuint(now),
                        math.asuint(TerrainHeightFunction.Height(point, oct, MacroSample.Zero, p)),
                        "MacroSample.Zero is not default(MacroSample)");

                    // A two-slope spectrum whose fine persistence is the coarse one is the same
                    // spectrum (C10), wherever its crossover falls.
                    var same = p.WithRoughness(p.Persistence, p.OctaveWavelength0 / 64.0);
                    Assert.AreEqual(math.asuint(m4), math.asuint(TerrainHeightFunction.Height(point, oct, default, same)),
                        $"{name}: FinePersistence equal to Persistence changed the height");

                    // And a continental cutoff of 0 is no cutoff, at any amplitude (K4).
                    var uncut = p.WithContinentalCutoff(0.0, 0f);
                    Assert.AreEqual(0, uncut.FirstOctave, $"{name}: a cutoff of 0 dropped octaves");
                    Assert.AreEqual(0f, uncut.CoarseGain, $"{name}: a cutoff of 0 left a gain");
                    Assert.AreEqual(math.asuint(m4), math.asuint(TerrainHeightFunction.Height(point, oct, default, uncut)),
                        $"{name}: a continental cutoff of 0 changed the height");
                }
            }
        }

        // C10: the octave amplitudes follow Persistence down to the crossover and FinePersistence
        // below it; the crossover falls on the octave the exponent says; and the sum is still a
        // strict prefix of itself, each octave adding exactly its own term, which is what the
        // geomorph target depends on.
        [Test]
        public void FineOctavesTakeFinePersistence()
        {
            var p = EarthScaleTests.RoughParams;

            // The crossover by the exponent: the octaves at least RoughnessCrossover metres long.
            int c = (int)math.floor(math.log2(p.OctaveWavelength0 / EarthScaleTests.RoughnessCrossover)) + 1;
            Assert.AreEqual(c, p.CrossoverOctave);
            Assert.AreNotEqual(p.Persistence, p.FinePersistence, "the test needs two slopes to tell apart");
            for (int k = 0; k < EarthScaleTests.OctaveCount; k++)
            {
                // The persistences are floats, so taken as the doubles they are, not as decimals.
                double want = k < c ? math.pow((double)p.Persistence, k)
                                    : math.pow((double)p.Persistence, c - 1) * math.pow((double)p.FinePersistence, k - c + 1);
                Assert.AreEqual(want, p.OctaveAmplitude(k), 1e-12 * want, $"octave {k}");
            }

            // An edge exactly on an octave's wavelength keeps that octave coarse.
            var exact = EarthScaleTests.Params(EarthScaleTests.OctaveCount)
                                       .WithRoughness(0.7f, EarthScaleTests.Params(EarthScaleTests.OctaveCount).OctaveWavelength0 / 8.0);
            Assert.AreEqual(4, exact.CrossoverOctave, "octave 3's wavelength is exactly the crossover");

            // Each octave's term, by the difference of successive prefixes, is its amplitude times
            // the single-slope sum's term for that octave.
            var rng = new Unity.Mathematics.Random(99u);
            for (int s = 0; s < 100; s++)
            {
                double3 x = rng.NextDouble3(-500.0, 500.0);
                for (int n = 1; n <= 20; n++)
                {
                    double rough = LatticeNoise.Fbm(x, n, p.Persistence, p.Lacunarity, p.Seed, float3.zero, 0, 0,
                                                    p.FinePersistence, p.CrossoverOctave)
                                 - LatticeNoise.Fbm(x, n - 1, p.Persistence, p.Lacunarity, p.Seed, float3.zero, 0, 0,
                                                    p.FinePersistence, p.CrossoverOctave);
                    double plain = LatticeNoise.Fbm(x, n, p.Persistence, p.Lacunarity, p.Seed)
                                 - LatticeNoise.Fbm(x, n - 1, p.Persistence, p.Lacunarity, p.Seed);
                    double scale = p.OctaveAmplitude(n - 1) / math.pow((double)p.Persistence, n - 1);
                    Assert.AreEqual(scale * plain, rough, 1e-12 * math.max(1.0, math.abs(plain)), $"octave {n - 1} at {x}");
                }
            }

            Assert.Throws<System.ArgumentOutOfRangeException>(() => p.WithRoughness(0f, 1000.0), "fine persistence 0");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => p.WithRoughness(1.5f, 1000.0), "fine persistence above 1");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => p.WithRoughness(0.7f, 0.0), "no crossover");
            var plainParams = EarthScaleTests.Params(EarthScaleTests.OctaveCount);
            Assert.AreEqual(plainParams.Persistence, plainParams.FinePersistence, "Create leaves one slope");
        }

        // K4: the continental cutoff drops every detail octave at least the cutoff long, counted
        // by exponent like the band edges, at every depth. Each dropped octave adds exactly
        // nothing and each kept one exactly its uncut term, so the sum is still a strict prefix of
        // itself and the geomorph target is still the parent; kept at amplitude 1 the band is the
        // uncut sum to the bit; and the height bound loses exactly the dropped terms.
        [Test]
        public void CutoffDropsTheCoarseOctaves()
        {
            var rough = EarthScaleTests.RoughParams.WithBands(150000.0, 5000.0);

            // By exponent. Earth's octave 0 is S0 across, so octaves 0-3 are at least 1,000 km.
            var earth = rough.WithContinentalCutoff(1000000.0, 0f);
            Assert.AreEqual(4, earth.FirstOctave, "Earth at 1,000 km");
            Assert.AreEqual(-1f, earth.CoarseGain, "an amplitude of 0 is a gain of -1");
            Assert.AreEqual(3, StressSceneTests.Ringworld.RoughParams.WithContinentalCutoff(250000.0, 0f).FirstOctave,
                "the ring at 250 km");
            var exact = rough.WithContinentalCutoff(rough.OctaveWavelength0 / 8.0, 0f);
            Assert.AreEqual(4, exact.FirstOctave, "octave 3's wavelength exactly drops octave 3");
            Assert.AreEqual(0, rough.WithContinentalCutoff(4.0 * rough.OctaveWavelength0, 0f).FirstOctave,
                "a cutoff longer than octave 0 drops nothing");
            Assert.AreEqual(0f, rough.WithContinentalCutoff(4.0 * rough.OctaveWavelength0, 0f).CoarseGain,
                "a cutoff that drops nothing leaves no gain");

            var kept = rough.WithContinentalCutoff(1000000.0, 1f);
            var half = rough.WithContinentalCutoff(1000000.0, 0.5f);
            var rng  = new Unity.Mathematics.Random(0xC07u);
            for (int s = 0; s < 100; s++)
            {
                double3 x    = rng.NextDouble3(-500.0, 500.0);
                float3  gain = rng.NextFloat3(-0.5f, 2f);
                for (int n = 1; n <= EarthScaleTests.OctaveCount; n++)
                {
                    double uncut     = Sum(rough, x, n, gain), uncutLess = Sum(rough, x, n - 1, gain);
                    double cut       = Sum(earth, x, n, gain), cutLess   = Sum(earth, x, n - 1, gain);
                    double term      = cut - cutLess;
                    if (n - 1 < earth.FirstOctave)
                        Assert.AreEqual(0.0, cut, $"octave {n - 1} at {x}: a dropped octave added something");
                    else
                        Assert.AreEqual(uncut - uncutLess, term, 1e-12 * math.max(1.0, math.abs(term)),
                            $"octave {n - 1} at {x}: a kept octave is not its uncut term");

                    Assert.AreEqual(math.asulong(uncut), math.asulong(Sum(kept, x, n, gain)),
                        $"{n} octaves at {x}: the continental band at amplitude 1 is not the uncut sum");
                    if (n - 1 < half.FirstOctave)
                        Assert.AreEqual(0.5 * uncut, Sum(half, x, n, gain), 1e-12 * math.max(1.0, math.abs(uncut)),
                            $"{n} octaves at {x}: the continental band at amplitude 0.5");
                }
            }

            // The height function takes the cut: at a depth whose band limit is all continental
            // band, the detail is exactly zero and only the macro layer is left.
            var macro = new MacroSample { BaseHeight = 1234.5 };
            for (int d = 0; d < 20; d++)
            {
                double3 point = rng.NextDouble3(-1.0, 1.0) * EarthScaleTests.Radius;
                float h = TerrainHeightFunction.Height(point, earth.MaxOctave(d), macro, earth);
                if (earth.MaxOctave(d) < earth.FirstOctave)
                    Assert.AreEqual(1234.5f, h, $"depth {d}: detail left below the cutoff");
                Assert.AreEqual((float)(earth.HeightScale * Sum(earth, point / earth.OctaveWavelength0, earth.MaxOctave(d) + 1, 0f) + macro.BaseHeight),
                                h, $"depth {d}: Height is not the cut sum");
            }

            // The bound loses exactly the dropped terms.
            double dropped = 0.0;
            for (int k = 0; k < earth.FirstOctave; k++) dropped += earth.OctaveAmplitude(k);
            Assert.AreEqual(TerrainHeightFunction.MaxAbsHeight(rough, MacroParams.Disabled) - rough.HeightScale * dropped,
                            TerrainHeightFunction.MaxAbsHeight(earth, MacroParams.Disabled), 1e-6,
                            "MaxAbsHeight with the cutoff");
            Assert.AreEqual(math.asulong(TerrainHeightFunction.MaxAbsHeight(rough, MacroParams.Disabled)),
                            math.asulong(TerrainHeightFunction.MaxAbsHeight(kept, MacroParams.Disabled)),
                            "MaxAbsHeight with the continental band kept at amplitude 1");

            Assert.Throws<System.ArgumentOutOfRangeException>(() => rough.WithContinentalCutoff(-1.0, 0f), "a negative cutoff");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => rough.WithContinentalCutoff(double.NaN, 0f), "a NaN cutoff");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => rough.WithContinentalCutoff(double.PositiveInfinity, 0f), "an infinite cutoff");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => rough.WithContinentalCutoff(1000000.0, -0.5f), "a negative amplitude");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => rough.WithContinentalCutoff(1000000.0, float.NaN), "a NaN amplitude");
        }

        static double Sum(in HeightParams p, double3 x, int n, float3 gain)
            => LatticeNoise.Fbm(x, n, p.Persistence, p.Lacunarity, p.Seed, gain, p.LowBandEnd, p.MidBandEnd,
                                p.FinePersistence, p.CrossoverOctave, p.FirstOctave, p.CoarseGain);
    }
}
