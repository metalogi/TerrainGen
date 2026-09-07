using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // The one place a chunk vertex index becomes a surface parameter.
    //
    // Both generation jobs call this and nothing else computes (u, v), because the exact
    // expression is load-bearing. Two same-depth neighbours inside one root quad share an
    // edge parameter that is bit-identical -- UMax(x) and UMin(x+1) both evaluate
    // (double)(x+1) / (1 << d) -- so as long as both sides reach it by the same arithmetic,
    // their shared vertices land on the same double3, feed the same bits to the noise, and
    // return the same height. That is what removes the need for seam stitching.
    //
    // Rewriting the lerp as UMin + (UMax - UMin) * t is algebraically equal and changes the
    // last bits, which turns an exact seam into a sub-millimetre one that no test would
    // notice until it became a crack. HeightFunctionTests.SameQuadSharedVerticesAreBitIdentical
    // pins it.
    public static class ChunkGrid
    {
        // i and j may run from -1 to resolution (inclusive) rather than 0 to resolution-1:
        // the sampling grid carries a one-vertex border so normals at the chunk edge can be
        // taken by central difference without querying a neighbour.
        //
        // Within a root quad the border extends the same lerp past the node's own range and
        // lands on the true surface point of the adjacent node -- not an extrapolation. Past
        // a *root quad* edge it genuinely is an extrapolation: it continues this quad's
        // parameterisation rather than crossing onto the neighbour's, which is what leaves a
        // 0.151-degree normal discontinuity along the cube edges. See HeightSampleJob.
        public static void VertexUV(in NodeId n, int i, int j, int resolution,
                                    out double u, out double v)
        {
            double du = i / (double)(resolution - 1);
            double dv = j / (double)(resolution - 1);

            u = math.lerp(n.UMin, n.UMax, du);
            v = math.lerp(n.VMin, n.VMax, dv);
        }
    }
}
