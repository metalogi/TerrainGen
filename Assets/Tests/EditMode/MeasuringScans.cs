using System;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The measuring tools' Editor half (Measuring): Burst versions of their slowest scans, and the
    // progress bar. Installed when the Editor loads the test assembly, so a tool run from the menu
    // item or the Test Runner window uses them; out of the Editor this file is not compiled, and the
    // managed references run.
    //
    // Under the Editor's Mono the managed eroded build took about a minute a scene, and the combined
    // relief scan, 20,000 nodes a depth through the full height function, longest of all
    // (Docs/Continents_Plan.md, the designer amendment, has the timings).
    [InitializeOnLoad]
    internal sealed class MeasuringScans : Measuring.IFastScans
    {
        const int Resolution = 33;
        // HeightSampleJobs in flight at once, each with its own buffers, as in WorkingSetTests.
        const int Batch = 64;

        static MeasuringScans()
        {
            Measuring.Fast   = new MeasuringScans();
            Measuring.Report = (title, info, fraction) => EditorUtility.DisplayCancelableProgressBar(title, info, fraction);
            Measuring.Finish = EditorUtility.ClearProgressBar;
        }

        // MacroMaps.Build, the runtime's Burst build, which ErodedMapsMatchTheManagedBuild holds to
        // the managed builder to the bit.
        public MacroTests.SceneErosion ErodedBuild(MacroTests.SceneMacro m, in HeightParams hp)
        {
            var c  = m.ErodedConfig;
            var mp = MacroParams.Create(m.Surface, hp, c);
            var ep = ErosionParams.Create(m.Surface, hp, mp, c);
            using var maps = MacroMaps.Build(m.Surface, SurfaceMath.BuildRoots(m.Surface), mp,
                                             ContinentParams.Create(m.Surface, mp, c), hp, ep);
            return new MacroTests.SceneErosion
            {
                Height = hp, Macro = mp, Texels = maps.Texels.ToArray(), Drainage = maps.Drainage.ToArray(), Stats = maps.Erosion,
            };
        }

        public double[] MacroRelief(MacroTests.SceneMacro m, MacroTests.SceneErosion built, int depth, int count, uint seed)
        {
            using var texels   = new NativeArray<float4>(built.Texels, Allocator.Persistent);
            using var drainage = new NativeArray<float2>(built.Drainage, Allocator.Persistent);
            using var values   = new NativeArray<double>(count, Allocator.Persistent);
            var mp = built.Macro;
            new MacroReliefJob
            {
                Macro = mp, Depth = depth, QuadCount = m.Surface.QuadCount, Level = mp.Layout.MacroLevel(depth), Seed = seed,
                S0 = built.Height.S0, Scale = mp.ContinentBound + mp.BaseHeightScale,
                Texels = texels, Drainage = drainage, Values = values,
            }.Schedule(count, 16).Complete();
            Measuring.Tick(1f);
            return values.ToArray();
        }

        // ReliefMeasurements.MacroReliefNodes, node for node: the base height alone, read at the node's
        // macro level at every vertex. No trigonometry, so the same bits as the managed scan.
        [BurstCompile(CompileSynchronously = true)]
        struct MacroReliefJob : IJobParallelFor
        {
            public MacroParams Macro;
            public int         Depth, QuadCount, Level;
            public uint        Seed;
            public double      S0, Scale;
            [ReadOnly]  public NativeArray<float4> Texels;
            [ReadOnly]  public NativeArray<float2> Drainage;
            [WriteOnly] public NativeArray<double> Values;

            public void Execute(int k)
            {
                var    n      = ReliefMeasurements.DrawNode(QuadCount, Depth, Seed, k);
                var    reader = new MacroMapReader(Texels, Drainage);
                double lo = double.MaxValue, hi = double.MinValue;
                for (int j = 0; j < Resolution; j++)
                for (int i = 0; i < Resolution; i++)
                {
                    ChunkGrid.VertexUV(n, i, j, Resolution, out double u, out double v);
                    double x = MacroSampler.Read(reader, Macro, n.Quad, Level, u, v).BaseHeight;
                    lo = math.min(lo, x); hi = math.max(hi, x);
                }
                Values[k] = 0.5 * (hi - lo) / (LodMath.NominalSize(S0, n.Depth) * Scale / S0);
            }
        }

        // ReliefMeasurements.CombinedReliefNodes through the real HeightSampleJob, as WorkingSetTests
        // measures SampleScene's relief: min and max over the R x R main grid, as ChunkMeshJob takes
        // them. The job's surface points use Burst's trigonometry, so a height can differ from the
        // managed one in its last bit (MeasuringScanTests bounds it).
        public double[] CombinedRelief(MacroTests.SceneMacro m, MacroTests.SceneErosion built, in HeightParams hp,
                                       int depth, int count, uint seed)
        {
            int    g      = Resolution + 2;
            var    roots  = SurfaceMath.BuildRoots(m.Surface);
            double size   = LodMath.NominalSize(hp.S0, depth);
            var    values = new double[count];
            var    nodes  = new NodeId[Batch];
            var    handles = new JobHandle[Batch];
            var    points  = new NativeArray<double3>[Batch];
            var    normals = new NativeArray<float3>[Batch];
            var    heights = new NativeArray<float>[Batch];
            var    splats  = new NativeArray<uint>[Batch];
            var    texels   = new NativeArray<float4>(built.Texels, Allocator.Persistent);
            var    drainage = new NativeArray<float2>(built.Drainage, Allocator.Persistent);
            try
            {
                for (int b = 0; b < Batch; b++)
                {
                    points[b]  = new NativeArray<double3>(g * g, Allocator.Persistent);
                    normals[b] = new NativeArray<float3>(g * g, Allocator.Persistent);
                    heights[b] = new NativeArray<float>(g * g, Allocator.Persistent);
                    splats[b]  = new NativeArray<uint>(g * g, Allocator.Persistent);
                }
                int maxOctave = hp.MaxOctave(depth);
                for (int start = 0; start < count; start += Batch)
                {
                    int n = math.min(Batch, count - start);
                    for (int b = 0; b < n; b++)
                    {
                        var node = nodes[b] = ReliefMeasurements.DrawNode(roots.Length, depth, seed, start + b);
                        handles[b] = new HeightSampleJob
                        {
                            Surface = m.Surface, Root = roots[node.Quad], Node = node, Params = hp, MaxOctave = maxOctave,
                            Resolution = Resolution, Macro = built.Macro, MacroTexels = texels, MacroDrainage = drainage,
                            BasePoints = points[b], BaseNormals = normals[b], Heights = heights[b], Splats = splats[b],
                        }.Schedule(g * g, 64);
                    }
                    JobHandle.ScheduleBatchedJobs();
                    for (int b = 0; b < n; b++)
                    {
                        handles[b].Complete();
                        var   h  = heights[b];
                        float lo = float.MaxValue, hi = float.MinValue;
                        for (int j = 0; j < Resolution; j++)
                        for (int i = 0; i < Resolution; i++)
                        {
                            float x = h[(j + 1) * g + (i + 1)];
                            lo = math.min(lo, x); hi = math.max(hi, x);
                        }
                        values[start + b] = 0.5 * (double)(hi - lo) / size;
                    }
                    if ((start / Batch) % 16 == 15) Measuring.Tick((float)(start + n) / count);
                }
            }
            finally
            {
                for (int b = 0; b < Batch; b++)
                {
                    handles[b].Complete();
                    if (points[b].IsCreated)  points[b].Dispose();
                    if (normals[b].IsCreated) normals[b].Dispose();
                    if (heights[b].IsCreated) heights[b].Dispose();
                    if (splats[b].IsCreated)  splats[b].Dispose();
                }
                texels.Dispose();
                drainage.Dispose();
            }
            Measuring.Tick(1f);
            return values;
        }
    }

    // The Burst scans against their managed references, node for node, on one eroding scene's real
    // maps at a few depths: C_m to the bit, and the combined relief to within the ULPs Burst's
    // trigonometry can move a float height by. Editor-only, with MeasuringScans.
    public class MeasuringScanTests
    {
        [Test]
        public void BurstScansMatchTheManagedScans()
        {
            var m     = MacroTests.For("NarrowRingStress");
            var rough = MacroTests.RoughParams(m.Scene);
            var hp    = MacroTests.SceneParams(m, rough);
            var built = new MeasuringScans().ErodedBuild(m, hp);
            var w     = new StressSceneTests.SceneHeights(m.Scene, m.Surface, rough, built.Texels, built.Drainage, built.Macro);
            // A float height's ULP at the envelope, twice over, in units of the node's half-relief.
            double envelope = TerrainHeightFunction.MaxAbsHeight(hp, built.Macro, built.Stats.MaxAbsBaseHeight);
            double ulp      = math.pow(2.0, math.floor(math.log2(envelope)) - 23.0);
            var    fast     = (Measuring.IFastScans)new MeasuringScans();
            const int count = 48;
            int combinedExact = 0, combinedNodes = 0;
            foreach (int d in new[] { 0, 3, 8, m.MaxDepth })
            {
                double[] macroManaged = ReliefMeasurements.MacroReliefNodes(m, built, d, count, ReliefMeasurements.MacroReliefSeed(d));
                double[] macroBurst   = fast.MacroRelief(m, built, d, count, ReliefMeasurements.MacroReliefSeed(d));
                for (int k = 0; k < count; k++)
                    Assert.AreEqual(macroManaged[k], macroBurst[k], 0.0, $"depth {d}, node {k}: C_m differs between Burst and managed");

                double[] managed = ReliefMeasurements.CombinedReliefNodes(m, w, d, count, ReliefMeasurements.CombinedReliefSeed(d));
                double[] burst   = fast.CombinedRelief(m, built, hp, d, count, ReliefMeasurements.CombinedReliefSeed(d));
                double   allowed = 0.5 * 2.0 * ulp / LodMath.NominalSize(hp.S0, d);
                for (int k = 0; k < count; k++)
                {
                    Assert.AreEqual(managed[k], burst[k], allowed, $"depth {d}, node {k}: the combined relief differs by more than two ULPs");
                    if (managed[k] == burst[k]) combinedExact++;
                    combinedNodes++;
                }
                Assert.Greater(Max(managed), 0.0, $"depth {d}: no relief at all, so the comparison proves nothing");
            }
            TestContext.WriteLine($"combined relief: {combinedExact} of {combinedNodes} nodes to the bit");
        }

        static double Max(double[] values)
        {
            double worst = 0.0;
            foreach (double x in values) worst = math.max(worst, x);
            return worst;
        }
    }
}
