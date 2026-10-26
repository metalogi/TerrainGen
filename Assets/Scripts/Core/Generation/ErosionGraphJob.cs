using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // The erosion graph as the erosion job reads it: flat persistent arrays, MaxNeighbours slots a
    // node. Owns them; Dispose frees them.
    public struct NativeErosionGraph : IDisposable
    {
        public int Cells, NodeCount;
        public NativeArray<int>    NodeOf;       // graph texel -> node
        public NativeArray<int>    NodeTexel;    // node -> its owner's graph texel
        public NativeArray<double3> Points;      // graph texel -> its surface point, from managed code
        public NativeArray<int>    Neighbours;   // -1 for none
        public NativeArray<float>  Lengths;
        public NativeArray<double> Areas;

        public void Dispose()
        {
            if (NodeOf.IsCreated)     NodeOf.Dispose();
            if (NodeTexel.IsCreated)  NodeTexel.Dispose();
            if (Points.IsCreated)     Points.Dispose();
            if (Neighbours.IsCreated) Neighbours.Dispose();
            if (Lengths.IsCreated)    Lengths.Dispose();
            if (Areas.IsCreated)      Areas.Dispose();
        }
    }

    // ErosionGraph.Fill in Burst. One job, single-threaded: a few hundred thousand nodes, once at
    // startup. The numbering and the points come from ErosionGraph.Prepare in managed code, and the
    // rest is the same static function as the managed reference (MacroMapsTests).
    [BurstCompile(CompileSynchronously = true)]
    public struct ErosionGraphJob : IJob
    {
        public SurfaceDef Surface;
        public int        Cells, NodeCount;

        [ReadOnly] public NativeArray<int>     NodeOf;
        [ReadOnly] public NativeArray<int>     NodeTexel;
        [ReadOnly] public NativeArray<double3> Points;

        public NativeArray<int>    Neighbours;
        public NativeArray<float>  Lengths;
        public NativeArray<double> Areas;

        public void Execute()
        {
            ErosionGraph.Fill(Surface, Cells, NodeCount, new NativeInts(NodeOf), new NativeInts(NodeTexel),
                              new NativeDouble3s(Points), new NativeInts(Neighbours), new NativeFloats(Lengths),
                              new NativeDoubles(Areas));
        }

        // Builds the graph and waits. Everything it returns is the caller's to dispose.
        public static NativeErosionGraph Build(in SurfaceDef s, RootQuad[] roots, in MacroLayout layout, Allocator allocator)
        {
            var g      = ErosionGraph.Prepare(s, roots, layout);
            var points = new NativeArray<double3>(g.Points, allocator);
            var graph  = new NativeErosionGraph
            {
                Cells      = g.Cells,
                NodeCount  = g.NodeCount,
                NodeOf     = new NativeArray<int>(g.NodeOf, allocator),
                NodeTexel  = new NativeArray<int>(g.NodeTexel, allocator),
                Points     = points,
                Neighbours = new NativeArray<int>(g.NodeCount * ErosionGraph.MaxNeighbours, allocator, NativeArrayOptions.UninitializedMemory),
                Lengths    = new NativeArray<float>(g.NodeCount * ErosionGraph.MaxNeighbours, allocator, NativeArrayOptions.UninitializedMemory),
                Areas      = new NativeArray<double>(g.NodeCount, allocator, NativeArrayOptions.UninitializedMemory),
            };
            try
            {
                new ErosionGraphJob
                {
                    Surface = s, Cells = g.Cells, NodeCount = g.NodeCount,
                    NodeOf = graph.NodeOf, NodeTexel = graph.NodeTexel, Points = points,
                    Neighbours = graph.Neighbours, Lengths = graph.Lengths, Areas = graph.Areas,
                }.Schedule().Complete();
            }
            catch
            {
                graph.Dispose();
                throw;
            }
            return graph;
        }
    }
}
