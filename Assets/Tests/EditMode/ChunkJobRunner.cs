using System.Collections.Generic;
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
            public ChunkKind        Kind => (ChunkKind)(int)HeightBounds[ChunkMeshJob.KindSlot];

            // What the sample jobs produced, for checks made independently of the mesh job.
            public double3[] BasePoints, CoarseBasePoints;
            public float3[]  BaseNormals, CoarseBaseNormals;
            public float[]   Heights, CoarseHeights;
        }

        // beforeOcean builds the mesh as main did before ocean-a instead: FrozenChunkMeshJob, with the
        // buffers, indices and submesh set outside the job, as GenerationScheduler set them then.
        internal static Built Build(in SurfaceDef surface, RootQuad[] roots, in HeightParams hp, NodeId node,
                                    MacroMaps macro, float skirtDepth = 0f, float skirtFraction = 0f,
                                    float chordSag = 0f, OceanParams ocean = default, float waterSkirtDrop = 0f,
                                    bool beforeOcean = false)
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
            var heightBounds  = new NativeArray<float>(ChunkMeshJob.HeightBoundsCount, Allocator.TempJob);
            var vertexBounds  = new NativeArray<float3>(2,        Allocator.TempJob);
            var attributes    = new NativeArray<VertexAttributeDescriptor>(
                                    ChunkMeshBuffers.VertexAttributes(), Allocator.TempJob);
            var indices       = Indices(R, flip);
            var doubled       = ocean.Enabled ? Indices(R, flip, true) : indices;

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

            if (beforeOcean)
            {
                // Main's counts and main's index buffer, from FrozenLayout, not the live layout the
                // other path reads.
                data.SetVertexBufferParams(FrozenLayout.VertexCount(R), attributes);
                data.SetIndexBufferParams(FrozenLayout.IndexCount(R), IndexFormat.UInt16);
                data.subMeshCount = 1;
                new FrozenChunkMeshJob
                {
                    Resolution = R, Anchor = anchor, Depth = node.Depth,
                    SkirtDepth = skirtDepth, SkirtFraction = skirtFraction, ChordSag = chordSag,
                    BasePoints = basePoints, BaseNormals = baseNormals, Heights = heights, Splats = splats,
                    CoarseBasePoints = coarsePoints, CoarseBaseNormals = coarseNormals,
                    CoarseHeights = coarseHeights, CoarseSplats = coarseSplats,
                    Mesh = data, HeightBounds = heightBounds, VertexBounds = vertexBounds,
                }.Schedule().Complete();
                data.GetIndexData<ushort>().CopyFrom(FrozenLayout.BuildIndices(R, flip));
                data.SetSubMesh(0, new SubMeshDescriptor(0, FrozenLayout.IndexCount(R)),
                                MeshUpdateFlags.DontRecalculateBounds);
            }
            else
            {
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
            }

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

        // The index buffers the mesh job copies, built once per resolution, winding and kind for the
        // tests. GenerationScheduler builds and owns its own; this cache lived in ChunkMeshBuffers
        // until nothing but the tests read it (PR #20's second review, finding 5). Tests that run the
        // jobs call DisposeIndices in their teardown.
        static readonly Dictionary<int, NativeArray<ushort>> s_indices = new Dictionary<int, NativeArray<ushort>>();

        internal static NativeArray<ushort> Indices(int resolution, bool flipWinding, bool doubled = false)
        {
            int key = resolution * 4 + (flipWinding ? 2 : 0) + (doubled ? 1 : 0);
            if (s_indices.TryGetValue(key, out var cached) && cached.IsCreated)
                return cached;

            var idx = new NativeArray<ushort>(ChunkMeshLayout.BuildIndices(resolution, flipWinding, doubled),
                                              Allocator.Persistent);
            s_indices[key] = idx;
            return idx;
        }

        internal static void DisposeIndices()
        {
            foreach (var kv in s_indices)
                if (kv.Value.IsCreated) kv.Value.Dispose();
            s_indices.Clear();
        }

        // Index into the (R+2) x (R+2) sampled grid, and into the coarse grid of vertex (i, j)'s
        // morph target: the mesh job's own addressing, written out again here rather than called,
        // so the tests' independent checks do not share the rule they check.
        internal static int Sample(int i, int j, int R) => (j + 1) * (R + 2) + (i + 1);
        internal static int CoarseSample(int i, int j, int R) =>
            (((j & ~1) + 2) / 2) * ChunkMeshLayout.CoarseGridSize(R) + ((i & ~1) + 2) / 2;
    }
}
