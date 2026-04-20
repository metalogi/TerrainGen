using UnityEngine;

namespace Sonoma.Systems.Configuration
{
    [CreateAssetMenu(fileName = "TerrainSettings", menuName = "Sonoma/Terrain Settings")]
    public class TerrainSettings : ScriptableObject
    {
        [Header("Chunk / LOD")]
        public int ChunkResolution = 33;
        public int MaxDepth = 8;
        public int MaxUploadsPerFrame = 3;
        public float HysteresisFactor = 1.2f;
        public float HeightScale = 200f;

        [Tooltip("Distance per LOD level indexed by depth (0 = root)")]
        public float[] LodDistances = new float[] { 1000f, 500f, 250f, 125f, 60f, 30f, 15f, 7f, 3f };

        [Header("Noise - Level 0")]
        public float BaseFrequency = 0.5f;
        public int Octaves = 6;
        public float Persistence = 0.5f;
        public uint Seed = 42;
    }
}
