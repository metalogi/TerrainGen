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
        [Tooltip("LOD selection and the morph measure distance as if every terrain height were " +
                 "scaled by this, while the terrain is drawn at full height (LodVertical). 1 " +
                 "measures in the true world, bit for bit as before this existed. The LOD relief " +
                 "allowance is a budget on slope, and HeightScale multiplies every slope; below 1 " +
                 "a terrain spends the allowance, and wants the chunks, of one this much flatter, " +
                 "so 0.2 carries five times the relief at the same SplitFactor. The LOD is never " +
                 "coarser than true distance would give; where height differences dominate, " +
                 "looking down into a deep valley, it is finer than it needs to be.")]
        [Range(0.05f, 1f)]
        public float LodVerticalScale = 1f;

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
                 "finest chunks hides nothing at coarse depths. One sized for coarse depths is " +
                 "never seen -- the skirt drops along the heightfield's own direction, so it lies " +
                 "underground and shows only through a crack -- but it pads every fine chunk's " +
                 "culling bounds by the same kilometres. SkirtFraction scales with the chunk.")]
        public float SkirtDepth = 10f;
        [Tooltip("Relief-relative part of the skirt's drop: each chunk's skirt hangs " +
                 "SkirtDepth + SkirtFraction x (its own measured relief, max - min height over " +
                 "its grid, + the surface's chord sag at its depth) below its edge, and its " +
                 "culling bounds are padded by the same. So a plain hangs a short skirt and a " +
                 "mountainside a long one. 0 leaves the skirt absolute and the meshes " +
                 "bit-identical to a build without this field. Needed when the depths span " +
                 "several orders of magnitude, as on an Earth-sized sphere, where the gap runs " +
                 "from under a metre at depth 19 to kilometres at depth 4. See " +
                 "ChunkMeshLayout.SkirtDrop.")]
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

        [Header("Close-up Roughness")]
        [Tooltip("Wavelength in metres below which the detail noise falls off by FinePersistence " +
                 "instead of Persistence. 0 turns it off: one slope throughout, bit for bit as " +
                 "before. At persistence 0.5 every octave is as steep as the last, so small " +
                 "features are tiny on a big world -- 10 m bumps about 9 mm tall on Earth -- and " +
                 "a FinePersistence above 0.5 below a crossover roughens the small scales alone.")]
        [Min(0f)]
        public double RoughnessCrossover = 0.0;
        [Tooltip("Persistence for the octaves finer than RoughnessCrossover, in (0, 1]. Each step " +
                 "above 0.5 makes every finer octave steeper, which spends the LOD relief " +
                 "allowance: size it against the relief tests' measured C, not by eye alone.")]
        public float FinePersistence = 0.5f;

        [Header("Gullies")]
        [Tooltip("Slope damping (M5c, G1): each octave no longer than GullyStartWavelength is " +
                 "divided by 1 + GullyDamping x slope^2, the slope of the coarser detail octaves in " +
                 "metres per metre. Steep ground loses fine detail, crests and flats keep it. 0 turns " +
                 "it off, bit for bit as before.")]
        [Min(0f)]
        public float GullyDamping = 0f;
        [Tooltip("Wavelength in metres: octaves no longer than this are damped. In a scene with " +
                 "erosion it must be shorter than the octaves erosion carries. At least 1 m; HeightParams " +
                 "refuses a start of 0.")]
        [Min(1f)]
        public double GullyStartWavelength = 1000.0;
        [Tooltip("Raises the damping on wet ground, by 1 + WetFloorDamping x wetness from the macro " +
                 "layer's erosion, so valley floors read smooth. 0 is off; without erosion wetness is " +
                 "0 and it does nothing.")]
        [Min(0f)]
        public float WetFloorDamping = 0f;

        [Header("Macro Layer")]
        [Tooltip("Persistent per-root-quad maps of base height and climate, built at startup and " +
                 "read by every chunk. None means no macro layer: the terrain is the detail noise " +
                 "alone, bit for bit as before the macro layer existed.")]
        public MacroLayerSettings Macro;

        [Header("Sky")]
        [Tooltip("Ambient light from straight up. Read only by a material with Sky Light and Haze " +
                 "on (_SONOMA_SKY): level ground gets this, a wall half this and half " +
                 "GroundAmbient, by the hemisphere each faces.")]
        public Color SkyAmbient = new Color(0.26f, 0.28f, 0.32f, 1f);
        [Tooltip("Ambient light from straight down: the light the ground bounces back.")]
        public Color GroundAmbient = new Color(0.09f, 0.08f, 0.07f, 1f);
        [Tooltip("The colour of the haze, lit by the sun on the day side and by SkyAmbient alone " +
                 "on the night side.")]
        public Color HazeColor = new Color(0.5f, 0.6f, 0.75f, 1f);
        [Tooltip("How far you can see through the haze at the base surface, in metres: the " +
                 "distance at which it has taken 98% of the contrast. 0 turns haze off.")]
        [Min(0f)]
        public float HazeVisibility = 0f;
        [Tooltip("Altitude over which the haze thins by a factor of e, in metres.")]
        [Min(1f)]
        public float HazeScaleHeight = 3000f;

        [Header("Ocean")]
        [Tooltip("A flat sea at SeaLevel, drawn on the terrain's own chunks and morphed with them " +
                 "(Docs/Ocean_A_Plan.md). Needs TerrainRoot's OceanMaterial. Off draws the terrain " +
                 "alone, bit for bit as before the ocean existed.")]
        public bool Ocean = false;
        [Tooltip("Metres above the base surface, along its normal: the height of the sea, and so of " +
                 "the coastline. A property of the world whether or not Ocean is on: with erosion it " +
                 "must equal the erosion's BaseLevel, whose outlets are the sea.")]
        public double SeaLevel = 0.0;
        [Tooltip("Metres. How far under the sea a whole chunk must lie to draw water alone; a chunk " +
                 "nearer the surface than this draws its terrain under the water too. 0 for a flat " +
                 "sea; waves will need at least their amplitude.")]
        [Min(0f)]
        public float OceanTerrainOverlap = 0f;
    }
}
