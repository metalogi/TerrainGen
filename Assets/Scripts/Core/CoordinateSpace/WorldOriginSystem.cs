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

        // The render-space view position TerrainRoot pushes once a frame for the morph. Named
        // here because it is a render-space position, and every render-space position has to
        // move with the origin -- see ShiftOrigin.
        public const string ViewPositionGlobal = "_SonomaViewPosition";

        [Tooltip("The camera the world is rebased around, and the one TerrainRoot selects LOD " +
                 "and morphs from. Empty means Camera.main. One field for both, because a " +
                 "rebase that moved one camera while the terrain measured from another would " +
                 "select and morph for a point a whole RebaseThreshold away from the view.")]
        public Camera ViewCamera;

        [Tooltip("Rebase once the camera is this far from the render-space origin, in metres. " +
                 "Float spacing at 1000 m is about 0.06 mm, so this bounds the error in " +
                 "anything near the camera far below what a pixel can show; lowering it buys " +
                 "nothing visible and costs a pass over every chunk per rebase. The floor of " +
                 "the threshold when RebasePerClearance is set.")]
        public float RebaseThreshold = 1000f;

        [Tooltip("Raise the threshold to this multiple of the camera's clearance above the " +
                 "ground, when something reports it through ViewClearance. 0 leaves the " +
                 "threshold fixed. The error a rebase guards against is about 6e-8 of the " +
                 "camera's render-space distance from the origin, and what it has to stay under " +
                 "is a pixel at the nearest geometry, about 1e-3 of the clearance; at 10 x the " +
                 "clearance that is under a thousandth of a pixel. It matters when the camera " +
                 "is fast and high: at a fixed 1000 m a descent at 50 km/s rebases every frame, " +
                 "each a pass over every chunk.")]
        [Min(0f)]
        public float RebasePerClearance = 0f;

        // The camera's clearance above the ground this frame, in metres, from whatever knows it
        // -- TopologyFlyCamera sets it. Zero (the default, or a camera at or below the ground)
        // leaves the threshold at RebaseThreshold.
        public float ViewClearance { get; set; }

        public float EffectiveRebaseThreshold =>
            math.max(RebaseThreshold, RebasePerClearance * ViewClearance);

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

        static readonly int OriginId       = Shader.PropertyToID(ShaderOriginGlobal);
        static readonly int ViewPositionId = Shader.PropertyToID(ViewPositionGlobal);

        // The system in the scene, for ActiveCamera. Not the origin's owner: the origin is
        // static and reset at play-mode boundaries (ResetOrigin), never by an instance going
        // away -- a component destroyed mid-session would otherwise yank the origin out from
        // under terrain that is still drawing.
        static WorldOriginSystem _active;

        // The camera the world is rebased around and the terrain is measured from.
        public static Camera ActiveCamera =>
            _active != null && _active.ViewCamera != null ? _active.ViewCamera : Camera.main;

        void Awake()
        {
            _active = this;
            PushOriginToShader();
        }

        void OnDestroy()
        {
            if (_active == this) _active = null;
        }

        // The origin is static, so it outlives the scene that moved it: the Editor does not
        // reload the domain on leaving play mode, and need not on entering it. Before M4 that
        // never mattered, because no scene ever moved it; now a session in EarthStress leaves it
        // megametres out, and the next session -- or an EditMode test run -- would place every
        // chunk relative to a camera on the other side of a planet. So it is zeroed at both
        // play-mode boundaries: on entering by ResetOnEnteringPlay, and on leaving by
        // WorldOriginPlayModeReset in the Editor assembly. Tests that set it save and restore it
        // themselves.
        public static void ResetOrigin()
        {
            ResetOnEnteringPlay();
            PushOriginToShader();
        }

        // Before the first scene loads, which is too early to be touching shader state; Awake
        // pushes the origin once the system exists. Matters only with domain reload disabled,
        // which otherwise carries the last session's statics straight into the next.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnEnteringPlay()
        {
            WorldOrigin = double3.zero;
            _active     = null;
        }

        void LateUpdate()
        {
            var cam = ActiveCamera;
            if (cam == null) return;

            Vector3 camPos = cam.transform.position;
            if (camPos.magnitude < EffectiveRebaseThreshold) return;

            ShiftOrigin(camPos);
            cam.transform.position = Vector3.zero;
        }

        // Moves the origin by `shift` (render-space metres) and everything rendered with it:
        // every chunk recomputed from its anchor, every tracked transform shifted by the delta,
        // and the view position the morph measures from. The camera is the caller's to move;
        // LateUpdate zeroes it.
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

            // TerrainRoot pushes the view position in Update and this runs in LateUpdate, so
            // without this the frame of every rebase renders chunks moved by -shift and a
            // camera at zero, morphing against a point a whole threshold away from either. Near
            // the ground, where morph windows are a few hundred metres wide, that is the near
            // field jumping to its parents' geometry for one frame per rebase. The point is
            // the same absolute position the selector ran from this frame, re-expressed in
            // the new render space, so selection and the morph still agree.
            Vector4 view = Shader.GetGlobalVector(ViewPositionId);
            Shader.SetGlobalVector(ViewPositionId,
                new Vector4(view.x - shift.x, view.y - shift.y, view.z - shift.z, view.w));

            PushOriginToShader();
        }

        static void PushOriginToShader()
        {
            Shader.SetGlobalVector(OriginId,
                new Vector4((float)WorldOrigin.x, (float)WorldOrigin.y, (float)WorldOrigin.z, 0f));
        }
    }
}
