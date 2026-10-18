using Unity.Mathematics;

namespace Sonoma.Core.Generation
{
    // Biomes from climate, per vertex: which biomes a macro sample belongs to, and how much
    // each band of the detail noise is scaled there. Docs/M5_MacroLayer_Plan.md C4 and C7.
    //
    // Classified from the interpolated climate rather than read from the maps, because a
    // bicubic read of a categorical field is meaningless -- halfway between desert (2) and
    // tundra (4) it reads grassland (3). The weights are continuous in the climate everywhere:
    // inverse-square distance in a normalised (temperature, moisture) plane, raised to an
    // integer sharpness and normalised to sum to one. A nearest-biome rule, or any kernel with
    // finite support and a fallback, would draw a hard edge where the envelopes stop overlapping.
    //
    // Pure, and float: the inputs are a macro sample that is bit-identical across a shared
    // root-quad edge, so the weights and gains are too, and two chunks sharing a vertex agree
    // on its height and its colour. No transcendental functions, so Burst and managed code
    // produce the same bits.
    public static class Biomes
    {
        // The vertex carries the weights as four bytes, COLOR (C6).
        public const int MaxBiomes = 4;

        // Added to every squared climate distance, so a sample exactly on a biome's centre has
        // a finite weight and the weights stay continuous through it. In normalised units: at
        // the shipped scales, a hundredth of a degree and a ten-thousandth of moisture.
        const float DistanceFloor = 1e-4f;

        // Weights of biomes 0..BiomeCount-1, summing to one; zero without biomes.
        //
        // Taken relative to the nearest biome, (d_nearest^2 / d_b^2)^sharpness, so the largest
        // weight before normalising is exactly one and nothing overflows or underflows at any
        // sharpness. The ratio is continuous because the nearest distance is.
        public static float4 Classify(in MacroSample m, in MacroParams p)
        {
            if (p.BiomeCount == 0) return 0f;

            float4 dt = (m.Temperature - p.BiomeTemperature) * p.InverseTemperatureScale;
            float4 dm = (m.Moisture    - p.BiomeMoisture)    * p.InverseMoistureScale;
            float4 d2 = dt * dt + dm * dm + DistanceFloor;

            bool4  used    = new bool4(p.BiomeCount > 0, p.BiomeCount > 1, p.BiomeCount > 2, p.BiomeCount > 3);
            float  nearest = math.cmin(math.select(float.MaxValue, d2, used));
            float4 ratio   = math.select(0f, nearest / d2, used);

            float4 w = ratio;
            for (int k = 1; k < p.BiomeSharpness; k++) w *= ratio;
            return w / math.csum(w);
        }

        // The detail noise's band gains here, relative to one (C5): the biomes' gains blended by
        // their weights, then the mid and high bands scaled by ruggedness. Ruggedness leaves the
        // low band, the continental shapes, to climate alone, and roughens or smooths the
        // terrain within it. With no biomes and a ruggedness range of 0..0 every term is
        // exactly zero, and the height is the M5a height to the bit.
        public static float3 BandGain(float4 weights, float ruggedness, in MacroParams p)
        {
            float3 amplitude = 1f + new float3(math.dot(weights, p.BiomeLowGain),
                                               math.dot(weights, p.BiomeMidGain),
                                               math.dot(weights, p.BiomeHighGain));
            float rugged = 1f + math.lerp(p.RuggednessGainMin, p.RuggednessGainMax, math.saturate(ruggedness));
            amplitude.yz *= rugged;
            return amplitude - 1f;
        }

        // Classifies a macro sample and folds its band gains into it: what the sample jobs and
        // TerrainHeightFunction.HeightAtPoint pass to the height function.
        public static MacroSample Modulate(MacroSample m, in MacroParams p, out float4 weights)
        {
            weights    = Classify(m, p);
            m.BandGain = BandGain(weights, m.Ruggedness, p);
            return m;
        }

        // The weights as the vertex carries them, UNorm8 x 4 with biome 0 in the lowest byte.
        // Rounding leaves the bytes summing to within two of 255; the shader renormalises.
        public static uint Pack(float4 weights)
        {
            uint4 b = (uint4)math.round(math.saturate(weights) * 255f);
            return b.x | (b.y << 8) | (b.z << 16) | (b.w << 24);
        }

        public static float4 Unpack(uint packed)
            => new float4(packed & 0xFFu, (packed >> 8) & 0xFFu, (packed >> 16) & 0xFFu, packed >> 24) / 255f;
    }
}
