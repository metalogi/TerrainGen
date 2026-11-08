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
        // The continent designer's two settings (Assets/Editor/ContinentSettingsEditor.cs). Its
        // Regenerate button turns them into the fields below, through ContinentDesign.Derive and a
        // measured calibration of LandFraction. The terrain never reads them: ToConfig leaves them
        // out, so a scene runs the fields below whatever these say. An asset written before the
        // designer reads its initial values, which are what its four continents and 30% land are.
        [Header("Design")]
        [Tooltip("The final terrain's land share by area, after the detail noise and erosion: what " +
                 "Regenerate calibrates LandFraction to reach.")]
        [Range(0.05f, 0.95f)]
        public float TargetLand = 0.3f;
        [Tooltip("0 is one supercontinent, 1 is many small continents. Sets ContinentCount, " +
                 "MacroCells and Separation when Regenerate runs.")]
        [Range(0f, 1f)]
        public float Fragmentation = ContinentDesign.FragmentationFor(4);

        // What the last Regenerate was asked for and what it measured, so the inspector can say
        // whether the fields below still come from the settings above. -1 is never regenerated.
        [HideInInspector] public float RegeneratedTargetLand    = -1f;
        [HideInInspector] public float RegeneratedFragmentation = -1f;
        [HideInInspector] public float MeasuredLand             = -1f;

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
        [Tooltip("Score taken off the micro cells where two continent centres' domains meet, so " +
                 "neighbouring continents keep a strait between them rather than merging. 0 is none.")]
        [Min(0f)]
        public float Separation = 0f;
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
            Separation            = Separation,
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
