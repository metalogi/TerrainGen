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
                 "measured worst case over a face at MaxDepth 8, the working set is 1898 " +
                 "chunks at 3 and 2946 at 4 on geometry alone, and rough terrain adds to both. " +
                 "What the extra buys is room for rough terrain -- it raises both the " +
                 "MorphStartFraction ceiling and LodMath.MaxHalfRelief.")]
        public float SplitFactor = 4f;
        [Tooltip("Collapse distance as a multiple of the split distance. Must be at least 1; " +
                 "applies to the split decision only, never to the morph range. It does eat " +
                 "into the usable MorphStartFraction, because a node held split by hysteresis " +
                 "reaches further out than the split distance alone would allow.")]
        public float HysteresisFactor = 1.1f;
        [Tooltip("Fraction of the morph range over which a chunk morphs towards its parent. " +
                 "Bounded by LodMath.MaxMorphStartFraction(SplitFactor, HysteresisFactor, " +
                 "surface size spread) -- above it the coarse side of a LOD boundary starts " +
                 "morphing before the fine side has finished, and the boundary cracks. " +
                 "LodMath.Create throws rather than let that ship. Terrain roughness spends the " +
                 "same budget and is checked separately, at runtime, because fbm relief cannot be " +
                 "bounded tightly in advance: LodSelector warns on the first chunk rougher than " +
                 "LodMath.MaxHalfRelief allows, and names the roughest when the terrain shuts " +
                 "down. Lowering this narrows the morph window and raises that allowance; size it " +
                 "against the roughest node, measured over the whole surface as " +
                 "TerrainReliefTests does, not a sample.")]
        [Range(0.02f, 0.5f)]
        public float MorphStartFraction = 0.15f;
        [Tooltip("A node asks for its four children once it is within this multiple of its own " +
                 "split distance, so they are usually resident before the camera crosses it. " +
                 "Must be at least 1; at exactly 1 there is no margin and the children are " +
                 "requested the instant the split is decided. This is the one LOD number that " +
                 "trades memory for latency directly, and above about 1.2 it is not cheap: " +
                 "measured on the default configuration, worst case over a face with every " +
                 "split hysteresis can hold, the working set is 2734 chunks at 1.0, 2946 at 1.15 " +
                 "and 4350 at 1.5 on geometry alone, and more on rough terrain. Keep " +
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
                 "SplitFactor 4, PreloadFactor 1.15) wants 2946 worst case over a face, 2430 " +
                 "near a face centre, on geometry alone. Terrain adds to that: a node's bounding " +
                 "sphere is widened by half its relief, so rough ground splits further out. " +
                 "Measure the working set with the terrain, as WorkingSetTests does; the default " +
                 "leaves about a third over the geometric figure for it. A budget below the " +
                 "working set makes the selector build and evict the same chunks every frame; it " +
                 "warns on the first frame over budget and reports the peak when the terrain " +
                 "shuts down. At resolution 33 a chunk is about 98 KB of vertex data, so this " +
                 "caps it at roughly 390 MB.")]
        public int MaxResidentChunks = 4000;

        [Header("Skirts")]
        [Tooltip("Skirts are the fallback for transient states where the tree is briefly more " +
                 "than one depth apart across an edge. Geomorphing is the real mechanism; turn " +
                 "these off to prove it, then leave them on.")]
        public bool SkirtsEnabled = true;
        [Tooltip("Absolute part of the skirt's drop below each chunk edge, in metres, the same " +
                 "at every depth. On its own it suits one scale only: the gap a skirt has to " +
                 "hide is on the order of the coarse node's relief, so a value sized for the " +
                 "finest chunks hides nothing at coarse depths, and one sized for coarse depths " +
                 "hangs out of every hillside below a fine chunk. SkirtFraction scales with the node.")]
        public float SkirtDepth = 10f;
        [Tooltip("Node-relative part of the skirt's drop: each chunk's skirt hangs " +
                 "SkirtDepth + SkirtFraction x its nominal node size below its edge, and its " +
                 "culling bounds are padded by the same. 0 leaves the skirt absolute and the " +
                 "meshes bit-identical to a build without this field. Needed when the depths " +
                 "span several orders of magnitude, as on an Earth-sized sphere, where the gap " +
                 "runs from about half a metre at depth 19 to a kilometre at depth 8.")]
        [Min(0f)]
        public float SkirtFraction = 0f;

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
