using UnityEngine;
using Sonoma.Core.Generation;

namespace Sonoma.Systems.Configuration
{
    // Stream-power erosion of the macro maps' top level, once, at startup (Docs/M5_MacroLayer_Plan.md,
    // M5c-1): drainage routed over the whole surface, channels incised by the implicit scheme of
    // Braun and Willett (2013), and hillslopes diffused. Referenced from MacroLayerSettings.Erosion;
    // none means no erosion, and the maps as the generator builds them, to the bit.
    //
    // Validated once, by ErosionConfig.Validate, when the terrain starts.
    [CreateAssetMenu(fileName = "Erosion", menuName = "Sonoma/Erosion Settings")]
    public class ErosionSettings : ScriptableObject
    {
        [Header("Stream Power")]
        [Tooltip("Implicit steps. The scheme is unconditionally stable, so a few tens of large steps " +
                 "reach the result.")]
        [Min(1)]
        public int Iterations = 30;
        [Tooltip("Metres. How far a knickpoint climbs a channel draining one root quad's area over " +
                 "the whole run, which is also how deep that channel is cut at unit slope; at slope S " +
                 "it is cut about this times S. Smaller rivers cut less, by their drainage area to the " +
                 "power AreaExponent. 0 routes the drainage and cuts nothing.")]
        [Min(0f)]
        public float IncisionDepth = 0f;
        [Tooltip("m, the drainage area's exponent in the incision rate, in (0, 1); 0.4 to 0.5 is usual. " +
                 "Higher concentrates the cutting in the large rivers.")]
        [Range(0.05f, 0.95f)]
        public float AreaExponent = 0.45f;

        [Header("Hillslopes")]
        [Tooltip("Hillslope diffusion per step, in top-level macro texel spacings squared. 0 is none.")]
        [Min(0f)]
        public float Diffusion = 0f;

        [Header("Outlets")]
        [Tooltip("Metres. Every node at or below it is an outlet, where the water leaves and nothing is " +
                 "eroded: the scene's sea level. The cylinders' rims are not outlets.")]
        public float BaseLevel = 0f;

        public ErosionConfig ToConfig() => new ErosionConfig
        {
            Iterations    = Iterations,
            IncisionDepth = IncisionDepth,
            AreaExponent  = AreaExponent,
            Diffusion     = Diffusion,
            BaseLevel     = BaseLevel,
        };
    }
}
