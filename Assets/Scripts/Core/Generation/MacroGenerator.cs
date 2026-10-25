using System;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // The plain-noise filler for the macro maps: each texel's four channels from the texel's own
    // surface point, a pure function of its address like the height function is of a point.
    //
    //   x  base height, metres      fBm at Wavelength0, times BaseHeightScale
    //   y  temperature, degrees C   EquatorTemperature, falling to PoleTemperature as (y / r)^2
    //                               on a cube sphere, minus the lapse rate times the base height
    //                               above zero, plus fBm times TemperatureNoise
    //   z  moisture, 0..1           0.5 + fBm times MoistureNoise, clamped
    //   w  ruggedness, 0..1         RuggednessCentre + fBm times RuggednessNoise, clamped
    //
    // Each level is band-limited by truncation, summing only the octaves MacroParams.MaxOctave
    // admits for it, so a level holds exactly the content its chunks can carry. Border texels
    // continue this quad's own parameterisation past its edge, the choice the height border
    // makes; on shared edges they are then overwritten by the owner's (MacroLayout.EdgeSource).
    //
    // With continents (Docs/Continents_Plan.md, K1) the base height also carries the continent
    // height, a channel derived before the generator runs -- ContinentMap, ContinentField, then
    // MacroPyramid -- and passed in per texel, so the lapse rate reads the sum and climate follows
    // the coasts. The maps exist so that something non-local -- continents, erosion, rivers -- can
    // write them, and nothing downstream assumes the fBm is all there is.
    public static class MacroGenerator
    {
        // The detail noise seeds its octaves Seed, Seed + 1, ... with the TerrainSettings seed,
        // and a macro layer configured with the same seed would otherwise reuse those lattices
        // at the same scale and simply scale up detail octaves 0 to 6. Each channel is a
        // stride apart so its octaves never meet another's.
        const uint SeedOffset    = 0x6D2B79F5u;
        const uint ChannelStride = 0x00010000u;

        // continent is this texel's continent height, read only when p.HasContinents.
        public static float4 Texel(in SurfaceDef s, in RootQuad q, in MacroParams p, int level, int i, int j, float continent)
        {
            double  n  = p.Layout.Cells(level);
            double3 pt = SurfaceMath.SurfacePoint(s, q, i / n, j / n);
            double3 x  = pt / p.Wavelength0;

            int  octaves = math.max(0, p.MaxOctave(level) + 1);
            uint seed    = p.Seed + SeedOffset;

            double baseHeight = p.BaseHeightScale * Fbm(x, octaves, p, seed);
            // Only with continents. Adding a zero is not the identity: it turns -0.0 into +0.0, and
            // the maps would no longer be M5b's to the bit.
            if (p.HasContinents) baseHeight += continent;

            double temperature = p.EquatorTemperature
                               + p.TemperatureNoise * Fbm(x, octaves, p, seed + ChannelStride)
                               - p.LapseRate * math.max(baseHeight, 0.0);
            if (p.HasLatitude)
            {
                double sinLatitude = pt.y / math.length(pt);
                temperature += (p.PoleTemperature - p.EquatorTemperature) * sinLatitude * sinLatitude;
            }

            double moisture   = math.saturate(0.5 + p.MoistureNoise   * Fbm(x, octaves, p, seed + 2u * ChannelStride));
            double ruggedness = math.saturate(p.RuggednessCentre + p.RuggednessNoise * Fbm(x, octaves, p, seed + 3u * ChannelStride));

            return new float4((float)baseHeight, (float)temperature, (float)moisture, (float)ruggedness);
        }

        static double Fbm(double3 x, int octaves, in MacroParams p, uint seed)
            => LatticeNoise.Fbm(x, octaves, p.Persistence, 2f, seed);

        // The whole pyramid in managed code: every texel generated, then the edge copies
        // applied. The reference MacroMaps' Burst build is held to, and what the pure tests read.
        public static float4[] Build(in SurfaceDef s, RootQuad[] roots, in MacroParams p)
            => Build(s, roots, p, ContinentParams.None, out _);

        // The same with continents (K1): Stages A to C into a continent channel, then every texel
        // with its continent height, then the edge copies. Refuses erosion, which needs the overload
        // below: these maps would be the uneroded ones.
        public static float4[] Build(in SurfaceDef s, RootQuad[] roots, in MacroParams p, in ContinentParams cp,
                                     out ContinentMap map)
        {
            ErosionBuild.Check(p, ErosionParams.None);
            return Texels(s, roots, p, cp, out map);
        }

        // Everything, erosion included (M5c-1): the maps above, then ErosionBuild.Erode on them. The
        // drainage maps come back beside the texels, cleared and one element long without erosion,
        // as MacroMaps holds them.
        public static float4[] Build(in SurfaceDef s, RootQuad[] roots, in MacroParams p, in ContinentParams cp,
                                     in HeightParams hp, in ErosionParams ep, out ContinentMap map,
                                     out float2[] drainage, out ErosionBuild.Managed erosion, out ErosionStats stats)
        {
            ErosionBuild.Check(p, ep);
            float4[] texels = Texels(s, roots, p, cp, out map);
            drainage = new float2[ep.Enabled ? p.Layout.TexelCount : 1];
            erosion  = null;
            stats    = null;
            if (ep.Enabled)
            {
                stats   = new ErosionStats();
                erosion = ErosionBuild.Erode(s, roots, hp, p, ep, texels, drainage, MacroPyramidPlan.Create(s, p.Layout), stats);
            }
            return texels;
        }

        static float4[] Texels(in SurfaceDef s, RootQuad[] roots, in MacroParams p, in ContinentParams cp,
                               out ContinentMap map)
        {
            CheckContinents(p, cp);
            map = null;
            float[] continent = cp.Enabled ? ContinentChannel(s, roots, p, cp, out map) : null;

            var texels = new float4[p.Layout.TexelCount];
            for (int t = 0; t < texels.Length; t++)
            {
                p.Layout.Decode(t, out int q, out int l, out int i, out int j);
                texels[t] = Texel(s, roots[q], p, l, i, j, continent != null ? continent[t] : 0f);
            }
            foreach (int2 c in p.Layout.EdgeCopies(s))
                texels[c.x] = texels[c.y];
            return texels;
        }

        // The continent height at every texel of every level, in managed code: the map (Stage A),
        // the field at the top level (B), and the pyramid filtered down from it (C), the top level
        // filtered once and its border refilled from the neighbours' filtered texels. Every shared
        // edge texel is its owner's, as C2 requires. The tests' lazy texels read this.
        public static float[] ContinentChannel(in SurfaceDef s, RootQuad[] roots, in MacroParams p, in ContinentParams cp,
                                               out ContinentMap map)
        {
            CheckContinents(p, cp);
            if (!cp.Enabled) throw new ArgumentException("MacroGenerator: no continents to build.", nameof(cp));

            map = ContinentMap.Build(s, roots, p.Layout, cp);
            var channel = new float[p.Layout.TexelCount];

            var clock = System.Diagnostics.Stopwatch.StartNew();
            map.Stats.WarpedSaturation   = math.sqrt(ContinentField.FillTop(channel, s, roots, p.Layout, cp, map));
            map.Stats.StageBMilliseconds = clock.Elapsed.TotalMilliseconds;

            clock.Restart();
            MacroPyramid.Run(channel, p.Layout, MacroPyramidPlan.Create(s, p.Layout), MacroPyramidTop.FilterAndFill);
            map.Stats.StageCMilliseconds = clock.Elapsed.TotalMilliseconds;
            return channel;
        }

        // The macro parameters and the continents must come from one configuration: the parameters
        // carry the bound the height envelope is built from, and the flag the generator reads.
        public static void CheckContinents(in MacroParams p, in ContinentParams cp)
        {
            if (p.HasContinents != cp.Enabled || (cp.Enabled && p.ContinentBound != cp.Bound))
                throw new ArgumentException(
                    "MacroGenerator: the macro parameters and the continents were made from different configurations.");
        }
    }
}
