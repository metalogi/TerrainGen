using System;
using Sonoma.Core.Surface;

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

        // With an ocean, a Coast chunk holds its terrain and its water, 2(R^2 + 4R) vertices, so
        // R^2 + 4R <= 32,768: 32,757 at R = 179, and 33,485 at the next odd resolution, 181.
        // OceanParams.Create refuses more; HeightParams.Create cannot see the ocean.
        public const int MaxResolutionWithOcean = 179;

        // Per kind (Docs/Ocean_A_Plan.md, A7). A Land chunk is today's terrain mesh; an Ocean
        // chunk is the water alone, laid out as a terrain mesh is, so it takes the same counts and
        // the single index buffer; a Coast chunk is the terrain at [0, n) and the water at
        // [n, 2n), drawn by the doubled buffer as two submeshes. The one-argument VertexCount and
        // IndexCount stay the terrain grid's.
        public static int VertexCount(int resolution, ChunkKind kind) =>
            kind == ChunkKind.Coast ? 2 * VertexCount(resolution) : VertexCount(resolution);

        public static int IndexCount(int resolution, ChunkKind kind) =>
            kind == ChunkKind.Coast ? 2 * IndexCount(resolution) : IndexCount(resolution);

        // Where a chunk's water vertices start: after the terrain in a Coast chunk, at 0 in an
        // Ocean one. A Land chunk has none.
        public static int WaterBase(int resolution, ChunkKind kind) => kind switch
        {
            ChunkKind.Coast => VertexCount(resolution),
            ChunkKind.Ocean => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(kind),
                     "ChunkMeshLayout: a Land chunk has no water vertices."),
        };

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

        // How far a chunk's skirt hangs below its edge: an absolute floor, a share of the
        // chunk's own relief (max - min over its R x R grid, which ChunkMeshJob measures before
        // it writes the skirt), and the surface's chord sag at its depth, whole.
        //
        // A skirt covers a neighbour up to SkirtDepthGap depths coarser, at any morph factor on
        // either side. The gap there is the detail one side has and the other lacks, plus the
        // coarse side's chords cutting under the curve. The first is a fraction of the relief
        // of whichever side has to cover it, measured at real chunk edges
        // (StressSceneTests.SkirtCoversTheTransientGap); the second is geometry, so it is added
        // whole rather than scaled by a fraction tuned to the terrain. A plain gets a short skirt
        // and a mountainside a long one, where a share of the node gave every chunk at a depth
        // the mountain's.
        //
        // In double and narrowed once, so a fraction of 0 and a sag of 0 return skirtDepth to the
        // bit: a float plus 0.0 is exact. GenerationScheduler passes no sag to a scene that sets
        // no fraction, so SampleScene keeps its absolute skirt and builds the same meshes it
        // always has.
        //
        // There is no cap. The node-relative rule before this one needed a cap at twice
        // TerrainHeightFunction.MaxAbsHeight: Earth's fraction of 2.9 node sizes hung a depth-0
        // skirt 30,000 km, through the planet and out of the far side as a fin seen from orbit.
        // A measured relief can never exceed that bound, so capping the relief term does
        // nothing, and capping the whole drop would clip the chord sag where it is largest and
        // reopen the curvature gaps there. StressSceneTests.NoSkirtReachesPastTheCentre holds
        // the longest drop the rule can hang under every stress scene's radius.
        public static float SkirtDrop(float skirtDepth, float skirtFraction, double relief, double chordSag)
            => (float)(skirtDepth + skirtFraction * relief + chordSag);

        // The water's skirt (A8): the same rule with no relief, since the water has none. What is
        // left is the floor and the chord sag, whole -- on a smooth surface the sag is the whole
        // gap. No wave term until ocean-b. With skirts off both arguments are zero, as for the
        // terrain, and so is the drop.
        public static float WaterSkirtDrop(float skirtDepth, float skirtFraction, double chordSag)
            => SkirtDrop(skirtDepth, skirtFraction, 0.0, chordSag);

        // How many depths coarser a neighbour can be and still be covered by a skirt. Three,
        // because the atomic swap can hold a parent at coarse LOD, waiting on its children,
        // while its neighbour is already three depths finer.
        public const int SkirtDepthGap = 3;

        // The part of the gap that is the surface's curvature, not the terrain's: how far a
        // neighbour's straight edge segments can cut under the curve this chunk's vertices sit
        // on (over it, inside a cylinder, where the normal points at the axis). A neighbour g
        // depths coarser, fully morphed to its own parent, draws segments 2^(g+1) of this chunk's
        // vertex spacings long -- 16 at SkirtDepthGap -- and a chord c of a circle of curvature k
        // sags k c^2 / 8. Spacing is the nominal one times the surface's node-size spread, an
        // upper bound. Zero on a plane. On a smooth sphere it is the whole gap.
        public static double ChordSag(double curvature, double sizeSpread, double nominalSize, int resolution)
        {
            double chord = (2 << SkirtDepthGap) * sizeSpread * nominalSize / (resolution - 1);
            return 0.125 * curvature * chord * chord;
        }

        // The same for a surface, where GenerationScheduler and the tests both get it: the
        // curvature is the radius's inverse on a cube sphere or a cylinder (in its curved
        // direction, the larger one), and the spread is SurfaceMath.MaxNodeSizeSpread's.
        public static double ChordSag(in SurfaceDef s, double nominalSize, int resolution)
            => ChordSag(s.Type == SurfaceType.PlaneGrid ? 0.0 : 1.0 / s.Radius,
                        SurfaceMath.MaxNodeSizeSpread(s), nominalSize, resolution);

        // The sag a chunk's skirt adds, as the mesh job receives it: none for a scene with no
        // SkirtFraction, which keeps an absolute skirt to the bit, and the whole ChordSag, in
        // float, otherwise. The one rule GenerationScheduler and the tests both call.
        public static float SkirtChordSag(in SurfaceDef s, float skirtFraction, double nominalSize, int resolution)
            => skirtFraction > 0f ? (float)ChordSag(s, nominalSize, resolution) : 0f;

        // Grid triangulation is (i00, i11, i10), (i00, i01, i11). It is not arbitrary: M3's
        // geomorph invariant -- a fully morphed child mesh is triangle-for-triangle its
        // parent -- depends on this exact diagonal. Do not switch to a flipping scheme.
        //
        // flipWinding exists because the surfaces do not agree on handedness. Measured,
        // dot(cross(du, dv), normal) is positive on a cube-sphere and negative on a plane grid
        // and a cylinder, so one fixed winding cannot face outward on all three: with the
        // order below, plane and cylinder face outward and a cube-sphere renders inside out.
        // Pass SurfaceMath.UvFrameIsRightHanded(surface).
        public static ushort[] BuildIndices(int R, bool flipWinding) => BuildIndices(R, flipWinding, false);

        // doubled is a Coast chunk's buffer (A7): the single buffer, then the single buffer again
        // with every index offset by n, so the water winds as the terrain does on every topology.
        // Its 16-bit limit is on 2n vertices, R 179 rather than 253.
        public static ushort[] BuildIndices(int R, bool flipWinding, bool doubled)
        {
            if (R > MaxResolutionFor16BitIndices)
                throw new ArgumentOutOfRangeException(nameof(R),
                    "ChunkMeshLayout: resolution needs 32-bit indices, which are not implemented yet.");
            if (doubled && 2 * VertexCount(R) > 65536)
                throw new ArgumentOutOfRangeException(nameof(R),
                    "ChunkMeshLayout: a Coast chunk at this resolution needs 32-bit indices; with an ocean R is at most 179.");

            int single = IndexCount(R);
            var idx = new ushort[doubled ? 2 * single : single];
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

            if (doubled)
            {
                int n = VertexCount(R);
                for (int k = 0; k < single; k++)
                    idx[single + k] = (ushort)(idx[k] + n);
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
