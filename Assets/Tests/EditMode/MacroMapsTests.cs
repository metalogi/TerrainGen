using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Rendering;
using Sonoma.Core.Surface;
using Sonoma.Systems.Configuration;

namespace Sonoma.Tests
{
    // The macro maps as the runtime builds and owns them: the Burst build against the managed
    // reference, the teardown order the jobs depend on, and the assets that switch the layer on.
    //
    // Editor-only: it needs a real Burst backend, the job system's safety checks, GameObjects for
    // the chunk pool, and AssetDatabase.
    public class MacroMapsTests
    {
        static MacroConfig Config(int levels) => new MacroConfig
        {
            LevelCount = levels, Wavelength0 = 0.0, Persistence = 0.5f, BaseHeightScale = 3000f,
            EquatorTemperature = 30f, PoleTemperature = -30f, LapseRate = 6.5f, TemperatureNoise = 5f,
            MoistureNoise = 1.5f, RuggednessNoise = 1.5f, Seed = 7u,
        };

        static uint UlpDistance(float a, float b)
        {
            int ia = math.asint(a), ib = math.asint(b);
            if (ia < 0) ia = int.MinValue - ia;   // order the negatives below the positives
            if (ib < 0) ib = int.MinValue - ib;
            return (uint)math.abs((long)ia - ib);
        }

        // The Burst build fills the same texels as MacroGenerator.Build, to the bit.
        //
        // If this fails by an ULP or two in a few texels, the likely cause is Burst's tan inside
        // SurfacePoint against managed Math.Tan: record the bound the message reports and relax
        // the check to it. The edge rule does not depend on the two agreeing, since both quads
        // read one stored texel. Small pyramids, because the managed reference runs under Mono.
        [Test]
        public void MacroJobMatchesTheManagedBuilder()
        {
            var cases = new[]
            {
                (s: SurfaceDef.CubeSphere(6371000.0),               r: 17, levels: 3),
                (s: SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4),    r: 9,  levels: 2),
                (s: SurfaceDef.PlaneGrid(1000.0, 2, 2),             r: 9,  levels: 2),
            };
            foreach (var (s, r, levels) in cases)
            {
                var roots = SurfaceMath.BuildRoots(s);
                var hp    = HeightParams.Create(s, r, 0.0, 20, 200f, 0.5f, 2f, 42u);
                var p     = MacroParams.Create(s, hp, Config(levels));

                float4[] managed = MacroGenerator.Build(s, roots, p);
                using var maps   = MacroMaps.Build(s, roots, p);

                Assert.AreEqual(managed.Length, maps.Texels.Length, $"{s.Type}: texel count");
                int  differ = 0;
                uint worst  = 0;
                for (int t = 0; t < managed.Length; t++)
                {
                    float4 a = managed[t], b = maps.Texels[t];
                    for (int c = 0; c < 4; c++)
                        worst = math.max(worst, UlpDistance(a[c], b[c]));
                    if (math.any(a != b)) differ++;
                }
                Assert.AreEqual(0, differ,
                    $"{s.Type}: {differ} of {managed.Length} texels differ between Burst and managed, " +
                    $"by up to {worst} ULP");
            }
        }

