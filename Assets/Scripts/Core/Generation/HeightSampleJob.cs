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

        [WriteOnly] public NativeArray<double3> BasePoints;
        [WriteOnly] public NativeArray<float3>  BaseNormals;
        [WriteOnly] public NativeArray<float>   Heights;

        public void Execute(int index)
        {
            int g = Resolution + 2;
            int i = index % g - 1;          // -1 .. Resolution
            int j = index / g - 1;

            ChunkGrid.VertexUV(Node, i, j, Resolution, out double u, out double v);
            SurfaceMath.SurfaceFrame(Surface, Root, u, v, out double3 p, out float3 n);

            BasePoints[index]  = p;
            BaseNormals[index] = n;
            Heights[index]     = TerrainHeightFunction.Height(p, MaxOctave, MacroSample.Zero, Params);
        }
    }

    // The same thing at the parent's band limit, on the even-indexed vertices only.
    //
    // This is the geomorph target M3 lerps towards: a depth-d chunk fully morphed becomes its
    // depth-(d-1) parent. Computed here rather than in M3 so the vertex layout is settled once.
    // At depth 0 the "parent" band is simply K0 - 1, which is well defined and unused.
    [BurstCompile(CompileSynchronously = true)]
    public struct CoarseHeightSampleJob : IJobParallelFor
    {
        public SurfaceDef   Surface;
        public RootQuad     Root;
        public NodeId       Node;
        public HeightParams Params;
        public int          MaxOctave;      // the *parent's* band limit
        public int          Resolution;

        [WriteOnly] public NativeArray<double3> BasePoints;
        [WriteOnly] public NativeArray<float3>  BaseNormals;
        [WriteOnly] public NativeArray<float>   Heights;

        public void Execute(int index)
        {
            int cg = ChunkMeshLayout.CoarseGridSize(Resolution);
            int i  = ChunkMeshLayout.CoarseToFine(index % cg);
            int j  = ChunkMeshLayout.CoarseToFine(index / cg);

            ChunkGrid.VertexUV(Node, i, j, Resolution, out double u, out double v);
            SurfaceMath.SurfaceFrame(Surface, Root, u, v, out double3 p, out float3 n);

            BasePoints[index]  = p;
            BaseNormals[index] = n;
            Heights[index]     = TerrainHeightFunction.Height(p, MaxOctave, MacroSample.Zero, Params);
        }
    }
}
