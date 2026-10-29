using System;
using NUnit.Framework;
using Sonoma.Core.Generation;

namespace Sonoma.Tests
{
    // The ocean's configuration and layout (Docs/Ocean_A_Plan.md), UnityEngine-free so the
    // out-of-Editor harness runs it. Step 2 is OceanParams; Step 3 adds the kinds and the layout.
    public class OceanLayoutTests
    {
        const int    R        = 33;
        const double Envelope = 36600.0;

        static OceanParams Create(bool ocean = true, double sea = 0.0, float overlap = 0f, int resolution = R,
                                  double envelope = Envelope, bool erosion = false, double baseLevel = 0.0) =>
            OceanParams.Create(ocean, sea, overlap, resolution, envelope, erosion, baseLevel);

        // default(OceanParams) is "no ocean", and so is a scene with Ocean off, whatever its sea
        // level: every field at zero, so no path can read a stale sea from a disabled ocean.
        [Test]
        public void DefaultOceanParamsMeanNoOcean()
        {
            Assert.IsFalse(default(OceanParams).Enabled);
            Assert.AreEqual(default(OceanParams), OceanParams.Disabled);
            Assert.AreEqual(default(OceanParams), Create(ocean: false, sea: 68.0, overlap: 5f));

            var on = Create(sea: 68.0, overlap: 5f);
            Assert.IsTrue(on.Enabled);
            Assert.AreEqual(68.0, on.SeaLevel);
            Assert.AreEqual(68f,  on.SeaLevelFloat);
            Assert.AreEqual(5f,   on.TerrainOverlap);
            Assert.AreEqual(0f,   on.WaveAmplitude, "no waves until ocean-b");
        }

        // C1: the sea lies strictly inside the terrain's height envelope, either side of the base
        // surface. Outside it the whole world is one kind and the sea leaves the band LodVertical
        // measures in. With the ocean off the sea level is only checked for being a number.
        [Test]
        public void SeaLevelOutsideTheEnvelopeIsRefused()
        {
            double inside = Envelope - 1e-6;
            Assert.DoesNotThrow(() => Create(sea: inside));
            Assert.DoesNotThrow(() => Create(sea: -inside));
            Assert.Throws<ArgumentOutOfRangeException>(() => Create(sea: Envelope));
            Assert.Throws<ArgumentOutOfRangeException>(() => Create(sea: -Envelope));
            Assert.Throws<ArgumentOutOfRangeException>(() => Create(sea: 0.0, envelope: double.NaN),
                "an envelope that is not a number bounds nothing");
            Assert.DoesNotThrow(() => Create(ocean: false, sea: 2.0 * Envelope));

            foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => Create(sea: bad));
                Assert.Throws<ArgumentOutOfRangeException>(() => Create(ocean: false, sea: bad));
            }
            foreach (float bad in new[] { -1f, float.NaN, float.PositiveInfinity })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => Create(overlap: bad));
                Assert.Throws<ArgumentOutOfRangeException>(() => Create(ocean: false, overlap: bad));
            }
        }

        // A3: with erosion the sea level is its base level, ocean or not, because the erosion's
        // outlets are the sea. Compared as the float the base level is set as.
        [Test]
        public void SeaLevelMustBeTheErosionBaseLevel()
        {
            Assert.DoesNotThrow(() => Create(sea: 68.0, erosion: true, baseLevel: 68.0));
            Assert.Throws<ArgumentException>(() => Create(sea: 68.0, erosion: true, baseLevel: 0.0));
            Assert.Throws<ArgumentException>(() => Create(ocean: false, sea: 68.0, erosion: true, baseLevel: 0.0),
                "the check holds with the ocean off");
            Assert.Throws<ArgumentException>(() => OceanParams.CheckSeaLevel(0.0, true, 68.0));
            Assert.DoesNotThrow(() => Create(sea: 68.0, erosion: false, baseLevel: 0.0),
                "without erosion there is no base level to agree with");
            Assert.DoesNotThrow(() => Create(sea: 0.1, erosion: true, baseLevel: 0.1f),
                "a sea level of 0.1 is the base level 0.1f");
        }

        // C4: a Coast chunk holds two grids, so with an ocean the 16-bit limit falls from R 253 to
        // R 179. HeightParams cannot see the ocean, so OceanParams refuses it.
        [Test]
        public void OceanCapsTheResolution()
        {
            Assert.LessOrEqual(2 * ChunkMeshLayout.VertexCount(179), 65536, "two R = 179 grids fit 16-bit indices");
            Assert.Greater(2 * ChunkMeshLayout.VertexCount(181), 65536,     "two R = 181 grids do not");
            Assert.AreEqual(179, ChunkMeshLayout.MaxResolutionWithOcean);

            Assert.DoesNotThrow(() => Create(resolution: ChunkMeshLayout.MaxResolutionWithOcean));
            Assert.Throws<ArgumentOutOfRangeException>(() => Create(resolution: 181));
            Assert.DoesNotThrow(() => Create(ocean: false, resolution: 181),
                "without an ocean the terrain's own limit, 253, is HeightParams' to enforce");
        }
    }
}