        // TerrainRoot disposes the maps after the scheduler, whose Dispose completes the jobs that
        // read them. Torn down mid-generation in that order there is nothing to report; in the
        // other order the safety system refuses to free an array jobs are still reading -- which
        // is the throw in the Editor, and a read after free in a build.
        [Test]
        public void MacroMapsOutliveTheJobs()
        {
            var s     = SurfaceDef.CubeSphere(1000.0);
            var roots = SurfaceMath.BuildRoots(s);
            var hp    = HeightParams.Create(s, 33, 0.0, 12, 20f, 0.5f, 2f, 42u);
            var host  = new GameObject("MacroMapsTestHost");
            var pool  = new ChunkPool(host.transform, null, 33);
            var maps  = MacroMaps.Build(s, roots, MacroParams.Create(s, hp, Config(3)));
            var sched = new GenerationScheduler(s, roots, hp, pool, 1f, 0f, 16, 100f, maps);
            try
            {
                for (int q = 0; q < 6; q++)
                for (int c = 0; c < 4; c++)
                    sched.Enqueue(new NodeId(q, 0, 0, 0).Child(c), q * 4 + c);
                sched.Update();
                Assert.Greater(sched.InFlightCount, 0, "nothing in flight; the teardown is not mid-generation");

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                Assert.Throws<InvalidOperationException>(() => maps.Dispose(),
                    "the maps were freed while the sample jobs that read them were still scheduled");
#endif
                sched.Dispose();
                Assert.DoesNotThrow(() => maps.Dispose());
                Assert.IsFalse(maps.Texels.IsCreated);
            }
            finally
            {
                sched.Dispose();
                maps.Dispose();
                pool.Dispose();
                UnityEngine.Object.DestroyImmediate(host);
                ChunkMeshBuffers.DisposeCache();
            }
        }

        // MacroTests' copies of the four stress scenes' macro layers are what the assets hold,
        // and each scene's settings reference its own. SampleScene has no macro layer at all:
        // that, and HeightIsBitIdenticalWithoutAMacroLayer, is ground rule 1.
        [Test]
        public void StressScenesRunTheirMacroLayers()
        {
            foreach (var m in MacroTests.StressMacros)
            {
                string root = SceneConfig.TerrainRoot(m.Scene);
                var settings = SceneConfig.Settings(root, out string path);
                Assert.AreEqual(m.Settings, path, $"{m.Scene} runs another settings asset");
                Assert.IsNotNull(settings.Macro, $"{m.Settings} has no macro layer");
                Assert.AreEqual(m.Asset, AssetDatabase.GetAssetPath(settings.Macro), $"{m.Settings}: macro asset");

                var a = AssetDatabase.LoadAssetAtPath<MacroLayerSettings>(m.Asset);
                var c = m.Config;
                Assert.AreEqual(c.LevelCount,         a.LevelCount,         $"{m.Asset}: LevelCount");
                Assert.AreEqual(c.Wavelength0,        a.Wavelength0,        $"{m.Asset}: Wavelength0");
                Assert.AreEqual(c.Persistence,        a.Persistence,        $"{m.Asset}: Persistence");
                Assert.AreEqual(c.BaseHeightScale,    a.BaseHeightScale,    $"{m.Asset}: BaseHeightScale");
                Assert.AreEqual(c.EquatorTemperature, a.EquatorTemperature, $"{m.Asset}: EquatorTemperature");
                Assert.AreEqual(c.PoleTemperature,    a.PoleTemperature,    $"{m.Asset}: PoleTemperature");
                Assert.AreEqual(c.LapseRate,          a.LapseRate,          $"{m.Asset}: LapseRate");
                Assert.AreEqual(c.TemperatureNoise,   a.TemperatureNoise,   $"{m.Asset}: TemperatureNoise");
                Assert.AreEqual(c.MoistureNoise,      a.MoistureNoise,      $"{m.Asset}: MoistureNoise");
                Assert.AreEqual(c.RuggednessNoise,    a.RuggednessNoise,    $"{m.Asset}: RuggednessNoise");
                Assert.AreEqual(c.Seed,               a.Seed,               $"{m.Asset}: Seed");

                // The biomes (M5b): the shared table, in order, and this scene's bands and ruggedness.
                Assert.AreEqual(MacroTests.BiomeTable.Length, a.Biomes.Length, $"{m.Asset}: biome count");
                for (int b = 0; b < a.Biomes.Length; b++)
                    Assert.AreEqual(MacroTests.BiomeTable[b].Asset, AssetDatabase.GetAssetPath(a.Biomes[b]),
                        $"{m.Asset}: biome slot {b}");
                Assert.AreEqual(MacroTests.BiomeSharpness,   a.BiomeSharpness,   $"{m.Asset}: BiomeSharpness");
                Assert.AreEqual(MacroTests.TemperatureScale, a.TemperatureScale, $"{m.Asset}: TemperatureScale");
                Assert.AreEqual(MacroTests.MoistureScale,    a.MoistureScale,    $"{m.Asset}: MoistureScale");
                Assert.AreEqual(m.LowBandEdge,               a.LowBandEdge,      $"{m.Asset}: LowBandEdge");
                Assert.AreEqual(m.MidBandEdge,               a.MidBandEdge,      $"{m.Asset}: MidBandEdge");
                Assert.AreEqual(m.RuggednessMin,             a.RuggednessMin,    $"{m.Asset}: RuggednessMin");
                Assert.AreEqual(m.RuggednessMax,             a.RuggednessMax,    $"{m.Asset}: RuggednessMax");

                // And the scene draws with the biome material, keyword on (C9).
                var material = SceneMaterial(root, out string materialPath);
                Assert.AreEqual(BiomeMaterial, materialPath, $"{m.Scene}: chunk material");
                Assert.IsTrue(material.IsKeywordEnabled(BiomeKeyword), $"{materialPath}: {BiomeKeyword} is off");
            }

            var sampleRoot = SceneConfig.TerrainRoot("SampleScene");
            var sample = SceneConfig.Settings(sampleRoot, out string samplePath);
            Assert.IsNull(sample.Macro, $"{samplePath}: SampleScene must not have a macro layer");
            var sampleMaterial = SceneMaterial(sampleRoot, out string sampleMaterialPath);
            Assert.IsFalse(sampleMaterial.IsKeywordEnabled(BiomeKeyword),
                $"{sampleMaterialPath}: SampleScene's material must not compile the biome path");
        }

