using UnityEngine;
using Sonoma.Core.CoordinateSpace;

namespace Sonoma.Core.Generation
{
    public static class HeightmapGenerator
    {
        // Simple CPU heightmap generator (Level 0 friendly surface sampling)
        public static float[,] Generate(int resolution, UnityEngine.Vector2 minUV, UnityEngine.Vector2 maxUV, BaseMeshQuad quad, float baseFreq = 0.005f, int octaves = 4)
        {
            var outHM = new float[resolution, resolution];

            for (int y = 0; y < resolution; y++)
            {
                float v = Mathf.Lerp(minUV.y, maxUV.y, y / (float)(resolution - 1));
                for (int x = 0; x < resolution; x++)
                {
                    float u = Mathf.Lerp(minUV.x, maxUV.x, x / (float)(resolution - 1));

                    var w = CoordinateTransform.ToWorldPosition(u, v, 0.0, quad);
                    float sample = 0f;
                    float amp = 1f;
                    float freq = baseFreq;
                    for (int o = 0; o < octaves; o++)
                    {
                        sample += amp * Mathf.PerlinNoise((float)(w.x * freq), (float)(w.z * freq));
                        amp *= 0.5f;
                        freq *= 2f;
                    }

                    outHM[x, y] = sample;
                }
            }

            return outHM;
        }
    }
}
