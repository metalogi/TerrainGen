using System;
using Unity.Mathematics;

namespace Sonoma.Core.Generation
{
    // The continent designer's rules (Docs/Continents_Plan.md, the continent designer amendment):
    // two high-level settings, a land share and a fragmentation, turned into the continent map's
    // own. The editor writes what this returns into the asset, so the runtime never sees the
    // high-level settings and reads the fields it always read.
    //
    // It changes only the land fraction, the continent count, the macro cells and the separation.
    // The micro cells, the coast's width and warp, the heights and the cutoff decide how steep the
    // coasts are, which is what the LOD relief allowance pays for, and the plateau and texel
    // limits; they are kept to the bit, and so is everything else. UnityEngine-free, for the
    // harness.
    //
    // Fitted on ContinentDesignSweep (2026-11-07). Without a separation neighbouring continents
    // merge: at a map land share of a half or more every count made one supercontinent. With 2,
    // which puts the domains' borders below every other cell's score, the number of continents
    // follows the count at every share from 0.1 to 0.7, on the spheres and both rings.
    public static class ContinentDesign
    {
        public const int   MaxContinents         = 24;
        public const int   MinMacroCells         = 30;
        public const int   MaxMacroCells         = 400;
        public const float LandCellsPerContinent = 2.25f;   // today's 30 cells x 0.3 / 4 continents
        public const float Separation            = 2f;

        // Fragmentation 0 is one continent and 1 is MaxContinents, geometrically between.
        public static int ContinentCountFor(float fragmentation)
        {
            double f = math.saturate(fragmentation);
            return (int)math.round(math.pow(MaxContinents, f));
        }

        // The fragmentation whose count is the nearest to n, for an asset written before the
        // designer: the inverse of ContinentCountFor.
        public static float FragmentationFor(int continentCount)
            => continentCount <= 1 ? 0f
             : (float)math.saturate(math.log((double)continentCount) / math.log((double)MaxContinents));

        // Enough macro cells that each continent's land spans LandCellsPerContinent of them, so the
        // BFS takes the same number of hops across a continent at any count, and the decay range
        // that suits four continents suits all of them.
        public static int MacroCellsFor(int continentCount, float mapLand)
        {
            double cells = LandCellsPerContinent * continentCount / math.max(mapLand, 0.01);
            return math.clamp((int)math.ceil(cells), math.max(MinMacroCells, continentCount), MaxMacroCells);
        }

        // The configuration the two settings make from the current one.
        public static ContinentConfig Derive(in ContinentConfig current, float fragmentation, float mapLand)
        {
            if (float.IsNaN(fragmentation))
                throw new ArgumentOutOfRangeException(nameof(fragmentation), "ContinentDesign: the fragmentation must be a number.");
            if (float.IsNaN(mapLand))
                throw new ArgumentOutOfRangeException(nameof(mapLand), "ContinentDesign: the map land share must be a number.");
            var k = current;
            k.LandFraction   = math.clamp(mapLand, 0.01f, 0.99f);
            k.ContinentCount = ContinentCountFor(fragmentation);
            k.MacroCells     = MacroCellsFor(k.ContinentCount, k.LandFraction);
            k.Separation     = Separation;
            return k;
        }

        // The next map land share to try, so the final land reaches its target: a secant through
        // the last two tries, or, with one, the map moved by the miss. The detail noise floods a
        // few points of land more than it raises, so the final share rises with the map's nearly
        // one for one, and two or three tries land within half a point. Clamped into the
        // configuration's range, and halfway back towards the last try when a secant runs flat or
        // backwards, which noise in the measurement can make it do.
        public static float NextMapLand(float target, float map0, float final0, float map1 = float.NaN, float final1 = float.NaN)
        {
            double next;
            if (float.IsNaN(map1)) next = map0 + (target - final0);
            else
            {
                double slope = (final1 - final0) / (double)(map1 - map0);
                next = slope > 0.25 && !double.IsInfinity(slope) && !double.IsNaN(slope)
                     ? map1 + (target - final1) / slope
                     : map1 + 0.5 * (target - final1);
            }
            return math.clamp((float)next, 0.01f, 0.99f);
        }
    }
}
