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

    // ErosionHeightJob's computation with every intermediate kept: the macro read, the biome
    // weights, the band gains and the height. A diagnostic for Tools > Sonoma > Compare Erosion
    // Builds, which compares each against the managed reference to find the first that differs;
    // nothing else uses it. Compiled as ErosionHeightJob is, so it sees what that job does.
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
    public struct ErosionHeightPartsJob : IJobParallelFor
    {
        public HeightParams Height;
        public MacroParams  Macro;
        public int          Cells;

        [ReadOnly] public NativeArray<float4>  Texels;
        [ReadOnly] public NativeArray<float2>  Drainage;
        [ReadOnly] public NativeArray<int>     NodeTexel;
        [ReadOnly] public NativeArray<double3> Points;

        [WriteOnly] public NativeArray<double> BaseHeight;
        [WriteOnly] public NativeArray<float4> Climate;    // temperature, moisture, ruggedness, 0
        [WriteOnly] public NativeArray<float4> Weights;
        [WriteOnly] public NativeArray<float4> BandGain;   // low, mid, high, 0
        [WriteOnly] public NativeArray<float>  Heights;

        public void Execute(int node)
        {
            int texel = NodeTexel[node];
            ErosionGraph.Coords(Cells, texel, out int q, out int i, out int j);
            ErosionChannels.UnerodedHeightParts(Height, Macro, new MacroMapReader(Texels, Drainage), Points[texel], q, i, j,
                                                out MacroSample read, out float4 weights, out float height);
            BaseHeight[node] = read.BaseHeight;
            Climate[node]    = new float4(read.Temperature, read.Moisture, read.Ruggedness, 0f);
            Weights[node]    = weights;
            BandGain[node]   = new float4(read.BandGain, 0f);
            Heights[node]    = height;
        }
    }

    // T at every node of the graph (E1), in parallel: ErosionChannels.UnerodedHeight, the managed
    // reference's own function, at the node's managed point, reading the generated maps on its
    // texel.
    //
    // T has to be the managed reference's to the bit: erosion amplifies an ULP, which can flip a
    // routing tie, so a world built twice would not have the same rivers. On the narrow ring 13.6% of
    // the nodes once came out an ULP apart (Tools > Sonoma > Compare Erosion Builds). The cause was
    // the biome weights' float arithmetic, now in double (Biomes); FloatMode.Strict, tried first,
    // changed nothing. It stays, so no contraction or reordering the default mode may allow can part
    // the two again.
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
