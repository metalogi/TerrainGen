using System;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // Biomes from climate (C4), the per-band amplitude they and ruggedness set (C7), and the
    // weights' packing into the vertex (C6). UnityEngine-free, so it runs in the harness.
    public class BiomeTests
    {
        static MacroParams Table(int sharpness = 2, float ruggedMin = 0f, float ruggedMax = 0f,
                                 params BiomeConfig[] biomes)
        {
            var s  = SurfaceDef.CubeSphere(6371000.0);
            var hp = HeightParams.Create(s, 33, 0.0, 20, 1000f, 0.5f, 2f, 42u);
            return MacroParams.Create(s, hp, new MacroConfig
            {
                LevelCount = 1, Persistence = 0.5f, Biomes = biomes, BiomeSharpness = sharpness,
                TemperatureScale = 15f, MoistureScale = 0.25f,
                RuggednessGainMin = ruggedMin, RuggednessGainMax = ruggedMax,
            });
        }

        // The shipped four, in MacroTests.Biomes; here only their climates matter.
        static MacroParams Shipped(int sharpness = 2) => Table(sharpness, 0f, 0f, MacroTests.BiomeConfigs);

        static MacroSample Climate(float t, float m, float rugged = 0.5f)
            => new MacroSample { Temperature = t, Moisture = m, Ruggedness = rugged };

        // C4: the weights are a partition of unity -- non-negative, summing to one within float
        // rounding -- everywhere, far outside every biome's centre included, and continuous: a
        // small step in climate moves them by a bounded amount. A nearest-biome rule, or a kernel
        // with finite support and a fallback, fails the second half with a jump of 1 at a border.
        [Test]
        public void BiomeWeightsArePartitionOfUnityAndContinuous()
        {
            foreach (int sharpness in new[] { 1, 2, 4 })
            {
                var p = Shipped(sharpness);
                const float StepT = 0.01f, StepM = 0.0005f;   // a hundredth of a degree; 0.05% moisture
                double worstSum = 0.0, worstStep = 0.0;

                for (float t = -80f; t <= 80f; t += 0.97f)
                for (float m = -0.5f; m <= 1.5f; m += 0.031f)
                {
                    float4 w = Biomes.Classify(Climate(t, m), p);
                    Assert.IsTrue(math.all(w >= 0f), $"sharpness {sharpness} at ({t}, {m}): a negative weight {w}");
                    worstSum = math.max(worstSum, math.abs(math.csum(w) - 1.0));

                    float4 wt = Biomes.Classify(Climate(t + StepT, m), p);
                    float4 wm = Biomes.Classify(Climate(t, m + StepM), p);
                    worstStep = math.max(worstStep, math.cmax(math.abs(wt - w)));
                    worstStep = math.max(worstStep, math.cmax(math.abs(wm - w)));
                }

                Assert.Less(worstSum, 1e-5, $"sharpness {sharpness}: the weights do not sum to one");
                // The steepest the weights get is where two biomes swap places as the nearest,
                // about 2 x sharpness / sqrt(DistanceFloor) per unit of normalised climate. These
                // steps are 6.7e-4 and 2e-3 of a unit, so the bound is generous and a jump is not.
                Assert.Less(worstStep, 0.05, $"sharpness {sharpness}: the weights jump by {worstStep} " +
                                             "for a small step in climate");
            }

            // Exactly on a biome's centre it takes nearly all the weight, and finitely.
            var shipped = Shipped();
            for (int b = 0; b < MacroTests.BiomeConfigs.Length; b++)
            {
                var c = MacroTests.BiomeConfigs[b];
                float4 w = Biomes.Classify(Climate(c.Temperature, c.Moisture), shipped);
                Assert.Greater(w[b], 0.99f, $"biome {b} on its own centre");
            }
        }

        // No biomes: no weights, and every band gain exactly zero -- the M5a height to the bit.
        // Biomes whose amplitudes are all one, and a ruggedness range of 1..1: the same.
        [Test]
        public void BandGainIsTheIdentityByDefault()
        {
            var none = Table();
            Assert.AreEqual(float4.zero, Biomes.Classify(Climate(20f, 0.5f), none));
            var m = Biomes.Modulate(Climate(20f, 0.5f, 0.9f), none, out _);
            Assert.IsTrue(math.all(m.BandGain == 0f), $"no biomes: gain {m.BandGain}");

            var flat = Table(2, 0f, 0f, new BiomeConfig { Temperature = 0f, Moisture = 0f },
                                        new BiomeConfig { Temperature = 30f, Moisture = 1f });
            for (float t = -20f; t <= 40f; t += 7f)
            {
                var g = Biomes.Modulate(Climate(t, 0.4f, 0.7f), flat, out float4 w).BandGain;
                Assert.IsTrue(math.all(g == 0f), $"unit amplitudes at {t} degrees: gain {g} from weights {w}");
            }

            Assert.IsTrue(math.all(MacroSample.Zero.BandGain == 0f));
            Assert.AreEqual(1f, none.MaxAmplitude);
        }

        // C7: one biome's weight carries its gains; ruggedness scales only the mid and high bands,
        // from RuggednessGainMin at 0 to RuggednessGainMax at 1; MaxAmplitude is the largest band
        // amplitude anywhere.
        [Test]
        public void BandGainBlendsBiomesAndRuggedness()
        {
            var a = new BiomeConfig { Temperature = 0f,  Moisture = 0f, Gain = new float3(-0.5f, 0.5f, 1f) };
            var b = new BiomeConfig { Temperature = 30f, Moisture = 1f, Gain = new float3(0.5f, -0.5f, 0f) };
            var p = Table(2, -0.5f, 1f, a, b);

            float3 G(float4 w, float rugged) => Biomes.BandGain(w, rugged, p);
            float3 only = new float3(0.5f, 1.5f, 2f);                  // a's amplitudes
            Assert.AreEqual(only.x - 1f, G(new float4(1, 0, 0, 0), 0.5f).x, 1e-6, "low band ignores ruggedness");
            Assert.AreEqual(only.y * 1.25f - 1f, G(new float4(1, 0, 0, 0), 0.5f).y, 1e-6, "mid at half ruggedness");
            Assert.AreEqual(only.z * 0.5f - 1f, G(new float4(1, 0, 0, 0), 0f).z, 1e-6, "high at no ruggedness");
            Assert.AreEqual(only.z * 2f - 1f, G(new float4(1, 0, 0, 0), 1f).z, 1e-6, "high at full ruggedness");

            float3 half = G(new float4(0.5f, 0.5f, 0, 0), 0.5f);
            Assert.AreEqual(0f, half.x, 1e-6, "an even blend of -0.5 and 0.5");

            // The largest: a's high band at full ruggedness, 2 x 2.
            Assert.AreEqual(4f, p.MaxAmplitude, 1e-6);
        }

        // The weights as the vertex carries them: four bytes, biome 0 lowest, each within half a
        // step of its weight, and summing to within two steps of 255.
        [Test]
        public void SplatPacksIntoFourBytes()
        {
            var p   = Shipped();
            var rng = new Unity.Mathematics.Random(31u);
            for (int k = 0; k < 2000; k++)
            {
                float4 w = Biomes.Classify(Climate(rng.NextFloat(-40f, 40f), rng.NextFloat(0f, 1f)), p);
                uint packed = Biomes.Pack(w);
                float4 back = Biomes.Unpack(packed);
                Assert.Less(math.cmax(math.abs(back - w)), 0.5f / 255f + 1e-6f, $"{w} packed as {packed:X8}");
                uint sum = (packed & 0xFF) + ((packed >> 8) & 0xFF) + ((packed >> 16) & 0xFF) + (packed >> 24);
                Assert.That(sum, Is.InRange(253u, 257u), $"{w} packed to bytes summing to {sum}");
            }
            Assert.AreEqual(0x000000FFu, Biomes.Pack(new float4(1, 0, 0, 0)), "biome 0 is the lowest byte");
            Assert.AreEqual(0xFF000000u, Biomes.Pack(new float4(0, 0, 0, 1)));
        }

        // C5: the band-gain sum with zero gains is the original sum to the bit, and with gains it
        // scales each octave by its band's amplitude and stays a strict prefix of itself.
        [Test]
        public void FbmBandGainsScaleTheirOctaves()
        {
            var rng = new Unity.Mathematics.Random(5u);
            const int Octaves = 12, Low = 3, Mid = 7;
            var gain = new float3(-0.5f, 0.75f, 2f);

            for (int k = 0; k < 300; k++)
            {
                double3 p = rng.NextDouble3(-1000.0, 1000.0);
                double original = LatticeNoise.Fbm(p, Octaves, 0.5f, 2f, 42u);
                double zero     = LatticeNoise.Fbm(p, Octaves, 0.5f, 2f, 42u, float3.zero, Low, Mid);
                Assert.AreEqual(math.asulong(original), math.asulong(zero), $"zero gains at {p}");

                // Octave by octave: each one's contribution is its band's amplitude times the
                // original's, so the difference between successive prefixes says which band it is in.
                for (int n = 1; n <= Octaves; n++)
                {
                    int    o        = n - 1;
                    double plain    = LatticeNoise.Fbm(p, n, 0.5f, 2f, 42u) - LatticeNoise.Fbm(p, n - 1, 0.5f, 2f, 42u);
                    double scaled   = LatticeNoise.Fbm(p, n, 0.5f, 2f, 42u, gain, Low, Mid)
                                    - LatticeNoise.Fbm(p, n - 1, 0.5f, 2f, 42u, gain, Low, Mid);
                    double amp      = 1.0 + (o < Low ? gain.x : o < Mid ? gain.y : gain.z);
                    Assert.AreEqual(amp * plain, scaled, 1e-12 * math.max(1.0, math.abs(plain)),
                        $"octave {o} at {p}");
                }
            }
        }

        // The band edges are wavelengths, turned into octave counts by the exponent, as K0 is:
        // on Earth 50 km and 500 m end the low band after octave 7 (81 km) and the mid band after
        // octave 14 (635 m). An edge exactly on an octave's wavelength keeps that octave in the
        // band above it, which a library log2 returning just under the integer would not.
        [Test]
        public void BandEdgesFallByExponent()
        {
            var earth = EarthScaleTests.Params(EarthScaleTests.OctaveCount).WithBands(50000.0, 500.0);
            Assert.AreEqual(8,  earth.LowBandEnd);
            Assert.AreEqual(15, earth.MidBandEnd);

            var p     = EarthScaleTests.Params(EarthScaleTests.OctaveCount);
            var exact = p.WithBands(p.OctaveWavelength0 / 8.0, p.OctaveWavelength0 / 1024.0);
            Assert.AreEqual(4,  exact.LowBandEnd, "octave 3's wavelength is exactly the edge");
            Assert.AreEqual(11, exact.MidBandEnd, "octave 10's wavelength is exactly the edge");

            // Above octave 0, and below the last: clamped to the octaves there are.
            var wide = p.WithBands(p.OctaveWavelength0 * 4.0, 1e-9);
            Assert.AreEqual(0, wide.LowBandEnd);
            Assert.AreEqual(EarthScaleTests.OctaveCount, wide.MidBandEnd);

            Assert.Throws<ArgumentOutOfRangeException>(() => p.WithBands(500.0, 50000.0), "edges reversed");
            Assert.Throws<ArgumentOutOfRangeException>(() => p.WithBands(500.0, 0.0), "zero edge");
            Assert.AreEqual(0, p.LowBandEnd, "Create leaves every octave in the high band");
            Assert.AreEqual(0, p.MidBandEnd);
        }

        // Configuration is validated in MacroParams.Create.
        [Test]
        public void BiomeTablesAreValidated()
        {
            var one = new BiomeConfig { Temperature = 10f, Moisture = 0.5f };
            Assert.Throws<ArgumentOutOfRangeException>(() => Table(2, 0f, 0f, one, one, one, one, one), "five biomes");
            Assert.Throws<ArgumentOutOfRangeException>(() => Table(0, 0f, 0f, one), "sharpness 0");
            Assert.Throws<ArgumentOutOfRangeException>(() => Table(2, 0.5f, -0.5f, one), "ruggedness range reversed");
            Assert.Throws<ArgumentOutOfRangeException>(() => Table(2, -2f, 0f, one), "negative ruggedness amplitude");
            var flatten = new BiomeConfig { Temperature = 10f, Gain = new float3(-1.5f, 0f, 0f) };
            Assert.Throws<ArgumentOutOfRangeException>(() => Table(2, 0f, 0f, flatten), "negative band amplitude");
            Assert.DoesNotThrow(() => Table(2, 0f, 0f, one, one, one, one));
        }
    }
}
