using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // Buffer arithmetic and triangle winding. Both fail silently when wrong: a miscounted
    // buffer corrupts memory rather than throwing, and a reversed winding just renders the
    // world inside out.
    public class ChunkMeshLayoutTests
    {
        [Test]
        public void VertexAndIndexCountsMatchTheTable()
        {
            // Docs/M2_Generation_Plan.md section C7, restated so a layout change has to be a
            // deliberate edit in two places.
            (int R, int samples, int coarse, int verts, int tris)[] table =
            {
                (33,   1225,  361,  1221,  2304),
                (65,   4489, 1225,  4485,  8704),
                (129, 17161, 4489, 17157, 33792),
            };

            foreach (var row in table)
            {
                Assert.AreEqual(row.samples,  ChunkMeshLayout.SampleCount(row.R),   $"R={row.R} sample count");
                Assert.AreEqual(row.coarse,   ChunkMeshLayout.CoarseCount(row.R),   $"R={row.R} coarse count");
                Assert.AreEqual(row.verts,    ChunkMeshLayout.VertexCount(row.R),   $"R={row.R} vertex count");
                Assert.AreEqual(row.tris,     ChunkMeshLayout.TriangleCount(row.R), $"R={row.R} triangle count");
                Assert.AreEqual(row.tris * 3, ChunkMeshLayout.IndexCount(row.R),    $"R={row.R} index count");
            }
        }

        [Test]
        public void SixteenBitIndicesReachResolution253()
        {
            // The constraint is distinct vertices, not triangles. SonomaRevisedPlan.md
            // section 4.3 says "16-bit for R <= 181", which is where the triangle count
            // reaches 65,520 -- a limit on nothing.
            Assert.LessOrEqual(ChunkMeshLayout.VertexCount(253), 65536, "R = 253 should fit 16-bit indices");
            Assert.Greater(ChunkMeshLayout.VertexCount(255), 65536,     "R = 255 should not");
            Assert.AreEqual(253, ChunkMeshLayout.MaxResolutionFor16BitIndices);
        }

        // The ceiling has to be enforced where configuration is validated, not where the index
        // buffer is finally built. BuildIndices only runs at the first upload, by which point
        // the jobs have allocated their persistent buffers and the scheduler has already
        // dropped the Pending that owns them -- so the throw leaks rather than reports.
        [Test]
        public void HeightParamsRejectsResolutionsThatNeed32BitIndices()
        {
            var s = SurfaceDef.CubeSphere(6371000.0);

            HeightParams Create(int resolution) =>
                HeightParams.Create(s, resolution, 0.0, 20, 200f, 0.5f, 2f, 42u);

            Assert.Throws<ArgumentOutOfRangeException>(() => Create(255),
                "a resolution past the 16-bit index ceiling must be refused at setup");
            Assert.Throws<ArgumentOutOfRangeException>(() => Create(1001));

            // The largest legal resolution still goes through, so the bound is the real one
            // and not an off-by-one that quietly costs a usable configuration.
            Assert.DoesNotThrow(() => Create(ChunkMeshLayout.MaxResolutionFor16BitIndices),
                "resolution 253 is within the 16-bit ceiling and must be accepted");
            Assert.DoesNotThrow(() => ChunkMeshLayout.BuildIndices(
                ChunkMeshLayout.MaxResolutionFor16BitIndices, false),
                "HeightParams and BuildIndices must agree on where the ceiling is");
        }

        [Test]
        public void CoarseGridCoversEveryEvenVertexPlusABorder()
        {
            const int R = 33;
            int      cg = ChunkMeshLayout.CoarseGridSize(R);

            Assert.AreEqual(-2,    ChunkMeshLayout.CoarseToFine(0),      "coarse grid should start one cell before the chunk");
            Assert.AreEqual(R + 1, ChunkMeshLayout.CoarseToFine(cg - 1), "coarse grid should end one cell after the chunk");

            // Every even chunk vertex must map to an interior coarse sample, so its morph
            // target has coarse neighbours on all four sides for a two-sided normal.
            for (int i = 0; i < R; i += 2)
            {
                int ci = ((i & ~1) + 2) / 2;
                Assert.AreEqual(i, ChunkMeshLayout.CoarseToFine(ci), $"even vertex {i} maps to the wrong coarse sample");
                Assert.Greater(ci, 0,   $"even vertex {i} has no coarse neighbour below");
                Assert.Less(ci, cg - 1, $"even vertex {i} has no coarse neighbour above");
            }
        }

        [Test]
        public void SkirtPostsWalkEachEdgeExactlyOnce()
        {
            const int R = 33;

            for (int edge = 0; edge < 4; edge++)
            {
                var seen = new HashSet<int>();
                for (int k = 0; k < R; k++)
                {
                    int top = ChunkMeshLayout.SkirtTopIndex(R, edge, k);
                    Assert.IsTrue(seen.Add(top), $"edge {edge} visits grid vertex {top} twice");
                    Assert.Less(top, R * R,      $"edge {edge} post {k} is not a grid vertex");

                    int i = top % R, j = top / R;
                    bool onEdge = edge switch
                    {
                        0 => j == 0,          // South
                        1 => i == R - 1,      // East
                        2 => j == R - 1,      // North
                        _ => i == 0,          // West
                    };
                    Assert.IsTrue(onEdge, $"edge {edge} post {k} (vertex {i},{j}) is not on that edge");

                    int bottom = ChunkMeshLayout.SkirtBottomIndex(R, edge, k);
                    Assert.GreaterOrEqual(bottom, R * R, "skirt vertices live after the grid");
                    Assert.Less(bottom, ChunkMeshLayout.VertexCount(R), "skirt vertex out of range");
                }
            }
        }

        [Test]
        public void EveryIndexIsInRangeAndNoTriangleIsDegenerate()
        {
            foreach (int R in new[] { 5, 33, 65 })
            foreach (bool flip in new[] { false, true })
            {
                var idx   = ChunkMeshLayout.BuildIndices(R, flip);
                int verts = ChunkMeshLayout.VertexCount(R);

                Assert.AreEqual(ChunkMeshLayout.IndexCount(R), idx.Length, $"R={R}: wrong index count");

                for (int t = 0; t < idx.Length; t += 3)
                {
                    int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                    Assert.Less(a, verts, $"R={R} flip={flip}: index out of range at {t}");
                    Assert.Less(b, verts, $"R={R} flip={flip}: index out of range at {t}");
                    Assert.Less(c, verts, $"R={R} flip={flip}: index out of range at {t}");
                    Assert.IsFalse(a == b || b == c || a == c,
                        $"R={R} flip={flip}: degenerate triangle at {t} ({a},{b},{c})");
                }
            }
        }

        [Test]
        public void EveryGridVertexIsReferencedByATriangle()
        {
            const int R = 33;
            var idx  = ChunkMeshLayout.BuildIndices(R, false);
            var used = new HashSet<int>(idx.Length);
            foreach (ushort v in idx) used.Add(v);

            for (int v = 0; v < ChunkMeshLayout.VertexCount(R); v++)
                Assert.IsTrue(used.Contains(v), $"vertex {v} is in the buffer but no triangle uses it");
        }

        [Test]
        public void FlippingWindingReversesEveryTriangle()
        {
            const int R = 33;
            var normal  = ChunkMeshLayout.BuildIndices(R, false);
            var flipped = ChunkMeshLayout.BuildIndices(R, true);

            for (int t = 0; t < normal.Length; t += 3)
            {
                Assert.AreEqual(normal[t],     flipped[t],     $"first vertex changed at {t}");
                Assert.AreEqual(normal[t + 1], flipped[t + 2], $"winding not reversed at {t}");
                Assert.AreEqual(normal[t + 2], flipped[t + 1], $"winding not reversed at {t}");
            }
        }

        [Test]
        public void UvFrameHandednessMatchesTheTable()
        {
            // SurfaceMath.UvFrameIsRightHanded is a hand-written switch, so derive the answer
            // numerically and compare -- the same guard CubeAdjacencyTests puts on the edge
            // table. Getting it wrong renders a whole topology inside out, which is exactly
            // what would have happened to the cube-sphere: the prototype's triangulation was
            // written for a plane, and the two have opposite handedness.
            void Check(SurfaceDef s, string name)
            {
                var roots = SurfaceMath.BuildRoots(s);
                const double e = 1e-4;

                foreach (var root in roots)
                {
                    SurfaceMath.SurfaceFrame(s, root, 0.5, 0.5, out double3 p0, out float3 n);
                    double3 pu = SurfaceMath.SurfacePoint(s, root, 0.5 + e, 0.5);
                    double3 pv = SurfaceMath.SurfacePoint(s, root, 0.5, 0.5 + e);

                    double sign = math.dot(math.cross(pu - p0, pv - p0), (double3)n);
                    Assert.AreNotEqual(0.0, sign, $"{name} quad {root.Index}: degenerate frame");
                    Assert.AreEqual(SurfaceMath.UvFrameIsRightHanded(s), sign > 0.0,
                        $"{name} quad {root.Index}: measured handedness {sign:E3} disagrees with the table");
                }
            }

            Check(SurfaceDef.CubeSphere(1000.0),          "CubeSphere");
            Check(SurfaceDef.PlaneGrid(100.0, 2, 2),      "PlaneGrid");
            Check(SurfaceDef.Cylinder(1000.0, 4000.0, 8), "Cylinder");
        }

        [Test]
        public void GridTrianglesFaceOutwardOnEveryTopology()
        {
            // The end-to-end statement of the handedness rule: build the first grid triangle
            // the way ChunkMeshLayout orders it, and check it faces along the surface normal.
            void Check(SurfaceDef s, string name)
            {
                bool flip  = SurfaceMath.UvFrameIsRightHanded(s);
                var  roots = SurfaceMath.BuildRoots(s);
                var  idx   = ChunkMeshLayout.BuildIndices(5, flip);
                var  root  = roots[0];

                // Triangle 0 is (i00, i11, i10) on the 5x5 grid, possibly reversed.
                double3 P(int v) => SurfaceMath.SurfacePoint(s, root, (v % 5) / 4.0, (v / 5) / 4.0);

                double3 a = P(idx[0]), b = P(idx[1]), c = P(idx[2]);
                float3  n = SurfaceMath.SurfaceNormal(s, root, 0.125, 0.125);

                // Unity's convention: the face normal of (a, b, c) is cross(b - a, c - a).
                Assert.Greater(math.dot(math.cross(b - a, c - a), (double3)n), 0.0,
                    $"{name}: the first grid triangle faces inward");
            }

            Check(SurfaceDef.CubeSphere(1000.0),          "CubeSphere");
            Check(SurfaceDef.PlaneGrid(100.0, 2, 2),      "PlaneGrid");
            Check(SurfaceDef.Cylinder(1000.0, 4000.0, 8), "Cylinder");
        }
    }
}
