using UnityEngine;
using Sonoma.Core.Generation;

namespace Sonoma.Systems.Configuration
{
    // The macro layer's generator: a pyramid of maps per root quad, built once at startup, whose
    // base height is added to the terrain and whose climate fields M5b's biomes are classified
    // from. A TerrainSettings with no MacroLayerSettings has no macro layer, and generates M4's
    // terrain to the bit.
    //
    // Validated once, by MacroParams.Create, when the terrain starts.
    [CreateAssetMenu(fileName = "MacroLayerSettings", menuName = "Sonoma/Macro Layer Settings")]
    public class MacroLayerSettings : ScriptableObject
    {
        [Header("Pyramid")]
        [Tooltip("Levels per root quad. Level L has (ChunkResolution - 1) x 2^L texel cells across " +
                 "the quad, so its texel spacing is depth L's vertex spacing: chunks up to depth " +
                 "LevelCount - 1 read their own level exactly, on texels, and deeper ones interpolate " +
                 "the top level. Memory grows four-fold per level -- at resolution 33, four levels on a " +
                 "cube sphere are 563,736 texels, 8.6 MB.")]
        [Min(1)]
        public int LevelCount = 4;

        [Header("Base Height")]
        [Tooltip("World-space wavelength of macro octave 0, in metres. 0 means 'one root quad " +
                 "across'. Each level carries every octave of at least four of its texel spacings.")]
        public double Wavelength0 = 0.0;
        [Tooltip("Amplitude falloff per macro octave, in (0, 1].")]
        public float Persistence = 0.5f;
        [Tooltip("Base height per unit of fBm, in metres. It adds to the terrain's relief, which the " +
                 "LOD relief allowance has to cover; the selector's end-of-session report of the " +
                 "roughest chunk is the check.")]
        public float BaseHeightScale = 3000f;

        [Header("Climate")]
        [Tooltip("Temperature at zero base height on the equator, in degrees C -- or everywhere, on " +
                 "a surface with no poles (a plane grid or cylinder).")]
        public float EquatorTemperature = 30f;
        [Tooltip("Temperature at zero base height at the poles, in degrees C. Cube sphere only; " +
                 "the latitude term is (y / r)^2, with +Y the polar axis.")]
        public float PoleTemperature = -30f;
        [Tooltip("Cooling with base height above zero, in degrees C per kilometre.")]
        public float LapseRate = 6.5f;
        [Tooltip("Temperature noise, in degrees C per unit of fBm.")]
        public float TemperatureNoise = 5f;
        [Tooltip("Moisture is 0.5 plus this times fBm, clamped to 0..1.")]
        public float MoistureNoise = 1.5f;
        [Tooltip("Ruggedness is 0.5 plus this times fBm, clamped to 0..1. It scales detail " +
                 "amplitude independently of climate (M5b).")]
        public float RuggednessNoise = 1.5f;

        [Header("Noise")]
        [Tooltip("Macro seed. Offset internally from the detail noise's, so the same value in both " +
                 "does not correlate them.")]
        public uint Seed = 7;

        public MacroConfig ToConfig() => new MacroConfig
        {
            LevelCount         = LevelCount,
            Wavelength0        = Wavelength0,
            Persistence        = Persistence,
            BaseHeightScale    = BaseHeightScale,
            EquatorTemperature = EquatorTemperature,
            PoleTemperature    = PoleTemperature,
            LapseRate          = LapseRate,
            TemperatureNoise   = TemperatureNoise,
            MoistureNoise      = MoistureNoise,
            RuggednessNoise    = RuggednessNoise,
            Seed               = Seed,
        };
    }
}
