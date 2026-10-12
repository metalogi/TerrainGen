using UnityEditor;
using Sonoma.Core.CoordinateSpace;

// Zeroes the floating origin when the Editor returns to edit mode.
//
// WorldOriginSystem.WorldOrigin is static, and the Editor does not reload the domain on leaving
// play mode, so a session in EarthStress would otherwise leave it megametres out for every
// EditMode test run and the next session. The runtime half -- zeroing it on entering play mode
// -- lives on WorldOriginSystem itself. This half needs UnityEditor, so it lives here rather
// than behind a UNITY_EDITOR guard in the runtime assembly.
//
// It replaces a reset in WorldOriginSystem.OnDestroy, which tied a global to one component's
// lifetime: destroying the system mid-session would have moved the origin under terrain that
// was still drawing.
[InitializeOnLoad]
static class WorldOriginPlayModeReset
{
    static WorldOriginPlayModeReset()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                WorldOriginSystem.ResetOrigin();
        };
    }
}
