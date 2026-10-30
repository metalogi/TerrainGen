using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sonoma.Core.Generation
{
    // Turns the sampled grids into a chunk mesh, writing straight into a MeshData obtained on
    // the main thread. Vertices are chunk-local floats around a double3 Anchor, so their
    // magnitude never exceeds the node size no matter where in the world the chunk sits.
    //
    // With an ocean the job also decides what the chunk draws (Docs/Ocean_A_Plan.md, A1): terrain,
    // terrain and water, or water alone, from the range its own vertices and morph targets span.
    // That is known only once the heights are, so the job sizes the mesh's buffers itself and
    // writes its index data and submeshes (step 0's spike, S1). Without an ocean every chunk is
    // Land and gets the vertices, indices and submesh it always did.
    [BurstCompile(CompileSynchronously = true)]
    public struct ChunkMeshJob : IJob
    {
        public int     Resolution;
        public double3 Anchor;
        // The skirt's drop is ChunkMeshLayout.SkirtDrop(SkirtDepth, SkirtFraction, relief,
        // ChordSag), with the relief this job measures over the main grid: known only once the
        // heights are. ChordSag is the curvature's part (ChunkMeshLayout.ChordSag), 0 for a
        // scene with no fraction.
        public float   SkirtDepth;
        public float   SkirtFraction;
        public float   ChordSag;
        public float   Depth;              // packed into the morph attribute for M3

        // The sea, default for none, and the water's skirt drop, ChunkMeshLayout.WaterSkirtDrop:
        // the water has no relief, so the scheduler computes it before the heights exist.
        public OceanParams Ocean;
        public float       WaterSkirtDrop;

        [ReadOnly] public NativeArray<double3> BasePoints;
        [ReadOnly] public NativeArray<float3>  BaseNormals;
        [ReadOnly] public NativeArray<float>   Heights;
        [ReadOnly] public NativeArray<uint>    Splats;

        [ReadOnly] public NativeArray<double3> CoarseBasePoints;
        [ReadOnly] public NativeArray<float3>  CoarseBaseNormals;
        [ReadOnly] public NativeArray<float>   CoarseHeights;
        [ReadOnly] public NativeArray<uint>    CoarseSplats;

        // ChunkMeshBuffers.VertexAttributes, and the shared index buffers for this resolution and
        // winding: the single one, and the doubled one a Coast chunk draws (A7). Without an ocean
        // the doubled one is never read, and the single one may be passed for it.
        [ReadOnly] public NativeArray<VertexAttributeDescriptor> VertexAttributes;
        [ReadOnly] public NativeArray<ushort> Indices;
        [ReadOnly] public NativeArray<ushort> DoubledIndices;

        public Mesh.MeshData Mesh;

        // [0] min, [1] max of the fine heights: the selector's bounding sphere. [2] the terrain
        // skirt's drop. [3] lo, [4] hi: the drawn range, fine heights and morph targets (A1).
        // [5] the ChunkKind, as a float. (A9)
        [WriteOnly] public NativeArray<float> HeightBounds;

        // The box round every position the mesh can draw, in chunk-local space: grid and skirt
        // vertices and their morph targets, terrain and water. [0] = min, [1] = max. The culling
        // bounds, exactly: a chunk is placed by translation alone, so local is world up to a shift.
        [WriteOnly] public NativeArray<float3> VertexBounds;

        public void Execute()
        {
            int R  = Resolution;
            int g  = R + 2;
            int cg = ChunkMeshLayout.CoarseGridSize(R);

            // The heights first: the fine range, and the drawn range, which adds the morph targets.
            // A morphing vertex lies between its height and its target's, and a triangle between
            // its vertices, so every frame of every morph state lies in [lo, hi].
            float minH = float.MaxValue, maxH = float.MinValue;
            float lo   = float.MaxValue, hi   = float.MinValue;
            for (int j = 0; j < R; j++)
            for (int i = 0; i < R; i++)
            {
                float h  = Heights[Sample(i, j, g)];
                float hc = CoarseHeights[CoarseSample(i, j, cg)];
                minH = math.min(minH, h);
                maxH = math.max(maxH, h);
                lo   = math.min(lo, math.min(h, hc));
                hi   = math.max(hi, math.max(h, hc));
            }

            ChunkKind kind = Ocean.Enabled
                ? ChunkKinds.Classify(lo, hi, Ocean.SeaLevelFloat, Ocean.TerrainOverlap, Ocean.WaveAmplitude)
                : ChunkKind.Land;

            // The buffers for that kind (S1). A Coast chunk's water triangles are the doubled
            // buffer's second half, already offset past the terrain, so both submeshes start at
            // vertex 0.
            int indexCount = ChunkMeshLayout.IndexCount(R);
            Mesh.SetVertexBufferParams(ChunkMeshLayout.VertexCount(R, kind), VertexAttributes);
            Mesh.SetIndexBufferParams(ChunkMeshLayout.IndexCount(R, kind), IndexFormat.UInt16);
            Mesh.GetIndexData<ushort>().CopyFrom(kind == ChunkKind.Coast ? DoubledIndices : Indices);
            Mesh.subMeshCount = kind == ChunkKind.Coast ? 2 : 1;
            Mesh.SetSubMesh(0, new SubMeshDescriptor(0, indexCount), MeshUpdateFlags.DontRecalculateBounds);
            if (kind == ChunkKind.Coast)
                Mesh.SetSubMesh(1, new SubMeshDescriptor(indexCount, indexCount), MeshUpdateFlags.DontRecalculateBounds);

            var verts = Mesh.GetVertexData<TerrainVertex>();

            float drop = ChunkMeshLayout.SkirtDrop(SkirtDepth, SkirtFraction, maxH - minH, ChordSag);
            if (kind != ChunkKind.Ocean)
            {
                WriteTerrain(verts, R, g, cg);
                WriteSkirts(verts, 0, drop);
            }
            if (kind != ChunkKind.Land)
            {
                int wb = ChunkMeshLayout.WaterBase(R, kind);
                WriteWater(verts, wb, R, g, cg);
                WriteSkirts(verts, wb, WaterSkirtDrop);
            }

            // A morphing vertex lies on the segment from its position to its target, so the box
            // round both ends of every vertex holds every frame of the morph.
            float3 bmin = float.MaxValue, bmax = float.MinValue;
            for (int n = 0; n < verts.Length; n++)
            {
                var v = verts[n];
                bmin = math.min(bmin, math.min(v.Position, v.MorphPosition.xyz));
                bmax = math.max(bmax, math.max(v.Position, v.MorphPosition.xyz));
            }

            HeightBounds[0] = minH;
            HeightBounds[1] = maxH;
            HeightBounds[2] = drop;
            HeightBounds[3] = lo;
            HeightBounds[4] = hi;
            HeightBounds[5] = (float)kind;
            VertexBounds[0] = bmin;
            VertexBounds[1] = bmax;
        }

        void WriteTerrain(NativeArray<TerrainVertex> verts, int R, int g, int cg)
        {
            for (int j = 0; j < R; j++)
            for (int i = 0; i < R; i++)
            {
                int s = Sample(i, j, g);
                float h = Heights[s];

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
                    Splat         = Splats[s],
                    // The even vertex's weights from the coarse job, as the position and the
                    // elevation are: what the parent draws there.
                    MorphSplat    = CoarseSplats[cs],
                    TopoElevation = new float4(baseN, h),
                    MorphPosition = new float4(CoarseLocal(cs), Depth),
                    // The coarse height, not the fine one: this is what the shader blends
                    // TEXCOORD1.w towards, so the shading follows the geometry through the
                    // morph instead of staying on the unmorphed elevation.
                    MorphNormalElevation = new float4(CoarseNormal(ci, cj, cg), CoarseHeights[cs]),
                };
            }
        }

        // The water grid (A8), at vertex wb on: the terrain's own position expression with the
        // height replaced by the sea level, so terrain and water are the same bits wherever a
        // height equals it and the coastline sits exactly where the heights cross it. The morph
        // target is the parent's water vertex, since the coarse base points are the parent's.
        // TEXCOORD3.w carries the fine floor height, for the ocean shader's depth colour.
        void WriteWater(NativeArray<TerrainVertex> verts, int wb, int R, int g, int cg)
        {
            float sea = Ocean.SeaLevelFloat;
            for (int j = 0; j < R; j++)
            for (int i = 0; i < R; i++)
            {
                int s  = Sample(i, j, g);
                int cs = CoarseSample(i, j, cg);
                float3 baseN = BaseNormals[s];

                verts[wb + j * R + i] = new TerrainVertex
                {
                    Position      = (float3)(BasePoints[s] + (double3)(baseN * sea) - Anchor),
                    Normal        = baseN,
                    Splat         = 0u,
                    MorphSplat    = 0u,
                    TopoElevation = new float4(baseN, sea),
                    MorphPosition = new float4(
                        (float3)(CoarseBasePoints[cs] + (double3)(CoarseBaseNormals[cs] * sea) - Anchor), Depth),
                    MorphNormalElevation = new float4(CoarseBaseNormals[cs], Heights[s]),
                };
            }
        }

        // Skirts: one duplicated row per edge of the grid at vertex b, pushed along the inward
        // base normal by drop. The terrain's drop is sized from its own relief, the water's from
        // the curve alone.
        void WriteSkirts(NativeArray<TerrainVertex> verts, int b, float drop)
        {
            int R = Resolution;
            for (int edge = 0; edge < 4; edge++)
            for (int k = 0; k < R; k++)
            {
                var v = verts[b + ChunkMeshLayout.SkirtTopIndex(R, edge, k)];
                v.Position -= v.TopoElevation.xyz * drop;
                // The morph target moves with it, so a morphing skirt stays attached to a
                // morphing edge instead of tearing away from it.
                v.MorphPosition = new float4(v.MorphPosition.xyz - v.TopoElevation.xyz * drop, Depth);
                verts[b + ChunkMeshLayout.SkirtBottomIndex(R, edge, k)] = v;
            }
        }

        // Index into the (R+2) x (R+2) sampled grid for chunk vertex (i, j), i and j in -1 .. R.
        static int Sample(int i, int j, int g) => (j + 1) * g + (i + 1);

        // Index into the coarse grid of chunk vertex (i, j)'s morph target, the even vertex.
        static int CoarseSample(int i, int j, int cg) => (((j & ~1) + 2) / 2) * cg + ((i & ~1) + 2) / 2;

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
