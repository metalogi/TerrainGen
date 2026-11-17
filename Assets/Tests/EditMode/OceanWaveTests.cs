using System;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;

namespace Sonoma.Tests
{
    // The sea's waves (Docs/Ocean_B_Plan.md), UnityEngine-free so the out-of-Editor harness runs it:
    // the table OceanWaves.Build makes (B4), the phase split that survives a rebase (B1), and the fade
    // by effective depth that two chunks always agree on (B2).
    public class OceanWaveTests
    {
        // OceanWaveSettings' defaults, which Assets/Settings/OceanWaves.asset starts from.
        static OceanWaveConfig Default => new OceanWaveConfig
        {
            Count = 8, ShortestWavelength = 1.5, LongestWavelength = 80.0,
            Steepness = 0.06, SpectrumExponent = 0.1, Choppiness = 0.6,
            WindDirection = new double3(0, 0, 1), SpreadDegrees = 50.0, ShoalFactor = 0.25, Seed = 1,
        };

        const double EarthRadius = 6_371_000.0;

        // B4: the same settings give the same bits; wavelengths run geometric from the shortest to
        // the longest, each K a float's value so the shader holds it exactly, each direction inside
        // the cone, deep-water frequencies; the bounds are the sums they claim; and a sea choppy
        // enough to loop over itself is refused.
        [Test]
        public void WaveTableIsDeterministicAndBounded()
        {
            var c = Default;
            var a = OceanWaves.Build(c);
            var b = OceanWaves.Build(c);
            Assert.AreEqual(c.Count, a.Length);
            for (int i = 0; i < a.Length; i++)
                Assert.AreEqual(a[i], b[i], $"wave {i} differs between two builds");

            double amplitude = 0.0, displacement = 0.0;
            for (int i = 0; i < a.Length; i++)
            {
                var w = a[i];
                Assert.AreEqual(w.K, (double3)(float3)w.K, $"wave {i}: K is not a float's value");
                Assert.AreEqual(1.0 / math.length(w.K), w.Wavelength, 1e-12 * w.Wavelength, $"wave {i}: wavelength");
                double expected = c.ShortestWavelength * math.pow(c.LongestWavelength / c.ShortestWavelength, i / (c.Count - 1.0));
                Assert.AreEqual(expected, w.Wavelength, 1e-6 * expected, $"wave {i}: not geometric");
                Assert.AreEqual(math.sqrt(OceanWaves.Gravity / (2.0 * math.PI_DBL * w.Wavelength)), w.Frequency, 1e-15,
                    $"wave {i}: not deep water");
                double angle = math.degrees(math.acos(math.clamp(math.dot(math.normalize(w.K), c.WindDirection), -1.0, 1.0)));
                Assert.LessOrEqual(angle, c.SpreadDegrees + 1e-4, $"wave {i} runs outside the cone");
                Assert.That(w.Phase0, Is.InRange(0.0, 1.0));
                amplitude    += w.Amplitude;
                displacement += w.Amplitude * math.max(1.0, w.Choppiness);
            }
            Assert.AreEqual(amplitude,    OceanWaves.MaxAmplitude(a));
            Assert.AreEqual(displacement, OceanWaves.MaxDisplacement(a));
            Assert.GreaterOrEqual(OceanWaves.MaxDisplacement(a), OceanWaves.MaxAmplitude(a));
            Assert.LessOrEqual(OceanWaves.NoLoopSum(a), 1.0);
            Assert.AreEqual(0.0, OceanWaves.MaxAmplitude(null), "a flat sea");
            TestContext.WriteLine($"default sea: amplitude {OceanWaves.MaxAmplitude(a):F3} m, displacement " +
                                  $"{OceanWaves.MaxDisplacement(a):F3} m, no-loop sum {OceanWaves.NoLoopSum(a):F3}");

            // The directions spread: with a cone of 50 degrees, not every wave runs with the wind.
            double widest = 0.0;
            foreach (var w in a) widest = math.max(widest, math.acos(math.dot(math.normalize(w.K), c.WindDirection)));
            Assert.Greater(math.degrees(widest), 10.0, "the directions do not spread");

            // A different seed draws different directions; a different count, different waves.
            var seeded = c; seeded.Seed = 2;
            Assert.AreNotEqual(a[3].K, OceanWaves.Build(seeded)[3].K);

            // Loops: choppiness just past the bound is refused, and just under it accepted.
            double perQ = OceanWaves.NoLoopSum(a) / c.Choppiness;
            var choppy = c; choppy.Choppiness = 1.0 / perQ * 1.001;
            Assert.Throws<ArgumentOutOfRangeException>(() => OceanWaves.Build(choppy));
            choppy.Choppiness = 1.0 / perQ * 0.999;
            Assert.DoesNotThrow(() => OceanWaves.Build(choppy));

            // What Build refuses.
            foreach (int n in new[] { 0, OceanWaves.MaxWaves + 1 })
            { var bad = c; bad.Count = n; Assert.Throws<ArgumentOutOfRangeException>(() => OceanWaves.Build(bad), $"count {n}"); }
            { var bad = c; bad.ShortestWavelength = 0.0; Assert.Throws<ArgumentOutOfRangeException>(() => OceanWaves.Build(bad)); }
            { var bad = c; bad.LongestWavelength = 1.0; Assert.Throws<ArgumentOutOfRangeException>(() => OceanWaves.Build(bad), "longest under shortest"); }
            { var bad = c; bad.WindDirection = double3.zero; Assert.Throws<ArgumentOutOfRangeException>(() => OceanWaves.Build(bad)); }
            { var bad = c; bad.Steepness = double.NaN; Assert.Throws<ArgumentOutOfRangeException>(() => OceanWaves.Build(bad)); }
            { var bad = c; bad.SpreadDegrees = 200.0; Assert.Throws<ArgumentOutOfRangeException>(() => OceanWaves.Build(bad)); }
            { var one = c; one.Count = 1; Assert.AreEqual(c.ShortestWavelength, OceanWaves.Build(one)[0].Wavelength, 1e-6); }
        }

