using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Sonoma.Core.Generation
{
    // MacroPyramid.Run in Burst, over a NativeArray channel. One job, single-threaded: the
    // sequence is level by level and each level needs the one above it finished, and the whole
    // of it is a 5 x 5 stencil per texel -- about 13 million weighted adds on Earth's four
    // levels, milliseconds, once at startup. It calls the same static functions as the managed
    // reference, so the two do the same arithmetic in the same order (MacroMapsTests).
    [BurstCompile(CompileSynchronously = true)]
    public struct MacroPyramidJob : IJob
    {
        public MacroLayout     Layout;
        public MacroPyramidTop TopMode;

        public NativeArray<float> Channel;
        public NativeArray<float> Scratch;   // one per texel, used only by FilterAndFill

        [ReadOnly] public NativeArray<int2> Halo;
        [ReadOnly] public NativeArray<int2> Copies;
        [ReadOnly] public NativeArray<int2> Ranges;

        public void Execute()
        {
            var ch      = new NativeScalars(Channel);
            var scratch = new NativeScalars(Scratch);
            MacroPyramid.Run(ref ch, ref scratch, Layout, new NativePairs(Halo), new NativePairs(Copies),
                             new NativePairs(Ranges), TopMode);
        }

        // Runs the pyramid on channel and waits: checked as the managed reference is checked, the
        // plan's lists copied in for the job, and every temporary freed whether or not the job
        // throws.
        public static void Run(NativeArray<float> channel, in MacroLayout layout, MacroPyramidPlan plan, MacroPyramidTop topMode)
        {
            if (!channel.IsCreated)
                throw new System.ArgumentException("MacroPyramidJob: the channel array was never created.", nameof(channel));
            MacroPyramid.Validate(channel.Length, layout, plan);

            var halo    = new NativeArray<int2>(plan.Halo,   Allocator.TempJob);
            var copies  = new NativeArray<int2>(plan.Copies, Allocator.TempJob);
            var ranges  = new NativeArray<int2>(plan.Ranges, Allocator.TempJob);
            var scratch = new NativeArray<float>(topMode == MacroPyramidTop.FilterAndFill ? channel.Length : 1,
                                                 Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                new MacroPyramidJob
                {
                    Layout = layout, TopMode = topMode, Channel = channel, Scratch = scratch,
                    Halo = halo, Copies = copies, Ranges = ranges,
                }.Schedule().Complete();
            }
            finally
            {
                halo.Dispose();
                copies.Dispose();
                ranges.Dispose();
                scratch.Dispose();
            }
        }

        struct NativeScalars : IMacroScalars
        {
            NativeArray<float> _a;
            public NativeScalars(NativeArray<float> a) { _a = a; }
            public float Get(int index)              => _a[index];
            public void  Set(int index, float value) => _a[index] = value;
        }

        readonly struct NativePairs : IMacroPairs
        {
            [ReadOnly] readonly NativeArray<int2> _a;
            public NativePairs(NativeArray<int2> a) { _a = a; }
            public int2 Get(int k) => _a[k];
        }
    }
}
