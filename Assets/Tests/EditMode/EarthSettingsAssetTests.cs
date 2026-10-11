using NUnit.Framework;
using UnityEditor;
using Sonoma.Systems.Configuration;
using static Sonoma.Tests.EarthScaleTests;

namespace Sonoma.Tests
{
    // EarthScaleTests' constants are hand copies of EarthTerrainSettings.asset -- that is what
    // lets it run out of Editor -- and every figure it asserts is a figure about them. This
    // holds the copies to the asset, so retuning the asset without re-deriving the tests fails
    // here rather than nowhere. The same guard WorkingSetTests.ConstantsMatchSampleScene keeps
    // for SampleScene, and for the same reason: without it, the M3 configuration that overran
    // could be restored and every test would stay green.
    //
    // Editor-only: it loads the asset through AssetDatabase.
    public class EarthSettingsAssetTests
    {
        const string AssetPath = "Assets/Settings/EarthTerrainSettings.asset";

        [Test]
        public void ConstantsMatchTheEarthAsset()
        {
            var s = AssetDatabase.LoadAssetAtPath<TerrainSettings>(AssetPath);
            Assert.IsNotNull(s, $"{AssetPath} is missing or is not a TerrainSettings");

            Assert.AreEqual(Resolution,    s.ChunkResolution,    "ChunkResolution");
            Assert.AreEqual(MaxDepth,      s.MaxDepth,           "MaxDepth");
            Assert.AreEqual(HeightScale,   s.HeightScale,        "HeightScale");
            Assert.AreEqual(SplitFactor,   s.SplitFactor,        "SplitFactor");
            Assert.AreEqual(Hysteresis,    s.HysteresisFactor,   "HysteresisFactor");
            Assert.AreEqual(MorphStart,    s.MorphStartFraction, "MorphStartFraction");
            Assert.AreEqual(Preload,       s.PreloadFactor,      "PreloadFactor");
            Assert.AreEqual(Budget,        s.MaxResidentChunks,  "MaxResidentChunks");
            Assert.IsTrue(s.SkirtsEnabled,                       "SkirtsEnabled");
            Assert.AreEqual(SkirtDepth,    s.SkirtDepth,         "SkirtDepth");
            Assert.AreEqual(SkirtFraction, s.SkirtFraction,      "SkirtFraction");
            Assert.AreEqual(0.0,           s.OctaveWavelength0,  "OctaveWavelength0");
            Assert.AreEqual(OctaveCount,   s.OctaveCount,        "OctaveCount");
            Assert.AreEqual(Persistence,   s.Persistence,        "Persistence");
            Assert.AreEqual(Lacunarity,    s.Lacunarity,         "Lacunarity");
            Assert.AreEqual(Seed,          s.Seed,               "Seed");
        }
    }
}
