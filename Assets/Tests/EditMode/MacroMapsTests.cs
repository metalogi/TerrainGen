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

        // The whole build with continents -- Stages A to C, then the generator reading the
        // channel, then the copies -- in Burst against MacroGenerator.Build in managed code, to the
        // bit, on ContinentMapTests' sphere, ring and plane.
        [Test]
        public void MacroJobMatchesTheManagedBuilderWithContinents()
        {
            if (!Unity.Burst.BurstCompiler.Options.EnableBurstCompilation)
                Assert.Ignore("Burst compilation is disabled in this Editor; the comparison would prove nothing.");

            foreach (var c in ContinentMapTests.Cases())
            {
                float4[] managed = MacroGenerator.Build(c.Surface, c.Roots, c.Macro, c.Continents, out ContinentMap map);
                using var maps   = MacroMaps.Build(c.Surface, c.Roots, c.Macro, c.Continents);
                Assert.IsTrue(ContinentJobs.WitnessCompiled(map, c.Surface, c.Roots[0], c.Continents),
                    "ContinentWitnessJob ran as managed code, so the continent jobs are not Burst-compiled");
                Assert.IsNotNull(maps.Continents, $"{c.Name}: the maps lost their continent map");

                // The texels first, so a difference names how many, before the radius it moves.
                int  differ = 0;
                uint worst  = 0;
                for (int t = 0; t < managed.Length; t++)
                {
                    for (int ch = 0; ch < 4; ch++) worst = math.max(worst, UlpDistance(managed[t][ch], maps.Texels[t][ch]));
                    if (math.any(math.asuint(managed[t]) != math.asuint(maps.Texels[t]))) differ++;
                }
                Assert.AreEqual(0, differ, $"{c.Name}: {differ} of {managed.Length} texels differ between Burst and managed, " +
                                           $"by up to {worst} ULP");
                Assert.AreEqual(map.Stats.WarpedSaturation, maps.Continents.Stats.WarpedSaturation, $"{c.Name}: warped saturation");

                // What a build asked to keep its top texels keeps, for the export: the managed
                // scan's nearest micro seeds and the managed field's s, texel for texel.
                using var kept = MacroMaps.Build(c.Surface, c.Roots, c.Macro, c.Continents, keepTopTexels: true);
                ContinentTexel[] scan = map.Scan(ContinentMap.TopPoints(c.Surface, c.Roots, c.Layout));
                int top = c.Layout.LevelCount - 1, n = c.Layout.Cells(top), w = c.Layout.Width(top);
                var tables = new ArrayContinentTables(map);
                Assert.AreEqual(scan.Length, kept.Continents.TopMicro.Length, $"{c.Name}: kept micro seeds");
                Assert.AreEqual(scan.Length, kept.Continents.TopCoast.Length, $"{c.Name}: kept coast distances");
                for (int k = 0; k < scan.Length; k++)
                {
                    ContinentMap.ScanCoords(k, w, out int q, out int i, out int j);
                    Assert.AreEqual(scan[k].Micro, kept.Continents.TopMicro[k], $"{c.Name}: kept micro seed at ({q}, {i}, {j})");
                    Assert.AreEqual((float)ContinentField.AtTexel(c.Surface, c.Roots[q], n, i, j, tables, c.Continents).S,
                                    kept.Continents.TopCoast[k], $"{c.Name}: kept coast distance at ({q}, {i}, {j})");
                }
                Assert.IsNull(maps.Continents.TopMicro, $"{c.Name}: a build not asked to keep its texels kept them");
            }
        }

        // A build that fails in a continent stage throws its own error and leaves nothing behind
        // that stops the next build: every temporary is freed in a finally. Stage B fails here, a warp
        // of nearly two micro spacings throwing points far off the surface, past Stage A's
        // unwarped check.
        [Test]
        public void FailedContinentStageThrowsCleanly()
        {
            var good = ContinentMapTests.Cases()[0];
            var k    = good.Config.Continents.Value; k.WarpAmplitude = 1.9f;
            var bad  = ContinentMapTests.Make("sphere, warped off the surface", good.Surface, k);

            Assert.Throws<InvalidOperationException>(() => MacroMaps.Build(bad.Surface, bad.Roots, bad.Macro, bad.Continents),
                "Stage B passed a warp that leaves the plateau");
            Assert.Throws<ArgumentException>(() => MacroMaps.Build(good.Surface, good.Roots, good.Macro, ContinentParams.None),
                "a macro layer with continents built without them");
            using var maps = MacroMaps.Build(good.Surface, good.Roots, good.Macro, good.Continents);
            Assert.IsNotNull(maps.Continents, "the next build after a failure");
        }

        // Stage A with its texel scan in Burst gives the managed map's tables to the bit: the scan
        // decides the adjacency, the micro areas and so the land threshold, so one nearest seed
        // decided differently would move the coastline. The witness first, since a scan that fell
        // back to managed code would pass trivially.
        [Test]
        public void ContinentScanJobMatchesTheManagedScan()
        {
            if (!Unity.Burst.BurstCompiler.Options.EnableBurstCompilation)
                Assert.Ignore("Burst compilation is disabled in this Editor; the comparison would prove nothing.");

            foreach (var c in ContinentMapTests.Cases())
            {
                ContinentMap managed = ContinentMapTests.Build(c);
                ContinentMap burst   = ContinentJobs.BuildMap(c.Surface, c.Roots, c.Layout, c.Continents);
                Assert.IsTrue(ContinentJobs.WitnessCompiled(managed, c.Surface, c.Roots[0], c.Continents),
                    "ContinentWitnessJob ran as managed code, so the continent jobs are not Burst-compiled");

                CollectionAssert.AreEqual(managed.MicroArea,      burst.MicroArea,      $"{c.Name}: micro areas");
                CollectionAssert.AreEqual(managed.MicroLand,      burst.MicroLand,      $"{c.Name}: land");
                CollectionAssert.AreEqual(managed.MacroElevation, burst.MacroElevation, $"{c.Name}: elevations");
                CollectionAssert.AreEqual(managed.MicroParent,    burst.MicroParent,    $"{c.Name}: parents");
                for (int m = 0; m < managed.Adjacency.Length; m++)
                    CollectionAssert.AreEqual(managed.Adjacency[m], burst.Adjacency[m], $"{c.Name}: adjacency of {m}");
                Assert.AreEqual(managed.BlendRadius2,           burst.BlendRadius2,           $"{c.Name}: blend radius");
                Assert.AreEqual(managed.Stats.SaturationRadius, burst.Stats.SaturationRadius, $"{c.Name}: saturation");
                Assert.AreEqual(managed.Stats.LandFraction,     burst.Stats.LandFraction,     $"{c.Name}: land fraction");
            }
        }

        // Stage B in Burst writes the managed field's top level to the bit, borders included, and
        // finds the same worst plateau distance; and touches no other level.
        [Test]
        public void ContinentFieldJobMatchesTheManagedField()
        {
            if (!Unity.Burst.BurstCompiler.Options.EnableBurstCompilation)
                Assert.Ignore("Burst compilation is disabled in this Editor; the comparison would prove nothing.");

            foreach (var c in ContinentMapTests.Cases())
            {
                ContinentMap map = ContinentMapTests.Build(c);
                Assert.IsTrue(ContinentJobs.WitnessCompiled(map, c.Surface, c.Roots[0], c.Continents),
                    "ContinentWitnessJob ran as managed code, so the continent jobs are not Burst-compiled");

                var    managed      = new float[c.Layout.TexelCount];
                double managedWorst = ContinentField.FillTop(managed, c.Surface, c.Roots, c.Layout, c.Continents, map);
                using var burst     = new NativeArray<float>(c.Layout.TexelCount, Allocator.TempJob);
                double burstWorst   = ContinentJobs.FillTop(burst, c.Surface, c.Roots, c.Layout, c.Continents, map);

                // The texels first, so a difference names how many, before the radius it moves.
                int  differ = 0;
                uint worst  = 0;
                for (int t = 0; t < managed.Length; t++)
                {
                    worst = math.max(worst, UlpDistance(managed[t], burst[t]));
                    if (math.asuint(managed[t]) != math.asuint(burst[t])) differ++;
                }
                Assert.AreEqual(0, differ, $"{c.Name}: {differ} of {managed.Length} texels differ between Burst and managed, " +
                                           $"by up to {worst} ULP");
                Assert.AreEqual(managedWorst, burstWorst, $"{c.Name}: the worst plateau distance");
            }
        }

        // MacroPyramidJob derives the same levels as the managed MacroPyramid.Run, to the bit, in
        // every top-level mode. It calls the same static functions, so a difference here means
        // Burst reordered or fused the filter's arithmetic.
        [Test]
        public void PyramidJobMatchesTheManagedPyramid()
        {
            var cases = new[]
            {
                (s: SurfaceDef.CubeSphere(6371000.0),            cells0: 16, levels: 3),
                (s: SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4), cells0: 8,  levels: 3),
                (s: SurfaceDef.PlaneGrid(1000.0, 2, 2),          cells0: 8,  levels: 2),
            };
            foreach (var (s, cells0, levels) in cases)
            {
                var layout = new MacroLayout(cells0, levels, s.QuadCount);
                var plan   = MacroPyramidPlan.Create(s, layout);
                foreach (var mode in new[] { MacroPyramidTop.FilterAndFill, MacroPyramidTop.Fill, MacroPyramidTop.Keep })
                {
                    var managed = new float[layout.TexelCount];
                    var rng     = new Unity.Mathematics.Random(0x51ED27u);
                    for (int t = 0; t < managed.Length; t++) managed[t] = rng.NextFloat(-1000f, 1000f);

                    using var native = new NativeArray<float>(managed, Allocator.TempJob);
                    MacroPyramid.Run(managed, layout, plan, mode);
                    MacroPyramidJob.Run(native, layout, plan, mode);

                    int  differ = 0;
                    uint worst  = 0;
                    for (int t = 0; t < managed.Length; t++)
                    {
                        if (managed[t] == native[t]) continue;
                        differ++;
                        worst = math.max(worst, UlpDistance(managed[t], native[t]));
                    }
                    Assert.AreEqual(0, differ,
                        $"{s.Type}, {mode}: {differ} of {managed.Length} texels differ between " +
                        $"Burst and managed, by up to {worst} ULP");
                }
            }
        }

        // The erosion graph (M5c-1, E2): ErosionGraphJob runs the same Fill as the managed reference,
        // on the same managed numbering and points, so every array is equal to the bit.
        [Test]
        public void ErosionGraphJobMatchesTheManagedGraph()
        {
            foreach (var c in ErosionGraphTests.Cases())
            {
                var managed = c.Graph;
                using var native = ErosionGraphJob.Build(c.Surface, c.Roots, c.Layout, Allocator.TempJob);

                Assert.AreEqual(managed.NodeCount, native.NodeCount, c.Name);
                CollectionAssert.AreEqual(managed.NodeOf,     native.NodeOf.ToArray(),     c.Name + ": nodes");
                CollectionAssert.AreEqual(managed.NodeTexel,  native.NodeTexel.ToArray(),  c.Name + ": owners");
                CollectionAssert.AreEqual(managed.Neighbours, native.Neighbours.ToArray(), c.Name + ": neighbours");
                CollectionAssert.AreEqual(managed.Lengths,    native.Lengths.ToArray(),    c.Name + ": lengths");
                CollectionAssert.AreEqual(managed.Areas,      native.Areas.ToArray(),      c.Name + ": areas");
            }
        }

        // The erosion pass (M5c-1, E3): ErosionJob runs the same ErosionPass.Run as the managed
        // reference, A^m included through ErosionParams.AreaPower's square roots, so the eroded
        // heights and the routing are equal to the bit, on pitted ground with incision and diffusion.
        [Test]
        public void ErosionJobMatchesTheManagedPass()
        {
            foreach (var c in ErosionGraphTests.Cases())
            {
                var    h = ErosionPassTests.PittedHeights(c);
                double u = ErosionPass.DiffusionNumber(1.0, c.Graph.NodeCount, new ErosionGraph.Floats(c.Graph.Lengths));
                var    p = ErosionPassTests.Params(iterations: 6, kdt: 0.05, kappa: 0.5 / u, mBits: 461);
                var managed = ErosionPass.Run(p, c.Graph, h);

                using var graph   = ErosionGraphJob.Build(c.Surface, c.Roots, c.Layout, Allocator.TempJob);
                using var heights = new NativeArray<double>(h, Allocator.TempJob);
                using var routing = ErosionJob.Run(p, graph, heights, Allocator.TempJob);

                CollectionAssert.AreEqual(managed.Heights,   heights.ToArray(),           c.Name + ": heights");
                CollectionAssert.AreEqual(managed.Filled,    routing.Filled.ToArray(),    c.Name + ": filled");
                CollectionAssert.AreEqual(managed.Drainage,  routing.Drainage.ToArray(),  c.Name + ": drainage");
                CollectionAssert.AreEqual(managed.Receivers, routing.Receivers.ToArray(), c.Name + ": receivers");
                CollectionAssert.AreEqual(managed.Stack,     routing.Stack.ToArray(),     c.Name + ": stack");
            }
        }

        // E5: each eroding scene's pinned largest base height (MacroTests.SceneMacro.ErodedMaxAbsBaseHeight),
        // which the pure tests' height envelopes are built from, is the one the scene's Burst build
        // measures, to the bit. A retune of the scene's erosion fails here until it is re-pinned.
        [Test]
        public void ErodedEnvelopesMatchTheBuild()
        {
            int scenes = 0;
            foreach (var m in MacroTests.StressMacros)
            {
                if (!m.Erosion.HasValue) continue;
                scenes++;
                var hp = MacroTests.SceneParams(m, MacroTests.RoughParams(m.Scene));
                var c  = m.ErodedConfig;
                var p  = MacroParams.Create(m.Surface, hp, c);
                var ep = ErosionParams.Create(m.Surface, hp, p, c);
                using var maps = MacroMaps.Build(m.Surface, SurfaceMath.BuildRoots(m.Surface), p,
                                                 ContinentParams.Create(m.Surface, p, c), hp, ep);
                Assert.AreEqual(m.ErodedMaxAbsBaseHeight, maps.Erosion.MaxAbsBaseHeight,
                    $"{m.Scene}: the pinned largest base height is not the build's; re-run ReliefMeasurements.ErodedMaps");
                Assert.AreEqual(MacroTests.MaxAbsHeight(m.Scene, MacroTests.RoughParams(m.Scene)), maps.MaxAbsHeight(hp),
                    $"{m.Scene}: the tests' height envelope is not the runtime's");
            }
            Assert.Greater(scenes, 0, "no stress scene erodes");
        }

        // Every eroding scene's skirts cover its transient gaps on the eroded heights, as
        // StressSceneTests.SkirtCoversTheTransientGap checks the uneroded: the same edges and the same
        // check, over the maps the Editor's Burst build makes in seconds. The pure tests cannot afford
        // eroded maps, a minute a scene under the Editor's Mono.
        [Test]
        public void SkirtsCoverTheErodedTransientGap()
        {
            int scenes = 0;
            foreach (var m in MacroTests.StressMacros)
            {
                if (!m.Erosion.HasValue) continue;
                scenes++;
                var rough = MacroTests.RoughParams(m.Scene);
                var hp    = MacroTests.SceneParams(m, rough);
                var c     = m.ErodedConfig;
                var p     = MacroParams.Create(m.Surface, hp, c);
                var ep    = ErosionParams.Create(m.Surface, hp, p, c);
                float4[] texels;
                using (var maps = MacroMaps.Build(m.Surface, SurfaceMath.BuildRoots(m.Surface), p,
                                                  ContinentParams.Create(m.Surface, p, c), hp, ep))
                    texels = maps.Texels.ToArray();

                int   maxDepth;
                float fraction;
                if (m.Scene == "EarthStress") { maxDepth = EarthScaleTests.MaxDepth; fraction = EarthScaleTests.SkirtFraction; }
                else
                {
                    var sc = Array.Find(StressSceneTests.All, x => x.Name == m.Scene);
                    maxDepth = sc.MaxDepth; fraction = sc.SkirtFraction;
                }
                StressSceneTests.AssertSkirtsCover($"{m.Scene} eroded",
                    new StressSceneTests.SceneHeights(m.Scene, m.Surface, rough, texels, p), maxDepth, fraction);
            }
            Assert.Greater(scenes, 0, "no stress scene erodes");
        }

        // The whole erosion build (M5c-1, task 6): MacroMaps.Build's Burst graph, heights, pass and
        // pyramid against the managed builder's, with and without continents, to the bit.
        [Test]
        public void ErodedMapsMatchTheManagedBuild()
        {
            foreach (var c in ErosionBuildTests.Cases(ErosionBuildTests.Incising(diffusion: 0.1f)))
            {
                var managed = ErosionBuildTests.Build(c, out float2[] drainage, out _, out ErosionStats stats);
                using var maps = MacroMaps.Build(c.Surface, c.Roots, c.Macro, c.Continents, c.Height, c.Erosion);

                Assert.AreEqual(managed.Length,  maps.Texels.Length,   c.Name);
                Assert.AreEqual(drainage.Length, maps.Drainage.Length, c.Name);
                int differ = 0;
                for (int t = 0; t < managed.Length; t++)
                {
                    if (math.any(math.asuint(managed[t])  != math.asuint(maps.Texels[t])))   differ++;
                    if (math.any(math.asuint(drainage[t]) != math.asuint(maps.Drainage[t]))) differ++;
                }
                Assert.AreEqual(0, differ, $"{c.Name}: {differ} texels differ between Burst and managed");
                Assert.AreEqual(stats.NodeCount,        maps.Erosion.NodeCount,        c.Name);
                Assert.AreEqual(stats.MaxCut,           maps.Erosion.MaxCut,           c.Name);
                Assert.AreEqual(stats.MaxAbsBaseHeight, maps.Erosion.MaxAbsBaseHeight, c.Name);
                Assert.AreEqual(TerrainHeightFunction.MaxAbsHeight(c.Height, c.Macro, stats.MaxAbsBaseHeight),
                                maps.MaxAbsHeight(c.Height), c.Name);
            }
        }

        // The drainage pyramid and the fold (M5c-1, E4): ErosionPyramidJob runs ErosionPyramid.Run,
        // the managed reference's own functions, so both arrays are equal to the bit.
        [Test]
        public void ErosionPyramidJobMatchesTheManagedPyramid()
        {
            foreach (var c in ErosionPyramidTests.Cases())
            {
                var texels   = (float4[])c.Texels.Clone();
                var drainage = (float2[])c.Drainage.Clone();
                ErosionPyramid.Run(texels, drainage, c.Layout, c.Plan);

                using var nativeTexels   = new NativeArray<float4>(c.Texels,   Allocator.TempJob);
                using var nativeDrainage = new NativeArray<float2>(c.Drainage, Allocator.TempJob);
                ErosionPyramidJob.Run(nativeTexels, nativeDrainage, c.Layout, c.Plan);

                for (int t = 0; t < texels.Length; t++)
                {
                    Assert.AreEqual(math.asuint(texels[t]),   math.asuint(nativeTexels[t]),   $"{c.Name}: texel {t}");
                    Assert.AreEqual(math.asuint(drainage[t]), math.asuint(nativeDrainage[t]), $"{c.Name}: drainage {t}");
                }
            }
        }

        // The job's overload refuses a channel or a plan that does not fit the layout before it
        // schedules anything: in a build the job would otherwise read and write outside the array.
        [Test]
        public void PyramidJobRefusesAMismatchedChannelOrPlan()
        {
            var s      = SurfaceDef.CubeSphere(6371000.0);
            var layout = new MacroLayout(8, 3, s.QuadCount);
            var plan   = MacroPyramidPlan.Create(s, layout);
            var other  = MacroPyramidPlan.Create(s, new MacroLayout(8, 2, s.QuadCount));

            using (var shortChannel = new NativeArray<float>(layout.TexelCount - 1, Allocator.TempJob))
                Assert.Throws<ArgumentException>(() => MacroPyramidJob.Run(shortChannel, layout, plan, MacroPyramidTop.Fill));
            using (var channel = new NativeArray<float>(layout.TexelCount, Allocator.TempJob))
            {
                Assert.Throws<ArgumentException>(() => MacroPyramidJob.Run(channel, layout, other, MacroPyramidTop.Fill));
                Assert.DoesNotThrow(() => MacroPyramidJob.Run(channel, layout, plan, MacroPyramidTop.Fill));
            }
            Assert.Throws<ArgumentException>(() => MacroPyramidJob.Run(default, layout, plan, MacroPyramidTop.Fill));
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
                Assert.AreEqual(m.RuggednessCentre,          a.RuggednessCentre, $"{m.Asset}: RuggednessCentre");

                // The continents (Docs/Continents_Plan.md): the asset the scene's copy names, field
                // for field, or none.
                if (m.ContinentAsset == null)
                    Assert.IsNull(a.Continents, $"{m.Asset}: continents the copy does not have");
                else
                {
                    Assert.AreEqual(m.ContinentAsset, AssetDatabase.GetAssetPath(a.Continents), $"{m.Asset}: Continents");
                    ContinentConfig k = a.Continents.ToConfig(), want = m.Continents.Value;
                    Assert.AreEqual(want.Seed,                  k.Seed,                  $"{m.ContinentAsset}: Seed");
                    Assert.AreEqual(want.MacroCells,            k.MacroCells,            $"{m.ContinentAsset}: MacroCells");
                    Assert.AreEqual(want.MicroCells,            k.MicroCells,            $"{m.ContinentAsset}: MicroCells");
                    Assert.AreEqual(want.ContinentCount,        k.ContinentCount,        $"{m.ContinentAsset}: ContinentCount");
                    Assert.AreEqual(want.DecayMin,              k.DecayMin,              $"{m.ContinentAsset}: DecayMin");
                    Assert.AreEqual(want.DecayMax,              k.DecayMax,              $"{m.ContinentAsset}: DecayMax");
                    Assert.AreEqual(want.ElevationFloor,        k.ElevationFloor,        $"{m.ContinentAsset}: ElevationFloor");
                    Assert.AreEqual(want.ScoreNoise,            k.ScoreNoise,            $"{m.ContinentAsset}: ScoreNoise");
                    Assert.AreEqual(want.LandFraction,          k.LandFraction,          $"{m.ContinentAsset}: LandFraction");
                    Assert.AreEqual(want.WarpAmplitude,         k.WarpAmplitude,         $"{m.ContinentAsset}: WarpAmplitude");
                    Assert.AreEqual(want.WarpWavelength,        k.WarpWavelength,        $"{m.ContinentAsset}: WarpWavelength");
                    Assert.AreEqual(want.CoastWidth,            k.CoastWidth,            $"{m.ContinentAsset}: CoastWidth");
                    Assert.AreEqual(want.LandHeight,            k.LandHeight,            $"{m.ContinentAsset}: LandHeight");
                    Assert.AreEqual(want.OceanDepth,            k.OceanDepth,            $"{m.ContinentAsset}: OceanDepth");
                    Assert.AreEqual(want.InteriorRise,          k.InteriorRise,          $"{m.ContinentAsset}: InteriorRise");
                    Assert.AreEqual(want.DetailCutoff,          k.DetailCutoff,          $"{m.ContinentAsset}: DetailCutoff");
                    Assert.AreEqual(want.CoarseOctaveAmplitude, k.CoarseOctaveAmplitude, $"{m.ContinentAsset}: CoarseOctaveAmplitude");
                }

                // Erosion (M5c-1): the asset the scene's copy names, field for field, or none.
                if (m.ErosionAsset == null)
                    Assert.IsNull(a.Erosion, $"{m.Asset}: erosion the copy does not have");
                else
                {
                    Assert.AreEqual(m.ErosionAsset, AssetDatabase.GetAssetPath(a.Erosion), $"{m.Asset}: Erosion");
                    ErosionConfig e = a.Erosion.ToConfig(), want = m.Erosion.Value;
                    Assert.AreEqual(want.Iterations,    e.Iterations,    $"{m.ErosionAsset}: Iterations");
                    Assert.AreEqual(want.IncisionDepth, e.IncisionDepth, $"{m.ErosionAsset}: IncisionDepth");
                    Assert.AreEqual(want.AreaExponent,  e.AreaExponent,  $"{m.ErosionAsset}: AreaExponent");
                    Assert.AreEqual(want.Diffusion,     e.Diffusion,     $"{m.ErosionAsset}: Diffusion");
                    Assert.AreEqual(want.BaseLevel,     e.BaseLevel,     $"{m.ErosionAsset}: BaseLevel");
                    Assert.AreEqual(want.MinOutletArea, e.MinOutletArea, $"{m.ErosionAsset}: MinOutletArea");
                    Assert.AreEqual(want.BreachDepth,   e.BreachDepth,   $"{m.ErosionAsset}: BreachDepth");
                }

                // And the scene draws with the biome material, keyword on (C9), and with its sky:
                // the sky keyword on and haze configured (M5b).
                var material = SceneMaterial(root, out string materialPath);
                Assert.AreEqual(BiomeMaterial, materialPath, $"{m.Scene}: chunk material");
                Assert.IsTrue(material.IsKeywordEnabled(BiomeKeyword), $"{materialPath}: {BiomeKeyword} is off");
                Assert.IsTrue(material.IsKeywordEnabled(SkyKeyword), $"{materialPath}: {SkyKeyword} is off");
                Assert.Greater(settings.HazeVisibility, 0f, $"{m.Settings}: no haze");
            }

            var sampleRoot = SceneConfig.TerrainRoot("SampleScene");
            var sample = SceneConfig.Settings(sampleRoot, out string samplePath);
            Assert.IsNull(sample.Macro, $"{samplePath}: SampleScene must not have a macro layer");
            var sampleMaterial = SceneMaterial(sampleRoot, out string sampleMaterialPath);
            Assert.IsFalse(sampleMaterial.IsKeywordEnabled(BiomeKeyword),
                $"{sampleMaterialPath}: SampleScene's material must not compile the biome path");
            Assert.IsFalse(sampleMaterial.IsKeywordEnabled(SkyKeyword),
                $"{sampleMaterialPath}: SampleScene's material must not compile the sky path");
        }

        const string BiomeMaterial = "Assets/Materials/TerrainBiomes.mat";
        const string BiomeKeyword  = "_SONOMA_BIOMES";
        const string SkyKeyword    = "_SONOMA_SKY";

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
