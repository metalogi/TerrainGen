using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;

namespace Sonoma.Tests
{
    // Properties of the noise itself: shape, statistics, determinism, and the precision
    // behaviour that is the whole reason this replaces the prototype's float sampling.
    public class LatticeNoiseTests
    {
        const uint Seed = 42u;

        [Test]
        public void NoiseIsZeroAtLatticePoints()
        {
            var rng = new Unity.Mathematics.Random(12345);   // fixed seed: failures reproduce

            for (int i = 0; i < 200; i++)
            {
                var c = new double3(rng.NextInt(-1000000, 1000000),
                                    rng.NextInt(-1000000, 1000000),
                                    rng.NextInt(-1000000, 1000000));

                // Every gradient dots against a zero offset vector at its own corner, and
                // the fade weights put all of it on that corner. Exactly zero, not nearly.
                Assert.AreEqual(0f, LatticeNoise.Gradient3D(c, Seed),
                    $"noise is not exactly zero at lattice point {c}");
            }
        }

        [Test]
        public void NoiseRangeIsWithinUnitInterval()
        {
            var   rng = new Unity.Mathematics.Random(23456);
            float peak = 0f;

            for (int i = 0; i < 200000; i++)
            {
                var p = new double3(rng.NextDouble(-1000.0, 1000.0),
                                    rng.NextDouble(-1000.0, 1000.0),
                                    rng.NextDouble(-1000.0, 1000.0));

                float n = LatticeNoise.Gradient3D(p, Seed);
                peak = math.max(peak, math.abs(n));

                Assert.LessOrEqual(math.abs(n), 1.0f + 1e-5f,
                    $"noise escaped [-1, 1] at {p}: {n}");
            }

            // The supremum is 1.0 and it is attained -- hill-climbing on this exact gradient
            // table converges to 1.000000, and 200k uniform samples reach 0.9266. Asserting
            // the lower bound too means a change that quietly shrinks the gradient set (or
            // adds a normalisation) fails here instead of just making the terrain flatter.
            //
            // The textbook sqrt(3)/2 ~ 0.866 bound for 3D Perlin does not apply to the
            // 16-entry improved-noise table. Do not "correct" the amplitude to match it.
            Assert.Greater(peak, 0.9f,
                $"peak |noise| over 200k samples was only {peak:F4}; expected ~0.93");
        }

        [Test]
        public void NoiseIsZeroMean()
        {
            var    rng = new Unity.Mathematics.Random(34567);
            double sum = 0.0;
            const int n = 200000;

            for (int i = 0; i < n; i++)
            {
                var p = new double3(rng.NextDouble(-1000.0, 1000.0),
                                    rng.NextDouble(-1000.0, 1000.0),
                                    rng.NextDouble(-1000.0, 1000.0));
                sum += LatticeNoise.Gradient3D(p, Seed);
            }

            double mean = sum / n;

            // Measured -1.6e-5. A reintroduced (n * 0.5 + 0.5) would land at +0.5 and lift
            // the whole terrain off the base surface.
            Assert.Less(math.abs(mean), 0.01,
                $"noise mean over {n} samples was {mean:F6}; expected ~0");
        }

        [Test]
        public void NoiseIsDeterministicAcrossThreads()
        {
            var rng = new Unity.Mathematics.Random(45678);
            var pts = new double3[10000];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new double3(rng.NextDouble(-1e6, 1e6),
                                     rng.NextDouble(-1e6, 1e6),
                                     rng.NextDouble(-1e6, 1e6));

            var expected = new uint[pts.Length];
            for (int i = 0; i < pts.Length; i++)
                expected[i] = math.asuint(LatticeNoise.Gradient3D(pts[i], Seed));

            var tasks = new Task<uint[]>[4];
            for (int t = 0; t < tasks.Length; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    var got = new uint[pts.Length];
                    for (int i = 0; i < pts.Length; i++)
                        got[i] = math.asuint(LatticeNoise.Gradient3D(pts[i], Seed));
                    return got;
                });
            }
            Task.WaitAll(tasks);

            // Bit-identical, not approximately equal. Chunks are generated on worker threads
            // in whatever order the scheduler picks; if two threads can disagree in the last
            // bit then two neighbouring chunks can disagree at a shared vertex, and the seam
            // guarantee is gone.
            for (int t = 0; t < tasks.Length; t++)
            for (int i = 0; i < pts.Length; i++)
                Assert.AreEqual(expected[i], tasks[t].Result[i],
                    $"worker {t} disagreed at sample {i} ({pts[i]})");
        }

        [Test]
        public void PrecisionDoesNotCollapseAtLargeCoordinates()
        {
            // The regression test for SonomaRevisedPlan.md section 2.3.
            //
            // A 1e-3 cell step must still move the value, however far out the sample is.
            // Float lattice coordinates are 5.37e8x coarser than double at every magnitude
            // (2^29, independent of |p|); at |p| = 1e7 cells that is a one-cell ULP, so the
            // fractional part quantises to 0 and the noise flattens into plateaus.
            //
            // Mutation check: change (float3)(p - fl) to (float3)p - (float3)fl in
            // LatticeNoise.Gradient3D and this must fail at 1e7 and 1e9. If it still passes,
            // it is not testing what it claims.
            double[] magnitudes = { 1e3, 1e6, 1e7, 1e9 };

            foreach (double mag in magnitudes)
            {
                var    rng  = new Unity.Mathematics.Random(56789);
                double best = 0.0;

                // Take the largest response over several offsets: a single probe can land
                // where the field is genuinely flat and say nothing about precision.
                for (int i = 0; i < 64; i++)
                {
                    double off = rng.NextDouble();
                    var a = new double3(mag + off, off * 3.0, off * 7.0);
                    var b = new double3(mag + off + 1e-3, off * 3.0, off * 7.0);

                    double d = math.abs(LatticeNoise.Gradient3D(b, Seed) -
                                        LatticeNoise.Gradient3D(a, Seed));
                    best = math.max(best, d);
                }

                // Reference deltas at a single probe were 5.4e-4, 1.4e-4, 1.1e-3, 1.3e-3 --
                // flat across nine orders of magnitude, which is the point. 1e-5 leaves room
                // for the probe landing on a shallow slope while still failing hard on a
                // plateau.
                Assert.Greater(best, 1e-5,
                    $"noise is flat at |p| = {mag:E0}: best delta over a 1e-3 step was {best:E3}. " +
                    "Precision has collapsed -- check that p - floor(p) is evaluated in double.");
            }
        }
    }
}
