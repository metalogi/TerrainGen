using System.Collections.Generic;
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
    // `(float3)(Anchor - Origin)` cannot drift, because nothing carries forward.
    // WorldOriginTests.RebaseIsExactAfterAThousandShifts holds that to the bit.
    //
    // The camera must be a ROOT object. A rebase sets its world position to zero, which is
    // right for a lone camera and wrong for one under a rig: the camera jumps to the origin
    // and the rig stays where it was, so from then on the camera sits at an offset from
    // everything the rig does. Put a rig's root in Tracked and parent nothing to the camera
    // that should not move with it.
    public class WorldOriginSystem : MonoBehaviour
    {
        public static double3 WorldOrigin = double3.zero;

        [Tooltip("Rebase once the camera is this far from the render-space origin, in metres. " +
                 "Float spacing at 1000 m is about 0.06 mm, so this bounds the error in " +
                 "anything near the camera far below what a pixel can show; lowering it buys " +
                 "nothing visible and costs a pass over every chunk per rebase.")]
        public float RebaseThreshold = 1000f;

        [Tooltip("Transforms that live in render space alongside the camera -- rigs, " +
                 "characters, props -- and must move with the world when it rebases. Each is " +
                 "shifted by the same delta the camera is. Unlike chunks they have no double " +
                 "anchor to be recomputed from, so they do accumulate float rounding across " +
                 "rebases; anything that has to stay put to the millimetre over a long session " +
                 "should carry its own double position, as TerrainChunk does. Do not list the " +
                 "camera or one of its ancestors: the camera is zeroed separately.")]
        public List<Transform> Tracked = new List<Transform>();

        // Triplanar texturing samples on true world position; mesh vertices are chunk-local
        // and the transform is render space (world - origin). The shader adds this back so
        // sample coordinates stay continuous across rebases. All chunks share one origin per
        // frame, so seams stay aligned.
        //
        // The shader's sum is a float the size of the world, so at Earth radius the triplanar
        // coordinate is quantised to 0.5 m and jumps by up to 0.25 m at each rebase. Invisible
        // today because the terrain material has no textures; read the shader header and
        // CLAUDE.md before giving it some.
        const string ShaderOriginGlobal = "_SonomaWorldOrigin";

        void Awake() => PushOriginToShader();

        // The origin is static, so it outlives the scene that moved it: the Editor does not
        // reload the domain on leaving play mode, and need not on entering it. Before M4 that
        // never mattered, because no scene ever moved it. Now a session in EarthStress leaves
        // it megametres out, and without this the next session -- or an EditMode test run --
        // would place every chunk relative to a camera on the other side of a planet.
        void OnDestroy()
        {
            WorldOrigin = double3.zero;
            PushOriginToShader();
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;

            Vector3 camPos = cam.transform.position;
            if (camPos.magnitude < RebaseThreshold) return;

            ShiftOrigin(camPos);
            cam.transform.position = Vector3.zero;
        }

        // Moves the origin by `shift` (render-space metres) and everything rendered with it:
        // every chunk recomputed from its anchor, every tracked transform shifted by the delta.
        // The camera is the caller's to move; LateUpdate zeroes it.
        public void ShiftOrigin(Vector3 shift)
        {
            WorldOrigin += new double3(shift.x, shift.y, shift.z);

            // Every chunk, including any held inactive, so a chunk shown later is already
            // in the right place.
            foreach (var chunk in TerrainChunk.AllChunks)
            {
                if (chunk == null) continue;
                chunk.Rebase(WorldOrigin);
            }

            foreach (var t in Tracked)
                if (t != null) t.position -= shift;

            PushOriginToShader();
        }

        static void PushOriginToShader()
        {
            Shader.SetGlobalVector(ShaderOriginGlobal,
                new Vector4((float)WorldOrigin.x, (float)WorldOrigin.y, (float)WorldOrigin.z, 0f));
        }
    }
}
