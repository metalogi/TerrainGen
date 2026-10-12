using System;
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

        // Unity builds the projection matrix in float and takes the culling frustum from it. Past
        // a far/near ratio of about 3.4e7, far + near rounds to far, the far plane's normal comes
        // out zero, and every renderer is culled: EarthStress first shipped at 0.3 m to 20,000 km
        // and showed nothing at all, with nothing in the log. Every scene camera's own planes must
        // stay well inside that -- TopologyFlyCamera.MaxClipRatio, 1e6 -- and EarthStress, which
        // flies from orbit to the ground, must hand them to the fly camera to manage.
        [Test]
        public void SceneCamerasKeepACullableFrustum()
        {
            var ci = CultureInfo.InvariantCulture;
            foreach (string scene in new[] { "SampleScene", "EarthStress" })
            {
                string cam  = SceneConfig.Camera(scene);
                double near = double.Parse(SceneConfig.Field(cam, "near clip plane", @"(\S+)"), ci);
                double far  = double.Parse(SceneConfig.Field(cam, "far clip plane",  @"(\S+)"), ci);
                Assert.LessOrEqual(far / near, 1e6,
                    $"{scene}: a far/near ratio of {far / near:G3} leaves the float culling frustum " +
                    "too imprecise, and past about 3.4e7 degenerate");
            }

            string fly = SceneConfig.Script("EarthStress", "Assets/Scripts/Tools/TopologyFlyCamera.cs");
            Assert.AreEqual("1", SceneConfig.Field(fly, "AutoClipPlanes", @"(\d)"),
                "EarthStress's fly camera must set its own clip planes; no fixed pair covers orbit and ground");
        }

        // The planes TopologyFlyCamera.AutoClipPlanes sets, swept from below the ground to beyond
        // orbit over EarthStress's sphere. Everywhere the ratio stays cullable, and the near
        // plane stays within a hundredth of the clearance (above its floor): terrain ahead can be
        // nearer than the ground below, and a tenth -- as the planes first shipped -- cut open a
        // ridge 300 m in front of a camera 3 km up.
        [Test]
        public void FlyCameraClipPlanesStayCullableAndTight()
        {
            double hMax = HeightScale / (1.0 - Persistence);
            for (double clearance = 0.05; clearance < 2.0e7; clearance *= 1.3)
            {
                TopologyFlyCamera.ClipPlanes((float)clearance, true, Radius + clearance, Radius, hMax,
                                             out float near, out float far);
                Assert.LessOrEqual(far / (double)near, TopologyFlyCamera.MaxClipRatio * 1.0001,
                    $"clearance {clearance:G3} m: far/near {far / (double)near:G3} is not cullable");
                Assert.Greater(far, near, $"clearance {clearance:G3} m");
                double ceiling = Math.Max(TopologyFlyCamera.MinNearClip,
                                          TopologyFlyCamera.NearPerClearance * clearance);
                Assert.LessOrEqual(near, ceiling * 1.0001,
                    $"clearance {clearance:G3} m: a near plane at {near:G3} m clips terrain ahead");
            }

            TopologyFlyCamera.ClipPlanes(3000f, true, Radius + 3000.0, Radius, hMax, out float n3k, out _);
            Assert.AreEqual(30f, n3k, 1e-3f, "3 km up: the near plane is 30 m out, not 300");

            // Two metres up the first cut's ground behaviour stands: 0.2 m to 200 km.
            TopologyFlyCamera.ClipPlanes(2f, true, Radius + 2.0, Radius, hMax, out float n2, out float f2);
            Assert.AreEqual(0.2f, n2, 1e-6f);
            Assert.AreEqual(200000f, f2, 1f);
        }

        // EarthStress rebases by clearance: at a fixed 1000 m its descent at up to 50 km/s would
        // rebase every frame, each a pass over every chunk.
        [Test]
        public void EarthStressScalesItsRebaseThreshold()
        {
            string origin = SceneConfig.Script("EarthStress", "Assets/Scripts/Core/CoordinateSpace/WorldOriginSystem.cs");
            var ci = CultureInfo.InvariantCulture;
            Assert.AreEqual(1000.0, double.Parse(SceneConfig.Field(origin, "RebaseThreshold", @"(\S+)"), ci), "RebaseThreshold");
            Assert.AreEqual(10.0, double.Parse(SceneConfig.Field(origin, "RebasePerClearance", @"(\S+)"), ci), "RebasePerClearance");
        }
    }
}
