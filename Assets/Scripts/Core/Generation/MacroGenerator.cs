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
    //   w  ruggedness, 0..1         0.5 + fBm times RuggednessNoise, clamped
    //
    // Each level is band-limited by truncation, summing only the octaves MacroParams.MaxOctave
    // admits for it, so a level holds exactly the content its chunks can carry. Border texels
    // continue this quad's own parameterisation past its edge, the choice the height border
    // makes; on shared edges they are then overwritten by the owner's (MacroLayout.EdgeSource).
    //
    // Plain noise only, for now. The maps exist so that something non-local -- erosion, rivers,
    // tectonics -- can write them later, and nothing downstream assumes they came from here.
    public static class MacroGenerator
    {
        // The detail noise seeds its octaves Seed, Seed + 1, ... with the TerrainSettings seed,
        // and a macro layer configured with the same seed would otherwise reuse those lattices
        // at the same scale and simply scale up detail octaves 0 to 6. Each channel is a
        // stride apart so its octaves never meet another's.
        const uint SeedOffset    = 0x6D2B79F5u;
        const uint ChannelStride = 0x00010000u;

        public static float4 Texel(in SurfaceDef s, in RootQuad q, in MacroParams p, int level, int i, int j)
        {
            double  n  = p.Layout.Cells(level);
            double3 pt = SurfaceMath.SurfacePoint(s, q, i / n, j / n);
            double3 x  = pt / p.Wavelength0;

            int  octaves = math.max(0, p.MaxOctave(level) + 1);
            uint seed    = p.Seed + SeedOffset;

            double baseHeight = p.BaseHeightScale * Fbm(x, octaves, p, seed);

            double temperature = p.EquatorTemperature
                               + p.TemperatureNoise * Fbm(x, octaves, p, seed + ChannelStride)
                               - p.LapseRate * math.max(baseHeight, 0.0);
            if (p.HasLatitude)
            {
                double sinLatitude = pt.y / math.length(pt);
                temperature += (p.PoleTemperature - p.EquatorTemperature) * sinLatitude * sinLatitude;
            }

            double moisture   = math.saturate(0.5 + p.MoistureNoise   * Fbm(x, octaves, p, seed + 2u * ChannelStride));
            double ruggedness = math.saturate(0.5 + p.RuggednessNoise * Fbm(x, octaves, p, seed + 3u * ChannelStride));

            return new float4((float)baseHeight, (float)temperature, (float)moisture, (float)ruggedness);
        }

        static double Fbm(double3 x, int octaves, in MacroParams p, uint seed)
            => LatticeNoise.Fbm(x, octaves, p.Persistence, 2f, seed);

        // The whole pyramid in managed code: every texel generated, then the edge copies
        // applied. The reference MacroMaps' Burst build is held to, and what the pure tests read.
        public static float4[] Build(in SurfaceDef s, RootQuad[] roots, in MacroParams p)
        {
            var texels = new float4[p.Layout.TexelCount];
            for (int t = 0; t < texels.Length; t++)
            {
                p.Layout.Decode(t, out int q, out int l, out int i, out int j);
                texels[t] = Texel(s, roots[q], p, l, i, j);
            }
            foreach (int2 c in p.Layout.EdgeCopies(s))
                texels[c.x] = texels[c.y];
            return texels;
        }
    }
}
