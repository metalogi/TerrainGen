using System;
using Unity.Mathematics;

namespace Sonoma.Core.Generation
{
    // The sea (ocean-a): a flat surface SeaLevel metres above the base surface, along its normal,
    // drawn on the terrain's own chunks (Docs/Ocean_A_Plan.md). Resolved and validated once, at
    // setup, and copied into the mesh job by value. Blittable.
    //
    // default(OceanParams) is "no ocean", as default(MacroSample) is "no macro layer", and every
    // field added must keep that true: a scene without an ocean writes the terrain it wrote before.
    public struct OceanParams
    {
        public bool   Enabled;
        public double SeaLevel;       // metres above the base surface (C1)
        public float  SeaLevelFloat;  // narrowed once, as the water vertex takes it (A8)
        public float  TerrainOverlap; // metres: how far under the sea a chunk must lie to be water alone
        public float  WaveAmplitude;  // metres; 0 until ocean-b

        public static OceanParams Disabled => default;

        // The sea for a scene, or Disabled if it has none. The envelope is the bound on the
        // terrain's height LodVertical takes, MacroMaps.MaxAbsHeight: a sea outside it lies above
        // every summit or below every trench, so the whole world would be one kind, and the C1
        // sea surface would leave the band the LOD measures in. The erosion's base level is checked
        // whether or not the ocean is on (A3), because its outlets are the sea either way.
        public static OceanParams Create(bool ocean, double seaLevel, float overlap, int resolution,
                                         double envelope, bool hasErosion, double erosionBaseLevel)
        {
            CheckSeaLevel(seaLevel, hasErosion, erosionBaseLevel);
            if (float.IsNaN(overlap) || float.IsInfinity(overlap) || overlap < 0f)
                throw new ArgumentOutOfRangeException(nameof(overlap),
                    "OceanParams: the ocean-terrain overlap must be finite and not negative.");
            if (!ocean) return Disabled;

            if (!(math.abs(seaLevel) < envelope))
                throw new ArgumentOutOfRangeException(nameof(seaLevel),
                    "OceanParams: the sea level is outside the terrain's height envelope, so the sea would " +
                    "lie above every summit or below every trench. See MacroMaps.MaxAbsHeight.");
            if (resolution > ChunkMeshLayout.MaxResolutionWithOcean)
                throw new ArgumentOutOfRangeException(nameof(resolution),
                    "OceanParams: with an ocean a coast chunk holds two grids, which at this chunk " +
                    "resolution need more than 16-bit mesh indices. See ChunkMeshLayout.MaxResolutionWithOcean.");

            return new OceanParams
            {
                Enabled        = true,
                SeaLevel       = seaLevel,
                SeaLevelFloat  = (float)seaLevel,
                TerrainOverlap = overlap,
                WaveAmplitude  = 0f,
            };
        }

        // A3: one sea level in two homes, TerrainSettings.SeaLevel and the erosion's BaseLevel, held
        // equal so the two cannot drift. Separate from Create so TerrainRoot can refuse a mismatch
        // before the macro build rather than after seconds of erosion. The base level is a float
        // setting, so the two are compared as floats: a sea level of 0.1 is the base level 0.1f.
        public static void CheckSeaLevel(double seaLevel, bool hasErosion, double erosionBaseLevel)
        {
            if (double.IsNaN(seaLevel) || double.IsInfinity(seaLevel))
                throw new ArgumentOutOfRangeException(nameof(seaLevel),
                    "OceanParams: the sea level must be finite.");
            if (hasErosion && (float)seaLevel != (float)erosionBaseLevel)
                throw new ArgumentException(
                    "OceanParams: the sea level differs from the erosion's base level, whose outlets are " +
                    "the sea. Set TerrainSettings.SeaLevel and ErosionSettings.BaseLevel equal.");
        }
    }
}
