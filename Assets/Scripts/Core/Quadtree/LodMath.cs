using System;
using Unity.Mathematics;
using Sonoma.Core.Generation;

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
        public float  SplitFactor;         // split when distance < SplitFactor * S_d
        public float  MorphStartFraction;  // morph begins this fraction back from the split distance
        public float  Hysteresis;          // collapse at SplitDistance * this; >= 1
        public float  PreloadFactor;       // request children this much further out than the split
        public int    MaxDepth;

        // The largest MorphStartFraction that still lets the two sides of a depth boundary
        // agree. See Create for the derivation; it is not a taste value.
        public const float MaxMorphStartFraction = 0.5f;

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

        // Distances over which a depth-`depth` chunk morphs towards its parent. `end` is the
        // split distance, so a chunk is fully morphed exactly where its parent takes over.
        public void MorphRange(int depth, out double start, out double end)
        {
            end   = SplitDistance(depth);
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

        public static LodMath Create(in HeightParams p, float splitFactor, float morphStartFraction,
                                     float hysteresis, float preloadFactor, int maxDepth)
            => Create(p.S0, splitFactor, morphStartFraction, hysteresis, preloadFactor, maxDepth);

        public static LodMath Create(double s0, float splitFactor, float morphStartFraction,
                                     float hysteresis, float preloadFactor, int maxDepth)
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
            // At a depth boundary the fine side must reach k = 1 exactly where the coarse
            // side is still at k = 0. Fine reaches k = 1 at end_d; coarse stays at 0 until
            // start_{d-1} = 2*end_d*(1 - frac). So 1 <= 2(1 - frac), i.e. frac <= 0.5.
            // Anything above that cracks at every LOD boundary in the world, so it is
            // refused here rather than shipped.
            if (!(morphStartFraction > 0f) || morphStartFraction > MaxMorphStartFraction)
                throw new ArgumentOutOfRangeException(nameof(morphStartFraction),
                    "LodMath: morph start fraction must be in (0, 0.5]. Above 0.5 the fine " +
                    "side of a depth boundary reaches full morph before the coarse side has " +
                    "started, and every LOD boundary cracks.");

            return new LodMath
            {
                S0                 = s0,
                SplitFactor        = splitFactor,
                MorphStartFraction = morphStartFraction,
                Hysteresis         = hysteresis,
                PreloadFactor      = preloadFactor,
                MaxDepth           = maxDepth,
            };
        }
    }
}
