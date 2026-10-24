using UnityEngine;
using Sonoma.Core.Generation;

namespace Sonoma.Systems.Configuration
{
    // A Voronoi continent map in the macro layer (Docs/Continents_Plan.md): macro cells spread from a
    // few continent centres, micro cells classed land or ocean, and a smooth continent height from
    // the signed distance to the coast, added to the macro base height. Referenced from
    // MacroLayerSettings.Continents; none means no continents, and M5b's maps to the bit.
    //
    // Sizes are counts and multiples of the micro spacing -- the side of a square of the surface's
    // area over MicroCells -- so the same asset makes the same shapes on any world. Validated once,
    // by ContinentConfig.Validate, when the terrain starts.
    [CreateAssetMenu(fileName = "Continents", menuName = "Sonoma/Continent Settings")]
    public class ContinentSettings : ScriptableObject
    {
        [Header("Map")]
        [Tooltip("The continent map's seed. Offset internally from the macro and detail seeds, so the " +
                 "same value in all three correlates none of them.")]
        public uint Seed = 1;
        [Tooltip("Macro cells: the continental templates the spread runs over.")]
        [Min(1)]
        public int MacroCells = 30;
        [Tooltip("Micro cells, each land or ocean. The coastline's shape at the map's scale; each " +
                 "should be at least four top-level macro texels across, which TerrainRoot warns of.")]
        [Min(1)]
        public int MicroCells = 5000;
        [Tooltip("Continent centres: macro cells the spread starts from, at elevation 1.")]
        [Min(1)]
        public int ContinentCount = 4;

        [Header("Spread")]
        [Tooltip("Each step of the spread multiplies the elevation by a decay drawn between these, " +
                 "in (0, 1].")]
        public float DecayMin = 0.5f;
        public float DecayMax = 0.75f;
        [Tooltip("The spread stops below this elevation.")]
        public float ElevationFloor = 0.1f;

        [Header("Land")]
        [Tooltip("Noise added to each micro cell's elevation before the land threshold.")]
        public float ScoreNoise = 0.25f;
        [Tooltip("The land share of the MAP by area. The detail noise floods more land than it " +
                 "raises, so the final terrain comes out lower; each scene's value is calibrated so " +
                 "the final terrain reaches its target.")]
        [Range(0.01f, 0.99f)]
        public float LandFraction = 0.3f;

        [Header("Coast")]
        [Tooltip("The coastline's warp, in micro spacings: it breaks the micro cells' straight edges.")]
        public float WarpAmplitude = 0.5f;
        [Tooltip("The warp's wavelength, in micro spacings.")]
        public float WarpWavelength = 2f;
        [Tooltip("The profile's half-width, in micro spacings, under 2: the distance from the coast to " +
                 "the land plateau and to the ocean floor.")]
        public float CoastWidth = 1f;

        [Header("Heights")]
        [Tooltip("Land plateau height, in metres, at the middle of the interior rise.")]
        public float LandHeight = 0f;
        [Tooltip("Ocean floor depth, in metres, as a positive number.")]
        public float OceanDepth = 0f;
        [Tooltip("Land stands from 1 - this/2 to 1 + this/2 times LandHeight, highest near a continent " +
                 "centre. In [0, 2].")]
        public float InteriorRise = 0.8f;

        [Header("Detail Cutoff")]
        [Tooltip("Detail noise octaves at least this long, in metres, are the continental band, which " +
                 "the continent map carries instead. 0 keeps every octave.")]
        public double DetailCutoff = 0.0;
        [Tooltip("The continental band's amplitude: 0 drops those octaves.")]
        [Min(0f)]
        public float CoarseOctaveAmplitude = 0f;

        public ContinentConfig ToConfig() => new ContinentConfig
        {
            Seed                  = Seed,
            MacroCells            = MacroCells,
            MicroCells            = MicroCells,
            ContinentCount        = ContinentCount,
            DecayMin              = DecayMin,
            DecayMax              = DecayMax,
            ElevationFloor        = ElevationFloor,
            ScoreNoise            = ScoreNoise,
            LandFraction          = LandFraction,
            WarpAmplitude         = WarpAmplitude,
            WarpWavelength        = WarpWavelength,
            CoastWidth            = CoastWidth,
            LandHeight            = LandHeight,
            OceanDepth            = OceanDepth,
            InteriorRise          = InteriorRise,
            DetailCutoff          = DetailCutoff,
            CoarseOctaveAmplitude = CoarseOctaveAmplitude,
        };
    }
}
