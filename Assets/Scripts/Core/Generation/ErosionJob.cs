using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Sonoma.Core.Generation
{
    // What ErosionJob.Run returns besides the eroded heights: the last routing, persistent, for the
    // wetness channel and the tests. Owns its arrays; Dispose frees them.
    public struct NativeErosionRouting : IDisposable
    {
        public NativeArray<double> Filled;
        public NativeArray<double> Drainage;
        public NativeArray<int>    Receivers;
        public NativeArray<int>    Stack;

        public void Dispose()
        {
            if (Filled.IsCreated)    Filled.Dispose();
            if (Drainage.IsCreated)  Drainage.Dispose();
            if (Receivers.IsCreated) Receivers.Dispose();
            if (Stack.IsCreated)     Stack.Dispose();
        }
    }

    // ErosionPass.Run in Burst over a NativeErosionGraph. One job, single-threaded: the flood, the
    // stack and the incision are sequential passes, and the pass is the same static function as the
    // managed reference, so the two are equal to the bit (MacroMapsTests).
    [BurstCompile(CompileSynchronously = true)]
    public struct ErosionJob : IJob
    {
        public ErosionParams Params;
        public int           NodeCount;

        [ReadOnly] public NativeArray<int>    Neighbours;
        [ReadOnly] public NativeArray<float>  Lengths;
        [ReadOnly] public NativeArray<double> Areas;

        public NativeArray<double> Heights;
        public NativeArray<double> Filled, Drainage, Scratch;
        public NativeArray<int>    Receivers, Stack, Heap, State;

        public void Execute()
        {
            var a = new ErosionArrays<Ints, Floats, Doubles>
            {
                Neighbours = new Ints(Neighbours), Lengths = new Floats(Lengths), Areas = new Doubles(Areas),
                Heights = new Doubles(Heights), Filled = new Doubles(Filled), Drainage = new Doubles(Drainage),
                Receivers = new Ints(Receivers), Stack = new Ints(Stack), Heap = new Ints(Heap),
                State = new Ints(State), Scratch = new Doubles(Scratch),
            };
            ErosionPass.Run(Params, NodeCount, ref a);
        }

        // Erodes heights in place, one per node of the graph, and waits. Checked as the managed
        // reference is checked; the scratch arrays are freed here whether or not the job throws.
        public static NativeErosionRouting Run(in ErosionParams p, in NativeErosionGraph g, NativeArray<double> heights,
                                               Allocator allocator)
        {
            if (!heights.IsCreated || heights.Length != g.NodeCount)
                throw new ArgumentException("ErosionJob: one height per node of the graph.", nameof(heights));
            ErosionPass.Validate(p, g.NodeCount, new Floats(g.Lengths));

            int n = g.NodeCount;
            var routing = new NativeErosionRouting
            {
                Filled    = new NativeArray<double>(n, allocator, NativeArrayOptions.UninitializedMemory),
                Drainage  = new NativeArray<double>(n, allocator, NativeArrayOptions.UninitializedMemory),
                Receivers = new NativeArray<int>(n, allocator, NativeArrayOptions.UninitializedMemory),
                Stack     = new NativeArray<int>(n, allocator, NativeArrayOptions.UninitializedMemory),
            };
            var heap    = new NativeArray<int>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var state   = new NativeArray<int>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var scratch = new NativeArray<double>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                new ErosionJob
                {
                    Params = p, NodeCount = n,
                    Neighbours = g.Neighbours, Lengths = g.Lengths, Areas = g.Areas,
                    Heights = heights, Filled = routing.Filled, Drainage = routing.Drainage, Scratch = scratch,
                    Receivers = routing.Receivers, Stack = routing.Stack, Heap = heap, State = state,
                }.Schedule().Complete();
            }
            catch
            {
                routing.Dispose();
                throw;
            }
            finally
            {
                heap.Dispose();
                state.Dispose();
                scratch.Dispose();
            }
            return routing;
        }

        // The graph's arrays are read-only to the job and never written through these: the pass
        // writes only its own.
        struct Ints : IErosionArray<int>
        {
            NativeArray<int> _a;
            public Ints(NativeArray<int> a) { _a = a; }
            public int  Get(int index)            => _a[index];
            public void Set(int index, int value) => _a[index] = value;
        }

        struct Floats : IErosionArray<float>
        {
            NativeArray<float> _a;
            public Floats(NativeArray<float> a) { _a = a; }
            public float Get(int index)              => _a[index];
            public void  Set(int index, float value) => _a[index] = value;
        }

        struct Doubles : IErosionArray<double>
        {
            NativeArray<double> _a;
            public Doubles(NativeArray<double> a) { _a = a; }
            public double Get(int index)               => _a[index];
            public void   Set(int index, double value) => _a[index] = value;
        }
    }
}
