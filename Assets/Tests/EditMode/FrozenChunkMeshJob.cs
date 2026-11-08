using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Sonoma.Core.Generation;

namespace Sonoma.Tests
{
    // ChunkMeshJob as it stood on main before ocean-a (0da8c16), frozen: the reference
    // OceanChunkTests.OceanOffBuildsTodaysMesh holds the job with no ocean to, so that a change to
    // the code both paths share cannot pass by changing both sides of the comparison (PR #20's
    // review, finding 6). It is HeightIsBitIdenticalWithoutAMacroLayer's pattern for the mesh.
    //
    // Burst-compiled like the real one, so the two are compared under the same compiler: Burst's
    // normalize and managed code's need not agree in the last bit, and that is not what is being
    // tested. The caller sets the buffer parameters, the indices and the submesh, as main's
    // GenerationScheduler did (ChunkJobRunner.Build with beforeOcean). Do not edit it to follow
    // ChunkMeshJob: it is the record of what the mesh was.
    //
    // The layout it reads is frozen too, in FrozenLayout below: the index buffer, the skirt rows,
    // the coarse grid and the skirt drop. Reading ChunkMeshLayout's, which both sides call, would
    // let a change there move both alike, and the comparison would check the change against
    // itself (PR #20's second review, finding 3).
    [BurstCompile(CompileSynchronously = true)]
    internal struct FrozenChunkMeshJob : IJob
    {
        public int     Resolution;
        public double3 Anchor;
        public float   SkirtDepth;
        public float   SkirtFraction;
        public float   ChordSag;
        public float   Depth;

        [ReadOnly] public NativeArray<double3> BasePoints;
        [ReadOnly] public NativeArray<float3>  BaseNormals;
        [ReadOnly] public NativeArray<float>   Heights;
        [ReadOnly] public NativeArray<uint>    Splats;

        [ReadOnly] public NativeArray<double3> CoarseBasePoints;
        [ReadOnly] public NativeArray<float3>  CoarseBaseNormals;
        [ReadOnly] public NativeArray<float>   CoarseHeights;
        [ReadOnly] public NativeArray<uint>    CoarseSplats;

        public Mesh.MeshData Mesh;
        [WriteOnly] public NativeArray<float>  HeightBounds;   // [0] = min, [1] = max, [2] = skirt drop
        [WriteOnly] public NativeArray<float3> VertexBounds;   // [0] = min, [1] = max

        public void Execute()
        {
            int R  = Resolution;
            int g  = R + 2;
            int cg = FrozenLayout.CoarseGridSize(R);

            var verts = Mesh.GetVertexData<TerrainVertex>();

            float minH = float.MaxValue, maxH = float.MinValue;

            for (int j = 0; j < R; j++)
            for (int i = 0; i < R; i++)
            {
                int s = Sample(i, j, g);
                float h = Heights[s];
                minH = math.min(minH, h);
                maxH = math.max(maxH, h);

                float3 pos = Local(s);

                float3 dU = Local(Sample(i + 1, j, g)) - Local(Sample(i - 1, j, g));
                float3 dV = Local(Sample(i, j + 1, g)) - Local(Sample(i, j - 1, g));
                float3 n  = math.cross(dV, dU);

                float3 baseN = BaseNormals[s];
                if (math.dot(n, baseN) < 0f) n = -n;
                n = math.lengthsq(n) > 1e-12f ? math.normalize(n) : baseN;

                int ci = ((i & ~1) + 2) / 2;
                int cj = ((j & ~1) + 2) / 2;
                int cs = cj * cg + ci;

                verts[j * R + i] = new TerrainVertex
                {
                    Position      = pos,
                    Normal        = n,
                    Splat         = Splats[s],
                    MorphSplat    = CoarseSplats[cs],
                    TopoElevation = new float4(baseN, h),
                    MorphPosition = new float4(CoarseLocal(cs), Depth),
                    MorphNormalElevation = new float4(CoarseNormal(ci, cj, cg), CoarseHeights[cs]),
                };
            }

            float drop = FrozenLayout.SkirtDrop(SkirtDepth, SkirtFraction, maxH - minH, ChordSag);
            for (int edge = 0; edge < 4; edge++)
            for (int k = 0; k < R; k++)
            {
                int top = FrozenLayout.SkirtTopIndex(R, edge, k);
                var v   = verts[top];
                v.Position -= v.TopoElevation.xyz * drop;
                v.MorphPosition = new float4(v.MorphPosition.xyz - v.TopoElevation.xyz * drop, Depth);
                verts[FrozenLayout.SkirtBottomIndex(R, edge, k)] = v;
            }

            float3 lo = float.MaxValue, hi = float.MinValue;
            for (int n = 0; n < verts.Length; n++)
            {
                var v = verts[n];
                lo = math.min(lo, math.min(v.Position, v.MorphPosition.xyz));
                hi = math.max(hi, math.max(v.Position, v.MorphPosition.xyz));
            }

            HeightBounds[0] = minH;
            HeightBounds[1] = maxH;
            HeightBounds[2] = drop;
            VertexBounds[0] = lo;
            VertexBounds[1] = hi;
        }

