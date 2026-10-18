using Unity.Mathematics;
using UnityEngine;
using Sonoma.Core.Generation;

namespace Sonoma.Systems.Configuration
{
    // One biome: where it sits in climate, how it scales the detail noise, and its colour.
    // A MacroLayerSettings lists up to four. Biomes are classified per vertex from the macro
    // layer's temperature and moisture (Biomes.Classify), never stored in the maps.
    [CreateAssetMenu(fileName = "Biome", menuName = "Sonoma/Biome Definition")]
    public class BiomeDefinition : ScriptableObject
    {
        [Tooltip("Ground colour under the biome material (the _SONOMA_BIOMES keyword), pushed to " +
                 "the shader as _SonomaBiomeColors. Textures per biome wait for M7.")]
        public Color Color = new Color(0.35f, 0.5f, 0.25f, 1f);

        [Header("Climate Centre")]
        [Tooltip("The temperature this biome is centred on, in degrees C.")]
        public float Temperature = 15f;
        [Tooltip("The moisture this biome is centred on, 0..1.")]
        [Range(0f, 1f)]
        public float Moisture = 0.5f;

        [Header("Band Amplitudes")]
        [Tooltip("Amplitude of the detail noise's low band (wavelengths above the macro layer's " +
                 "LowBandEdge) here; 1 leaves it as configured. The largest amplitude any biome " +
                 "and ruggedness can reach multiplies the LOD relief demand, so the relief tests " +
                 "size it against each scene's allowance.")]
        [Min(0f)]
        public float LowAmplitude = 1f;
        [Tooltip("Amplitude of the mid band, between the two band edges; scaled again by ruggedness.")]
        [Min(0f)]
        public float MidAmplitude = 1f;
        [Tooltip("Amplitude of the high band, below MidBandEdge; scaled again by ruggedness.")]
        [Min(0f)]
        public float HighAmplitude = 1f;

        // Amplitudes are what an inspector should show; the runtime stores gains relative to one,
        // so that a default is the identity (C5).
        public BiomeConfig ToConfig() => new BiomeConfig
        {
            Temperature = Temperature,
            Moisture    = Moisture,
            Gain        = new float3(LowAmplitude - 1f, MidAmplitude - 1f, HighAmplitude - 1f),
        };
    }
}
