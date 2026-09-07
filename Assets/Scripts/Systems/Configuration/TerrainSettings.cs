using UnityEngine;

namespace Sonoma.Systems.Configuration
{
    [CreateAssetMenu(fileName = "TerrainSettings", menuName = "Sonoma/Terrain Settings")]
    public class TerrainSettings : ScriptableObject
    {
        [Header("Chunk / LOD")]
        [Tooltip("Vertices per chunk edge. Must be odd: the geomorph target sub-grid is the " +
                 "even-indexed vertices, and an even resolution leaves it half a vertex out of step.")]
        public int ChunkResolution = 33;
        public int MaxDepth = 8;
        public float HeightScale = 200f;

        [Header("LOD")]
        [Tooltip("Split a node when the camera is closer than SplitFactor x the node's nominal " +
                 "size. Nominal, not measured: see LodMath.NominalSize. " +
                 "2 is NOT usable with the geomorph: it leaves a morph window under 1% wide on " +
                 "a cube sphere, which is a pop by another name. Costs scale with the square, " +
                 "so 3 is roughly 2.25x the chunks of 2.")]
        public float SplitFactor = 3f;
        [Tooltip("Collapse distance as a multiple of the split distance. Must be at least 1; " +
                 "applies to the split decision only, never to the morph range. It does eat " +
                 "into the usable MorphStartFraction, because a node held split by hysteresis " +
                 "reaches further out than the split distance alone would allow.")]
        public float HysteresisFactor = 1.1f;
        [Tooltip("Fraction of the morph range over which a chunk morphs towards its parent. " +
                 "Bounded by LodMath.MaxMorphStartFraction(SplitFactor, HysteresisFactor, " +
                 "surface size spread) -- above it the coarse side of a LOD boundary starts " +
                 "morphing before the fine side has finished, and the boundary cracks. " +
                 "LodMath.Create throws rather than let that ship.")]
        [Range(0.02f, 0.5f)]
        public float MorphStartFraction = 0.15f;
        [Tooltip("Children are requested within this multiple of the split distance, so they " +
                 "are usually resident before the camera crosses it.")]
        public float PreloadFactor = 1.5f;

        [Header("Generation Pipeline")]
        [Tooltip("Maximum chunk generation jobs in flight at once.")]
        public int MaxInFlightJobs = 8;
        [Tooltip("Main-thread time budget per frame for mesh uploads, in milliseconds. " +
                 "Time-based rather than a chunk count, because upload cost scales with resolution.")]
        public float UploadBudgetMs = 1.5f;

        [Header("Memory Budget")]
        [Tooltip("Maximum number of resident chunks, counting hidden parents held for an " +
                 "instant collapse -- not just the visible ones. 0 = unlimited. The default " +
                 "LOD configuration (MaxDepth 8, SplitFactor 2, Earth-radius cube-sphere) " +
                 "wants about 770; a budget below the working set makes the selector build " +
                 "and evict the same chunks every frame, and it says so once in the console.")]
        public int MaxResidentChunks = 2000;

        [Header("Skirts")]
        [Tooltip("Skirts are the fallback for transient states where the tree is briefly more " +
                 "than one depth apart across an edge. Geomorphing is the real mechanism; turn " +
                 "these off to prove it, then leave them on.")]
        public bool SkirtsEnabled = true;
        [Tooltip("World-space depth of the skirt geometry hanging below each chunk edge.")]
        public float SkirtDepth = 10f;

        [Header("Noise")]
        [Tooltip("World-space wavelength of octave 0, in metres. 0 means 'one root quad across'.")]
        public double OctaveWavelength0 = 0.0;
        [Tooltip("Total octaves available. The band limit per depth picks a prefix of these.")]
        public int OctaveCount = 20;
        public float Persistence = 0.5f;
        public float Lacunarity = 2f;
        public uint Seed = 42;
    }
}
