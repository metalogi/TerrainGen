using System;
using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Surface;
using Debug = UnityEngine.Debug;

// Compares the open scene's erosion build in Burst with the managed reference, stage by stage, and
// logs where they first differ (Tools > Sonoma > Compare Erosion Builds). Each stage gets the same
// inputs both ways -- the managed result of the stage before -- so a difference reported at a stage
// started there, not upstream:
//
//   0. the generator's texels, uneroded (MacroMaps.Build against MacroGenerator.Build)
//   1. the graph (ErosionGraphJob against ErosionGraph)
//   2. T at every node (ErosionHeightJob against ErosionChannels.UnerodedHeight)
//   3. the pass (ErosionJob against ErosionPass.Run)
//   4. the drainage pyramid and the fold (ErosionPyramidJob against ErosionPyramid.Run)
//
// Each line gives how many values differ, of how many, the largest difference, and the first index
// that differs. The tests hold Burst to managed on small surfaces only; this checks a whole scene,
// which takes a few minutes, mostly the managed side under the Editor's Mono. Added when the narrow
// ring's largest base height came out one float ULP apart (amendment 12 of the M5 plan).
static class ErosionBuildCompare
{
    [MenuItem("Tools/Sonoma/Compare Erosion Builds")]
    static void Compare()
    {
        var root = UnityEngine.Object.FindAnyObjectByType<TerrainRoot>();
        if (root == null || root.Settings == null || root.Settings.Macro == null || root.Settings.Macro.Erosion == null)
        {
            Debug.LogError("[ErosionBuildCompare] The open scene has no TerrainRoot whose macro layer erodes.");
            return;
        }
        try
        {
            Run(root);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    static void Run(TerrainRoot root)
    {
        var clock = Stopwatch.StartNew();
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        MacroMapExport.SceneSetup(root, root.Settings, out SurfaceDef s, out RootQuad[] roots, out HeightParams hp, out MacroConfig config);
        var plainConfig = config;
        plainConfig.Erosion = null;
        var plain = MacroParams.Create(s, hp, plainConfig);
        var mp    = MacroParams.Create(s, hp, config);
        var ep    = ErosionParams.Create(s, hp, mp, config);

        // 0. The generator.
        Progress("the generator", 0f);
        float4[] texels = MacroGenerator.Build(s, roots, plain, ContinentParams.Create(s, plain, plainConfig), out _);
        using (var maps = MacroMaps.Build(s, roots, plain, ContinentParams.Create(s, plain, plainConfig)))
            Report(scene, "0 generator texels", texels, maps.Texels);

        // 1. The graph.
        Progress("the graph", 0.2f);
        var g = ErosionGraph.Build(s, roots, mp.Layout);
        using var ng = ErosionGraphJob.Build(s, roots, mp.Layout, Allocator.TempJob);
        Report(scene, "1 graph neighbours", g.Neighbours, ng.Neighbours);
        Report(scene, "1 graph lengths", g.Lengths, ng.Lengths);
        Report(scene, "1 graph areas", g.Areas, ng.Areas);

        // 2. T, both from the managed texels.
        Progress("T at every node", 0.35f);
        var reader = new ArrayMacroTexels(texels, new float2[texels.Length]);
        var t      = new double[g.NodeCount];
        for (int node = 0; node < g.NodeCount; node++)
        {
            int tx = g.NodeTexel[node];
            ErosionGraph.Coords(g.Cells, tx, out int q, out int i, out int j);
            t[node] = ErosionChannels.UnerodedHeight(hp, mp, reader, g.Points[tx], q, i, j);
        }
        using (var nativeTexels = new NativeArray<float4>(texels, Allocator.TempJob))
        using (var nativeDrainage = new NativeArray<float2>(texels.Length, Allocator.TempJob))
        using (var heights = new NativeArray<double>(g.NodeCount, Allocator.TempJob))
        {
            new ErosionHeightJob
            {
                Height = hp, Macro = mp, Cells = ng.Cells, Texels = nativeTexels, Drainage = nativeDrainage,
                NodeTexel = ng.NodeTexel, Points = ng.Points, Heights = heights,
            }.Schedule(g.NodeCount, 256).Complete();
            Report(scene, "2 T", t, heights);
        }

        // 2a-2d. T's parts, both from the managed texels, to find which one first differs.
        Progress("the parts of T", 0.45f);
        var baseM = new double[g.NodeCount];
        var climM = new float4[g.NodeCount];
        var wM    = new float4[g.NodeCount];
        var gainM = new float4[g.NodeCount];
        var hM    = new float[g.NodeCount];
        for (int node = 0; node < g.NodeCount; node++)
        {
            int tx = g.NodeTexel[node];
            ErosionGraph.Coords(g.Cells, tx, out int q, out int i, out int j);
            ErosionChannels.UnerodedHeightParts(hp, mp, reader, g.Points[tx], q, i, j, out MacroSample m, out float4 w, out float h);
            baseM[node] = m.BaseHeight;
            climM[node] = new float4(m.Temperature, m.Moisture, m.Ruggedness, 0f);
            wM[node]    = w;
            gainM[node] = new float4(m.BandGain, 0f);
            hM[node]    = h;
        }
        using (var nativeTexels = new NativeArray<float4>(texels, Allocator.TempJob))
        using (var nativeDrainage = new NativeArray<float2>(texels.Length, Allocator.TempJob))
        using (var baseB = new NativeArray<double>(g.NodeCount, Allocator.TempJob))
        using (var climB = new NativeArray<float4>(g.NodeCount, Allocator.TempJob))
        using (var wB    = new NativeArray<float4>(g.NodeCount, Allocator.TempJob))
        using (var gainB = new NativeArray<float4>(g.NodeCount, Allocator.TempJob))
        using (var hB    = new NativeArray<float>(g.NodeCount, Allocator.TempJob))
        {
            new ErosionHeightPartsJob
            {
                Height = hp, Macro = mp, Cells = ng.Cells, Texels = nativeTexels, Drainage = nativeDrainage,
                NodeTexel = ng.NodeTexel, Points = ng.Points,
                BaseHeight = baseB, Climate = climB, Weights = wB, BandGain = gainB, Heights = hB,
            }.Schedule(g.NodeCount, 256).Complete();
            Report(scene, "2a macro read: base height", baseM, baseB);
            Report(scene, "2a macro read: climate", climM, climB);
            Report(scene, "2b biome weights", wM, wB);
            Report(scene, "2c band gains", gainM, gainB);
            Report(scene, "2d height", hM, hB);
        }

        // 3. The pass, both from the managed T.
        Progress("the pass", 0.55f);
        var r = ErosionPass.Run(ep, g, t);
        using (var heights = new NativeArray<double>(t, Allocator.TempJob))
        {
            using var routing = ErosionJob.Run(ep, ng, heights, Allocator.TempJob);
            Report(scene, "3 eroded heights", r.Heights, heights);
            Report(scene, "3 filled", r.Filled, routing.Filled);
            Report(scene, "3 drainage", r.Drainage, routing.Drainage);
            Report(scene, "3 receivers", r.Receivers, routing.Receivers);
        }

        // 4. The pyramid and the fold, both from the managed write of the managed pass.
        Progress("the pyramid", 0.8f);
        var drainage = new float2[texels.Length];
        ErosionChannels.WriteTop(mp.Layout, g, ep, t, r, drainage);
        var plan = MacroPyramidPlan.Create(s, mp.Layout);
        using (var nativeTexels = new NativeArray<float4>(texels, Allocator.TempJob))
        using (var nativeDrainage = new NativeArray<float2>(drainage, Allocator.TempJob))
        {
            var managedTexels = (float4[])texels.Clone();
            ErosionPyramid.Run(managedTexels, drainage, mp.Layout, plan);
            ErosionPyramidJob.Run(nativeTexels, nativeDrainage, mp.Layout, plan);
            Report(scene, "4 folded texels", managedTexels, nativeTexels);
            Report(scene, "4 drainage maps", drainage, nativeDrainage);
        }

        Debug.Log($"[ErosionBuildCompare] {scene}: done in {clock.Elapsed.TotalSeconds:F0} s. A stage with no difference " +
                  "says 'identical'; the first that differs is where Burst and the managed reference part.");
    }

    static void Progress(string what, float f) =>
        EditorUtility.DisplayProgressBar("Compare Erosion Builds", $"Comparing {what}", f);

    static void Report(string scene, string stage, float4[] a, NativeArray<float4> b)
    {
        int differ = 0, first = -1; double worst = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (math.all(math.asuint(a[i]) == math.asuint(b[i]))) continue;
            if (first < 0) first = i;
            differ++;
            worst = math.max(worst, math.cmax(math.abs((double4)a[i] - (double4)b[i])));
        }
        Log(scene, stage, differ, a.Length, worst, first);
    }

    static void Report(string scene, string stage, float2[] a, NativeArray<float2> b)
    {
        int differ = 0, first = -1; double worst = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (math.all(math.asuint(a[i]) == math.asuint(b[i]))) continue;
            if (first < 0) first = i;
            differ++;
            worst = math.max(worst, math.cmax(math.abs((double2)a[i] - (double2)b[i])));
        }
        Log(scene, stage, differ, a.Length, worst, first);
    }

