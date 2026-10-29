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

            // The general sum -- band gains, a two-slope spectrum and the continental cutoff -- is
            // the original's bits with zero gains, FinePersistence equal to Persistence and no
            // cutoff, but the original stays the path for them, so a terrain without biomes,
            // roughness or continents runs the code M4 shipped.
            //
            // The gully term (M5c, E6) is a path of its own, taken only when it is on, so with
            // GullyDamping 0 every scene keeps the bits it had. Its damping is raised on wet ground,
            // read from the macro layer's wetness -- a macro value, which agrees across every
            // root-quad edge, never a macro derivative, which does not.
            //
            // A band limit short of the first damped octave damps nothing, and the gully sum is then
            // the general sum to the bit, so it takes the general path and skips the derivatives: at
            // the coarse depths and in erosion's T, which never reach the damped octaves.
            double fbm;
            if (p.HasGullies && maxOctave >= p.GullyStartOctave)
                fbm = LatticeNoise.Fbm(n, maxOctave + 1, p.Persistence, p.Lacunarity, p.Seed,
                                       macro.BandGain, p.LowBandEnd, p.MidBandEnd,
                                       p.FinePersistence, p.CrossoverOctave,
                                       p.FirstOctave, p.CoarseGain,
                                       p.GullyStartOctave,
                                       p.GullyDamping * (1.0 + (double)p.WetFloorDamping * macro.Wetness),
                                       p.HeightScale / p.OctaveWavelength0, p.SurfaceUp(surfacePos));
            else if (math.all(macro.BandGain == 0f) && p.FinePersistence == p.Persistence && p.FirstOctave == 0)
                fbm = LatticeNoise.Fbm(n, maxOctave + 1, p.Persistence, p.Lacunarity, p.Seed);
            else
                fbm = LatticeNoise.Fbm(n, maxOctave + 1, p.Persistence, p.Lacunarity, p.Seed,
                                       macro.BandGain, p.LowBandEnd, p.MidBandEnd,
                                       p.FinePersistence, p.CrossoverOctave,
                                       p.FirstOctave, p.CoarseGain);

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
            int octave = p.MaxOctave(maxDepth);
            if (macro.Enabled)
            {
                SurfaceMath.Locate(s, surfacePoint, out int quad, out double u, out double v);
                sample = Macro(texels, macro, p, quad, macro.Layout.MacroLevel(maxDepth), u, v, octave, out _);
            }
            return Height(surfacePoint, octave, sample, p);
        }

        // The macro sample Height takes at (quad, u, v) on this level: the bicubic read, the biomes'
        // band gains (Biomes.Modulate, whose weights come back for the splat), and the wetness the
        // gully term's wet-floor damping reads -- only when that damping is on and maxOctave reaches
        // the damped octaves, since otherwise nothing reads it. The one route for both sample jobs,
        // HeightAtPoint and the tests' copies, so none can leave the wetness out or read it at
        // another level than the rest of the sample.
        public static MacroSample Macro<T>(T texels, in MacroParams macro, in HeightParams p, int quad, int level,
                                           double u, double v, int maxOctave, out float4 weights)
            where T : struct, IMacroTexels
        {
            var sample = Biomes.Modulate(MacroSampler.Read(texels, macro, quad, level, u, v), macro, out weights);
            if (p.WetFloorDamping > 0f && maxOctave >= p.GullyStartOctave)
                sample.Wetness = MacroSampler.ReadWetness(texels, macro, quad, level, u, v);
            return sample;
        }

        // A bound on |Height| anywhere on the surface, at any depth: every octave at the largest
        // amplitude its band can take anywhere (MacroParams.MaxBandAmplitude, which is 1 on every
        // band without a macro layer), the noise at its bound of 1, which it attains, and the
        // macro base height's bound the same way over the octaves the finest level carries, times
        // the most the bicubic read can overshoot its texels (MacroSampler.MaxOvershoot).
        //
        // A bound, not an estimate, for its two users. LodVertical's envelope needs one: a vertex
        // outside it is flattened by a different rule from the camera, and the LOD picked for it
        // may then be coarser than true distance would give. And twice it caps a skirt's drop
        // (ChunkMeshLayout.SkirtDrop), which must still reach any gap. It runs well above the real
        // extremes, since the octaves rarely all peak at one point -- on Earth at HeightScale
        // 20,000, 84 km -- and that costs only finer LOD than needed for a camera between the real
        // peaks and it, and skirts longer than they need to be at the coarsest depths.
        //
        // Not for eroded maps, which it refuses: erosion writes the base height channel, and the
        // analytic bound no longer holds for it. The overload below takes the measured largest.
        public static double MaxAbsHeight(in HeightParams p, in MacroParams macro)
        {
            if (macro.Enabled && macro.HasErosion)
                throw new System.ArgumentException(
                    "TerrainHeightFunction.MaxAbsHeight: eroded maps need the measured bound, the overload taking the " +
                    "largest base height (MacroMaps.MaxAbsHeight); the analytic one does not bound them.");
            double baseHeight = 0.0;
            if (macro.Enabled)
            {
                int    octaves = math.max(0, macro.MaxOctave(macro.Layout.LevelCount - 1) + 1);
                double a       = 1.0;
                for (int j = 0; j < octaves; j++)
                {
                    baseHeight += a;
                    a          *= macro.Persistence;
                }
                baseHeight *= math.abs(macro.BaseHeightScale) * MacroSampler.MaxOvershoot;

                // The continent height in the same channel, at its profile's largest magnitude: the
                // pyramid's filter never leaves the field's range, so the read overshoots it at most
                // as it does any texel. Added separately rather than folded into the product above,
                // so a macro layer without continents keeps its bound to the bit.
                if (macro.HasContinents)
                    baseHeight += macro.ContinentBound * MacroSampler.MaxOvershoot;
            }

            return DetailBound(p, macro) + baseHeight;
        }

        // The same bound over eroded maps (M5c, E5). Erosion writes the base height channel as
        // T' - detail(0 .. Oe), which can leave the analytic base-height bound by up to the detail
        // bound, so the base-height term becomes the largest |BaseHeight'| the build stored, every
        // level, times the read's overshoot; the detail term is unchanged, every octave at its
        // largest amplitude. Without erosion it is the analytic bound above, to the bit, so every
        // envelope and LodVertical anchor without erosion stays as it was. MacroMaps.MaxAbsHeight
        // passes its build's measured maximum.
        public static double MaxAbsHeight(in HeightParams p, in MacroParams macro, double maxAbsBaseHeight)
        {
            if (!macro.Enabled || !macro.HasErosion) return MaxAbsHeight(p, macro);
            if (!(maxAbsBaseHeight >= 0.0) || double.IsInfinity(maxAbsBaseHeight))
                throw new System.ArgumentOutOfRangeException(nameof(maxAbsBaseHeight),
                    "TerrainHeightFunction.MaxAbsHeight: the measured largest base height must be finite and not negative.");
            return DetailBound(p, macro) + maxAbsBaseHeight * MacroSampler.MaxOvershoot;
        }

        // The detail noise's share of the bound: every octave at the largest amplitude its band can
        // take anywhere, the noise at its bound of 1.
        static double DetailBound(in HeightParams p, in MacroParams macro)
        {
            float3 band = macro.Enabled ? macro.MaxBandAmplitude : new float3(1f);

            double detail = 0.0;
            for (int k = 0; k < p.OctaveCount; k++)
            {
                // The band as LatticeNoise.Fbm applies it, and the fall-off as it does too. The
                // continental band at its own amplitude, which is none when it is dropped.
                float gain = k < p.LowBandEnd ? band.x : k < p.MidBandEnd ? band.y : band.z;
                if (k >= p.FirstOctave)
                    detail += p.OctaveAmplitude(k) * math.abs(gain);
                else
                    detail += p.OctaveAmplitude(k) * math.abs(gain) * math.abs(1.0 + p.CoarseGain);
            }
            return math.abs(p.HeightScale) * detail;
        }
    }
}
