using System;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // Everything the height function needs, resolved once at setup and then copied into
    // jobs by value. Blittable: no reference fields.
    //
    // Create() runs on the main thread and may allocate; MaxOctave() runs inside jobs and
    // is integer arithmetic only.
    public struct HeightParams
    {
        public double S0;                 // nominal size of a depth-0 node, in world metres
        public double OctaveWavelength0;  // world wavelength of octave 0, in metres
        public int    Resolution;         // vertices per chunk edge; odd
        public int    OctaveCount;
        public int    K0;                 // band-limit offset; see Create
        public float  HeightScale;
        public float  Persistence;
        public float  Lacunarity;
        public uint   Seed;

        // Octaves representable at this depth's vertex spacing.
        //
        // Octave k has wavelength lambda0 / 2^k; a chunk at depth d has nominal vertex
        // spacing s_d = S0 / (2^d * (R-1)), and Nyquist admits octave k while
        // lambda_k >= 2*s_d. The 2^d factors out exactly, leaving a shift of the octave
        // window by one per depth -- so this is a clamp on d + K0 and not an approximation.
        //
        // Critically this depends on *depth*, never on SurfaceMath.NodeWorldSize. The
        // actual size of a node varies across a cube face by up to 1.33:1 at a fixed depth
        // (measured 1.2509 at depth 4, 1.3320 at depth 10), so two same-depth neighbours
        // deriving their band limit from their own measured size can straddle a floor()
        // boundary, take different octave counts, evaluate different functions and crack
        // along the edge they share. Depth is the same integer on both sides by
        // construction. HeightFunctionTests.BandLimitIsDepthOnly guards this.
        public int MaxOctave(int depth) => math.clamp(depth + K0, 0, OctaveCount - 1);

        // No MaxAbsHeight helper here on purpose. An analytic bound on |Height| is easy --
        // HeightScale times the geometric series in Persistence -- but it is about 2.6x the
        // measured maximum, and the quantity the LOD boundary actually cares about is the
        // *relief across a node*, not the absolute elevation. See LodMath.MaxHalfRelief and
        // LodSelector.NodeDistance: padding a bounding sphere by absolute elevation is what
        // made deep nodes 21x oversized, and bounding fbm relief in advance is what made a
        // Create-time check refuse perfectly good worlds.

        public static HeightParams Create(in SurfaceDef s, int resolution, double octaveWavelength0,
                                          int octaveCount, float heightScale, float persistence,
                                          float lacunarity, uint seed)
        {
            // Resolution must be odd so (R-1)/2 is exact: the morph-target sub-grid M3 needs
            // is the even-indexed vertices, and an even R leaves it half a vertex out of step.
            if (resolution < 3 || (resolution & 1) == 0)
                throw new ArgumentOutOfRangeException(nameof(resolution),
                    "HeightParams: chunk resolution must be odd and at least 3.");
            // The mesh side cannot address more than 65,536 distinct vertices, and 32-bit
            // indices are not implemented. Checked here rather than left to BuildIndices,
            // which only runs at the first upload -- by then the jobs have allocated their
            // buffers and thrown from a path that cannot free them.
            if (resolution > ChunkMeshLayout.MaxResolutionFor16BitIndices)
                throw new ArgumentOutOfRangeException(nameof(resolution),
                    "HeightParams: chunk resolution needs more than 16-bit mesh indices, " +
                    "which are not implemented. See ChunkMeshLayout.MaxResolutionFor16BitIndices.");
            if (octaveCount < 1)
                throw new ArgumentOutOfRangeException(nameof(octaveCount),
                    "HeightParams: octave count must be at least 1.");
            if (!(persistence > 0.0f) || persistence > 1.0f)
                throw new ArgumentOutOfRangeException(nameof(persistence),
                    "HeightParams: persistence must be in (0, 1].");
            if (!(lacunarity >= 1.0f))
                throw new ArgumentOutOfRangeException(nameof(lacunarity),
                    "HeightParams: lacunarity must be at least 1.");
            if (octaveWavelength0 < 0.0)
                throw new ArgumentOutOfRangeException(nameof(octaveWavelength0),
                    "HeightParams: octave-0 wavelength must be positive, or 0 for 'one root quad across'.");

            // S0 is the same for every root of a given surface -- all six cube faces are
            // congruent, as is every tile of a plane grid -- so one value describes the
            // whole surface.
            var    roots = SurfaceMath.BuildRoots(s);
            double s0    = SurfaceMath.NodeWorldSize(s, roots[0], new NodeId(0, 0, 0, 0));

            double lambda0 = octaveWavelength0 > 0.0 ? octaveWavelength0 : s0;

            return new HeightParams
            {
                S0                = s0,
                OctaveWavelength0 = lambda0,
                Resolution        = resolution,
                OctaveCount       = octaveCount,
                K0                = FloorLog2(lambda0 * (resolution - 1) / (2.0 * s0)),
                HeightScale       = heightScale,
                Persistence       = persistence,
                Lacunarity        = lacunarity,
                Seed              = seed,
            };
        }

        // floor(log2(x)) read straight off the IEEE exponent.
        //
        // Not math.log2: K0 lands on an exact power of two for every sensible configuration
        // (lambda0 = S0 with R = 33 gives log2(16)), and a library log that returns
        // 3.9999999999999996 there would quietly drop an octave from every chunk in the
        // world. Reading the exponent field is exact by construction.
        static int FloorLog2(double x)
        {
            if (!(x > 0.0) || double.IsInfinity(x))
                throw new ArgumentOutOfRangeException(nameof(x),
                    "HeightParams: band-limit ratio must be finite and positive.");

            ulong bits = math.asulong(x);
            int   exp  = (int)((bits >> 52) & 0x7FFul) - 1023;

            // Subnormals (exp field 0) would need a different path. A ratio that small means
            // octave 0 is already finer than the coarsest chunk can represent, which is a
            // configuration error rather than something to round.
            if (exp == -1023)
                throw new ArgumentOutOfRangeException(nameof(x),
                    "HeightParams: band-limit ratio is subnormal; check OctaveWavelength0 against the surface size.");

            return exp;
        }
    }
}
