using Unity.Mathematics;
using Sonoma.Core.Surface;

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
        // macro is the macro layer's sample here, band gains included (Biomes.Modulate), or
        // MacroSample.Zero without one -- which returns M4's bits exactly.
        public static float Height(in double3 surfacePos, int maxOctave,
                                   in MacroSample macro, in HeightParams p)
        {
            // Into noise cells. The division is the only scale conversion, and it happens
            // in double so the cell index stays exact at planetary distances.
            double3 n = surfacePos / p.OctaveWavelength0;

            // The general sum -- band gains and a two-slope spectrum -- is the original's bits
            // with zero gains and FinePersistence equal to Persistence, but the original stays
            // the path for them, so a terrain without biomes or roughness runs the code M4 shipped.
            double fbm = math.all(macro.BandGain == 0f) && p.FinePersistence == p.Persistence
                ? LatticeNoise.Fbm(n, maxOctave + 1, p.Persistence, p.Lacunarity, p.Seed)
                : LatticeNoise.Fbm(n, maxOctave + 1, p.Persistence, p.Lacunarity, p.Seed,
                                   macro.BandGain, p.LowBandEnd, p.MidBandEnd,
                                   p.FinePersistence, p.CrossoverOctave);

            // Signed noise, so the base surface is the mean and the terrain sits around it.
            // The prototype summed amp * (snoise * 0.5 + 0.5), which biases every sample
            // positive and floats the whole surface above the sphere -- SonomaRevisedPlan.md
            // section 2.6.
            //
            // The noise and its sum are double, and this is the one narrowing. What is left is
            // the float height itself -- one ULP of |height|, 0.06 mm at 800 m and 0.5 mm at
            // 5 km -- which is noise, not the axis-aligned staircase a float fraction made.
            return (float)(p.HeightScale * fbm + macro.BaseHeight);
        }

        // The height at full detail at a base-surface point, as the deepest chunks draw it:
        // octaves to MaxOctave(maxDepth), and the macro maps at MacroLevel(maxDepth) with their
        // biome band gains. For tools, through TerrainRoot.HeightAt -- the fly camera's
        // clearance above the ground.
        //
        // Here rather than in TerrainRoot so the tests call it instead of rebuilding it: a
        // test-side replica would stay green with HeightAt reading level 0, or passing
        // MacroSample.Zero, either of which understates the ground by up to the base height.
        //
        // The maps are addressed by (quad, u, v) and a tool has only a point, so the point is
        // located first. Locate's rounding (about 1e-15 in u) makes this a tool path, never a
        // generation one; a point on a shared edge may be located on either quad, and the macro
        // reads the same there from both.
        public static float HeightAtPoint<T>(in SurfaceDef s, double3 surfacePoint, int maxDepth,
                                             in HeightParams p, in MacroParams macro, T texels)
            where T : struct, IMacroTexels
        {
            var sample = MacroSample.Zero;
            if (macro.Enabled)
            {
                SurfaceMath.Locate(s, surfacePoint, out int quad, out double u, out double v);
                sample = Biomes.Modulate(
                    MacroSampler.Read(texels, macro, quad, macro.Layout.MacroLevel(maxDepth), u, v),
                    macro, out _);
            }
            return Height(surfacePoint, p.MaxOctave(maxDepth), sample, p);
        }
    }
}
