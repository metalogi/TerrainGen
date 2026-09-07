using System;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Quadtree
{
    // The LOD thresholds, resolved once at setup and then copied by value -- the same shape
    // as HeightParams, and for the same reason: the arithmetic that decides where a chunk
    // splits and where it morphs must be reproducible, cheap, and testable on its own.
    //
    // No UnityEngine here on purpose (the TerrainSettings overload of Create lives in
    // LodMathSettings.cs), so this file compiles and runs in the out-of-Editor test harness.
    // That is the ChunkMeshLayout / ChunkMeshBuffers split again, and it is what turns a
    // threshold bug into a test failure instead of a crack somebody has to spot by eye.
    public partial struct LodMath
    {
        public double S0;                  // nominal size of a depth-0 node, in world metres
        public double SizeSpread;          // SurfaceMath.MaxNodeSizeSpread for this surface
        public float  SplitFactor;         // split when distance < SplitFactor * S_d
        public float  MorphStartFraction;  // morph begins this fraction back from the morph end
        public float  Hysteresis;          // collapse at SplitDistance * this; >= 1
        public float  PreloadFactor;       // request children this much further out than the split
        public int    MaxDepth;

        // The ceiling that follows from the ranges having to nest: see Create. It is the
        // weaker of the two bounds; MaxMorphStartFraction is the one that actually binds.
        public const float NestingMorphStartFraction = 0.5f;

        // The largest MorphStartFraction that keeps every LOD boundary closed.
        //
        // A depth-d chunk reaches k = 1 at end_d = SplitDistance(d-1). At a boundary the
        // coarse side must still be at k = 0 there, i.e. every point of a coarse leaf must
        // be nearer than its own start. A coarse leaf's finer neighbour exists because their
        // shared parent-level node split, so that node's bounding-sphere distance is under
        // SplitDistance(d) * Hysteresis -- and a point of it can be a whole sphere diameter
        // further again, which is SizeSpread nominal node sizes. Hence
        //
        //     max(distance / SplitDistance(d)) <= Hysteresis + SizeSpread / SplitFactor
        //     start_d >= that, and start_d = 2 * SplitDistance(d) * (1 - frac), so
        //     frac <= 1 - (Hysteresis + SizeSpread / SplitFactor) / 2
        //
        // Measured against a simulated selection this is correct and slightly conservative
        // (predicted 0.204 against 0.224 measured at SplitFactor 4 on a cube sphere).
        //
        // The practical consequence is that SplitFactor 2 is not usable with a per-vertex
        // morph: on a cube sphere with any hysteresis at all it allows a morph window under
        // 1% wide, which is a pop by another name. LodBoundaryAgreement pins all of this.
        public static float MaxMorphStartFraction(float splitFactor, float hysteresis, double sizeSpread)
            => (float)math.min(NestingMorphStartFraction,
                               1.0 - (hysteresis + sizeSpread / splitFactor) / 2.0);

        // Nominal node size at a depth: S0 / 2^depth.
        //
        // Nominal, never SurfaceMath.NodeWorldSize. The measured size of a node varies across
        // a cube face at a fixed depth (1.18:1 at depth 3, 1.33:1 at depth 8), so thresholds
        // derived from it do not nest by exactly 2 -- measured, end_{d-1} / end_d lands
        // anywhere in 1.28..1.69. Nesting must be exactly 2 or the morph ranges of adjacent
        // depths stop meeting, and two same-depth neighbours at a shared edge compute
        // different morph factors: measured at depth 8, k = 1.000 on one side and k = 0.383
        // on the other, which is a gaping crack.
        //
        // This is the same rule as HeightParams.MaxOctave taking a depth rather than a size.
        // The *distance* still uses the node's real bounding sphere; only the threshold is
        // depth-only. LodMathTests.MorphRangesNestExactly guards it.
        // The divisor is built by shifting rather than by math.pow, so it is an exact power
        // of two and the division is exact in IEEE. That is what makes end_{d-1} / end_d
        // come out as literally 2.0 rather than 1.9999999999999998, which the nesting test
        // asserts without a delta.
        public double NominalSize(int depth) => S0 / (double)(1L << math.clamp(depth, 0, 62));

        public double SplitDistance(int depth)   => SplitFactor * NominalSize(depth);

        // How far out a child is requested ahead of the split, so it is usually resident
        // before the camera crosses the split distance and the swap has nothing to wait for.
        public double PreloadDistance(int depth) => PreloadFactor * SplitDistance(depth);

        // Distances over which a depth-`depth` chunk morphs towards its parent.
        //
        // `end` is the distance at which this node is REPLACED BY ITS PARENT, which is the
        // parent's split distance -- not the node's own. That distinction is the whole
        // mechanism. A node's own split distance is where it hands over to its *children*,
        // and a chunk fully morphed there would be drawing its parent's geometry while its
        // coarser neighbour, also fully morphed, draws its grandparent's: every boundary in
        // the world one LOD level apart, and every swap a full level of pop.
        //
        // SonomaRevisedPlan.md section 4.4 and the M3 plan both say "end_d is the split
        // distance of depth d"; measured, that puts 100% of cross-depth boundary samples
        // exactly 1.0 effective LOD apart. Corrected during M3b, and pinned by
        // LodMathTests.MorphCompletesWhereTheParentTakesOver.
        //
        // Written as 2 * SplitDistance(depth) rather than SplitDistance(depth - 1) so depth 0
        // needs no special case: a root has no parent, and morphing it is harmless because
        // nothing ever replaces it.
        public void MorphRange(int depth, out double start, out double end)
        {
            end   = 2.0 * SplitDistance(depth);
            start = end * (1.0 - MorphStartFraction);
        }

        // saturate((distance - start_d) / (end_d - start_d)).
        //
        // Per vertex in the shader, not per chunk: two chunks sharing a vertex compute the
        // same world position, hence the same distance, hence the same k. That is what makes
        // the shared vertex land in the same place from both sides.
        public float MorphFactor(double distance, int depth)
        {
            MorphRange(depth, out double start, out double end);
            double span = end - start;
            if (!(span > 0.0)) return distance >= end ? 1f : 0f;
            return (float)math.saturate((distance - start) / span);
        }

        // Hysteresis applies to the *decision* only, never to the morph range -- widening the
        // range with it would stop the ranges nesting, which is the one thing C1 is about.
        public bool ShouldSplit(double distance, int depth, bool currentlySplit)
        {
            if (depth >= MaxDepth) return false;
            double split = SplitDistance(depth);
            return currentlySplit ? distance <= split * Hysteresis : distance < split;
        }

        public double CollapseDistance(int depth) => SplitDistance(depth) * Hysteresis;

        public static LodMath Create(in SurfaceDef surface, in HeightParams p, float splitFactor,
                                     float morphStartFraction, float hysteresis,
                                     float preloadFactor, int maxDepth)
            => Create(p.S0, SurfaceMath.MaxNodeSizeSpread(surface), splitFactor, morphStartFraction,
                      hysteresis, preloadFactor, maxDepth);

        public static LodMath Create(double s0, double sizeSpread, float splitFactor,
                                     float morphStartFraction, float hysteresis,
                                     float preloadFactor, int maxDepth)
        {
            if (!(s0 > 0.0) || double.IsInfinity(s0))
                throw new ArgumentOutOfRangeException(nameof(s0),
                    "LodMath: the depth-0 node size must be finite and positive.");
            if (!(splitFactor > 0f))
                throw new ArgumentOutOfRangeException(nameof(splitFactor),
                    "LodMath: split factor must be positive.");
            if (maxDepth < 0)
                throw new ArgumentOutOfRangeException(nameof(maxDepth),
                    "LodMath: maximum depth cannot be negative.");
            if (!(hysteresis >= 1f))
                throw new ArgumentOutOfRangeException(nameof(hysteresis),
                    "LodMath: hysteresis must be at least 1; below 1 a node collapses closer " +
                    "than it splits and oscillates every frame.");
            if (!(preloadFactor >= 1f))
                throw new ArgumentOutOfRangeException(nameof(preloadFactor),
                    "LodMath: preload factor must be at least 1, or children are requested " +
                    "only after the split has already been decided.");
            if (!(sizeSpread >= 1.0) || double.IsInfinity(sizeSpread))
                throw new ArgumentOutOfRangeException(nameof(sizeSpread),
                    "LodMath: node size spread must be at least 1; see SurfaceMath.MaxNodeSizeSpread.");

            // Two bounds, and the tighter one wins. Nesting alone requires frac <= 0.5: the
            // fine side reaches k = 1 at end_d while the coarse side stays at 0 until
            // start_{d-1} = 2*end_d*(1 - frac). Node extent then tightens it further, because
            // a coarse leaf's own patch reaches past its bounding-sphere distance -- see
            // MaxMorphStartFraction. Refused here rather than shipped, because the symptom is
            // a hairline seam along every LOD boundary that is very easy to blame on
            // something else.
            float ceiling = MaxMorphStartFraction(splitFactor, hysteresis, sizeSpread);
            if (!(morphStartFraction > 0f) || morphStartFraction > ceiling)
                throw new ArgumentOutOfRangeException(nameof(morphStartFraction),
                    "LodMath: morph start fraction is above what this split factor, hysteresis " +
                    "and surface allow, so the coarse side of a LOD boundary would start " +
                    "morphing before the fine side has finished. Raise SplitFactor, lower " +
                    "HysteresisFactor, or lower MorphStartFraction; see " +
                    "LodMath.MaxMorphStartFraction for the ceiling.");

            return new LodMath
            {
                S0                 = s0,
                SizeSpread         = sizeSpread,
                SplitFactor        = splitFactor,
                MorphStartFraction = morphStartFraction,
                Hysteresis         = hysteresis,
                PreloadFactor      = preloadFactor,
                MaxDepth           = maxDepth,
            };
        }
    }
}
