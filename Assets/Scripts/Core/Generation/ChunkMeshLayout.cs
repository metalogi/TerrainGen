using System;

namespace Sonoma.Core.Generation
{
    // Vertex counts, index counts and the triangle list for a chunk mesh.
    //
    // Deliberately free of UnityEngine: this is the arithmetic that fails silently when it is
    // wrong -- a miscounted buffer corrupts memory rather than throwing, and a reversed
    // winding just renders the world inside out -- so it is kept where it can be tested
    // without an Editor. ChunkMeshBuffers holds the Unity-side vertex layout and the
    // NativeArray cache built from this.
    public static class ChunkMeshLayout
    {
        // The sampling grid carries a one-vertex border on all four sides so edge normals can
        // be taken by central difference without querying a neighbouring chunk.
        public static int SampleGridSize(int resolution) => resolution + 2;
        public static int SampleCount(int resolution)    => SampleGridSize(resolution) * SampleGridSize(resolution);

        // Geomorph target grid: the even-indexed vertices, plus a one-cell border of its own
        // so the coarse normals are also two-sided. Coarse index c maps to fine index 2c - 2,
        // spanning -2 .. resolution + 1.
        public static int CoarseGridSize(int resolution) => (resolution - 1) / 2 + 3;
        public static int CoarseCount(int resolution)    => CoarseGridSize(resolution) * CoarseGridSize(resolution);
        public static int CoarseToFine(int c)            => 2 * c - 2;

        // Skirts are one row of duplicated vertices per edge rather than a shared ring, so
        // each edge is independent and corners are simply coincident. This is the layout the
        // prototype shipped, which is known to close the cracks it is there to close.
        //
        // Layout: [0, R*R)              main grid, row-major, index j * R + i
        //         [R*R,      R*R +  R)  South skirt
        //         [R*R +  R, R*R + 2R)  East  skirt
        //         [R*R + 2R, R*R + 3R)  North skirt
        //         [R*R + 3R, R*R + 4R)  West  skirt
        // Edge order is South, East, North, West, matching Sonoma.Core.Surface.Edge.
        public static int VertexCount(int resolution) => resolution * resolution + 4 * resolution;
        public static int SkirtBase(int resolution, int edge) => resolution * resolution + edge * resolution;

        // 2 triangles per grid cell, plus 2 per skirt quad on each of the 4 edges.
        public static int TriangleCount(int resolution) =>
            (resolution - 1) * (resolution - 1) * 2 + 8 * (resolution - 1);
        public static int IndexCount(int resolution) => TriangleCount(resolution) * 3;

        // UInt16 indices address at most 65,536 distinct vertices. That is the binding
        // constraint, not the triangle count -- SonomaRevisedPlan.md section 4.3 cites
        // R <= 181, which is where triangles reach 65,520 and constrains nothing.
        public const int MaxResolutionFor16BitIndices = 253;

        // The top vertex of skirt post k on the given edge.
        //
        // The four edges are walked anticlockwise around the chunk as seen from outside --
        // South +u, East +v, North -u, West -v -- so one triangle order serves all four. The
        // prototype walked North and West the other way; only West was ever noticed, and
        // CLAUDE.md records it as the reversed one. Re-deriving the facing shows it was
        // actually North: the prototype's West came out right precisely because it had
        // already been given the flipped triangle order.
        public static int SkirtTopIndex(int R, int edge, int k) => edge switch
        {
            0 => k,                                  // South (j = 0),   walking +u
            1 => k * R + (R - 1),                    // East  (i = R-1), walking +v
            2 => (R - 1) * R + (R - 1 - k),          // North (j = R-1), walking -u
            3 => (R - 1 - k) * R,                    // West  (i = 0),   walking -v
            _ => throw new ArgumentOutOfRangeException(nameof(edge),
                     "ChunkMeshLayout: edge must be 0..3 (South, East, North, West)."),
        };

        public static int SkirtBottomIndex(int R, int edge, int k) => SkirtBase(R, edge) + k;

        // How far a chunk's skirt hangs below its edge: an absolute floor plus a share of the
        // node. The gap a skirt has to hide in a transient two-depth state is on the order of
        // the coarse node's relief, which halves with every depth, so an absolute depth alone
        // suits one scale only -- on an Earth-sized sphere the depths run from 20 m nodes to
        // 10,400 km ones.
        //
        // In double and narrowed once, so a fraction of 0 returns skirtDepth to the bit: a
        // float plus 0.0 is exact. That is what lets SampleScene, which sets no fraction, build
        // the same meshes it always has.
        //
        // The node's share is capped at maxRelief, the largest height difference the terrain
        // has anywhere: twice TerrainHeightFunction.MaxAbsHeight, since every height lies within
        // that bound and no gap between two meshes can be deeper. Without the cap the share grows
        // with the node and outruns the terrain at coarse depths: Earth's SkirtFraction of 2.9
        // hung a depth-0 skirt 30,000 km, through the planet and 17,000 km out of the far side,
        // drawn from orbit as a fin over the near hemisphere. The cap also keeps a coarse chunk's
        // culling bounds, padded by its drop, to the terrain's size.
        public static float SkirtDrop(float skirtDepth, float skirtFraction, double nominalSize, double maxRelief)
            => (float)(skirtDepth + Math.Min(skirtFraction * nominalSize, maxRelief));

        // Grid triangulation is (i00, i11, i10), (i00, i01, i11). It is not arbitrary: M3's
        // geomorph invariant -- a fully morphed child mesh is triangle-for-triangle its
        // parent -- depends on this exact diagonal. Do not switch to a flipping scheme.
        //
        // flipWinding exists because the surfaces do not agree on handedness. Measured,
        // dot(cross(du, dv), normal) is positive on a cube-sphere and negative on a plane grid
        // and a cylinder, so one fixed winding cannot face outward on all three: with the
        // order below, plane and cylinder face outward and a cube-sphere renders inside out.
        // Pass SurfaceMath.UvFrameIsRightHanded(surface).
        public static ushort[] BuildIndices(int R, bool flipWinding)
        {
            if (R > MaxResolutionFor16BitIndices)
                throw new ArgumentOutOfRangeException(nameof(R),
                    "ChunkMeshLayout: resolution needs 32-bit indices, which are not implemented yet.");

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