        static int Sample(int i, int j, int g) => (j + 1) * g + (i + 1);

        float3 Local(int s) =>
            (float3)(BasePoints[s] + (double3)(BaseNormals[s] * Heights[s]) - Anchor);

        float3 CoarseLocal(int cs) =>
            (float3)(CoarseBasePoints[cs] + (double3)(CoarseBaseNormals[cs] * CoarseHeights[cs]) - Anchor);

        float3 CoarseNormal(int ci, int cj, int cg)
        {
            float3 dU = CoarseLocal(cj * cg + ci + 1) - CoarseLocal(cj * cg + ci - 1);
            float3 dV = CoarseLocal((cj + 1) * cg + ci) - CoarseLocal((cj - 1) * cg + ci);
            float3 n  = math.cross(dV, dU);

            float3 baseN = CoarseBaseNormals[cj * cg + ci];
            if (math.dot(n, baseN) < 0f) n = -n;
            return math.lengthsq(n) > 1e-12f ? math.normalize(n) : baseN;
        }
    }

    // The parts of ChunkMeshLayout the mesh on main was built from, as they stood at 0da8c16, for
    // FrozenChunkMeshJob and the buffers ChunkJobRunner sets around it. Frozen for the job's
    // reason; do not edit them to follow ChunkMeshLayout.
    internal static class FrozenLayout
    {
        public static int CoarseGridSize(int resolution) => (resolution - 1) / 2 + 3;
        public static int VertexCount(int resolution) => resolution * resolution + 4 * resolution;
        public static int SkirtBase(int resolution, int edge) => resolution * resolution + edge * resolution;
        public static int TriangleCount(int resolution) =>
            (resolution - 1) * (resolution - 1) * 2 + 8 * (resolution - 1);
        public static int IndexCount(int resolution) => TriangleCount(resolution) * 3;

        public static int SkirtTopIndex(int R, int edge, int k) => edge switch
        {
            0 => k,                                  // South (j = 0),   walking +u
            1 => k * R + (R - 1),                    // East  (i = R-1), walking +v
            2 => (R - 1) * R + (R - 1 - k),          // North (j = R-1), walking -u
            _ => (R - 1 - k) * R,                    // West  (i = 0),   walking -v
        };

        public static int SkirtBottomIndex(int R, int edge, int k) => SkirtBase(R, edge) + k;

        public static float SkirtDrop(float skirtDepth, float skirtFraction, double relief, double chordSag)
            => (float)(skirtDepth + skirtFraction * relief + chordSag);

        public static ushort[] BuildIndices(int R, bool flipWinding)
        {
            var idx = new ushort[IndexCount(R)];
            int t = 0;
            int b = flipWinding ? 2 : 1;    // swap the 2nd and 3rd index of every triangle
            int c = flipWinding ? 1 : 2;

            for (int j = 0; j < R - 1; j++)
            for (int i = 0; i < R - 1; i++)
            {
                int i00 = j * R + i, i10 = i00 + 1, i01 = i00 + R, i11 = i01 + 1;
                Tri(idx, ref t, i00, i11, i10, b, c);
                Tri(idx, ref t, i00, i01, i11, b, c);
            }

            for (int edge = 0; edge < 4; edge++)
            for (int k = 0; k < R - 1; k++)
            {
                int t0 = SkirtTopIndex(R, edge, k),    t1 = SkirtTopIndex(R, edge, k + 1);
                int b0 = SkirtBottomIndex(R, edge, k), b1 = SkirtBottomIndex(R, edge, k + 1);

                Tri(idx, ref t, t0, t1, b1, b, c);
                Tri(idx, ref t, t0, b1, b0, b, c);
            }

            return idx;
        }

        static void Tri(ushort[] idx, ref int t, int a, int second, int third, int b, int c)
        {
            idx[t]     = (ushort)a;
            idx[t + b] = (ushort)second;
            idx[t + c] = (ushort)third;
            t += 3;
        }
    }
}
