using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace Sonoma.Core.Generation
{
    // One chunk vertex, matching VertexAttributes() exactly. Sequential and blittable, so the
    // mesh job writes this struct straight into the mesh's vertex buffer with no marshalling.
    //
    // Field order is attribute order, and Unity requires a stream's attributes in the order of
    // the VertexAttribute enum -- Position, Normal, Tangent, Color, TexCoord0 .. 7 -- so COLOR
    // sits before TEXCOORD0 here even though it was added later (M5b, C6).
    [StructLayout(LayoutKind.Sequential)]
    public struct TerrainVertex
    {
        public float3 Position;      // chunk-local, relative to the node's Anchor
        public float3 Normal;
        // Biome weights, UNorm8 x 4, biome 0 in the lowest byte (Biomes.Pack). They took the
        // eight bytes of a float2 uv nothing read, so the vertex is still 80 bytes.
        public uint   Splat;
        // The geomorph target's weights: the coarse job's, at the even vertex and the parent's
        // macro level. Splat weights morph for the reason elevation does -- fully morphed, three
        // of every four child cells collapse and the survivor is drawn at the even vertices, so
        // the vertex's own weights would shade the parent's triangles with values a fine vertex
        // away, a colour pop at every swap.
        public uint   MorphSplat;
        public float4 TopoElevation; // xyz = base surface normal, w = height above it
        public float4 MorphPosition; // xyz = geomorph target, w = node depth
        // xyz = geomorph target normal, w = geomorph target elevation. The elevation rides
        // along in the spare component because the shader shades by elevation as well as
        // drawing by position: without it a fully morphed chunk is shaded with its fine
        // elevation while drawn with its coarse geometry, and the band blend shifts at the
        // swap. 80 bytes a vertex rather than 76, which is the price of that.
        public float4 MorphNormalElevation;
    }

    // The Unity-side half of the mesh layout: the vertex descriptor set and the index format.
    // The arithmetic lives in ChunkMeshLayout, which has no UnityEngine dependency so it can be
    // tested outside the Editor.
    public static class ChunkMeshBuffers
    {
        public static IndexFormat IndexFormatFor(int resolution) =>
            ChunkMeshLayout.VertexCount(resolution) > 65536 ? IndexFormat.UInt32 : IndexFormat.UInt16;

        public static VertexAttributeDescriptor[] VertexAttributes() => new[]
        {
            new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal,    VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color,     VertexAttributeFormat.UNorm8,  4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.UNorm8,  4),
            // TexCoord1 is the existing shader contract: (topoUp.xyz, elevation). The morph
            // attributes M3 needs had to move up to TexCoord2/3 because of it -- the plan had
            // pencilled them into TexCoord1 without checking the shader.
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 4),
        };

        // There is no shared index cache: GenerationScheduler builds and owns its index buffers,
        // because a static cache one TerrainRoot's teardown freed would be freed under another's
        // jobs. The tests keep theirs in ChunkJobRunner.
    }
}
