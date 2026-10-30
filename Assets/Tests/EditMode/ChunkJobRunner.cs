using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // One chunk built through the real job path -- HeightSampleJob, CoarseHeightSampleJob and
    // ChunkMeshJob, under Burst, with the macro maps and the sea the scheduler would hand them --
    // and everything the mesh job wrote, read back. The jobs pick each one's macro level from the
    // node themselves, and the mesh job sizes the buffers for the kind it decides (ocean-a, S1).
    //
    // Editor-only: it needs the job system and a real Mesh.MeshData.
    internal static class ChunkJobRunner
    {
        internal struct Built
        {
            public double3          Anchor;
            public TerrainVertex[]  Verts;
            public ushort[]         Indices;
            public SubMeshDescriptor[] SubMeshes;
            public float[]          HeightBounds;   // min, max, skirt drop, lo, hi, kind (A9)
            public float3[]         VertexBounds;   // min, max
            public ChunkKind        Kind => (ChunkKind)(int)HeightBounds[5];

            // What the sample jobs produced, for checks made independently of the mesh job.
            public double3[] BasePoints, CoarseBasePoints;
            public float3[]  BaseNormals, CoarseBaseNormals;
            public float[]   Heights, CoarseHeights;
        }

        internal static Built Build(in SurfaceDef surface, RootQuad[] roots, in HeightParams hp, NodeId node,
                                    MacroMaps macro, float skirtDepth = 0f, float skirtFraction = 0f,
                                    float chordSag = 0f, OceanParams ocean = default, float waterSkirtDrop = 0f)
        {
            var root    = roots[node.Quad];
            int R       = hp.Resolution;
            int samples = ChunkMeshLayout.SampleCount(R);
            int coarse  = ChunkMeshLayout.CoarseCount(R);
            bool flip   = SurfaceMath.UvFrameIsRightHanded(surface);

            var basePoints    = new NativeArray<double3>(samples, Allocator.TempJob);
            var baseNormals   = new NativeArray<float3>(samples,  Allocator.TempJob);
            var heights       = new NativeArray<float>(samples,   Allocator.TempJob);
            var coarsePoints  = new NativeArray<double3>(coarse,  Allocator.TempJob);
            var coarseNormals = new NativeArray<float3>(coarse,   Allocator.TempJob);
            var coarseHeights = new NativeArray<float>(coarse,    Allocator.TempJob);
            var splats        = new NativeArray<uint>(samples,    Allocator.TempJob);
            var coarseSplats  = new NativeArray<uint>(coarse,     Allocator.TempJob);
            var heightBounds  = new NativeArray<float>(6,         Allocator.TempJob);
            var vertexBounds  = new NativeArray<float3>(2,        Allocator.TempJob);
            var attributes    = new NativeArray<VertexAttributeDescriptor>(
                                    ChunkMeshBuffers.VertexAttributes(), Allocator.TempJob);
            var indices       = ChunkMeshBuffers.SharedIndices(R, flip);
            var doubled       = ocean.Enabled ? ChunkMeshBuffers.SharedIndices(R, flip, true) : indices;

            var meshData = Mesh.AllocateWritableMeshData(1);
            var data     = meshData[0];

            double3 anchor = SurfaceMath.SurfacePoint(surface, root,
                                 0.5 * (node.UMin + node.UMax), 0.5 * (node.VMin + node.VMax));

            new HeightSampleJob
            {
                Surface = surface, Root = root, Node = node, Params = hp,
                MaxOctave = hp.MaxOctave(node.Depth), Resolution = R,
                Macro = macro.Params, MacroTexels = macro.Texels, MacroDrainage = macro.Drainage,
                BasePoints = basePoints, BaseNormals = baseNormals, Heights = heights, Splats = splats,
            }.Schedule(samples, 64).Complete();

            new CoarseHeightSampleJob
            {
                Surface = surface, Root = root, Node = node, Params = hp,
                MaxOctave = hp.MaxOctave(node.Depth - 1), Resolution = R,
                Macro = macro.Params, MacroTexels = macro.Texels, MacroDrainage = macro.Drainage,
                BasePoints = coarsePoints, BaseNormals = coarseNormals, Heights = coarseHeights,
                Splats = coarseSplats,
            }.Schedule(coarse, 64).Complete();

            new ChunkMeshJob
            {
                Resolution = R, Anchor = anchor, Depth = node.Depth,
                SkirtDepth = skirtDepth, SkirtFraction = skirtFraction, ChordSag = chordSag,
                Ocean = ocean, WaterSkirtDrop = waterSkirtDrop,
                BasePoints = basePoints, BaseNormals = baseNormals, Heights = heights, Splats = splats,
                CoarseBasePoints = coarsePoints, CoarseBaseNormals = coarseNormals,
                CoarseHeights = coarseHeights, CoarseSplats = coarseSplats,
                VertexAttributes = attributes, Indices = indices, DoubledIndices = doubled,
                Mesh = data, HeightBounds = heightBounds, VertexBounds = vertexBounds,
            }.Schedule().Complete();

            var built = new Built
            {
                Anchor       = anchor,
                Verts        = data.GetVertexData<TerrainVertex>().ToArray(),
                Indices      = data.GetIndexData<ushort>().ToArray(),
                SubMeshes    = new SubMeshDescriptor[data.subMeshCount],
                HeightBounds = heightBounds.ToArray(),
                VertexBounds = vertexBounds.ToArray(),
                BasePoints = basePoints.ToArray(), CoarseBasePoints = coarsePoints.ToArray(),
                BaseNormals = baseNormals.ToArray(), CoarseBaseNormals = coarseNormals.ToArray(),
                Heights = heights.ToArray(), CoarseHeights = coarseHeights.ToArray(),
            };
            for (int k = 0; k < built.SubMeshes.Length; k++) built.SubMeshes[k] = data.GetSubMesh(k);

            meshData.Dispose();
            basePoints.Dispose(); baseNormals.Dispose(); heights.Dispose();
            coarsePoints.Dispose(); coarseNormals.Dispose(); coarseHeights.Dispose();
            splats.Dispose(); coarseSplats.Dispose();
            heightBounds.Dispose(); vertexBounds.Dispose(); attributes.Dispose();
            return built;
        }

        // Index into the (R+2) x (R+2) sampled grid, and into the coarse grid of vertex (i, j)'s
        // morph target: the mesh job's own addressing.
        internal static int Sample(int i, int j, int R) => (j + 1) * (R + 2) + (i + 1);
        internal static int CoarseSample(int i, int j, int R) =>
            (((j & ~1) + 2) / 2) * ChunkMeshLayout.CoarseGridSize(R) + ((i & ~1) + 2) / 2;
    }
}
