using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Sonoma.Core.Generation
{
    // ErosionPyramid.Run in Burst, in place on the maps' two persistent arrays. One job,
    // single-threaded, for MacroPyramidJob's reason: level by level, each needing the one above. The
    // same static functions as the managed reference, so the two are equal to the bit
    // (MacroMapsTests).
    [BurstCompile(CompileSynchronously = true)]
    public struct ErosionPyramidJob : IJob
    {
        public MacroLayout Layout;

        public NativeArray<float2> Drainage;
        public NativeArray<float4> Texels;

        [ReadOnly] public NativeArray<int2> Halo;
        [ReadOnly] public NativeArray<int2> Copies;
        [ReadOnly] public NativeArray<int2> Ranges;

        public void Execute()
        {
            ErosionPyramid.Run(new NativeFloat2s(Drainage), new NativeFloat4s(Texels), Texels.Length, Layout,
                               new NativeIntPairs(Halo), new NativeIntPairs(Copies), new NativeIntPairs(Ranges));
        }

        // Derives the drainage levels and folds D into the base height, and waits: checked as the
        // managed reference is checked, the plan's lists copied in for the job and freed whether or
        // not it throws.
        public static void Run(NativeArray<float4> texels, NativeArray<float2> drainage, in MacroLayout layout,
                               MacroPyramidPlan plan)
        {
            if (!texels.IsCreated || !drainage.IsCreated)
                throw new System.ArgumentException("ErosionPyramidJob: an array was never created.");
            if (texels.Length != drainage.Length)
                throw new System.ArgumentException("ErosionPyramidJob: one drainage texel per macro texel.", nameof(drainage));
            MacroPyramid.Validate(drainage.Length, layout, plan);

            var halo   = new NativeArray<int2>(plan.Halo,   Allocator.TempJob);
            var copies = new NativeArray<int2>(plan.Copies, Allocator.TempJob);
            var ranges = new NativeArray<int2>(plan.Ranges, Allocator.TempJob);
            try
            {
                new ErosionPyramidJob
                {
                    Layout = layout, Drainage = drainage, Texels = texels,
                    Halo = halo, Copies = copies, Ranges = ranges,
                }.Schedule().Complete();
            }
            finally
            {
                halo.Dispose();
                copies.Dispose();
                ranges.Dispose();
            }
        }
    }
}