        const string BiomeMaterial = "Assets/Materials/TerrainBiomes.mat";
        const string BiomeKeyword  = "_SONOMA_BIOMES";

        static Material SceneMaterial(string terrainRoot, out string path)
        {
            string guid = SceneConfig.Field(terrainRoot, "ChunkMaterial",
                                            @"\{fileID: \d+, guid: ([0-9a-f]{32}), type: \d+\}");
            path = AssetDatabase.GUIDToAssetPath(guid);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            Assert.IsNotNull(material, $"the TerrainRoot's ChunkMaterial {guid} is not a material");
            return material;
        }

        // MacroTests' copy of the biome table is what the BiomeDefinition assets hold.
        [Test]
        public void BiomeAssetsMatchTheTable()
        {
            foreach (var (asset, colour, config) in MacroTests.BiomeTable)
            {
                var b = AssetDatabase.LoadAssetAtPath<BiomeDefinition>(asset);
                Assert.IsNotNull(b, $"{asset} is missing or is not a BiomeDefinition");
                Assert.AreEqual(config.Temperature, b.Temperature, $"{asset}: Temperature");
                Assert.AreEqual(config.Moisture,    b.Moisture,    $"{asset}: Moisture");
                var c = b.ToConfig();
                Assert.AreEqual(config.Gain.x, c.Gain.x, 1e-6, $"{asset}: LowAmplitude");
                Assert.AreEqual(config.Gain.y, c.Gain.y, 1e-6, $"{asset}: MidAmplitude");
                Assert.AreEqual(config.Gain.z, c.Gain.z, 1e-6, $"{asset}: HighAmplitude");
                Assert.AreEqual(colour.x, b.Color.r, 1e-6, $"{asset}: Color.r");
                Assert.AreEqual(colour.y, b.Color.g, 1e-6, $"{asset}: Color.g");
                Assert.AreEqual(colour.z, b.Color.b, 1e-6, $"{asset}: Color.b");
            }
        }
    }
}
