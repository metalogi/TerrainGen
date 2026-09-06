using Unity.Mathematics;

namespace Sonoma.Core.Generation
{
    // A bicubic read of the persistent per-root-quad macro maps: base height, biome,
    // moisture, temperature, and later erosion and river masks.
    //
    // M5 fills this in. It appears in TerrainHeightFunction.Height's signature from M2
    // onwards, holding zero, so that adding the macro layer later is a change to one
    // function body rather than a signature change rippling through both jobs and every
    // test. See SonomaRevisedPlan.md section 4.6.
    //
    // Note the direction: the macro layer is sampled *into* the height function, not
    // upsampled from a parent chunk. Chunks never depend on their ancestors, which is what
    // lets them generate in any order and on any thread.
    public struct MacroSample
    {
        public float BaseHeight;

        public static MacroSample Zero => default;
    }
}
