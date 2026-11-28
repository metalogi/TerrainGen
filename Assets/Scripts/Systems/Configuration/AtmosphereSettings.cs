using UnityEngine;
using Sonoma.Core.Rendering;

namespace Sonoma.Systems.Configuration
{
    // A world's atmosphere: how far it reaches, and the colour of its air. Referenced from
    // TerrainSettings.Atmosphere, one asset per world.
    //
    // The colour is the haze's, the one home it has: the terrain and the sea fade to it with
    // distance under _SONOMA_SKY, and the skybox (SonomaSkybox.shader) is that same colour, lit the
    // same way, at the horizon, rising to TerrainSettings.SkyAmbient overhead. So the far terrain
    // meets the sky with no seam, and the sea reflects the sky the camera sees.
    //
    // Above the atmosphere the skybox is black: on a sphere by the camera's altitude above the base
    // surface; inside a cylinder likewise, measured from the wall towards the axis; outside a
    // cylinder from the wall itself, since the air is held inside. See AtmosphereParams.
    //
    // Validated once, by AtmosphereParams.Create, when the terrain starts.
    [CreateAssetMenu(fileName = "Atmosphere", menuName = "Sonoma/Atmosphere Settings")]
    public class AtmosphereSettings : ScriptableObject
    {
        [Header("Extent")]
        [Tooltip("Metres above the base surface up to which the sky is at full colour. Inside a cylinder " +
                 "it is measured from the wall towards the axis; a camera outside one fades from the wall.")]
        [Min(0f)]
        public float Thickness = 15000f;
        [Tooltip("Metres past the Thickness, or past a cylinder's wall from outside, over which the sky " +
                 "fades to black.")]
        [Min(1f)]
        public float FadeDistance = 45000f;

        [Header("Colour")]
        [Tooltip("The colour of the air: the haze the terrain and sea fade to with distance, and the sky " +
                 "at the horizon. Lit by the sun on the day side and by TerrainSettings.SkyAmbient alone " +
                 "on the night side.")]
        public Color HazeColor = new Color(0.5f, 0.6f, 0.75f, 1f);
        [Tooltip("The sine of the elevation at which the sky has risen from the haze at the horizon to " +
                 "TerrainSettings.SkyAmbient overhead. The sea's reflection rises by the same gradient.")]
        [Range(0.01f, 1f)]
        public float SkyGradientHeight = (float)AtmosphereParams.DefaultSkyGradientHeight;

        public AtmosphereParams ToParams() => AtmosphereParams.Create(Thickness, FadeDistance, SkyGradientHeight);
    }
}
