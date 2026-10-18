using Unity.Mathematics;

namespace Sonoma.Core.Generation
{
    // A bicubic read of the persistent per-root-quad macro maps (MacroSampler): base height and
    // the climate fields biomes are classified from. Later, erosion and river masks.
    //
    // It has appeared in TerrainHeightFunction.Height's signature since M2, holding zero, so
    // that adding the macro layer in M5 was a change to one function body rather than a
    // signature change rippling through both jobs and every test. See SonomaRevisedPlan.md
    // section 4.6.
    //
    // Note the direction: the macro layer is sampled *into* the height function, not
    // upsampled from a parent chunk. Chunks never depend on their ancestors, which is what
    // lets them generate in any order and on any thread.
    //
    // default(MacroSample) means "no macro layer", and every field added here must keep it so:
    // a default sample has to leave the height function returning M4's bits exactly, which is
    // what keeps SampleScene and every relief figure pinned against it valid. BaseHeight 0 adds
    // exactly nothing. M5b's per-band amplitude is therefore stored as a gain relative to one,
    // whose default is the identity, and not as a multiplier, whose default flattens the world.
    // HeightFunctionTests.HeightIsBitIdenticalWithoutAMacroLayer pins it.
    //
    // There is no biome id here, nor in the maps. A bicubic read of a categorical field is
    // meaningless -- halfway between desert (2) and tundra (4) it reads grassland (3) -- so the
    // maps store the continuous fields and biomes are classified per vertex from them (M5b).
    public struct MacroSample
    {
        // Metres, added to the height. Double so the height narrows once, at the end of
        // TerrainHeightFunction.Height, as the detail noise does.
        public double BaseHeight;

        public float Temperature;   // degrees C
        public float Moisture;      // 0..1
        public float Ruggedness;    // 0..1; scales detail amplitude independently of climate (M5b)

        public static MacroSample Zero => default;
    }
}
