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
        [Tooltip("Metres: the scene's sea level. Ground at or below it is water, where rivers end and " +
                 "nothing is eroded. The cylinders' rims are not outlets.")]
        public float BaseLevel = 0f;
        [Tooltip("In root quads' area. A connected body of water smaller than this is not a sea but a " +
                 "hollow: the flood fills it and rivers run on through it to the sea. 0 makes every body " +
                 "of water a sea. Without one large enough, the largest is the sea.")]
        [Min(0f)]
        public float MinOutletArea = 0f;

        [Header("Basins")]
        [Tooltip("Metres. Before erosion, each closed basin gets an outlet carved through its rim to the " +
                 "sea, lowering no ground more than this; a basin deeper than that is filled up to the " +
                 "deepest outlet it can have. Without it basins stay lakes that rivers cross uncut. 0 is off.")]
        [Min(0f)]
        public float BreachDepth = 0f;

        public ErosionConfig ToConfig() => new ErosionConfig
        {
            Iterations    = Iterations,
            IncisionDepth = IncisionDepth,
            AreaExponent  = AreaExponent,
            Diffusion     = Diffusion,
            BaseLevel     = BaseLevel,
            MinOutletArea = MinOutletArea,
            BreachDepth   = BreachDepth,
        };
    }
}
