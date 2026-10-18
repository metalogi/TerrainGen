using System;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
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
        public float  RuggednessNoise;      // ruggedness per unit of fBm, around 0.5
        public uint   Seed;
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
        public uint        Seed;

        public static MacroParams Disabled => default;

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
                !IsFinite(c.RuggednessNoise))
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

            return new MacroParams
            {
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
                Seed               = c.Seed,
            };
        }

        static bool IsFinite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    }
}
