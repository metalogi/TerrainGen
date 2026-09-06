using Unity.Mathematics;

namespace Sonoma.Core.Generation
{
    // The contract the rest of the terrain system is built on.
    //
    // Height is a pure function of a surface position and a band limit. Same arguments,
    // same bits -- on any thread, in any order, in any frame. That single property is what
    // makes shared edge vertices agree without stitching, makes chunk generation trivially
    // parallel and cancellable, and makes the world reproducible from a seed.
    //
    // Consequently: no mutable statics, no time, no frame counter, no random state, no
    // dependence on which chunk asked. If something here ever needs to know its caller,
    // the design has gone wrong.
    //
    // No [BurstCompile] attribute: these are plain static methods, compiled as part of
    // whichever job calls them. The attribute would only matter for a function pointer.
    public static class TerrainHeightFunction
    {
        // Height in world metres above the base surface, added along the surface normal.
        //
        // maxOctave comes from HeightParams.MaxOctave(depth), so a chunk and its parent
        // evaluate deliberately *different* functions at the same point -- the parent is a
        // band-limited version of the child. Geomorphing (M3) bridges the two; nothing here
        // tries to hide the difference.
        //
        // macro is MacroSample.Zero until M5 adds the macro layer.
        public static float Height(in double3 surfacePos, int maxOctave,
                                   in MacroSample macro, in HeightParams p)
        {
            // Into noise cells. The division is the only scale conversion, and it happens
            // in double so the cell index stays exact at planetary distances.
            double3 n = surfacePos / p.OctaveWavelength0;

            float fbm = LatticeNoise.Fbm(n, maxOctave + 1, p.Persistence, p.Lacunarity, p.Seed);

            // Signed noise, so the base surface is the mean and the terrain sits around it.
            // The prototype summed amp * (snoise * 0.5 + 0.5), which biases every sample
            // positive and floats the whole surface above the sphere -- SonomaRevisedPlan.md
            // section 2.6.
            return p.HeightScale * fbm + macro.BaseHeight;
        }
    }
}
