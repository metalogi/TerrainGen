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
            int cg = ChunkMeshLayout.CoarseGridSize(R);

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

            float drop = ChunkMeshLayout.SkirtDrop(SkirtDepth, SkirtFraction, maxH - minH, ChordSag);
            for (int edge = 0; edge < 4; edge++)
            for (int k = 0; k < R; k++)
            {
                int top = ChunkMeshLayout.SkirtTopIndex(R, edge, k);
                var v   = verts[top];
                v.Position -= v.TopoElevation.xyz * drop;
                v.MorphPosition = new float4(v.MorphPosition.xyz - v.TopoElevation.xyz * drop, Depth);
                verts[ChunkMeshLayout.SkirtBottomIndex(R, edge, k)] = v;
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
}
