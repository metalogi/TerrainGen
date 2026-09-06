using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using Sonoma.Core.Rendering;

namespace Sonoma.Core.CoordinateSpace
{
    public class WorldOriginSystem : MonoBehaviour
    {
        public static double3 WorldOrigin = new double3(0, 0, 0);
        public float RebaseThreshold = 1000f;

        // Triplanar texturing samples on true world position; mesh vertices are stored in
        // render space (world − origin). The shader adds this back to recover continuous
        // sample coordinates across origin rebases. All chunks share one origin per frame,
        // so seams stay aligned.
        const string ShaderOriginGlobal = "_SonomaWorldOrigin";

        void Awake()
        {
            PushOriginToShader();
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;

            Vector3 camPos = cam.transform.position;
            if (camPos.magnitude < RebaseThreshold) return;

            double3 shift = new double3(camPos.x, camPos.y, camPos.z);
            WorldOrigin += shift;

            // Shift ALL chunks — including hidden parents — so they're correct if re-shown
            var shiftVec = new Vector3((float)shift.x, (float)shift.y, (float)shift.z);
            foreach (var t in TerrainChunk.AllChunks)
            {
                if (t == null) continue;
                t.transform.position -= shiftVec;
            }

            cam.transform.position = Vector3.zero;
            PushOriginToShader();

            Debug.Log($"[WorldOriginSystem] Rebase fired. Shift={shift}, WorldOrigin now={WorldOrigin}, chunks shifted={TerrainChunk.AllChunks.Count}");
        }

        static void PushOriginToShader()
        {
            Shader.SetGlobalVector(ShaderOriginGlobal,
                new Vector4((float)WorldOrigin.x, (float)WorldOrigin.y, (float)WorldOrigin.z, 0f));
        }
    }
}
