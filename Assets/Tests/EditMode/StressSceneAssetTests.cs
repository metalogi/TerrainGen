using System.Globalization;
using NUnit.Framework;
using UnityEditor;
using Sonoma.Core.Surface;
using Sonoma.Systems.Configuration;
using static Sonoma.Tests.StressSceneTests;

namespace Sonoma.Tests
{
    // StressSceneTests' constants are hand copies of the four stress scenes' assets and
    // TerrainRoots, which is what lets it run out of Editor. This holds the copies to the files,
    // as EarthSettingsAssetTests does for EarthStress, so retuning a scene without re-deriving its
    // tests fails here rather than nowhere.
    //
    // Editor-only: it loads the assets through AssetDatabase.
    public class StressSceneAssetTests
    {
        [Test]
        public void ConstantsMatchTheAssets()
        {
            foreach (var sc in All)
            {
                var s = AssetDatabase.LoadAssetAtPath<TerrainSettings>(sc.Asset);
                Assert.IsNotNull(s, $"{sc.Asset} is missing or is not a TerrainSettings");

                Assert.AreEqual(Resolution,       s.ChunkResolution,    $"{sc.Asset}: ChunkResolution");
                Assert.AreEqual(sc.MaxDepth,      s.MaxDepth,           $"{sc.Asset}: MaxDepth");
                Assert.AreEqual(sc.HeightScale,   s.HeightScale,        $"{sc.Asset}: HeightScale");
                Assert.AreEqual(sc.SplitFactor,   s.SplitFactor,        $"{sc.Asset}: SplitFactor");
                Assert.AreEqual(Hysteresis,       s.HysteresisFactor,   $"{sc.Asset}: HysteresisFactor");
                Assert.AreEqual(sc.MorphStart,    s.MorphStartFraction, $"{sc.Asset}: MorphStartFraction");
                Assert.AreEqual(Preload,          s.PreloadFactor,      $"{sc.Asset}: PreloadFactor");
                Assert.AreEqual(sc.LodVerticalScale, s.LodVerticalScale, $"{sc.Asset}: LodVerticalScale");
                Assert.AreEqual(sc.Budget,        s.MaxResidentChunks,  $"{sc.Asset}: MaxResidentChunks");
                Assert.IsTrue(s.SkirtsEnabled,                          $"{sc.Asset}: SkirtsEnabled");
                Assert.AreEqual(SkirtDepth,       s.SkirtDepth,         $"{sc.Asset}: SkirtDepth");
                Assert.AreEqual(sc.SkirtFraction, s.SkirtFraction,      $"{sc.Asset}: SkirtFraction");
                Assert.AreEqual(0.0,              s.OctaveWavelength0,  $"{sc.Asset}: OctaveWavelength0");
                Assert.AreEqual(sc.OctaveCount,   s.OctaveCount,        $"{sc.Asset}: OctaveCount");
                Assert.AreEqual(sc.Persistence,   s.Persistence,        $"{sc.Asset}: Persistence");
                Assert.AreEqual(Lacunarity,       s.Lacunarity,         $"{sc.Asset}: Lacunarity");
                Assert.AreEqual(Seed,             s.Seed,               $"{sc.Asset}: Seed");
                Assert.AreEqual(sc.RoughnessCrossover, s.RoughnessCrossover, $"{sc.Asset}: RoughnessCrossover");
                Assert.AreEqual(sc.FinePersistence,    s.FinePersistence,    $"{sc.Asset}: FinePersistence");
                Assert.AreEqual(sc.GullyDamping,         s.GullyDamping,         $"{sc.Asset}: GullyDamping");
                Assert.AreEqual(sc.GullyStartWavelength, s.GullyStartWavelength, $"{sc.Asset}: GullyStartWavelength");
                Assert.AreEqual(sc.WetFloorDamping,      s.WetFloorDamping,      $"{sc.Asset}: WetFloorDamping");
            }
        }

        // Each scene runs its own asset, on the surface the constants describe.
        [Test]
        public void ScenesRunTheirAssets()
        {
            var ci = CultureInfo.InvariantCulture;
            foreach (var sc in All)
            {
                string root = SceneConfig.TerrainRoot(sc.Name);
                SceneConfig.Settings(root, out string path);
                Assert.AreEqual(sc.Asset, path, $"{sc.Name}'s TerrainRoot points at another settings asset");

                var s = sc.Surface;
                Assert.AreEqual((int)s.Type, int.Parse(SceneConfig.Field(root, "Topology", @"(\d+)"), ci), $"{sc.Name}: Topology");
                Assert.AreEqual(s.Radius, double.Parse(SceneConfig.Field(root, "Radius", @"(\S+)"), ci), $"{sc.Name}: Radius");
                if (s.Type == SurfaceType.Cylinder)
                {
                    Assert.AreEqual(s.Length, double.Parse(SceneConfig.Field(root, "Length", @"(\S+)"), ci), $"{sc.Name}: Length");
                    Assert.AreEqual(s.Cols, int.Parse(SceneConfig.Field(root, "Cols", @"(\d+)"), ci), $"{sc.Name}: Cols");
                    Assert.AreEqual(s.Rows, int.Parse(SceneConfig.Field(root, "Rows", @"(\d+)"), ci), $"{sc.Name}: Rows");
                }
                else
                    Assert.AreEqual("1", SceneConfig.Field(root, "TangentAdjust", @"(\d)"), $"{sc.Name}: TangentAdjust");
            }
        }

        // Every stress scene spans more than a fixed pair of clip planes can: the planets from
        // orbit to the ground, the cylinders from the floor to the far wall overhead. So each
        // camera's own planes stay cullable and the fly camera manages them.
        [Test]
        public void SceneCamerasManageTheirClipPlanes()
        {
            var ci = CultureInfo.InvariantCulture;
            foreach (var sc in All)
            {
                string cam  = SceneConfig.Camera(sc.Name);
                double near = double.Parse(SceneConfig.Field(cam, "near clip plane", @"(\S+)"), ci);
                double far  = double.Parse(SceneConfig.Field(cam, "far clip plane",  @"(\S+)"), ci);
                Assert.LessOrEqual(far / near, TopologyFlyCamera.MaxClipRatio,
                    $"{sc.Name}: a far/near ratio of {far / near:G3} leaves the float culling frustum too imprecise");

                string fly = SceneConfig.Script(sc.Name, "Assets/Scripts/Tools/TopologyFlyCamera.cs");
                Assert.AreEqual("1", SceneConfig.Field(fly, "AutoClipPlanes", @"(\d)"), $"{sc.Name}: AutoClipPlanes");
            }
        }

        // Each rebases by clearance, as EarthStress does: the planets and the ring span megametres,
        // and at a fixed 1000 m a fast descent rebases every frame.
        [Test]
        public void ScenesScaleTheirRebaseThreshold()
        {
            var ci = CultureInfo.InvariantCulture;
            foreach (var sc in All)
            {
                string origin = SceneConfig.Script(sc.Name, "Assets/Scripts/Core/CoordinateSpace/WorldOriginSystem.cs");
                Assert.AreEqual(1000.0, double.Parse(SceneConfig.Field(origin, "RebaseThreshold", @"(\S+)"), ci), $"{sc.Name}: RebaseThreshold");
                Assert.AreEqual(10.0, double.Parse(SceneConfig.Field(origin, "RebasePerClearance", @"(\S+)"), ci), $"{sc.Name}: RebasePerClearance");
            }
        }
    }
}
