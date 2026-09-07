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
        public float HysteresisFactor = 1.2f;
        public float HeightScale = 200f;

        [Tooltip("Distance per LOD level indexed by depth (0 = root). Replaced by SplitFactor in M3.")]
        public float[] LodDistances = new float[] { 1000f, 500f, 250f, 125f, 60f, 30f, 15f, 7f, 3f };

        [Header("Generation Pipeline")]
        [Tooltip("Maximum chunk generation jobs in flight at once.")]
        public int MaxInFlightJobs = 8;
        [Tooltip("Main-thread time budget per frame for mesh uploads, in milliseconds. " +
                 "Time-based rather than a chunk count, because upload cost scales with resolution.")]
        public float UploadBudgetMs = 1.5f;

        [Header("Memory Budget")]
        [Tooltip("Maximum number of simultaneously active (visible) chunks. 0 = unlimited.")]
        public int MaxActiveChunks = 500;

        [Header("Skirts")]
        [Tooltip("World-space depth of the skirt geometry hanging below each chunk edge. " +
                 "A fallback for transient LOD differences; geomorphing (M3) is the real mechanism.")]
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
