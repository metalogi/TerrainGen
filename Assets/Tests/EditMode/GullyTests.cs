using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // M5c-2 (Docs/M5_MacroLayer_Plan.md, E6): the gully term's slope damping, G1. Every octave from
    // HeightParams.GullyStartOctave on is divided by 1 + damping |g|^2, g the tangent slope of the
    // octaves before it. These hold what the plan asks of it: the identity when off, the start
    // counted by exponent, the slope measured in metres per metre on the tangent plane of every
    // topology, the bound kept, the sum still a prefix of itself, and agreement across a cube edge.
    public class GullyTests
    {
        // Earth's detail at the stress scene's spectrum, bands and continental cutoff.
        // Without its gully term, which these tests apply themselves.
        static HeightParams Earth => EarthScaleTests.SpectrumParams.WithBands(150000.0, 5000.0)
                                                                   .WithContinentalCutoff(1000000.0, 0f);

        static double Sum(in HeightParams p, double3 x, int n, float3 gain)
            => LatticeNoise.Fbm(x, n, p.Persistence, p.Lacunarity, p.Seed, gain, p.LowBandEnd, p.MidBandEnd,
                                p.FinePersistence, p.CrossoverOctave, p.FirstOctave, p.CoarseGain);

        static double Gullied(in HeightParams p, double3 surfacePos, int n, float3 gain, double damping, int start)
            => LatticeNoise.Fbm(surfacePos / p.OctaveWavelength0, n, p.Persistence, p.Lacunarity, p.Seed, gain,
                                p.LowBandEnd, p.MidBandEnd, p.FinePersistence, p.CrossoverOctave,
                                p.FirstOctave, p.CoarseGain, start, damping,
                                p.HeightScale / p.OctaveWavelength0, p.SurfaceUp(surfacePos));

        // Off is off: damping 0 never takes the gully path, and the gully sum itself is the general
        // sum to the bit with damping 0, or with no octave at or past its start.
        [Test]
        public void GulliesOffKeepTheBits()
        {
            var p   = Earth;
            var off = p.WithGullies(0f, 5000.0, 3f);
            Assert.IsFalse(off.HasGullies, "damping 0 turned the gully term on");
            Assert.AreEqual(0f, off.WetFloorDamping, "damping 0 kept a wet-floor damping");

            var s     = EarthScaleTests.Earth;
            var roots = SurfaceMath.BuildRoots(s);
            var rng   = new Unity.Mathematics.Random(0x6011u);
            for (int k = 0; k < 200; k++)
            {
                double3 point = SurfaceMath.SurfacePoint(s, roots[rng.NextInt(0, 6)], rng.NextDouble(), rng.NextDouble());
                var     macro = new MacroSample { BaseHeight = 321.0, BandGain = rng.NextFloat3(-0.5f, 2f), Wetness = 0.7f };
                int     oct   = p.MaxOctave(rng.NextInt(0, EarthScaleTests.MaxDepth + 1));
                Assert.AreEqual(math.asuint(TerrainHeightFunction.Height(point, oct, macro, p)),
                                math.asuint(TerrainHeightFunction.Height(point, oct, macro, off)),
                                $"damping 0 changed the height at {point}");

                for (int n = 1; n <= p.OctaveCount; n++)
                {
                    double general = Sum(p, point / p.OctaveWavelength0, n, macro.BandGain);
                    Assert.AreEqual(math.asulong(general), math.asulong(Gullied(p, point, n, macro.BandGain, 0.0, 0)),
                        $"{n} octaves at {point}: the gully sum at damping 0 is not the general sum");
                    Assert.AreEqual(math.asulong(general), math.asulong(Gullied(p, point, n, macro.BandGain, 10.0, n)),
                        $"{n} octaves at {point}: the gully sum with nothing past its start is not the general sum");
                }
            }
        }

        // By exponent, like the band edges, but inclusive at the start: an octave exactly the start
        // wavelength is damped.
        [Test]
        public void GullyStartCountsByExponent()
        {
            var p  = Earth;
            double l3 = p.OctaveWavelength0 / 8.0;
            Assert.AreEqual(3, p.WithGullies(1f, l3, 0f).GullyStartOctave, "octave 3's wavelength exactly damps octave 3");
            Assert.AreEqual(3, p.WithGullies(1f, l3 * 1.01, 0f).GullyStartOctave, "just above octave 3's wavelength");
            Assert.AreEqual(4, p.WithGullies(1f, l3 * 0.99, 0f).GullyStartOctave, "just below octave 3's wavelength");
            Assert.AreEqual(0, p.WithGullies(1f, 4.0 * p.OctaveWavelength0, 0f).GullyStartOctave, "longer than octave 0");
            Assert.AreEqual(p.OctaveCount, p.WithGullies(1f, 1e-6, 0f).GullyStartOctave, "shorter than every octave");

            Assert.Throws<System.ArgumentOutOfRangeException>(() => p.WithGullies(-1f, 1000.0, 0f), "negative damping");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => p.WithGullies(float.NaN, 1000.0, 0f), "NaN damping");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => p.WithGullies(1f, 0.0, 0f), "no start");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => p.WithGullies(1f, double.PositiveInfinity, 0f), "an infinite start");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => p.WithGullies(1f, 1000.0, -1f), "negative wet damping");
        }

        // The damping of the first damped octave is 1 / (1 + E |g|^2), with g the slope of the undamped
        // octaves before it in metres per metre along the surface, measured here by central
        // differences along two tangent directions. On a sphere, inside a cylinder and on a plane,
        // so each topology's up is checked; a slope taken in 3D, or in noise units, would miss.
        [Test]
        public void DampingFollowsTheTangentSlope()
        {
            var cases = new[]
            {
                (name: "EarthStress",     s: EarthScaleTests.Earth, p: Earth),
                (name: "RingworldStress", s: StressSceneTests.Ringworld.Surface, p: StressSceneTests.Ringworld.RoughParams),
                (name: "plane",           s: SurfaceDef.PlaneGrid(20000.0, 2, 2),
                 p: HeightParams.Create(SurfaceDef.PlaneGrid(20000.0, 2, 2), 33, 0.0, 16, 400f, 0.6f, 2f, 9u)),
            };
            const double E = 3.0;

            foreach (var (name, s, p) in cases)
            {
                var    roots = SurfaceMath.BuildRoots(s);
                var    rng   = new Unity.Mathematics.Random(0xD4u);
                int    start = 9;
                double worst = 0.0, steepest = 0.0;
                for (int k = 0; k < 200; k++)
                {
                    SurfaceMath.SurfaceFrame(s, roots[rng.NextInt(0, roots.Length)], rng.NextDouble(), rng.NextDouble(),
                                             out double3 point, out float3 nf);
                    float3 gain = rng.NextFloat3(-0.5f, 2f);

                    double plain  = Sum(p, point / p.OctaveWavelength0, start + 1, gain) - Sum(p, point / p.OctaveWavelength0, start, gain);
                    double damped = Gullied(p, point, start + 1, gain, E, start) - Gullied(p, point, start, gain, E, start);

                    // The slope of the octaves before the start, in metres per metre.
                    double3 n  = math.normalize((double3)nf);
                    double3 t1 = math.normalize(math.cross(n, math.abs(n.x) < 0.9 ? new double3(1, 0, 0) : new double3(0, 1, 0)));
                    double3 t2 = math.cross(n, t1);
                    // A thousandth of the finest wavelength in the slope, so the difference is exact to 1e-7.
                    double h = 1e-3 * p.OctaveWavelength0 / (1 << (start - 1));
                    double H(double3 x) => p.HeightScale * Sum(p, x / p.OctaveWavelength0, start, gain);
                    double g1 = (H(point + h * t1) - H(point - h * t1)) / (2.0 * h);
                    double g2 = (H(point + h * t2) - H(point - h * t2)) / (2.0 * h);
                    double expected = plain / (1.0 + E * (g1 * g1 + g2 * g2));

                    steepest = math.max(steepest, math.sqrt(g1 * g1 + g2 * g2));
                    if (math.abs(plain) > 1e-6)
                        worst = math.max(worst, math.abs(damped - expected) / math.abs(plain));
                }
                TestContext.WriteLine($"{name}: steepest slope before octave {start} {steepest:F3}, " +
                                      $"worst damping error {worst:E2} of the octave");
                Assert.Greater(steepest, 0.05, $"{name}: the slopes are too gentle for the damping to show");
                Assert.Less(worst, 1e-4, $"{name}: the damping does not follow the tangent slope");
            }
        }

        // Each octave adds at most its undamped amplitude, so the height stays inside MaxAbsHeight;
        // and the damping really damps, the fine band losing energy against the undamped sum.
        [Test]
        public void DampingKeepsTheBoundAndLowersTheFineBand()
        {
            var p     = Earth;
            var g     = p.WithGullies(4f, 5000.0, 0f);
            var s     = EarthScaleTests.Earth;
            var roots = SurfaceMath.BuildRoots(s);
            var rng   = new Unity.Mathematics.Random(0xB0Du);
            double bound = TerrainHeightFunction.MaxAbsHeight(g, MacroParams.Disabled);
            Assert.AreEqual(TerrainHeightFunction.MaxAbsHeight(p, MacroParams.Disabled), bound, 0.0,
                            "the gully term changed the analytic bound");

            int    oct  = p.OctaveCount - 1;
            double fine = 0.0, fineDamped = 0.0;
            for (int k = 0; k < 2000; k++)
            {
                double3 point = SurfaceMath.SurfacePoint(s, roots[rng.NextInt(0, 6)], rng.NextDouble(), rng.NextDouble());
                var     macro = new MacroSample { BandGain = new float3(0f, rng.NextFloat(-0.7f, 2f), -0.1f) };
                float   h     = TerrainHeightFunction.Height(point, oct, macro, g);
                Assert.LessOrEqual(math.abs(h), bound, $"the gullied height at {point} leaves the bound");

                // The detail at and past the start, damped and not.
                double a = TerrainHeightFunction.Height(point, oct, macro, p) - TerrainHeightFunction.Height(point, g.GullyStartOctave - 1, macro, p);
                double b = h - TerrainHeightFunction.Height(point, g.GullyStartOctave - 1, macro, g);
                fine       += a * a;
                fineDamped += b * b;
                for (int n = g.GullyStartOctave; n < oct; n++)
                {
                    // Each term no larger than its undamped amplitude, with its band at its largest.
                    double term = Gullied(g, point, n + 1, macro.BandGain, 4.0, g.GullyStartOctave)
                                - Gullied(g, point, n,     macro.BandGain, 4.0, g.GullyStartOctave);
                    Assert.LessOrEqual(math.abs(term), g.OctaveAmplitude(n) * 3.0 + 1e-12, $"octave {n} exceeds its amplitude");
                }
            }
            TestContext.WriteLine($"fine band rms {math.sqrt(fine / 2000):F2} m, damped {math.sqrt(fineDamped / 2000):F2} m");
            Assert.Less(fineDamped, 0.95 * fine, "the damping barely lowers the fine band");
        }

        // Across a cube edge the two faces' points differ by about a nanometre, and the gully term,
        // a function of the point alone -- its up included -- keeps the heights as close as the
        // positions. A term steered by anything per-quad would differ by metres.
        [Test]
        public void GulliesAgreeAcrossCubeEdges()
        {
            var    s     = EarthScaleTests.Earth;
            var    roots = SurfaceMath.BuildRoots(s);
            var    p     = Earth.WithGullies(4f, 5000.0, 0f);
            int    oct   = p.OctaveCount - 1;
            double worst = 0.0;

            for (int face = 0; face < 6; face++)
            for (int e = 0; e < 4; e++)
            {
                var edge = (Edge)e;
                var link = SurfaceMath.CubeEdgeLink(face, edge);
                for (int k = 1; k < 32; k++)
                {
                    double t = k / 32.0;
                    EdgeUV(edge, t, out double u, out double v);
                    EdgeUV(link.Edge, link.Reversed ? 1.0 - t : t, out double un, out double vn);
                    double3 a = SurfaceMath.SurfacePoint(s, roots[face], u, v);
                    double3 b = SurfaceMath.SurfacePoint(s, roots[link.Face], un, vn);
                    Assert.Less(math.distance(a, b), 1e-6, $"face {face} edge {edge}: not the same point");

                    float ha = TerrainHeightFunction.Height(a, oct, MacroSample.Zero, p);
                    float hb = TerrainHeightFunction.Height(b, oct, MacroSample.Zero, p);
                    worst = math.max(worst, math.abs(ha - hb));
                }
            }
            TestContext.WriteLine($"worst height disagreement across a cube edge with gullies: {worst:E3} m");
            Assert.Less(worst, 1e-3, "the gully term disagrees across a cube edge");
        }

        // Every stress scene runs the gully term, and in every scene with erosion it starts past the
        // octaves erosion carries -- the condition ErosionParams.Create enforces at startup, checked
        // here on the scenes' own configurations so a retune fails a test rather than play.
        [Test]
        public void SceneGulliesStartPastTheErodedOctaves()
        {
            foreach (var m in MacroTests.StressMacros)
            {
                var hp = MacroTests.SceneParams(m, MacroTests.RoughParams(m.Scene));
                Assert.IsTrue(hp.HasGullies, $"{m.Scene}: no gully term");
                if (!m.Erosion.HasValue) continue;
                var mp = MacroParams.Create(m.Surface, hp, m.ErodedConfig);
                int eroded = ErosionChannels.ErodedOctave(hp, mp);
                TestContext.WriteLine($"{m.Scene}: gullies from octave {hp.GullyStartOctave}, erosion carries 0 .. {eroded}");
                Assert.Greater(hp.GullyStartOctave, eroded, $"{m.Scene}: the gully term reaches into the eroded octaves");
                Assert.IsTrue(ErosionParams.Create(m.Surface, hp, mp, m.ErodedConfig).Enabled, $"{m.Scene}: erosion refused");
            }
        }

        static void EdgeUV(Edge e, double t, out double u, out double v)
        {
            switch (e)
            {
                case Edge.South: u = t;   v = 0.0; return;
                case Edge.East:  u = 1.0; v = t;   return;
                case Edge.North: u = t;   v = 1.0; return;
                default:         u = 0.0; v = t;   return;
            }
        }
    }
}
