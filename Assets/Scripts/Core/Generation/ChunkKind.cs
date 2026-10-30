namespace Sonoma.Core.Generation
{
    // What a chunk draws, with an ocean (Docs/Ocean_A_Plan.md, A1 and A7): terrain alone, terrain
    // and water, or water alone. A scene with no ocean writes only Land chunks, by the code it
    // always ran. Stored as a float in ChunkMeshJob's HeightBounds[5], so the values are fixed.
    public enum ChunkKind : byte
    {
        Land  = 0,
        Coast = 1,
        Ocean = 2,
    }

    public static class ChunkKinds
    {
        // A chunk's kind from its drawn range (A1): lo and hi are the min and max over its fine
        // heights and the coarse heights at its morph targets, so every frame of every morph state
        // lies in [lo, hi]. Land when all of it is above the highest the water can reach, Ocean
        // when all of it is under the sea by more than the overlap, Coast otherwise. Exact, so it
        // cannot hide an island or drain a lake.
        //
        // A tie is Coast, and so is anything not a number, lo above hi included: Coast draws both,
        // so the kind that cannot be decided is the one that hides nothing. Each test also asks
        // the range to be ordered, which is false for a NaN at either end.
        public static ChunkKind Classify(float lo, float hi, float sea, float overlap, float waveAmplitude)
        {
            if (lo > sea + waveAmplitude && hi >= lo) return ChunkKind.Land;
            if (hi < sea - overlap       && lo <= hi) return ChunkKind.Ocean;
            return ChunkKind.Coast;
        }
    }
}
