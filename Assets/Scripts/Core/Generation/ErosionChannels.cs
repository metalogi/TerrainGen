using System;
using Unity.Mathematics;

namespace Sonoma.Core.Generation
{
    // What the erosion pass leaves in the drainage maps (Docs/M5_MacroLayer_Plan.md, M5c, E4): per
    // node, the wetness and the correction D, written to the top level of every quad that stores
    // the node. MacroPyramid then derives the levels below (task 5).
    //
    // Managed in both builds, after the Burst pass: wetness takes a logarithm, and Burst's log is
    // not managed code's to the bit, which is the continent jobs' rule once more. It is one log a
    // node, a few milliseconds at half a million.
    public static class ErosionChannels
    {
        // log(A / a) / log(Q / a), saturated to 0..1, where A is the node's drainage area, a its own
        // area and Q a root quad's: 0 on a ridge, which drains only itself, and 1 on a river that
        // drains a quad. Logarithmic, because drainage area spans the whole range from a texel to a
        // continent, and every decade of it reads as a step in how wet the ground is.
        public static float Wetness(double drainage, double nodeArea, double quadArea)
        {
            // A graph so coarse a node is a quad or more has no scale between a ridge and a river.
            if (!(quadArea > nodeArea)) return drainage > nodeArea ? 1f : 0f;
            double w = math.log(drainage / nodeArea) / math.log(quadArea / nodeArea);
            return (float)math.saturate(w);
        }

        // Writes (wetness, D) for every node into every top-level texel in [0, n] that stores it,
        // copies included, so the shared edge rows hold the owner's bits on both sides; the border
        // past [0, n] is the pyramid's halo (MacroPyramidTop.Fill). D is the eroded height less the
        // uneroded, which erosion and its clamp keep at or below zero wherever diffusion is off.
        public static void WriteTop<TI, TD, TC>(in MacroLayout layout, int cells, int texelsInGraph, double quadArea,
                                                TI nodeOf, TD areas, TD drainage, TD before, TD after, TC channel)
            where TI : struct, IErosionArray<int>
            where TD : struct, IErosionArray<double>
            where TC : struct, IErosionArray<float2>
        {
            int top = layout.LevelCount - 1;
            if (layout.Cells(top) != cells)
                throw new ArgumentException("ErosionChannels: the graph was built for another layout.");

            for (int t = 0; t < texelsInGraph; t++)
            {
                int node = nodeOf.Get(t);
                ErosionGraph.Coords(cells, t, out int q, out int i, out int j);
                float wet = Wetness(drainage.Get(node), areas.Get(node), quadArea);
                float d   = (float)(after.Get(node) - before.Get(node));
                channel.Set(layout.Index(q, top, i, j), new float2(wet, d));
            }
        }

        // The managed form, over the graph and the pass's result.
        public static void WriteTop(in MacroLayout layout, ErosionGraph g, in ErosionParams p, double[] before,
                                    ErosionPass.Result r, float2[] channel)
        {
            if (channel == null || channel.Length != layout.TexelCount)
                throw new ArgumentException("ErosionChannels: one drainage texel per macro texel.", nameof(channel));
            WriteTop(layout, g.Cells, g.NodeOf.Length, p.QuadArea, new ErosionGraph.Ints(g.NodeOf),
                     new ErosionGraph.Doubles(g.Areas), new ErosionGraph.Doubles(r.Drainage),
                     new ErosionGraph.Doubles(before), new ErosionGraph.Doubles(r.Heights), new Float2s(channel));
        }

        public readonly struct Float2s : IErosionArray<float2>
        {
            readonly float2[] _a;
            public Float2s(float2[] a) { _a = a; }
            public float2 Get(int index)               => _a[index];
            public void   Set(int index, float2 value) => _a[index] = value;
        }
    }
}
