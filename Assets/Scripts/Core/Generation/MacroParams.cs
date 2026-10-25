using System;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // One biome as configured: a climate centre, and the amplitude of the detail noise's three
    // octave bands there, each stored as a gain relative to one (C5): amplitude = 1 + gain, so
    // default(BiomeConfig) leaves the terrain as it was rather than flattening it.
    public struct BiomeConfig
    {
        public float  Temperature;   // degrees C
        public float  Moisture;      // 0..1
        public float3 Gain;          // low, mid, high band
    }

    // The macro layer's generator settings as configured: what MacroLayerSettings holds, as a
    // plain struct so pure code and the tests can build one without a ScriptableObject.
    public struct MacroConfig
    {
        public int    LevelCount;
        public double Wavelength0;          // metres, macro octave 0; 0 = one root quad across (S0)
        public float  Persistence;
        public float  BaseHeightScale;      // metres
        public float  EquatorTemperature;   // degrees C, cube sphere only
        public float  PoleTemperature;      // degrees C, cube sphere only
        public float  LapseRate;            // degrees C per kilometre of base height above zero
        public float  TemperatureNoise;     // degrees C per unit of fBm
        public float  MoistureNoise;        // moisture per unit of fBm, around 0.5
        public float  RuggednessNoise;      // ruggedness per unit of fBm, around 0.5 + RuggednessShift
        // Added to the 0.5 the ruggedness varies around, relative to it like the gains (C5), so
        // the default 0 is M5a's map to the bit. Below zero, more of the world saturates at 0
        // (plains) and less at 1 (mountain belts).
        public float  RuggednessShift;
        public uint   Seed;

        // Biomes (M5b). None leaves the band gains at zero and the terrain as M5a drew it.
        public BiomeConfig[] Biomes;            // up to Biomes.MaxBiomes
        public int           BiomeSharpness;    // inverse-square distance raised to this
        public float         TemperatureScale;  // degrees C per unit of climate distance
        public float         MoistureScale;     // moisture per unit of climate distance
        // Ruggedness scales the mid band, the mountain band, between these, as gains relative to
        // one like the biomes' (C5): 0 and 0 leave the amplitude alone.
        public float         RuggednessGainMin, RuggednessGainMax;

        // A Voronoi continent map whose height is added to the base height (Docs/Continents_Plan.md).
        // Null, the default, is none, and the maps are M5b's to the bit.
        public ContinentConfig? Continents;

        // Stream-power erosion of the top level (M5c-1). Null, the default, is none, and the maps
        // are as the generator builds them, to the bit.
        public ErosionConfig? Erosion;
    }

    // Everything the macro generator and the read need, resolved once at setup and copied into
    // jobs by value. Blittable: no reference fields.
    //
    // Enabled false is the "no macro layer" path, and it still comes with a valid one-texel map
    // (MacroMaps.Disabled): a job whose NativeArray field was never constructed fails the safety
    // check at schedule time, so the jobs always get an array and test this flag instead.
    public struct MacroParams
    {
        public MacroLayout Layout;
        public bool        Enabled;
        public bool        HasLatitude;          // cube sphere: temperature falls towards the poles
        public double      Wavelength0;
        public int         K0;                   // octave limit offset; see MaxOctave
        public float       Persistence;
        public float       BaseHeightScale;
        public float       EquatorTemperature, PoleTemperature;
        public float       LapseRate;            // degrees C per metre
        public float       TemperatureNoise, MoistureNoise, RuggednessNoise;
        public float       RuggednessCentre;     // 0.5 + MacroConfig.RuggednessShift
        public uint        Seed;

        // The biome table, flattened to a fixed four so it is blittable: component b of each
        // float4 is biome b, and only the first BiomeCount mean anything (Biomes.Classify).
        public int         BiomeCount;
        public int         BiomeSharpness;
        public float       InverseTemperatureScale, InverseMoistureScale;
        public float4      BiomeTemperature, BiomeMoisture;
        public float4      BiomeLowGain, BiomeMidGain, BiomeHighGain;
        public float       RuggednessGainMin, RuggednessGainMax;

        // Continents: the base height channel carries a continent height besides the fBm
        // (ContinentField), at most ContinentBound in magnitude. TerrainHeightFunction.MaxAbsHeight
        // adds the bound to the envelope; MacroGenerator adds the height only when the flag is set.
        public bool        HasContinents;
        public double      ContinentBound;

        // Erosion (M5c-1): the maps have a drainage array beside the texels, and the read takes
        // wetness from it. The generator itself does not read the flag, so the texels it builds
        // are the same with it or without.
        public bool        HasErosion;

        public static MacroParams Disabled => default;

        // The largest amplitude each band of the detail noise -- low, mid, high -- can take
        // anywhere: the largest biome amplitude on the band, with ruggedness at its maximum on
        // the mid band (Biomes.BandGain). The relief the LOD boundary has to cover depends on
        // them, and not in proportion: the mid band's kilometre-scale octaves cost far less
        // relief per metre of height than the fine band's. So the relief tests measure each
        // scene's C with the bands at amplitudes at least these (C7), rather than scaling one C
        // by the largest of them.
        public float3 MaxBandAmplitude
        {
            get
            {
                float rugged = 1f + RuggednessGainMax;
                if (BiomeCount == 0) return new float3(1f, rugged, 1f);
                float3 max = float.MinValue;
                for (int b = 0; b < BiomeCount; b++)
                    max = math.max(max, new float3(1f + BiomeLowGain[b], (1f + BiomeMidGain[b]) * rugged,
                                                   1f + BiomeHighGain[b]));
                return max;
            }
        }

        // The largest amplitude of any band anywhere.
        public float MaxAmplitude => math.cmax(MaxBandAmplitude);

        // The finest macro octave a level carries: every octave whose wavelength is at least
        // four of the level's texel spacings. Four rather than Nyquist's two, because
        // Catmull-Rom interpolation of a component near Nyquist rings.
        //
        // Octave k has wavelength lambda0 / 2^k and level L has texel spacing S0 / (Cells0 2^L),
        // so the condition is 2^k <= lambda0 Cells0 2^L / (4 S0), a shift by one per level --
        // the same truncation by octave HeightParams.MaxOctave does for the detail noise, and,
        // like it, read off the IEEE exponent in Create rather than from log2. At lambda0 = S0
        // and Cells0 = 32 the ratio is exactly 8, and a library log returning 2.9999999999999996
        // would drop an octave from every level. Negative means the level carries no octave at
        // all and is flat. MacroTests.MacroLevelsAreBandLimited pins it.
        public int MaxOctave(int level) => K0 + level;

        // Pyramid levels no read ever reaches at this MaxDepth: chunks read MacroLevel(depth)
        // with depth at most maxDepth, and HeightAt reads MacroLevel(maxDepth). Each level has
        // four times the texels of the one below, so with MaxDepth 3 and LevelCount 6, levels 4
        // and 5 are 93% of the memory and build time, built and never read. Not refused, since
        // nothing breaks; TerrainRoot warns, as it does for HeightParams.IsOctaveStarved.
        public int UnreadLevels(int maxDepth) => math.max(0, Layout.LevelCount - (maxDepth + 1));

        public static MacroParams Create(in SurfaceDef s, in HeightParams hp, in MacroConfig c)
        {
            if (!(c.Wavelength0 >= 0.0) || double.IsInfinity(c.Wavelength0))
                throw new ArgumentOutOfRangeException(nameof(c),
                    "MacroParams: the octave-0 wavelength must be finite and positive, or 0 for " +
                    "'one root quad across'.");
            if (!(c.Persistence > 0.0f) || c.Persistence > 1.0f)
                throw new ArgumentOutOfRangeException(nameof(c),
                    "MacroParams: persistence must be in (0, 1].");
            if (!IsFinite(c.BaseHeightScale) || !IsFinite(c.EquatorTemperature) ||
                !IsFinite(c.PoleTemperature) || !IsFinite(c.LapseRate) ||
                !IsFinite(c.TemperatureNoise) || !IsFinite(c.MoistureNoise) ||
                !IsFinite(c.RuggednessNoise) || !IsFinite(c.RuggednessShift))
                throw new ArgumentOutOfRangeException(nameof(c),
                    "MacroParams: amplitudes, temperatures and the lapse rate must be finite.");

            // Validates the level count and refuses a pyramid too big to address.
            var layout = new MacroLayout(hp.Resolution - 1, c.LevelCount, s.QuadCount);

            double lambda0 = c.Wavelength0 > 0.0 ? c.Wavelength0 : hp.S0;
            double ratio   = lambda0 * layout.Cells0 / (4.0 * hp.S0);
            if (!(ratio > 0.0) || double.IsInfinity(ratio))
                throw new ArgumentOutOfRangeException(nameof(c),
                    "MacroParams: the octave-0 wavelength is too short to compare with the surface; " +
                    "check Wavelength0 against the surface size.");
            int k0 = HeightParams.FloorLog2(ratio);

            if (k0 + c.LevelCount - 1 < 0)
                throw new ArgumentOutOfRangeException(nameof(c),
                    "MacroParams: the octave-0 wavelength is shorter than four texels of the finest " +
                    "level, so every level would be flat. Lengthen Wavelength0 or add levels.");

            int biomes = c.Biomes?.Length ?? 0;
            if (biomes > Biomes.MaxBiomes)
                throw new ArgumentOutOfRangeException(nameof(c),
                    "MacroParams: at most four biomes; the vertex carries their weights in four bytes.");
            if (!IsFinite(c.RuggednessGainMin) || !IsFinite(c.RuggednessGainMax) ||
                c.RuggednessGainMin > c.RuggednessGainMax || 1f + c.RuggednessGainMin < 0f)
                throw new ArgumentOutOfRangeException(nameof(c),
                    "MacroParams: the ruggedness range must be finite, ordered, and leave a " +
                    "non-negative amplitude.");

            float4 temperature = 0f, moisture = 0f, low = 0f, mid = 0f, high = 0f;
            if (biomes > 0)
            {
                if (c.BiomeSharpness < 1)
                    throw new ArgumentOutOfRangeException(nameof(c),
                        "MacroParams: biome sharpness must be at least 1.");
                if (!(c.TemperatureScale > 0f) || !(c.MoistureScale > 0f) ||
                    !IsFinite(c.TemperatureScale) || !IsFinite(c.MoistureScale))
                    throw new ArgumentOutOfRangeException(nameof(c),
                        "MacroParams: the climate distance scales must be finite and positive.");

                for (int b = 0; b < biomes; b++)
                {
                    var bc = c.Biomes[b];
                    if (!IsFinite(bc.Temperature) || !IsFinite(bc.Moisture) ||
                        !math.all(math.isfinite(bc.Gain)) || math.any(1f + bc.Gain < 0f))
                        throw new ArgumentOutOfRangeException(nameof(c),
                            "MacroParams: a biome's climate must be finite and its band amplitudes " +
                            "non-negative.");
                    temperature[b] = bc.Temperature;
                    moisture[b]    = bc.Moisture;
                    low[b]         = bc.Gain.x;
                    mid[b]         = bc.Gain.y;
                    high[b]        = bc.Gain.z;
                }
            }

            // The continents' own checks, here as well as in ContinentParams.Create, so the bound the
            // envelope is built from comes from a configuration the map would accept.
            c.Continents?.Validate();
            c.Erosion?.Validate();

            return new MacroParams
            {
                HasErosion              = c.Erosion.HasValue,

                HasContinents           = c.Continents.HasValue,
                ContinentBound          = c.Continents.HasValue ? c.Continents.Value.Bound : 0.0,

                BiomeCount              = biomes,
                BiomeSharpness          = c.BiomeSharpness,
                InverseTemperatureScale = biomes > 0 ? 1f / c.TemperatureScale : 0f,
                InverseMoistureScale    = biomes > 0 ? 1f / c.MoistureScale    : 0f,
                BiomeTemperature        = temperature,
                BiomeMoisture           = moisture,
                BiomeLowGain            = low,
                BiomeMidGain            = mid,
                BiomeHighGain           = high,
                RuggednessGainMin       = c.RuggednessGainMin,
                RuggednessGainMax       = c.RuggednessGainMax,

                Layout             = layout,
                Enabled            = true,
                HasLatitude        = s.Type == SurfaceType.CubeSphere,
                Wavelength0        = lambda0,
                K0                 = k0,
                Persistence        = c.Persistence,
                BaseHeightScale    = c.BaseHeightScale,
                EquatorTemperature = c.EquatorTemperature,
                PoleTemperature    = c.PoleTemperature,
                LapseRate          = c.LapseRate / 1000f,
                TemperatureNoise   = c.TemperatureNoise,
                MoistureNoise      = c.MoistureNoise,
                RuggednessNoise    = c.RuggednessNoise,
                RuggednessCentre   = 0.5f + c.RuggednessShift,
                Seed               = c.Seed,
            };
        }

        static bool IsFinite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    }
}
