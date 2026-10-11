using System.Globalization;
using NUnit.Framework;
using UnityEditor;
using Sonoma.Core.Surface;
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

        // And the stress scene runs that asset, on the sphere the constants describe. Without
        // this, EarthStress could point at SampleScene's asset or a 300 m radius and fly a
        // configuration no test had looked at.
        [Test]
        public void EarthStressRunsTheEarthAsset()
        {
            string root = SceneConfig.TerrainRoot("EarthStress");
            SceneConfig.Settings(root, out string path);
            Assert.AreEqual(AssetPath, path, "EarthStress's TerrainRoot points at another settings asset");

            var ci = CultureInfo.InvariantCulture;
            Assert.AreEqual((int)SurfaceType.CubeSphere, int.Parse(SceneConfig.Field(root, "Topology", @"(\d+)"), ci), "Topology");
            Assert.AreEqual("1", SceneConfig.Field(root, "TangentAdjust", @"(\d)"), "TangentAdjust");
            Assert.AreEqual(Radius, double.Parse(SceneConfig.Field(root, "Radius", @"(\S+)"), ci), "Radius");
        }
    }
}
