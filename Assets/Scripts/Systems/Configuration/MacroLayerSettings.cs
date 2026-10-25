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
        [Tooltip("Ruggedness is RuggednessCentre plus this times fBm, clamped to 0..1. It scales the " +
                 "mid band, the mountain band, independently of climate (M5b).")]
        public float RuggednessNoise = 1.5f;
        [Tooltip("The ruggedness the fBm varies around. 0.5 is even; lower puts more of the world " +
                 "at 0, plains, and less at 1, mountain belts. The macro map export's ruggedness " +
                 "image shows the belts.")]
        public float RuggednessCentre = 0.5f;

        [Header("Noise")]
        [Tooltip("Macro seed. Offset internally from the detail noise's, so the same value in both " +
                 "does not correlate them.")]
        public uint Seed = 7;

        [Header("Biomes")]
        [Tooltip("Up to four. Each vertex takes a weight of every biome from its temperature and " +
                 "moisture, so their borders blend rather than cut. None leaves the terrain as the " +
                 "macro layer alone makes it.")]
        public BiomeDefinition[] Biomes = new BiomeDefinition[0];
        [Tooltip("How sharply a biome's weight falls off with climate distance: the weights are " +
                 "inverse-square distance raised to this. Higher gives narrower borders.")]
        [Min(1)]
        public int BiomeSharpness = 2;
        [Tooltip("Degrees C that count as one unit of climate distance.")]
        public float TemperatureScale = 15f;
        [Tooltip("Moisture that counts as one unit of climate distance.")]
        public float MoistureScale = 0.5f;

        [Header("Octave Bands")]
        [Tooltip("Wavelength in metres dividing the detail noise's low band from its mid band. In " +
                 "metres, so a band means the same scale of feature on any world.")]
        public double LowBandEdge = 50000.0;
        [Tooltip("Wavelength in metres dividing the mid band, the one ruggedness scales, from the " +
                 "high band.")]
        public double MidBandEdge = 500.0;

        [Header("Ruggedness")]
        [Tooltip("Amplitude of the mid band where the ruggedness map is 0, on top of the biomes'. " +
                 "1 and 1 leave it alone.")]
        [Min(0f)]
        public float RuggednessMin = 1f;
        [Tooltip("Amplitude of the mid band where the ruggedness map is 1: the mountain belts. The " +
                 "LOD relief allowance has to cover it, and the relief tests measure each scene's " +
                 "demand at it.")]
        [Min(0f)]
        public float RuggednessMax = 1f;

        [Header("Continents")]
        [Tooltip("A Voronoi continent map whose height is added to the base height, and whose detail " +
                 "cutoff drops the coarse detail octaves it replaces. None means no continents.")]
        public ContinentSettings Continents;

        [Header("Erosion")]
        [Tooltip("Stream-power erosion of the top level, once, at startup (M5c). None means no erosion.")]
        public ErosionSettings Erosion;

        public MacroConfig ToConfig()
        {
            var biomes = new BiomeConfig[Biomes?.Length ?? 0];
            for (int b = 0; b < biomes.Length; b++)
            {
                if (Biomes[b] == null)
                    throw new System.ArgumentException(
                        $"MacroLayerSettings '{name}': biome slot {b} is empty.");
                biomes[b] = Biomes[b].ToConfig();
            }

            return new MacroConfig
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
                // A centre in the inspector, a shift from 0.5 at runtime (C5), so 0.5 is exact.
                RuggednessShift    = RuggednessCentre - 0.5f,
                Seed               = Seed,
                Biomes             = biomes,
                BiomeSharpness     = BiomeSharpness,
                TemperatureScale   = TemperatureScale,
                MoistureScale      = MoistureScale,
                // Amplitudes in the inspector, gains relative to one at runtime (C5).
                RuggednessGainMin  = RuggednessMin - 1f,
                RuggednessGainMax  = RuggednessMax - 1f,
                Continents         = Continents != null ? Continents.ToConfig() : (ContinentConfig?)null,
                Erosion            = Erosion != null ? Erosion.ToConfig() : (ErosionConfig?)null,
            };
        }
    }
}
