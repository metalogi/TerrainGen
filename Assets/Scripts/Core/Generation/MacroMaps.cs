using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // MacroSampler's view of the maps from inside a job or on the main thread.
    public readonly struct MacroMapReader : IMacroTexels
    {
        [ReadOnly] readonly NativeArray<float4> _texels;
        [ReadOnly] readonly NativeArray<float2> _drainage;

        public MacroMapReader(NativeArray<float4> texels, NativeArray<float2> drainage)
        {
            _texels   = texels;
            _drainage = drainage;
        }

        public float4 Texel(int index)    => _texels[index];
        public float2 Drainage(int index) => _drainage[index];
    }

    // Fills every texel of every level of every quad from MacroGenerator. One texel per index,
    // independent of every other, so it is a plain parallel-for; the edge copies run after it.
    // Continent is the derived continent channel, one value per texel, or a single element when
    // there are no continents, which is then never read.
    [BurstCompile(CompileSynchronously = true)]
    public struct MacroBuildJob : IJobParallelFor
    {
        public SurfaceDef  Surface;
        public MacroParams Params;

        [ReadOnly]  public NativeArray<RootQuad> Roots;
        [ReadOnly]  public NativeArray<float>    Continent;
        [WriteOnly] public NativeArray<float4>   Texels;

        public void Execute(int index)
        {
            Params.Layout.Decode(index, out int q, out int level, out int i, out int j);
            float continent = Params.HasContinents ? Continent[index] : 0f;
            Texels[index] = MacroGenerator.Texel(Surface, Roots[q], Params, level, i, j, continent);
        }
    }

    // The persistent macro maps: one flat array holding every root quad's pyramid
    // (MacroLayout), built once when the terrain starts and read by every sample job after.
    //
    // Built on workers and waited for, because nothing can generate before it exists: a startup
    // hitch, measured into BuildMilliseconds for TerrainRoot to report, and nothing per frame.
    // Many sample jobs read the array at once, which the safety system allows for [ReadOnly]; it
    // must be disposed only after every job that reads it has completed, so TerrainRoot disposes
    // it after GenerationScheduler.Dispose, which completes them. The other order throws in the
    // Editor and reads freed memory in a build.
    public sealed class MacroMaps : IDisposable
    {
        NativeArray<float4> _texels;
        NativeArray<float2> _drainage;

        public MacroParams         Params            { get; }
        public NativeArray<float4> Texels            => _texels;
        // The drainage maps (M5c, E4): wetness and the erosion correction D, with the texels'
        // indexing. One element without erosion, never read then, because a job's NativeArray
        // must be constructed to be scheduled at all.
        public NativeArray<float2> Drainage          => _drainage;
        public double              BuildMilliseconds { get; }
        public long                Bytes             => (long)_texels.Length * 16 + (long)_drainage.Length * 8;

        // The continent map the base height was built from, with its stats, or null without
        // continents. Managed and small: a few thousand seeds and a bucket grid.
        public ContinentMap        Continents        { get; }

        // What the erosion build measured, or null without erosion (M5c-1).
        public ErosionStats        Erosion           { get; }

        MacroMaps(NativeArray<float4> texels, NativeArray<float2> drainage, in MacroParams p,
                  double buildMilliseconds, ContinentMap continents, ErosionStats erosion)
        {
            _texels           = texels;
            _drainage         = drainage;
            Params            = p;
            BuildMilliseconds = buildMilliseconds;
            Continents        = continents;
            Erosion           = erosion;
        }

        // A bound on |Height| over these maps (E5): the analytic one without erosion, to the bit,
        // and with it the largest |BaseHeight'| the build stored in place of the base height's
        // analytic term, since erosion writes the channel and no formula bounds it any more.
        public double MaxAbsHeight(in HeightParams hp)
            => TerrainHeightFunction.MaxAbsHeight(hp, Params, Erosion != null ? Erosion.MaxAbsBaseHeight : 0.0);

        public static MacroMaps Build(in SurfaceDef s, RootQuad[] roots, in MacroParams p)
            => Build(s, roots, p, ContinentParams.None);

        // Without erosion, which the overload below refuses here: these would be the uneroded maps.
        public static MacroMaps Build(in SurfaceDef s, RootQuad[] roots, in MacroParams p, in ContinentParams cp,
                                      bool keepTopTexels = false)
            => Build(s, roots, p, cp, default, ErosionParams.None, keepTopTexels);

        // With continents, K1's sequence (Docs/Continents_Plan.md): the continent map with its
        // texel scan in Burst (Stage A), the field at the top level (B), the pyramid filtered down
        // (C), then the generator reading the channel, then the edge copies. The continent channel
        // is temporary, freed before this returns. keepTopTexels keeps, on the continent map, each
        // top-level texel's nearest micro seed and coast distance, for tools that draw them
        // (ContinentMap.TopMicro and TopCoast).
        //
        // With erosion, ErosionBuild's sequence after that, the graph, the heights, the pass and the
        // pyramid in Burst (Erode, below). hp is the terrain's own height parameters, whose detail
        // octaves the pass erodes with the base height (E1); it is read only with erosion.
        public static MacroMaps Build(in SurfaceDef s, RootQuad[] roots, in MacroParams p, in ContinentParams cp,
                                      in HeightParams hp, in ErosionParams ep, bool keepTopTexels = false)
        {
            MacroGenerator.CheckContinents(p, cp);
            ErosionBuild.Check(p, ep);

            var clock  = Stopwatch.StartNew();
            var texels = new NativeArray<float4>(p.Layout.TexelCount, Allocator.Persistent,
                                                 NativeArrayOptions.UninitializedMemory);
            var drainage = default(NativeArray<float2>);
            ContinentMap     map     = null;
            ErosionStats     erosion = null;
            MacroPyramidPlan plan    = null;
            // Everything that can throw is inside the try, so a failed build frees the pyramid
            // rather than leaking 8-12 MB of persistent memory that nothing owns: TerrainRoot.Start
            // aborts with no MacroMaps to dispose, and the leak would be reported at the next
            // domain reload with nothing naming the macro build.
            try
            {
                var continent = default(NativeArray<float>);
                var rootArray = default(NativeArray<RootQuad>);
                var points    = default(NativeArray<double3>);
                try
                {
                    int2[] copies;
                    if (cp.Enabled)
                    {
                        // The top level's points, in managed code, for both stages: see
                        // ContinentMap.TopPoints. Counted in Stage A's time.
                        var stage = Stopwatch.StartNew();
                        points = ContinentJobs.TopPoints(s, roots, p.Layout, Allocator.TempJob);
                        double pointsMs = stage.Elapsed.TotalMilliseconds;
                        map = ContinentJobs.BuildMap(s, roots, p.Layout, cp, points, keepTopTexels);
                        map.Stats.StageAMilliseconds += pointsMs;

                        // Cleared: Stage B writes the top level and the pyramid every level below,
                        // and a texel either missed would differ between this and the managed build.
                        stage.Restart();
                        continent = new NativeArray<float>(p.Layout.TexelCount, Allocator.TempJob);
                        map.Stats.WarpedSaturation   = math.sqrt(ContinentJobs.FillTop(continent, points, p.Layout, cp, map, keepTopTexels));
                        map.Stats.StageBMilliseconds = stage.Elapsed.TotalMilliseconds;

                        stage.Restart();
                        plan = MacroPyramidPlan.Create(s, p.Layout);
                        MacroPyramidJob.Run(continent, p.Layout, plan, MacroPyramidTop.FilterAndFill);
                        map.Stats.StageCMilliseconds = stage.Elapsed.TotalMilliseconds;

                        // The same list as EdgeCopies(s), already built.
                        copies = plan.Copies;
                    }
                    else
                    {
                        continent = new NativeArray<float>(1, Allocator.TempJob);
                        copies    = p.Layout.EdgeCopies(s);
                    }

                    rootArray = new NativeArray<RootQuad>(roots, Allocator.TempJob);
                    new MacroBuildJob { Surface = s, Params = p, Roots = rootArray, Continent = continent, Texels = texels }
                        .Schedule(texels.Length, 256).Complete();

                    // Shared edges: every copy reads a texel nothing copies into, so one pass in any
                    // order is enough (MacroLayout.EdgeSource). A few thousand texels; main thread.
                    foreach (int2 c in copies)
                        texels[c.x] = texels[c.y];

                    // Cleared: the pass writes the top level inside the quads and the pyramid the
                    // rest, and the heights read wetness 0 from it before either.
                    drainage = new NativeArray<float2>(ep.Enabled ? p.Layout.TexelCount : 1, Allocator.Persistent);
                    if (ep.Enabled)
                        erosion = Erode(s, roots, hp, p, ep, texels, drainage, plan ?? MacroPyramidPlan.Create(s, p.Layout));
                }
                finally
                {
                    if (continent.IsCreated) continent.Dispose();
                    if (rootArray.IsCreated) rootArray.Dispose();
                    if (points.IsCreated)    points.Dispose();
                }
            }
            catch
            {
                texels.Dispose();
                if (drainage.IsCreated) drainage.Dispose();
                throw;
            }

            return new MacroMaps(texels, drainage, p, clock.Elapsed.TotalMilliseconds, map, erosion);
        }

        // ErosionBuild.Erode's steps on the maps' persistent arrays: the graph, the heights, the pass
        // and the pyramid in Burst, wetness and D in managed code (ErosionChannels), as the managed
        // reference does them. Every temporary is freed whether or not a step throws.
        static ErosionStats Erode(in SurfaceDef s, RootQuad[] roots, in HeightParams hp, in MacroParams p,
                                  in ErosionParams ep, NativeArray<float4> texels, NativeArray<float2> drainage,
                                  MacroPyramidPlan plan)
        {
            var stats   = new ErosionStats();
            var clock   = Stopwatch.StartNew();
            var graph   = ErosionGraphJob.Build(s, roots, p.Layout, Allocator.TempJob);
            var heights = default(NativeArray<double>);
            var before  = default(NativeArray<double>);
            var routing = default(NativeErosionRouting);
            try
            {
                stats.GraphMilliseconds = clock.Elapsed.TotalMilliseconds;
                stats.NodeCount         = graph.NodeCount;

                clock.Restart();
                heights = new NativeArray<double>(graph.NodeCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                new ErosionHeightJob
                {
                    Height = hp, Macro = p, Cells = graph.Cells, Texels = texels, Drainage = drainage,
                    NodeTexel = graph.NodeTexel, Points = graph.Points, Heights = heights,
                }.Schedule(graph.NodeCount, 256).Complete();
                before = new NativeArray<double>(heights, Allocator.TempJob);
                stats.HeightsMilliseconds = clock.Elapsed.TotalMilliseconds;

                clock.Restart();
                routing = ErosionJob.Run(ep, graph, heights, Allocator.TempJob);
                stats.PassMilliseconds = clock.Elapsed.TotalMilliseconds;

                clock.Restart();
                ErosionChannels.WriteTop(p.Layout, graph.Cells, graph.NodeOf.Length, ep.QuadArea,
                                         new NativeInts(graph.NodeOf), new NativeDoubles(graph.Areas),
                                         new NativeDoubles(routing.Drainage), new NativeDoubles(before),
                                         new NativeDoubles(heights), new NativeFloat2s(drainage));
                double cut = 0.0;
                for (int i = 0; i < graph.NodeCount; i++) cut = math.max(cut, before[i] - heights[i]);
                stats.MaxCut            = cut;
                stats.WriteMilliseconds = clock.Elapsed.TotalMilliseconds;

                clock.Restart();
                ErosionPyramidJob.Run(texels, drainage, p.Layout, plan);
                stats.MaxAbsBaseHeight    = ErosionBuild.MaxAbsBaseHeight(new NativeFloat4s(texels), texels.Length);
                stats.PyramidMilliseconds = clock.Elapsed.TotalMilliseconds;
            }
            finally
            {
                graph.Dispose();
                if (heights.IsCreated) heights.Dispose();
                if (before.IsCreated)  before.Dispose();
                routing.Dispose();
            }
            return stats;
        }

        // No macro layer: Enabled false, and one-texel arrays so the jobs still have a
        // constructed NativeArray to carry -- an unconstructed one fails the safety check when
        // the job is scheduled, before the job ever gets to test the flag.
        public static MacroMaps Disabled()
            => new MacroMaps(new NativeArray<float4>(1, Allocator.Persistent), new NativeArray<float2>(1, Allocator.Persistent),
                             MacroParams.Disabled, 0.0, null, null);

        // A main-thread read, for tools. Generation reads the same maps through the same
        // arithmetic inside the sample jobs.
        public MacroSample Sample(int quad, int level, double u, double v)
            => MacroSampler.Read(Reader, Params, quad, level, u, v);

        public MacroMapReader Reader => new MacroMapReader(_texels, _drainage);

        public void Dispose()
        {
            if (_texels.IsCreated)   _texels.Dispose();
            if (_drainage.IsCreated) _drainage.Dispose();
        }
    }
}
