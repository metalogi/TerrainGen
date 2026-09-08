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
                 "a cube sphere, which is a pop by another name. Costs scale with the square: " +
                 "measured worst case over a face at MaxDepth 8, the working set is 1878 " +
                 "chunks at 3 and 2970 at 4. What the extra buys is room for rough terrain -- " +
                 "it raises both the MorphStartFraction ceiling and LodMath.MaxHalfRelief, so " +
                 "4 is what lets a 300 m sphere carry a 50 m height scale without seams.")]
        public float SplitFactor = 4f;
        [Tooltip("Collapse distance as a multiple of the split distance. Must be at least 1; " +
                 "applies to the split decision only, never to the morph range. It does eat " +
                 "into the usable MorphStartFraction, because a node held split by hysteresis " +
                 "reaches further out than the split distance alone would allow.")]
        public float HysteresisFactor = 1.1f;
        [Tooltip("Fraction of the morph range over which a chunk morphs towards its parent. " +
                 "Bounded by LodMath.MaxMorphStartFraction(SplitFactor, HysteresisFactor, " +
                 "surface size spread, terrain amplitude as a multiple of the smallest leaf) " +
                 "-- above it the coarse side of a LOD boundary starts morphing before the " +
                 "fine side has finished, and the boundary cracks. LodMath.Create throws " +
                 "rather than let that ship. Terrain roughness spends the same budget and is " +
                 "checked separately, at runtime, because fbm relief cannot be bounded tightly " +
                 "in advance: LodSelector warns once naming the numbers if the generated " +
                 "terrain is rougher than LodMath.MaxHalfRelief allows.")]
        [Range(0.02f, 0.5f)]
        public float MorphStartFraction = 0.15f;
        [Tooltip("A node asks for its four children once it is within this multiple of its own " +
                 "split distance, so they are usually resident before the camera crosses it. " +
                 "Must be at least 1; at exactly 1 there is no margin and the children are " +
                 "requested the instant the split is decided. This is the one LOD number that " +
                 "trades memory for latency directly, and it is not cheap: measured on the " +
                 "default configuration, worst case over a face, the resident working set is " +
                 "2382 chunks at 1.0, 2970 at 1.15 and 4326 at 1.5. Keep " +
                 "MaxResidentChunks above whichever you pick.")]
        public float PreloadFactor = 1.15f;

        [Header("Generation Pipeline")]
        [Tooltip("Maximum chunk generation jobs in flight at once.")]
        public int MaxInFlightJobs = 8;
        [Tooltip("Main-thread time budget per frame for mesh uploads, in milliseconds. " +
                 "Time-based rather than a chunk count, because upload cost scales with resolution.")]
        public float UploadBudgetMs = 1.5f;

        [Header("Memory Budget")]
        [Tooltip("Maximum number of resident chunks, counting hidden parents held for an " +
                 "instant collapse and children held by the preload margin -- not just the " +
                 "visible ones. 0 = unlimited. The default LOD configuration (MaxDepth 8, " +
                 "SplitFactor 4, PreloadFactor 1.15) wants 2970 worst case over a face, 2430 " +
                 "near a face centre. A budget below the working set makes the selector build " +
                 "and evict the same chunks every frame, and it says so once in the console. " +
                 "At resolution 33 a chunk is about 98 KB of vertex data, so this default is " +
                 "roughly 310 MB; drop PreloadFactor to 1.0 to save about 600 chunks of it.")]
        public int MaxResidentChunks = 3200;

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
