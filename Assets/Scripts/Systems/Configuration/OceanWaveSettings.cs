using UnityEngine;
using Unity.Mathematics;
using Sonoma.Core.Generation;

namespace Sonoma.Systems.Configuration
{
    // The sea's waves (ocean-b, Docs/Ocean_B_Plan.md, B4): a sum of Gerstner waves generated from these
    // few numbers by OceanWaves.Build, not entered wave by wave. Referenced from TerrainSettings.Waves;
    // none means a flat sea, ocean-a's to the bit.
    //
    // Validated once, by OceanWaves.Build, when the terrain starts. The largest amplitude it gives,
    // every wave cresting at once, sizes the water skirt, the culling bounds, the camera's floor and
    // the least OceanTerrainOverlap the settings accept (B6).
    [CreateAssetMenu(fileName = "OceanWaves", menuName = "Sonoma/Ocean Wave Settings")]
    public class OceanWaveSettings : ScriptableObject
    {
        [Header("Spectrum")]
        [Tooltip("How many waves, at most OceanWaves.MaxWaves. Each costs a sincos per vertex in every " +
                 "ocean pass and per fragment in ForwardLit.")]
        [Range(1, OceanWaves.MaxWaves)]
        public int Count = 8;
        [Tooltip("Metres. The wavelengths are geometric from this to LongestWavelength. A wave shorter " +
                 "than four vertex spacings at MaxDepth never moves the geometry; it shades the normals.")]
        [Min(0.01f)]
        public float ShortestWavelength = 1.5f;
        [Tooltip("Metres.")]
        [Min(0.01f)]
        public float LongestWavelength = 80f;
        [Tooltip("2 pi a / lambda of the shortest wave: its height over its length. Real seas run about " +
                 "0.02 to 0.1.")]
        [Min(0f)]
        public float Steepness = 0.06f;
        [Tooltip("Steepness falls with wavelength as (lambda / shortest) to the minus this. 0 keeps every " +
                 "wave equally steep, so the longest carries most of the height.")]
        public float SpectrumExponent = 0.1f;
        [Tooltip("Q: how far each wave moves the water sideways, as a share of its amplitude, sharpening " +
                 "its crests. Build refuses a sea whose crests would loop over themselves.")]
        [Min(0f)]
        public float Choppiness = 0.6f;

        [Header("Direction")]
        [Tooltip("A world direction the waves run along, not a local one: on a sphere the wind turns " +
                 "with the surface. Along a cylinder's axis, world Z, it never points along the local up " +
                 "inside one.")]
        public Vector3 WindDirection = new Vector3(0f, 0f, 1f);
        [Tooltip("Degrees. The half-angle of the cone the waves' directions are drawn in, about the wind. " +
                 "Wide enough that where one wave runs along the local up, others carry the surface.")]
        [Range(0f, 180f)]
        public float SpreadDegrees = 50f;
        [Tooltip("Draws the directions and starting phases.")]
        public uint Seed = 1;

        [Header("Shore")]
        [Tooltip("A wave is damped where the water is shallower than this times its wavelength, to " +
                 "nothing at the shore, so long waves flatten first.")]
        [Min(0f)]
        public float ShoalFactor = 0.25f;

        public OceanWaveConfig ToConfig() => new OceanWaveConfig
        {
            Count              = Count,
            ShortestWavelength = ShortestWavelength,
            LongestWavelength  = LongestWavelength,
            Steepness          = Steepness,
            SpectrumExponent   = SpectrumExponent,
            Choppiness         = Choppiness,
            WindDirection      = new double3(WindDirection.x, WindDirection.y, WindDirection.z),
            SpreadDegrees      = SpreadDegrees,
            ShoalFactor        = ShoalFactor,
            Seed               = Seed,
        };
    }
}