        // B1, the parent plan's test 6: the shader takes K · p in float from the render-space
        // position and adds the offset computed here in double. Over a thousand rebases at Earth
        // radius, with the camera wandering and every point within 3 km of it, that stays within
        // float rounding of the true phase. The two ways to get it wrong both fail: an offset not
        // re-pushed when the origin moves, and an offset computed from the origin narrowed to float.
        [Test]
        public void WavePhaseSurvivesRebases()
        {
            var waves = OceanWaves.Build(Default);
            var rng   = new Unity.Mathematics.Random(20261116u);

            // Where the wind's axis, z, is large: a float origin rounds every coordinate, but only the
            // ones the waves run along move their phase, and these run mostly along z.
            double3 camera = math.normalize(new double3(0.2, 0.3, 0.93)) * EarthRadius;
            double3 origin = camera;
            double3 stale  = origin;   // the origin the mutation's offset was last computed at
            double  time   = 0.0;
            double  worst = 0.0, worstStale = 0.0, worstFloat = 0.0;
            int     shifts = 0;

            while (shifts < 1000)
            {
                camera += rng.NextDouble3Direction() * rng.NextDouble(0.0, 1500.0);
                time   += rng.NextDouble(0.0, 0.5);

                // WorldOriginSystem: past the threshold, the shift is the camera's render-space
                // position as a float, and the origin moves by exactly that.
                float3 cameraRender = (float3)(camera - origin);
                if (math.length(cameraRender) > 1000f)
                {
                    stale   = origin;
                    origin += (double3)cameraRender;
                    shifts++;
                }

                double3 point  = camera + rng.NextDouble3Direction() * rng.NextDouble(0.0, 3000.0);
                float3  render = (float3)(point - origin);
                foreach (var w in waves)
                {
                    double truth = OceanWaves.Phase(w, point, time);
                    worst      = math.max(worst,      Cycles(OceanWaves.ShaderPhase(w, render, OceanWaves.PhaseOffset(w, origin, time)), truth));
                    worstStale = math.max(worstStale, Cycles(OceanWaves.ShaderPhase(w, render, OceanWaves.PhaseOffset(w, stale, time)), truth));
                    worstFloat = math.max(worstFloat, Cycles(OceanWaves.ShaderPhase(w, render,
                                     OceanWaves.PhaseOffset(w, (double3)(float3)origin, time)), truth));
                }
            }

            TestContext.WriteLine($"worst phase error over {shifts} rebases: {worst:G3} cycles; offset not re-pushed " +
                                  $"{worstStale:G3}; offset from the float origin {worstFloat:G3}");
            Assert.LessOrEqual(worst, 1e-3, "the split phase strays past float rounding of K · p");
            Assert.Greater(worstStale, 0.05, "an offset left at the old origin should jump at a rebase");
            Assert.Greater(worstFloat, 0.05, "an offset from the float origin should be half a metre out at Earth radius");
        }

