using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

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
    //
    // Burst's safety checks are off for this job alone. With them, the Editor bounds-checked every
    // one of the pass's array reads, tens of millions a run, and ran it slower than .NET 8 runs the
    // managed reference: 3.2 s against 2.2 on the terrarium. Every array is the graph's or sized by
    // Run from its node count, and every index the pass forms is a node, a neighbour slot or a heap
    // slot below that count, which the managed reference's checked arrays and ErosionPassTests
    // exercise. A player build has no checks anyway. Jobs > Burst > Safety Checks > Force On turns
    // them back on, to debug the job in the Editor.
    [BurstCompile(CompileSynchronously = true, DisableSafetyChecks = true)]
    public struct ErosionJob : IJob
    {
        public ErosionParams Params;
        public int           NodeCount;

        [ReadOnly] public NativeArray<int>    Neighbours;
        [ReadOnly] public NativeArray<float>  Lengths;
        [ReadOnly] public NativeArray<double> Areas;

        public NativeArray<double> Heights;
        public NativeArray<double> Filled, Drainage, Scratch, HeapKeys;
        public NativeArray<int>    Receivers, Stack, Heap, State;

        public void Execute()
        {
            var a = new ErosionArrays<NativeInts, NativeFloats, NativeDoubles>
            {
                Neighbours = new NativeInts(Neighbours), Lengths = new NativeFloats(Lengths), Areas = new NativeDoubles(Areas),
                Heights = new NativeDoubles(Heights), Filled = new NativeDoubles(Filled), Drainage = new NativeDoubles(Drainage),
                Receivers = new NativeInts(Receivers), Stack = new NativeInts(Stack), Heap = new NativeInts(Heap),
                HeapKeys = new NativeDoubles(HeapKeys), State = new NativeInts(State), Scratch = new NativeDoubles(Scratch),
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
            ErosionPass.Validate(p, g.NodeCount, new NativeFloats(g.Lengths));

            int n = g.NodeCount;
            var routing = new NativeErosionRouting
            {
                Filled    = new NativeArray<double>(n, allocator, NativeArrayOptions.UninitializedMemory),
                Drainage  = new NativeArray<double>(n, allocator, NativeArrayOptions.UninitializedMemory),
                Receivers = new NativeArray<int>(n, allocator, NativeArrayOptions.UninitializedMemory),
                Stack     = new NativeArray<int>(n, allocator, NativeArrayOptions.UninitializedMemory),
            };
            var heap     = new NativeArray<int>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var heapKeys = new NativeArray<double>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var state    = new NativeArray<int>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var scratch  = new NativeArray<double>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                new ErosionJob
                {
                    Params = p, NodeCount = n,
                    Neighbours = g.Neighbours, Lengths = g.Lengths, Areas = g.Areas,
                    Heights = heights, Filled = routing.Filled, Drainage = routing.Drainage, Scratch = scratch,
                    Receivers = routing.Receivers, Stack = routing.Stack, Heap = heap, HeapKeys = heapKeys,
                    State = state,
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
                heapKeys.Dispose();
                state.Dispose();
                scratch.Dispose();
            }
            return routing;
        }
    }

    // T at every node of the graph (E1), in parallel: ErosionChannels.UnerodedHeight, the managed
    // reference's own function, at the node's managed point, reading the generated maps on its
    // texel.
    //
    // FloatMode.Strict, because T has to be the managed reference's to the bit and Burst's default
    // mode does not keep it so: on the narrow ring 13.6% of the nodes came out one float ULP apart
    // (Tools > Sonoma > Compare Erosion Builds, 2026-10-26), on the general Fbm path the stress scenes
    // take, while the pass and the pyramid fed the same T matched exactly. Erosion amplifies such a
    // difference -- an ULP can flip a routing tie -- and the default mode's arithmetic can also vary
    // with the target CPU, so a world built on two machines would not have the same rivers.
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
    public struct ErosionHeightJob : IJobParallelFor
    {
        public HeightParams Height;
        public MacroParams  Macro;
        public int          Cells;

        [ReadOnly] public NativeArray<float4>  Texels;
        [ReadOnly] public NativeArray<float2>  Drainage;
        [ReadOnly] public NativeArray<int>     NodeTexel;
        [ReadOnly] public NativeArray<double3> Points;

        [WriteOnly] public NativeArray<double> Heights;

        public void Execute(int node)
        {
            int texel = NodeTexel[node];
            ErosionGraph.Coords(Cells, texel, out int q, out int i, out int j);
            Heights[node] = ErosionChannels.UnerodedHeight(Height, Macro, new MacroMapReader(Texels, Drainage),
                                                           Points[texel], q, i, j);
        }
    }
}
