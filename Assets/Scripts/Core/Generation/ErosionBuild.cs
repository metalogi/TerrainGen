using System;
using System.Diagnostics;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // What one erosion build measured, for TerrainRoot's log and the tests.
    public sealed class ErosionStats
    {
        public int    NodeCount;
        public double GraphMilliseconds, HeightsMilliseconds, PassMilliseconds, WriteMilliseconds, PyramidMilliseconds;
        public double MaxCut;              // metres: the most any node was lowered, max (T - T')
        public double MaxAbsBaseHeight;    // metres: the largest |BaseHeight'| stored, every level (E5)

        public double TotalMilliseconds =>
            GraphMilliseconds + HeightsMilliseconds + PassMilliseconds + WriteMilliseconds + PyramidMilliseconds;
    }

    // Macro erosion from built maps to eroded ones (Docs/M5_MacroLayer_Plan.md, M5c-1), after the
    // generator and its edge copies:
    //
    //   1. The graph over the top level (ErosionGraph).
    //   2. T at every node: the base height and the detail octaves to Oe (E1).
    //   3. The pass (ErosionPass): T', the drainage and the routing.
    //   4. Wetness and D = T' - T into the drainage maps' top level (ErosionChannels.WriteTop).
    //   5. The drainage levels derived, and D folded into the base height (ErosionPyramid).
    //   6. The largest |BaseHeight'| stored, which bounds the eroded height (E5).
    //
    // This is the managed reference, as MacroGenerator.Build is for the generator. MacroMaps.Build
    // runs the same steps with steps 1, 2, 3 and 5 in Burst; MacroMapsTests holds the two to the bit.
    public static class ErosionBuild
    {
        // The macro parameters and the erosion parameters must come from one configuration: the
        // flag the read takes wetness by, and the maps the pass writes.
        public static void Check(in MacroParams p, in ErosionParams ep)
        {
            if (p.HasErosion != ep.Enabled)
                throw new ArgumentException(
                    "ErosionBuild: the macro parameters and the erosion parameters were made from different configurations.");
        }

        // The largest |base height| over every stored texel: what TerrainHeightFunction.MaxAbsHeight
        // takes in place of the analytic base-height bound once erosion has written the channel.
        public static double MaxAbsBaseHeight<TT>(TT texels, int count) where TT : struct, IErosionArray<float4>
        {
            double max = 0.0;
            for (int t = 0; t < count; t++) max = math.max(max, math.abs((double)texels.Get(t).x));
            return max;
        }

        // The result of steps 1 to 3, kept for the tests.
        public sealed class Managed
        {
            public ErosionGraph       Graph;
            public double[]           Uneroded;
            public ErosionPass.Result Result;
        }

        // Steps 1 to 6 in place on the maps' two arrays.
        public static Managed Erode(in SurfaceDef s, RootQuad[] roots, in HeightParams hp, in MacroParams p,
                                    in ErosionParams ep, float4[] texels, float2[] drainage, MacroPyramidPlan plan,
                                    ErosionStats stats)
        {
            Check(p, ep);
            if (!ep.Enabled) throw new ArgumentException("ErosionBuild: no erosion to run.", nameof(ep));

            var clock = Stopwatch.StartNew();
            var graph = ErosionGraph.Build(s, roots, p.Layout);
            stats.GraphMilliseconds = clock.Elapsed.TotalMilliseconds;
            stats.NodeCount         = graph.NodeCount;

            clock.Restart();
            var reader = new ArrayMacroTexels(texels, drainage);
            var t0     = new double[graph.NodeCount];
            for (int node = 0; node < graph.NodeCount; node++)
            {
                int texel = graph.NodeTexel[node];
                ErosionGraph.Coords(graph.Cells, texel, out int q, out int i, out int j);
                t0[node] = ErosionChannels.UnerodedHeight(hp, p, reader, graph.Points[texel], q, i, j);
            }
            stats.HeightsMilliseconds = clock.Elapsed.TotalMilliseconds;

            clock.Restart();
            var r = ErosionPass.Run(ep, graph, t0);
            stats.PassMilliseconds = clock.Elapsed.TotalMilliseconds;

            clock.Restart();
            ErosionChannels.WriteTop(p.Layout, graph, ep, t0, r, drainage);
            stats.MaxCut = MaxCut(t0, r.Heights);
            stats.WriteMilliseconds = clock.Elapsed.TotalMilliseconds;

            clock.Restart();
            ErosionPyramid.Run(texels, drainage, p.Layout, plan);
            stats.MaxAbsBaseHeight    = MaxAbsBaseHeight(new ErosionPyramid.Float4s(texels), texels.Length);
            stats.PyramidMilliseconds = clock.Elapsed.TotalMilliseconds;

            return new Managed { Graph = graph, Uneroded = t0, Result = r };
        }

        public static double MaxCut(double[] before, double[] after)
        {
            double max = 0.0;
            for (int i = 0; i < before.Length; i++) max = math.max(max, before[i] - after[i]);
            return max;
        }
    }

    // The macro maps' two arrays as managed arrays, for the managed builds and the tests.
    public readonly struct ArrayMacroTexels : IMacroTexels
    {
        readonly float4[] _texels;
        readonly float2[] _drainage;
        public ArrayMacroTexels(float4[] texels, float2[] drainage) { _texels = texels; _drainage = drainage; }
        public float4 Texel(int index)    => _texels[index];
        public float2 Drainage(int index) => _drainage != null ? _drainage[index] : default;
    }
}
