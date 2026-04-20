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

            Debug.Log($"[WorldOriginSystem] Rebase fired. Shift={shift}, WorldOrigin now={WorldOrigin}, chunks shifted={TerrainChunk.AllChunks.Count}");
        }
    }
}
