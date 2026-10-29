using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // Samples the base surface and the height function over a chunk's grid.
    //
    // The grid is (R+2) x (R+2): the chunk's own R x R vertices plus a one-vertex border on
    // every side. The border is what lets the mesh job take two-sided central differences at
    // the chunk edge without knowing anything about its neighbours.
    //
    // Inside a root quad that is exact: the height function is pure and ChunkGrid.VertexUV
    // gives the border sample bit-identical (u, v) to the adjacent chunk's own vertex, so
    // edge normals agree by construction rather than by stitching -- measured 1.9e-6 degrees.
    //
    // Across a cube face it is only close. The border continues *this* face's tangent-adjusted
    // parameterisation past its own edge instead of crossing onto the neighbour's, so the
    // central difference is not centred: at v = -1/32 the offsets from the edge are -0.1067
    // and +0.0982 in face-local b, and the neighbouring face makes the mirrored error. The
    // measured worst normal disagreement over all 24 links is 0.151 degrees at Earth radius.
    // It is geometric, not terrain-driven -- at HeightScale 0, on a perfect sphere, it is
    // still 0.1503 degrees -- so it does not shrink with gentler terrain. Vertex *positions*
    // are unaffected (1.3 nm), so this is a shading discontinuity along the 12 cube edges and
    // not a crack. HeightFunctionTests.CrossFaceEdgeNormalsAgreeToABoundedAngle pins the bound.
    [BurstCompile(CompileSynchronously = true)]
    public struct HeightSampleJob : IJobParallelFor
    {
        public SurfaceDef   Surface;
        public RootQuad     Root;
        public NodeId       Node;
        public HeightParams Params;
        public int          MaxOctave;
        public int          Resolution;

        // The macro maps. Enabled false reads nothing and passes MacroSample.Zero, but the
        // array is always a constructed one (MacroMaps.Disabled). The level is not a field:
        // it is MacroLayout.MacroLevel(Node.Depth), derived below from the node the job already
        // carries, so no caller can leave it at a default 0 and quietly read level 0.
        public MacroParams  Macro;
        [ReadOnly] public NativeArray<float4> MacroTexels;
        [ReadOnly] public NativeArray<float2> MacroDrainage;   // MacroMaps.Drainage, read only with erosion

        [WriteOnly] public NativeArray<double3> BasePoints;
        [WriteOnly] public NativeArray<float3>  BaseNormals;
        [WriteOnly] public NativeArray<float>   Heights;
        // Biome weights, packed as the vertex carries them (Biomes.Pack).
        [WriteOnly] public NativeArray<uint>    Splats;

        public void Execute(int index)
        {
            int g = Resolution + 2;
            int i = index % g - 1;          // -1 .. Resolution
            int j = index / g - 1;

            ChunkGrid.VertexUV(Node, i, j, Resolution, out double u, out double v);
            SurfaceMath.SurfaceFrame(Surface, Root, u, v, out double3 p, out float3 n);

            BasePoints[index]  = p;
            BaseNormals[index] = n;
            // At the vertex's own (quad, u, v), not its point: the maps are addressed by the
            // parameterisation, which is what makes the read exact on texels and bit-identical
            // across a shared edge (MacroSampler).
            int level = Macro.Layout.MacroLevel(Node.Depth);
            // Wetness included, at this job's level, so the coarse job's morph target is still the
            // parent's (M5c, E6).
            var macro = TerrainHeightFunction.Macro(new MacroMapReader(MacroTexels, MacroDrainage), Macro, Params,
                                                    Node.Quad, level, u, v, MaxOctave, out float4 weights);
            Heights[index] = TerrainHeightFunction.Height(p, MaxOctave, macro, Params);
            Splats[index]  = Biomes.Pack(weights);
        }
    }

    // The same thing at the parent's band limit, on the even-indexed vertices only.
    //
    // This is the geomorph target M3 lerps towards: a depth-d chunk fully morphed becomes its
    // depth-(d-1) parent. Computed here rather than in M3 so the vertex layout is settled once.
    // At depth 0 the "parent" band is simply K0 - 1. It is well defined, and drawn: roots morph
    // towards it from far out (LodMath.MorphRange(0)).
    [BurstCompile(CompileSynchronously = true)]
    public struct CoarseHeightSampleJob : IJobParallelFor
    {
        public SurfaceDef   Surface;
        public RootQuad     Root;
        public NodeId       Node;
        public HeightParams Params;
        public int          MaxOctave;      // the *parent's* band limit
        public int          Resolution;

        // The macro maps, read at the *parent's* level, MacroLayout.MacroLevel(Node.Depth - 1),
        // so the morph target is the parent's macro as well as its octaves. Derived below rather
        // than passed in, so the parent-level rule lives here and every caller, the tests
        // included, exercises it rather than copying it: reading the chunk's own level instead
        // would put the child's finest macro octave into the morph target, and every swap up to
        // the top level would pop by it -- about 190 m at depth 1 on Earth.
        //
        // At depth 0 the coarse border reaches two texels past the edge, the furthest any read
        // goes, and the stored border is three texels so that read lands on a texel rather than
        // extrapolating (MacroLayout).
        public MacroParams  Macro;
        [ReadOnly] public NativeArray<float4> MacroTexels;
        [ReadOnly] public NativeArray<float2> MacroDrainage;   // MacroMaps.Drainage, read only with erosion

        [WriteOnly] public NativeArray<double3> BasePoints;
        [WriteOnly] public NativeArray<float3>  BaseNormals;
        [WriteOnly] public NativeArray<float>   Heights;
        // Biome weights, packed as the vertex carries them (Biomes.Pack).
        [WriteOnly] public NativeArray<uint>    Splats;

        public void Execute(int index)
        {
            int cg = ChunkMeshLayout.CoarseGridSize(Resolution);
            int i  = ChunkMeshLayout.CoarseToFine(index % cg);
            int j  = ChunkMeshLayout.CoarseToFine(index / cg);

            ChunkGrid.VertexUV(Node, i, j, Resolution, out double u, out double v);
            SurfaceMath.SurfaceFrame(Surface, Root, u, v, out double3 p, out float3 n);

            BasePoints[index]  = p;
            BaseNormals[index] = n;
            int level = Macro.Layout.MacroLevel(Node.Depth - 1);
            // Wetness included, at this job's level, so the coarse job's morph target is still the
            // parent's (M5c, E6).
            var macro = TerrainHeightFunction.Macro(new MacroMapReader(MacroTexels, MacroDrainage), Macro, Params,
                                                    Node.Quad, level, u, v, MaxOctave, out float4 weights);
            Heights[index] = TerrainHeightFunction.Height(p, MaxOctave, macro, Params);
            Splats[index]  = Biomes.Pack(weights);
        }
    }
}