    static void Report(string scene, string stage, double[] a, NativeArray<double> b)
    {
        int differ = 0, first = -1; double worst = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (math.asulong(a[i]) == math.asulong(b[i])) continue;
            if (first < 0) first = i;
            differ++;
            worst = math.max(worst, math.abs(a[i] - b[i]));
        }
        Log(scene, stage, differ, a.Length, worst, first);
    }

    static void Report(string scene, string stage, float[] a, NativeArray<float> b)
    {
        int differ = 0, first = -1; double worst = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (math.asuint(a[i]) == math.asuint(b[i])) continue;
            if (first < 0) first = i;
            differ++;
            worst = math.max(worst, math.abs((double)a[i] - b[i]));
        }
        Log(scene, stage, differ, a.Length, worst, first);
    }

    static void Report(string scene, string stage, int[] a, NativeArray<int> b)
    {
        int differ = 0, first = -1;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            if (first < 0) first = i;
            differ++;
        }
        Log(scene, stage, differ, a.Length, double.NaN, first);
    }

    static void Log(string scene, string stage, int differ, int count, double worst, int first)
    {
        if (differ == 0)
            Debug.Log($"[ErosionBuildCompare] {scene} {stage}: identical ({count:N0} values)");
        else
            Debug.LogWarning($"[ErosionBuildCompare] {scene} {stage}: {differ:N0} of {count:N0} differ" +
                             (double.IsNaN(worst) ? "" : $", by up to {worst:G6}") + $"; the first at index {first}");
    }
}
