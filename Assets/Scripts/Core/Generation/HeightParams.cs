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

        // The detail noise's octave bands, which biomes scale separately (Biomes.BandGain):
        // octaves 0 .. LowBandEnd-1 are the low band, LowBandEnd .. MidBandEnd-1 the mid band,
        // the rest the high band. Both zero, as Create leaves them, puts every octave in the high
        // band; WithBands sets them from wavelengths.
        public int    LowBandEnd;
        public int    MidBandEnd;

        // The band boundaries as wavelengths in metres, so a band means the same scale of
        // feature on any planet: each band end is the number of octaves at least that long.
        // Octave k has wavelength OctaveWavelength0 / 2^k, so the count is
        // floor(log2(OctaveWavelength0 / edge)) + 1, read off the exponent as K0 is. On Earth,
        // 50 km and 500 m put octaves 0-7 in the low band and 8-14 in the mid band.
        public HeightParams WithBands(double lowEdge, double midEdge)
        {
            if (!(midEdge > 0.0) || !(lowEdge > midEdge) || double.IsInfinity(lowEdge))
                throw new ArgumentOutOfRangeException(nameof(lowEdge),
                    "HeightParams: band edges must be finite wavelengths with the low edge above the mid.");

            var p = this;
            p.LowBandEnd = BandEnd(lowEdge);
            p.MidBandEnd = BandEnd(midEdge);
            return p;
        }

        int BandEnd(double edge) => math.clamp(FloorLog2(OctaveWavelength0 / edge) + 1, 0, OctaveCount);

        // Close-up roughness (C10): octaves 0 .. CrossoverOctave-1 fall off by Persistence and
        // the finer ones by FinePersistence. Create sets FinePersistence to Persistence, which
        // makes the crossover irrelevant and the sum bit-identical to the single-slope one.
        public float  FinePersistence;
        public int    CrossoverOctave;

        // The crossover as a wavelength in metres, like the band edges: octaves at least that
        // long keep Persistence. Counted by exponent, so an octave exactly at the crossover
        // stays coarse however the division rounds.
        public HeightParams WithRoughness(float finePersistence, double crossover)
        {
            if (!(finePersistence > 0.0f) || finePersistence > 1.0f)
                throw new ArgumentOutOfRangeException(nameof(finePersistence),
                    "HeightParams: fine persistence must be in (0, 1].");
            if (!(crossover > 0.0) || double.IsInfinity(crossover))
                throw new ArgumentOutOfRangeException(nameof(crossover),
                    "HeightParams: the roughness crossover must be a finite, positive wavelength.");

            var p = this;
            p.FinePersistence = finePersistence;
            p.CrossoverOctave = BandEnd(crossover);
            return p;
        }

        // The continental cutoff (Docs/Continents_Plan.md, K4): octaves 0 .. FirstOctave-1 are the
        // continental band, which a continent map in the macro layer carries instead, and their
        // amplitude is 1 + CoarseGain, relative to one like the band gains (C5). CoarseGain -1
        // drops them, and LatticeNoise.Fbm then skips them rather than multiplying by zero. Create
        // leaves both at 0, which is no cutoff at all.
        public int    FirstOctave;
        public float  CoarseGain;

        // The cutoff as a wavelength in metres, like the band edges: every octave at least that
        // long is in the continental band, counted by exponent, so an octave exactly at the cutoff
        // is dropped however the division rounds. A cutoff of 0 is none, and leaves the gain at 0
        // too, so the height function keeps its original path.
        //
        // The same octaves go at every depth, so a coarse evaluation is still a strict prefix of a
        // finer one and the geomorph target is still the parent. MaxOctave is unchanged: where it
        // falls below FirstOctave the detail is exactly zero and the macro layer carries everything.
        public HeightParams WithContinentalCutoff(double cutoff, float coarseAmplitude)
        {
            if (!(cutoff >= 0.0) || double.IsInfinity(cutoff))
                throw new ArgumentOutOfRangeException(nameof(cutoff),
                    "HeightParams: the continental cutoff must be a finite wavelength, or 0 for none.");
            if (!(coarseAmplitude >= 0.0f) || float.IsInfinity(coarseAmplitude))
                throw new ArgumentOutOfRangeException(nameof(coarseAmplitude),
                    "HeightParams: the continental band's amplitude must be finite and non-negative.");

            var p = this;
            // Not BandEnd(0): OctaveWavelength0 / 0 is infinite, which FloorLog2 refuses.
            p.FirstOctave = cutoff > 0.0 ? BandEnd(cutoff) : 0;
            p.CoarseGain  = p.FirstOctave > 0 ? coarseAmplitude - 1f : 0f;
            return p;
        }

        // The gully term's slope damping (M5c, E6, G1): octaves GullyStartOctave and finer are
        // divided by 1 + GullyDamping (1 + WetFloorDamping x wetness) |g|^2, g the slope of the
        // octaves before them (LatticeNoise.Fbm). GullyDamping 0, as Create leaves it, is off, and
        // the height function then keeps the path it had without it. Topology says which way is
        // up for the slope's tangent projection; Create sets it.
        public int         GullyStartOctave;
        public float       GullyDamping;
        public float       WetFloorDamping;
        public SurfaceType Topology;

        // The start as a wavelength in metres, like the band edges: every octave no longer than it
        // is damped, counted by exponent, so an octave exactly at the start is damped however the
        // division rounds. Both factors must be non-negative, which keeps the damping at most 1
        // and so MaxAbsHeight a bound.
        public HeightParams WithGullies(float damping, double startWavelength, float wetFloorDamping)
        {
            if (!(damping >= 0.0f) || float.IsInfinity(damping))
                throw new ArgumentOutOfRangeException(nameof(damping),
                    "HeightParams: gully damping must be finite and non-negative.");
            if (!(startWavelength > 0.0) || double.IsInfinity(startWavelength))
                throw new ArgumentOutOfRangeException(nameof(startWavelength),
                    "HeightParams: the gully start must be a finite, positive wavelength.");
            if (!(wetFloorDamping >= 0.0f) || float.IsInfinity(wetFloorDamping))
                throw new ArgumentOutOfRangeException(nameof(wetFloorDamping),
                    "HeightParams: wet floor damping must be finite and non-negative.");

            var p = this;
            p.GullyDamping     = damping;
            p.WetFloorDamping  = damping > 0f ? wetFloorDamping : 0f;
            p.GullyStartOctave = LongerThan(startWavelength);
            return p;
        }

        // The number of octaves strictly longer than the wavelength: BandEnd counts those at least
        // as long, so where the ratio is an exact power of two the octave at the edge is dropped.
        // Read off the exponent and mantissa, never a log, for K0's reason.
        int LongerThan(double edge)
        {
            double ratio = OctaveWavelength0 / edge;
            if (!(ratio > 1.0)) return 0;
            bool exact = (math.asulong(ratio) & 0x000FFFFFFFFFFFFFul) == 0ul;
            return math.clamp(FloorLog2(ratio) + (exact ? 0 : 1), 0, OctaveCount);
        }

        // Whether the gully term changes anything.
        public bool HasGullies => GullyDamping > 0f;

        // A vector along the surface normal at a base-surface point, for the gully term's tangent
        // projection: not unit, which the projection does not need. A function of the point alone,
        // so it agrees wherever positions do.
        public double3 SurfaceUp(in double3 surfacePos) => Topology switch
        {
            SurfaceType.CubeSphere => surfacePos,
            SurfaceType.Cylinder   => new double3(surfacePos.x, surfacePos.y, 0.0),
            _                      => new double3(0.0, 1.0, 0.0),
        };

        // The amplitude of octave k relative to octave 0, as the sum applies it. For the tests
        // and the roughness figures; the sum itself multiplies as it goes. The fall-off only:
        // neither the band gains nor the continental band's gain are in it.
        public double OctaveAmplitude(int k)
        {
            double amp = 1.0;
            for (int o = 0; o < k; o++) amp *= o + 1 < CrossoverOctave ? Persistence : FinePersistence;
            return amp;
        }

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

        // Whether this depth's band limit is set by OctaveCount rather than by its vertex
        // spacing -- the chunk could carry a finer octave, but there is none to give it.
        //
        // Not an error, and nothing refuses it, but at depth it is the one configuration
        // mistake that looks like a rendering bug. On an Earth-sized sphere at resolution 33,
        // K0 is 4, so depth 19 wants octave 23; at OctaveCount 20 it clamps to 19, whose
        // wavelength is 19.8 m against 0.62 m vertex spacing. The last four depths then
        // subdivide perfectly smooth ground, 32 vertices to the finest feature, which reads as
        // "the detail levels aren't working". TerrainRoot warns at startup when MaxDepth is
        // starved.
        public bool IsOctaveStarved(int depth) => depth + K0 > OctaveCount - 1;

        // The analytic bound on |Height| -- HeightScale times the geometric series in
        // Persistence, about 2.6x the measured maximum -- is TerrainHeightFunction.MaxAbsHeight,
        // with the band gains and the macro layer. It is for what needs a true bound everywhere:
        // LodVertical's envelope and the cap on a skirt's drop. It is the wrong tool for the LOD
        // boundary, which cares about the *relief across a node*, not the absolute elevation.
        // See LodMath.MaxHalfRelief and LodSelector.NodeDistance: padding a bounding sphere by
        // absolute elevation is what made deep nodes 21x oversized, and bounding fbm relief in
        // advance is what made a Create-time check refuse perfectly good worlds.

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
                FinePersistence   = persistence,
                CrossoverOctave   = octaveCount,
                Topology          = s.Type,
            };
        }

        // floor(log2(x)) read straight off the IEEE exponent.
        //
        // Not math.log2: K0 lands on an exact power of two for every sensible configuration
        // (lambda0 = S0 with R = 33 gives log2(16)), and a library log that returns
        // 3.9999999999999996 there would quietly drop an octave from every chunk in the
        // world. Reading the exponent field is exact by construction.
        internal static int FloorLog2(double x)
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
