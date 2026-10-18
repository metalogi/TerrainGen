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

        public MacroMapReader(NativeArray<float4> texels) { _texels = texels; }

        public float4 Texel(int index) => _texels[index];
    }

    // Fills every texel of every level of every quad from MacroGenerator. One texel per index,
    // independent of every other, so it is a plain parallel-for; the edge copies run after it.
    [BurstCompile(CompileSynchronously = true)]
    public struct MacroBuildJob : IJobParallelFor
    {
        public SurfaceDef  Surface;
        public MacroParams Params;

        [ReadOnly]  public NativeArray<RootQuad> Roots;
        [WriteOnly] public NativeArray<float4>   Texels;

        public void Execute(int index)
        {
            Params.Layout.Decode(index, out int q, out int level, out int i, out int j);
            Texels[index] = MacroGenerator.Texel(Surface, Roots[q], Params, level, i, j);
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

        public MacroParams         Params            { get; }
        public NativeArray<float4> Texels            => _texels;
        public double              BuildMilliseconds { get; }
        public long                Bytes             => (long)_texels.Length * 16;

        MacroMaps(NativeArray<float4> texels, in MacroParams p, double buildMilliseconds)
        {
            _texels           = texels;
            Params            = p;
            BuildMilliseconds = buildMilliseconds;
        }

        public static MacroMaps Build(in SurfaceDef s, RootQuad[] roots, in MacroParams p)
        {
            var clock  = Stopwatch.StartNew();
            var texels = new NativeArray<float4>(p.Layout.TexelCount, Allocator.Persistent,
                                                 NativeArrayOptions.UninitializedMemory);
            var rootArray = new NativeArray<RootQuad>(roots, Allocator.TempJob);
            try
            {
                new MacroBuildJob { Surface = s, Params = p, Roots = rootArray, Texels = texels }
                    .Schedule(texels.Length, 256).Complete();
            }
            finally
            {
                rootArray.Dispose();
            }

            // Shared edges: every copy reads a texel nothing copies into, so one pass in any
            // order is enough (MacroLayout.EdgeSource). A few thousand texels; main thread.
            foreach (int2 c in p.Layout.EdgeCopies(s))
                texels[c.x] = texels[c.y];

            return new MacroMaps(texels, p, clock.Elapsed.TotalMilliseconds);
        }

        // No macro layer: Enabled false, and a one-texel array so the jobs still have a
        // constructed NativeArray to carry -- an unconstructed one fails the safety check when
        // the job is scheduled, before the job ever gets to test the flag.
        public static MacroMaps Disabled()
            => new MacroMaps(new NativeArray<float4>(1, Allocator.Persistent), MacroParams.Disabled, 0.0);

        // A main-thread read, for tools. Generation reads the same maps through the same
        // arithmetic inside the sample jobs.
        public MacroSample Sample(int quad, int level, double u, double v)
            => Params.Enabled
                ? MacroSampler.Sample(new MacroMapReader(_texels), Params.Layout, quad, level, u, v)
                : MacroSample.Zero;

        public void Dispose()
        {
            if (_texels.IsCreated) _texels.Dispose();
        }
    }
}