        // The distance between two phases on the circle, in cycles, at most a half.
        static double Cycles(double a, double b)
        {
            double d = OceanWaves.Frac(a - b);
            return math.min(d, 1.0 - d);
        }

        // B2: each wave fades by the spacing at the effective depth, depth − k. A fully morphed child
        // and its parent share an effective depth, so they get the same fade to the bit, at every
        // depth and wavelength; the fade is continuous in the effective depth; and nothing displaces
        // at fewer than four vertices a wavelength. A fade by depth alone, the mutation, jumps where a
        // fully morphed child meets its parent.
        [Test]
        public void WaveFadeAgreesAcrossDepths()
        {
            // Earth's depth-0 vertex spacing: a quarter of the circumference over 32 cells.
            double unit = 0.5 * math.PI_DBL * EarthRadius / 32.0;
            var waves = OceanWaves.Build(Default);

            int jumps = 0;
            foreach (var w in waves)
            {
                for (int d = 1; d <= 25; d++)
                {
                    double child  = OceanWaves.Fade(w.Wavelength, OceanWaves.Spacing(unit, d - 1.0));   // depth d, k = 1
                    double parent = OceanWaves.Fade(w.Wavelength, OceanWaves.Spacing(unit, (d - 1) - 0.0));
                    Assert.AreEqual(parent, child, $"λ {w.Wavelength:G3} m, depth {d}: a fully morphed child fades as its parent");

                    if (OceanWaves.Fade(w.Wavelength, OceanWaves.Spacing(unit, d)) !=
                        OceanWaves.Fade(w.Wavelength, OceanWaves.Spacing(unit, d - 1)))
                        jumps++;
                }

                double previous = OceanWaves.Fade(w.Wavelength, OceanWaves.Spacing(unit, 0.0));
                for (double e = 1e-3; e <= 26.0; e += 1e-3)
                {
                    double spacing = OceanWaves.Spacing(unit, e);
                    double f = OceanWaves.Fade(w.Wavelength, spacing);
                    Assert.LessOrEqual(math.abs(f - previous), 0.005, $"λ {w.Wavelength:G3} m: the fade jumps at e = {e:F3}");
                    if (f > 0.0)
                        Assert.Greater(w.Wavelength / spacing, OceanWaves.FadeStart,
                            $"λ {w.Wavelength:G3} m displaces at e = {e:F3}, under four vertices a wavelength");
                    previous = f;
                }
                Assert.AreEqual(1.0, OceanWaves.Fade(w.Wavelength, w.Wavelength / OceanWaves.FadeFull), "fully drawn from FadeFull");
                Assert.AreEqual(0.0, OceanWaves.Fade(w.Wavelength, w.Wavelength / OceanWaves.FadeStart), "not at all at FadeStart");
            }
            Assert.Greater(jumps, 0, "a fade by depth alone would not jump at a boundary, so this test cannot tell the two apart");
        }
    }
}
