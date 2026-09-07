using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Sonoma.Core.Generation
{
    // Turns the sampled grids into a chunk mesh, writing straight into a MeshData obtained on
    // the main thread. Vertices are chunk-local floats around a double3 Anchor, so their
    // magnitude never exceeds the node size no matter where in the world the chunk sits.
    [BurstCompile(CompileSynchronously = true)]
    public struct ChunkMeshJob : IJob
    {
        public int     Resolution;
        public double3 Anchor;
        public float   SkirtDepth;
        public float   Depth;              // packed into the morph attribute for M3

        [ReadOnly] public NativeArray<double3> BasePoints;
        [ReadOnly] public NativeArray<float3>  BaseNormals;
        [ReadOnly] public NativeArray<float>   Heights;

        [ReadOnly] public NativeArray<double3> CoarseBasePoints;
        [ReadOnly] public NativeArray<float3>  CoarseBaseNormals;
        [ReadOnly] public NativeArray<float>   CoarseHeights;

        public Mesh.MeshData Mesh;
        [WriteOnly] public NativeArray<float> HeightBounds;   // [0] = min, [1] = max

        public void Execute()
        {
            int R  = Resolution;
            int g  = R + 2;
            int cg = ChunkMeshLayout.CoarseGridSize(R);

            var verts = Mesh.GetVertexData<TerrainVertex>();

            float minH = float.MaxValue, maxH = float.MinValue;

            // Main grid.
            for (int j = 0; j < R; j++)
            for (int i = 0; i < R; i++)
            {
                int s = Sample(i, j, g);
                float h = Heights[s];
                minH = math.min(minH, h);
                maxH = math.max(maxH, h);

                float3 pos = Local(s);

                // Central difference over the border-extended grid: every vertex, edge ones
                // included, gets a two-sided difference.
                float3 dU = Local(Sample(i + 1, j, g)) - Local(Sample(i - 1, j, g));
                float3 dV = Local(Sample(i, j + 1, g)) - Local(Sample(i, j - 1, g));
                float3 n  = math.cross(dV, dU);

                // Orient against the base surface normal rather than relying on a fixed
                // cross-product order. The topologies disagree on the handedness of
                // (du, dv, normal) -- see SurfaceMath.UvFrameIsRightHanded -- so a fixed
                // order points inward on one of them. A heightmap surface never tilts more
                // than 90 degrees from its base normal, so this sign test is unambiguous.
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
                    Uv            = new float2(i / (float)(R - 1), j / (float)(R - 1)),
                    TopoElevation = new float4(baseN, h),
                    MorphPosition = new float4(CoarseLocal(cs), Depth),
                    // The coarse height, not the fine one: this is what the shader blends
                    // TEXCOORD1.w towards, so the shading follows the geometry through the
                    // morph instead of staying on the unmorphed elevation.
                    MorphNormalElevation = new float4(CoarseNormal(ci, cj, cg), CoarseHeights[cs]),
                };
            }

            // Skirts: one duplicated row per edge, pushed along the inward base normal.
            for (int edge = 0; edge < 4; edge++)
            for (int k = 0; k < R; k++)
            {
                int top = ChunkMeshLayout.SkirtTopIndex(R, edge, k);
                var v   = verts[top];
                v.Position -= v.TopoElevation.xyz * SkirtDepth;
                // The morph target moves with it, so a morphing skirt stays attached to a
                // morphing edge instead of tearing away from it.
                v.MorphPosition = new float4(v.MorphPosition.xyz - v.TopoElevation.xyz * SkirtDepth, Depth);
                verts[ChunkMeshLayout.SkirtBottomIndex(R, edge, k)] = v;
            }

            HeightBounds[0] = minH;
            HeightBounds[1] = maxH;
        }

        // Index into the (R+2) x (R+2) sampled grid for chunk vertex (i, j), i and j in -1 .. R.
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
