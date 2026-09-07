using System;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The LOD thresholds, asserted at the strongest level each one supports.
    //
    // Everything here is UnityEngine-free by design, so it also runs in the out-of-Editor
    // harness. That is deliberate: the numbers below are the ones that crack the world if
    // they are wrong, and they should fail in seconds rather than being spotted by eye in
    // the Game view.
    public class LodMathTests
    {
        const double EarthRadius = 6371000.0;
        const int    Resolution  = 33;
        const int    MaxDepth    = 12;
        const float  SplitFactor = 3f;
        const float  MorphStart  = 0.15f;
        const float  Hysteresis  = 1.1f;

        static SurfaceDef Sphere() => SurfaceDef.CubeSphere(EarthRadius);

        static HeightParams Params() =>
            HeightParams.Create(Sphere(), Resolution, 0.0, 20, 200f, 0.5f, 2f, 42u);

        static LodMath Lod() =>
            LodMath.Create(Sphere(), Params(), SplitFactor, MorphStart, Hysteresis, 1.5f, MaxDepth);

        // The headline guard for C1. Nesting must be *exactly* 2, not 2 within a delta:
        // the morph range of a depth-d chunk has to end precisely where its parent's
        // begins, and a last-bit discrepancy there is a last-bit gap in the mesh.
        //
        // This is why LodMath.NominalSize divides by a shifted power of two rather than by
        // math.pow -- the division is then exact in IEEE and the ratio survives intact.
        [Test]
        public void MorphRangesNestExactly()
        {
            var lod = Lod();

            for (int d = 1; d <= MaxDepth; d++)
            {
                lod.MorphRange(d,     out double start,       out double end);
                lod.MorphRange(d - 1, out double parentStart, out double parentEnd);

                Assert.AreEqual(2.0, parentEnd / end, 0.0,
                    $"morph range end at depth {d - 1} is not exactly twice depth {d}");
                Assert.AreEqual(2.0, parentStart / start, 0.0,
                    $"morph range start at depth {d - 1} is not exactly twice depth {d}");
                Assert.AreEqual(2.0, lod.SplitDistance(d - 1) / lod.SplitDistance(d), 0.0,
                    $"split distance at depth {d - 1} is not exactly twice depth {d}");
            }
        }

        // The correction M3b needed, and the check that was missing.
        //
        // A chunk must be fully morphed where its PARENT takes over -- that is the parent's
        // split distance, not its own. Its own split distance is where it hands over to its
        // *children*, and finishing the morph there means a chunk is drawing its parent's
        // geometry while its coarser neighbour, also finished, draws its grandparent's.
        // Measured with the original definition, 100% of cross-depth boundary samples came
        // out exactly 1.0 effective LOD apart: a seam along every boundary in the world, and
        // a full level of pop at every swap.
        //
        // MorphFactorIsOneAndZeroAtEveryBoundary below passes either way -- it only ever
        // evaluates the factor at `end`, and never asks whether `end` is in the right place.
        // This is the assertion that pins where.
        [Test]
        public void MorphCompletesWhereTheParentTakesOver()
        {
            var lod = Lod();

            for (int d = 1; d <= MaxDepth; d++)
            {
                lod.MorphRange(d, out _, out double end);
                Assert.AreEqual(lod.SplitDistance(d - 1), end, 0.0,
                    $"depth {d} does not finish morphing where its parent takes over");
                Assert.AreNotEqual(lod.SplitDistance(d), end,
                    $"depth {d} finishes morphing at its own split distance, which is the " +
                    "off-by-one M3b corrected");
            }

            // A root has no parent; the range is still well defined so nothing needs a
            // special case, and nothing ever replaces a root anyway.
            lod.MorphRange(0, out _, out double rootEnd);
            Assert.AreEqual(2.0 * lod.SplitDistance(0), rootEnd, 0.0);
        }

        // At a depth boundary the fine side must be fully morphed exactly where the coarse
        // side has not begun. Both are exact values, not approximations: the fine side's
        // distance equals its own range end, and the coarse side's range does not start
        // until 1.2x further out.
        [Test]
        public void MorphFactorIsOneAndZeroAtEveryBoundary()
        {
            var lod = Lod();

            for (int d = 1; d <= MaxDepth; d++)
            {
                lod.MorphRange(d, out double start, out double end);

                Assert.AreEqual(1f, lod.MorphFactor(end, d), 0f,
                    $"depth {d} is not fully morphed at its own split distance");
                Assert.AreEqual(0f, lod.MorphFactor(end, d - 1), 0f,
                    $"depth {d - 1} has already begun morphing where depth {d} finishes");

                // And the fine side has not begun at its own range start.
                Assert.AreEqual(0f, lod.MorphFactor(start, d), 0f,
                    $"depth {d} has begun morphing before its range start");
            }
        }

        // The mutation this design exists to rule out: thresholds taken from the *measured*
        // node size, which is how SonomaRevisedPlan.md section 4.4 words it.
        //
        // SurfaceMath.NodeWorldSize varies across one cube face at a fixed depth -- measured
        // here as 1.1793:1 at depth 3, 1.2908 at depth 5, 1.3279 at depth 8. Feed that to the
        // morph range and two nodes at the *same depth on the same face* disagree about how
        // far through the morph they are: at depth 8 the smallest node is at k = 1.000 where
        // the largest is still at k = 0.383. A 0.617 mismatch in morph factor is a gaping
        // crack, and nothing in the shader can recover from it.
        //
        // Note the pairing: the plan describes these as "two same-depth neighbours at a
        // shared edge", but immediately adjacent nodes differ far too little to show it --
        // their worst measured disagreement is 0.0000. The figure is the face's extremes,
        // which is what this reproduces.
        [Test]
        public void MeasuredNodeSizeWouldBreakTheBoundary()
        {
            var s     = Sphere();
            var roots = SurfaceMath.BuildRoots(s);

            // depth, measured size spread across the face, and the morph factor the largest
            // node reaches at the distance where the smallest is exactly fully morphed.
            var reference = new[] { (3, 1.1793, 0.6199), (5, 1.2908, 0.4368), (8, 1.3279, 0.3827) };

            foreach (var (depth, expectedSpread, expectedLargeK) in reference)
            {
                MeasuredSizeExtremes(s, roots[0], depth, out double smallest, out double largest);

                Assert.AreEqual(expectedSpread, largest / smallest, 1e-4,
                    $"measured node size spread at depth {depth} is not the figure C1 was derived from");

                // The distance at which the smallest node is exactly fully morphed.
                double distance = C1SplitFactor * smallest;

                Assert.AreEqual(1.0, MeasuredMorphFactor(distance, smallest), 1e-9,
                    $"the smallest depth-{depth} node should be fully morphed here");

                double large = MeasuredMorphFactor(distance, largest);
                Assert.AreEqual(expectedLargeK, large, 1e-3,
                    $"measured-size threshold at depth {depth} did not reproduce the C1 figure; " +
                    "if the disagreement has genuinely gone away, the reasoning needs revisiting " +
                    "rather than the assert relaxing");
                Assert.Less(large, 0.7,
                    $"measured-size thresholds at depth {depth} no longer disagree enough to matter");
            }
        }

        static void MeasuredSizeExtremes(in SurfaceDef s, in RootQuad root, int depth,
                                         out double smallest, out double largest)
        {
            int span = 1 << depth;
            smallest = double.MaxValue;
            largest  = 0.0;

            for (int x = 0; x < span; x++)
            for (int y = 0; y < span; y++)
            {
                double size = SurfaceMath.NodeWorldSize(s, root, new NodeId(root.Index, depth, x, y));
                smallest = math.min(smallest, size);
                largest  = math.max(largest,  size);
            }
        }

        // The configuration C1's figures were derived under, kept here rather than read from
        // the shipped defaults: the point of this test is to reproduce a specific historical
        // measurement, and it should not shift every time a default is retuned.
        const float C1SplitFactor = 2f;
        const float C1MorphStart  = 0.4f;

        // LodMath.MorphFactor with the nominal size replaced by a measured one.
        static double MeasuredMorphFactor(double distance, double measuredSize)
        {
            double end   = C1SplitFactor * measuredSize;
            double start = end * (1.0 - C1MorphStart);
            return math.saturate((distance - start) / (end - start));
        }

        // C2: frac <= 0.5 follows from requiring the fine side at k = 1 where the coarse
        // side is at k = 0. Exactly 0.5 is the real bound and must be accepted -- rejecting
        // it would be an off-by-one costing a usable configuration.
        [Test]
        public void MorphStartFractionAboveTheCeilingIsRejected()
        {
            var s = Sphere();
            var p = Params();
            double spread = SurfaceMath.MaxNodeSizeSpread(s);

            // The ceiling is the tighter of the nesting bound (0.5) and the node-extent
            // bound. On a cube sphere the second always wins.
            float ceiling = LodMath.MaxMorphStartFraction(SplitFactor, Hysteresis, spread);
            Assert.AreEqual(1.0 - (Hysteresis + spread / SplitFactor) / 2.0, ceiling, 1e-6,
                "the ceiling is not the derived expression");
            Assert.Less(ceiling, LodMath.NestingMorphStartFraction,
                "on a cube sphere the node-extent bound should be the binding one");

            Assert.DoesNotThrow(
                () => LodMath.Create(s, p, SplitFactor, ceiling, Hysteresis, 1.5f, MaxDepth),
                "the ceiling itself is the real bound and must be accepted");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => LodMath.Create(s, p, SplitFactor, ceiling + 1e-3f, Hysteresis, 1.5f, MaxDepth),
                "above the ceiling the coarse side of a boundary starts morphing before the " +
                "fine side has finished, and every boundary cracks");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => LodMath.Create(s, p, SplitFactor, 0f, Hysteresis, 1.5f, MaxDepth));

            // The shipped configuration must sit under its own ceiling, with margin. This is
            // the assertion that fails if somebody edits TerrainSettings back to the M3a
            // defaults, which are unreachable: SplitFactor 2 with hysteresis allows a morph
            // window under 1% wide.
            Assert.Less(MorphStart, ceiling,
                "the shipped MorphStartFraction is above its own ceiling");
            Assert.Less(LodMath.MaxMorphStartFraction(2f, 1.2f, spread), 0.01f,
                "SplitFactor 2 with hysteresis should be effectively unusable; if this ever " +
                "passes, the geometry has changed and the defaults deserve revisiting");

            // A plane grid has no size spread, so it gets a more generous ceiling.
            var plane = SurfaceDef.PlaneGrid(1000.0, 1, 1);
            Assert.Greater(LodMath.MaxMorphStartFraction(SplitFactor, Hysteresis,
                               SurfaceMath.MaxNodeSizeSpread(plane)), ceiling,
                "a uniformly parameterised surface should allow a wider morph window");

            // The other setup guards, in the same place and for the same reason.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => LodMath.Create(s, p, 0f,          MorphStart, Hysteresis, 1.5f, MaxDepth));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => LodMath.Create(s, p, SplitFactor, MorphStart, 0.9f,       1.5f, MaxDepth));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => LodMath.Create(s, p, SplitFactor, MorphStart, Hysteresis, 0.5f, MaxDepth));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => LodMath.Create(s, p, SplitFactor, MorphStart, Hysteresis, 1.5f, -1));
        }

        // Hysteresis is applied to the decision and to nothing else. A node parked on the
        // split distance must not flip every frame, but the morph range it feeds must be
        // untouched -- widening that with hysteresis is what stops the ranges nesting.
        [Test]
        public void SplitAndCollapseDoNotThrash()
        {
            var lod = Lod();

            for (int d = 0; d < MaxDepth; d++)
            {
                Assert.Greater(lod.CollapseDistance(d), lod.SplitDistance(d),
                    $"collapse distance at depth {d} does not exceed the split distance");

                lod.MorphRange(d, out _, out double end);
                Assert.AreEqual(2.0 * lod.SplitDistance(d), end, 0.0,
                    $"morph range end at depth {d} has picked up the hysteresis factor");
            }

            // A node oscillating across the split distance, inside the hysteresis band.
            const int depth = 6;
            double split = lod.SplitDistance(depth);
            bool  state  = false;
            int   changes = 0;

            for (int step = 0; step < 256; step++)
            {
                double distance = split * (1.0 + 0.05 * math.sin(step * 0.7));
                bool   next     = lod.ShouldSplit(distance, depth, state);
                if (next != state) changes++;
                state = next;
            }

            Assert.AreEqual(1, changes,
                "a node oscillating by 5% about the split distance should settle after one change");
            Assert.IsTrue(state, "it should settle split, not collapsed");

            // MaxDepth is a hard floor on subdivision regardless of distance.
            Assert.IsFalse(lod.ShouldSplit(0.0, MaxDepth, false));
            Assert.IsFalse(lod.ShouldSplit(0.0, MaxDepth, true));
        }

        // C3, and the substance of the geomorph invariant: a child's even vertices land on
        // its parent's vertices exactly, so the morph target a child stores is a real parent
        // vertex rather than an interpolation of one.
        //
        // Bit-identical, not merely close. Both sides evaluate the same math.lerp on dyadic
        // values, so the doubles agree to the last bit -- which only holds because R is odd,
        // making R-1 even and q(R-1) + i even for even i. HeightParams.Create enforcing an
        // odd resolution is load-bearing here, not stylistic.
        [Test]
        public void ChildEvenVerticesCoincideWithParentVertices()
        {
            var s     = Sphere();
            var roots = SurfaceMath.BuildRoots(s);
            var p     = Params();
            int R     = p.Resolution;

            for (int depth = 0; depth <= 4; depth++)
            {
                var parent = new NodeId(0, depth, (1 << depth) / 2, (1 << depth) / 3);

                for (int q = 0; q < 4; q++)
                {
                    var child = parent.Child(q);
                    int qu = q & 1, qv = (q >> 1) & 1;

                    for (int j = 0; j < R; j += 2)
                    for (int i = 0; i < R; i += 2)
                    {
                        ChunkGrid.VertexUV(child, i, j, R, out double uc, out double vc);
                        ChunkGrid.VertexUV(parent, (qu * (R - 1) + i) / 2,
                                                   (qv * (R - 1) + j) / 2, R,
                                           out double up, out double vp);

                        Assert.AreEqual(up, uc, 0.0,
                            $"depth {depth} quadrant {q} vertex ({i},{j}): u is not bit-identical");
                        Assert.AreEqual(vp, vc, 0.0,
                            $"depth {depth} quadrant {q} vertex ({i},{j}): v is not bit-identical");

                        // And therefore the height, evaluated at the parent's band limit,
                        // is bit-identical too -- which is exactly the morph target the
                        // child's coarse sample job writes into TEXCOORD2.
                        double3 pointC = SurfaceMath.SurfacePoint(s, roots[0], uc, vc);
                        double3 pointP = SurfaceMath.SurfacePoint(s, roots[0], up, vp);
                        int     octave = p.MaxOctave(parent.Depth);

                        Assert.AreEqual(TerrainHeightFunction.Height(pointP, octave, MacroSample.Zero, p),
                                        TerrainHeightFunction.Height(pointC, octave, MacroSample.Zero, p),
                                        0f,
                            $"depth {depth} quadrant {q} vertex ({i},{j}): height differs");
                    }
                }
            }
        }
    }
}
