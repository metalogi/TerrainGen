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

        MacroMaps(NativeArray<float4> texels, NativeArray<float2> drainage, in MacroParams p,
                  double buildMilliseconds, ContinentMap continents)
        {
            _texels           = texels;
            _drainage         = drainage;
            Params            = p;
            BuildMilliseconds = buildMilliseconds;
            Continents        = continents;
        }

        public static MacroMaps Build(in SurfaceDef s, RootQuad[] roots, in MacroParams p)
            => Build(s, roots, p, ContinentParams.None);

        // With continents, K1's sequence (Docs/Continents_Plan.md): the continent map with its
        // texel scan in Burst (Stage A), the field at the top level (B), the pyramid filtered down
        // (C), then the generator reading the channel, then the edge copies. The continent channel
        // is temporary, freed before this returns. keepTopTexels keeps, on the continent map, each
        // top-level texel's nearest micro seed and coast distance, for tools that draw them
        // (ContinentMap.TopMicro and TopCoast).
        public static MacroMaps Build(in SurfaceDef s, RootQuad[] roots, in MacroParams p, in ContinentParams cp,
                                      bool keepTopTexels = false)
        {
            MacroGenerator.CheckContinents(p, cp);

            var clock  = Stopwatch.StartNew();
            var texels = new NativeArray<float4>(p.Layout.TexelCount, Allocator.Persistent,
                                                 NativeArrayOptions.UninitializedMemory);
            ContinentMap map = null;
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
                        var plan = MacroPyramidPlan.Create(s, p.Layout);
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
                throw;
            }

            // Cleared, so until the erosion pass writes it (M5c-1, task 6) every read takes
            // wetness 0, the identity.
            var drainage = new NativeArray<float2>(p.HasErosion ? p.Layout.TexelCount : 1, Allocator.Persistent);
            return new MacroMaps(texels, drainage, p, clock.Elapsed.TotalMilliseconds, map);
        }

        // No macro layer: Enabled false, and one-texel arrays so the jobs still have a
        // constructed NativeArray to carry -- an unconstructed one fails the safety check when
        // the job is scheduled, before the job ever gets to test the flag.
        public static MacroMaps Disabled()
            => new MacroMaps(new NativeArray<float4>(1, Allocator.Persistent), new NativeArray<float2>(1, Allocator.Persistent),
                             MacroParams.Disabled, 0.0, null);

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
