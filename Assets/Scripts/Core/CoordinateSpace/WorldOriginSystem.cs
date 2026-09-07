using Unity.Mathematics;
using UnityEngine;
using Sonoma.Core.Rendering;

namespace Sonoma.Core.CoordinateSpace
{
    // Keeps rendered coordinates small by moving the world under the camera.
    //
    // Chunks are repositioned from their double anchors rather than shifted by a delta.
    // The prototype did `transform.position -= shift` in float on every rebase, which
    // accumulates error over a long session (SonomaRevisedPlan.md section 2.6); recomputing
    // `(float3)(Anchor - Origin)` cannot drift, because nothing carries forward. M4 stress
    // tests this over a thousand rebases and adds camera-rig handling.
    public class WorldOriginSystem : MonoBehaviour
    {
        public static double3 WorldOrigin = double3.zero;
        public float RebaseThreshold = 1000f;

        // Triplanar texturing samples on true world position; mesh vertices are chunk-local
        // and the transform is render space (world - origin). The shader adds this back so
        // sample coordinates stay continuous across rebases. All chunks share one origin per
        // frame, so seams stay aligned.
        const string ShaderOriginGlobal = "_SonomaWorldOrigin";

        void Awake() => PushOriginToShader();

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;

            Vector3 camPos = cam.transform.position;
            if (camPos.magnitude < RebaseThreshold) return;

            WorldOrigin += new double3(camPos.x, camPos.y, camPos.z);

            // Every chunk, including any held inactive, so a chunk shown later is already
            // in the right place.
            foreach (var chunk in TerrainChunk.AllChunks)
            {
                if (chunk == null) continue;
                chunk.Rebase(WorldOrigin);
            }

            cam.transform.position = Vector3.zero;
            PushOriginToShader();
        }

        static void PushOriginToShader()
        {
            Shader.SetGlobalVector(ShaderOriginGlobal,
                new Vector4((float)WorldOrigin.x, (float)WorldOrigin.y, (float)WorldOrigin.z, 0f));
        }
    }
}
