using System;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // The continent map as configured (Docs/Continents_Plan.md, K9): what ContinentSettings holds,
    // as a plain struct so pure code and the tests can build one without a ScriptableObject. Sizes
    // are counts and multiples of the micro spacing, so the same values mean the same shapes on any
    // world; heights are in metres.
    public struct ContinentConfig
    {
        public uint   Seed;
        public int    MacroCells, MicroCells, ContinentCount;
        public float  DecayMin, DecayMax;      // BFS decay per step
        public float  ElevationFloor;          // the BFS stops spreading below it
        public float  ScoreNoise;              // micro score noise amplitude
        public float  LandFraction;            // land share of the map by area, not of the final terrain
        public float  WarpAmplitude, WarpWavelength, CoastWidth;   // in micro spacings
        public float  LandHeight, OceanDepth;  // metres
        public float  InteriorRise;            // land from 1 - InteriorRise/2 to 1 + InteriorRise/2 of LandHeight
        public double DetailCutoff;            // metres: detail octaves at least this long are dropped; 0 none
        public float  CoarseOctaveAmplitude;   // their amplitude, 0 dropping them

        public static ContinentConfig Default => new ContinentConfig
        {
            Seed = 1, MacroCells = 30, MicroCells = 5000, ContinentCount = 4,
            DecayMin = 0.5f, DecayMax = 0.75f, ElevationFloor = 0.1f, ScoreNoise = 0.25f,
            LandFraction = 0.3f, WarpAmplitude = 0.5f, WarpWavelength = 2f, CoastWidth = 1f,
            InteriorRise = 0.8f,
        };

        // The profile's largest magnitude (K7): land at the full interior rise, or the ocean floor.
        // E is a weighted mean of BFS elevations, all in [0, 1], and the profile is at most 1, so no
        // continent height exceeds it. MacroParams and ContinentParams both take it from here.
        public double Bound => math.max(LandHeight * (1.0 + InteriorRise / 2.0), (double)OceanDepth);

        // Everything the continent map assumes of its configuration, checked once. MacroParams.Create
        // and ContinentParams.Create both call it, so neither can accept what the other refuses.
        public void Validate()
        {
            if (MacroCells < 1 || MicroCells < 1 || ContinentCount < 1)
                throw new ArgumentOutOfRangeException(nameof(MacroCells),
                    "ContinentConfig: the cell and continent counts must be at least 1.");
            if (ContinentCount > MacroCells)
                throw new ArgumentOutOfRangeException(nameof(ContinentCount),
                    "ContinentConfig: there cannot be more continents than macro cells.");
            if (!IsFinite(DecayMin) || !IsFinite(DecayMax) || !IsFinite(ElevationFloor) ||
                !IsFinite(ScoreNoise) || !IsFinite(LandFraction) || !IsFinite(WarpAmplitude) ||
                !IsFinite(WarpWavelength) || !IsFinite(CoastWidth) || !IsFinite(LandHeight) ||
                !IsFinite(OceanDepth) || !IsFinite(InteriorRise) || !IsFinite(CoarseOctaveAmplitude) ||
                double.IsNaN(DetailCutoff) || double.IsInfinity(DetailCutoff))
                throw new ArgumentOutOfRangeException(nameof(DecayMin),
                    "ContinentConfig: every setting must be finite.");
            // Above 1 a step can beat the elevation it came from, and the BFS never settles.
            if (!(DecayMin > 0f) || DecayMin > DecayMax || DecayMax > 1f)
                throw new ArgumentOutOfRangeException(nameof(DecayMin),
                    "ContinentConfig: the BFS decay range must be ordered, in (0, 1].");
            if (ElevationFloor < 0f)
                throw new ArgumentOutOfRangeException(nameof(ElevationFloor),
                    "ContinentConfig: the elevation floor must not be negative.");
            if (!(LandFraction > 0f) || !(LandFraction < 1f))
                throw new ArgumentOutOfRangeException(nameof(LandFraction),
                    "ContinentConfig: the land fraction must be in (0, 1).");
            if (WarpAmplitude < 0f || !(WarpWavelength > 0f) || !(CoastWidth > 0f))
                throw new ArgumentOutOfRangeException(nameof(CoastWidth),
                    "ContinentConfig: the warp amplitude must not be negative, and the warp wavelength " +
                    "and the coast width must be positive.");
            // The class distances are capped at two spacings, so a profile two spacings wide or more
            // never reaches its plateau: the cell ripple the squared distance exists to remove.
            if (!(CoastWidth < 2f))
                throw new ArgumentOutOfRangeException(nameof(CoastWidth),
                    "ContinentConfig: the coast width must be under two micro spacings, the distance cap.");
            if (LandHeight < 0f || OceanDepth < 0f)
                throw new ArgumentOutOfRangeException(nameof(LandHeight),
                    "ContinentConfig: the land height and ocean depth are magnitudes, not negative.");
            // Above 2 the land multiplier goes negative where E is 0, and the bound no longer holds.
            if (InteriorRise < 0f || InteriorRise > 2f)
                throw new ArgumentOutOfRangeException(nameof(InteriorRise),
                    "ContinentConfig: the interior rise must be in [0, 2].");
            if (DetailCutoff < 0.0 || CoarseOctaveAmplitude < 0f)
                throw new ArgumentOutOfRangeException(nameof(DetailCutoff),
                    "ContinentConfig: the detail cutoff and the coarse octaves' amplitude must not be negative.");
        }

        static bool IsFinite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    }

    // Everything the continent map, the field and their jobs need, resolved once at setup and
    // copied into jobs by value. Blittable. Lengths are metres here: the micro spacing D is the
    // side of a square of the surface's area over MicroCells, and every size in the configuration
    // is a multiple of it.
    public struct ContinentParams
    {
        public bool   Enabled;
        public int    MacroCells, MicroCells, ContinentCount;
        public double MicroSpacing;      // D
        public double MacroSpacing;      // the same over MacroCells
        public double Cap2;              // the class distances' cap, (2D)^2
        public double CoastWidth;        // metres: the profile's half-width
        public double WarpAmplitude;     // metres
        public double WarpWavelength;    // metres
        public double LandHeight, OceanDepth, InteriorRise;
        public double Bound;             // ContinentConfig.Bound
        public double DecayMin, DecayMax, ElevationFloor, ScoreNoise, LandFraction;
        public uint   PlacementSeed;     // Unity.Mathematics.Random.CreateFromIndex
        public uint   WarpSeed;          // three LatticeNoise channels, a stride apart
        public uint   ScoreSeed;

        public static ContinentParams None => default;

        // The largest squared distance to a seed of a point's own class at which the profile is
        // still exactly on its plateau, where the other class is beyond the cap (K2):
        // s = (Cap2 - d^2) / 2D must reach CoastWidth. At CoastWidth D that is d <= sqrt(2) D.
        public double PlateauLimit2 => Cap2 - 2.0 * MicroSpacing * CoastWidth;

        // The detail noise seeds its octaves Seed, Seed + 1, ..., and the macro generator its
        // channels from 0x6D2B79F5 a stride apart; the continent channels start elsewhere, so the
        // same configured seed in all three correlates none of them.
        const uint SeedOffset    = 0x2545F491u;
        public const uint ChannelStride = 0x00010000u;

        // Resolves the configuration's continents for this surface, or None if it has none. The
        // macro parameters must have been made from the same configuration: they carry the height
        // bound LodVertical's envelope is built from, and it must bound this field.
        public static ContinentParams Create(in SurfaceDef s, in MacroParams mp, in MacroConfig c)
        {
            if (!c.Continents.HasValue)
            {
                if (mp.HasContinents)
                    throw new ArgumentException("ContinentParams: the macro parameters have continents and the configuration none.");
                return None;
            }

            var k = c.Continents.Value;
            k.Validate();
            if (!mp.HasContinents || mp.ContinentBound != k.Bound)
                throw new ArgumentException("ContinentParams: the macro parameters were made from a different configuration.");

            double area = SurfaceArea(s);
            double d    = math.sqrt(area / k.MicroCells);
            uint   seed = k.Seed * 0x9E3779B9u + SeedOffset;

            return new ContinentParams
            {
                Enabled        = true,
                MacroCells     = k.MacroCells,
                MicroCells     = k.MicroCells,
                ContinentCount = k.ContinentCount,
                MicroSpacing   = d,
                MacroSpacing   = math.sqrt(area / k.MacroCells),
                Cap2           = 4.0 * d * d,
                CoastWidth     = k.CoastWidth * d,
                WarpAmplitude  = k.WarpAmplitude * d,
                WarpWavelength = k.WarpWavelength * d,
                LandHeight     = k.LandHeight,
                OceanDepth     = k.OceanDepth,
                InteriorRise   = k.InteriorRise,
                Bound          = k.Bound,
                DecayMin       = k.DecayMin,
                DecayMax       = k.DecayMax,
                ElevationFloor = k.ElevationFloor,
                ScoreNoise     = k.ScoreNoise,
                LandFraction   = k.LandFraction,
                PlacementSeed  = seed,
                WarpSeed       = seed + ChannelStride,
                ScoreSeed      = seed + 4u * ChannelStride,
            };
        }

        // A top-level macro texel's edge: the root's nominal size S0 is its diagonal
        // (SurfaceMath.NodeWorldSize), so an edge is S0 / sqrt 2, over the top level's cells. The
        // one spacing the startup warnings (K10), the tests that assert them and the macro map
        // export count micro cells and the cutoff in.
        public static double TopTexelSpacing(double s0, in MacroLayout layout)
            => s0 / math.SQRT2_DBL / layout.Cells(layout.LevelCount - 1);

        // How many top-level texels a micro cell is across: at least four, or the pyramid's band
        // limit aliases the coastline (K10).
        public double MicroCellTexels(double s0, in MacroLayout layout) => MicroSpacing / TopTexelSpacing(s0, layout);

        // The base surface's area, analytically: the micro spacing is defined from it.
        public static double SurfaceArea(in SurfaceDef s) => s.Type switch
        {
            SurfaceType.CubeSphere => 4.0 * math.PI_DBL * s.Radius * s.Radius,
            SurfaceType.Cylinder   => 2.0 * math.PI_DBL * s.Radius * s.Length,
            _                      => s.Cols * s.Rows * s.TileSize * s.TileSize,
        };
    }
}
