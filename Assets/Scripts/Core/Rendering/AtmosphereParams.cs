using System;
using Unity.Mathematics;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Rendering
{
    // The atmosphere the skybox draws (SonomaSkybox.shader), resolved and validated once from a
    // scene's AtmosphereSettings, and the arithmetic of its fade, which the shader repeats in float
    // (SonomaSkyFactor in SonomaSky.hlsl).
    //
    // The sky is at full colour while the camera is inside the atmosphere and fades to black above
    // it, by the camera's altitude above the base surface, as LodVertical.Altitude measures it:
    //
    //   sphere, plane grid   full up to Thickness, black from Thickness + FadeDistance
    //   inside a cylinder    the same, the altitude measured from the wall towards the axis
    //   outside a cylinder   the air is held inside, so the fade starts at the wall: black from
    //                        FadeDistance beyond it, whatever the Thickness
    //
    // between them by a smoothstep of the distance into the fade. default(AtmosphereParams) is no
    // atmosphere: a black sky everywhere.
    //
    // UnityEngine-free, so the fade runs in the out-of-Editor harness.
    public readonly struct AtmosphereParams
    {
        // The ocean's sky gradient as it shipped before it moved here (Docs/Ocean_B_Shading_Plan.md):
        // the sine of the elevation at which the sky has risen from the haze at the horizon to the
        // sky's ambient. A scene with no atmosphere still reflects by it.
        public const double DefaultSkyGradientHeight = 0.35;

        public readonly bool   Enabled;
        public readonly double Thickness;         // metres above the base surface
        public readonly double FadeDistance;      // metres
        public readonly double SkyGradientHeight; // sine of the elevation, (0, 1]

        AtmosphereParams(double thickness, double fadeDistance, double skyGradientHeight)
        {
            Enabled           = true;
            Thickness         = thickness;
            FadeDistance      = fadeDistance;
            SkyGradientHeight = skyGradientHeight;
        }

        public static AtmosphereParams Create(double thickness, double fadeDistance, double skyGradientHeight)
        {
            if (!(thickness >= 0.0) || double.IsInfinity(thickness))
                throw new ArgumentOutOfRangeException(nameof(thickness), thickness,
                    "AtmosphereParams: the thickness must be finite and at least 0 metres.");
            if (!(fadeDistance > 0.0) || double.IsInfinity(fadeDistance))
                throw new ArgumentOutOfRangeException(nameof(fadeDistance), fadeDistance,
                    "AtmosphereParams: the fade distance must be finite and above 0 metres; a sky that " +
                    "cuts to black at once is a fade of a metre.");
            if (!(skyGradientHeight > 0.0 && skyGradientHeight <= 1.0))
                throw new ArgumentOutOfRangeException(nameof(skyGradientHeight), skyGradientHeight,
                    "AtmosphereParams: the sky gradient height is the sine of an elevation, in (0, 1].");
            return new AtmosphereParams(thickness, fadeDistance, skyGradientHeight);
        }

        // How far into the fade a camera at the absolute point p is, in metres: at most 0 inside the
        // air, FadeDistance or more where the sky is black.
        public double FadeDepth(in SurfaceDef surface, double3 p)
        {
            LodVertical.Altitude(surface, p, out double altitude, out _);
            return surface.Type == SurfaceType.Cylinder && altitude < 0.0
                ? -altitude
                : altitude - Thickness;
        }

        // The sky's brightness for a camera at the absolute point p: 1 inside the atmosphere, 0
        // above it, a smoothstep between. 0 everywhere without an atmosphere.
        public double SkyFactor(in SurfaceDef surface, double3 p) =>
            Enabled ? 1.0 - Smoothstep(FadeDepth(surface, p) / FadeDistance) : 0.0;

        // HLSL's smoothstep(0, 1, t).
        public static double Smoothstep(double t)
        {
            t = math.saturate(t);
            return t * t * (3.0 - 2.0 * t);
        }

        // _SonomaAtmosphere: (Thickness, 1 / FadeDistance, SkyGradientHeight, 1), and (0, 0,
        // DefaultSkyGradientHeight, 0) without an atmosphere, whose w zeroes the sky. An unset
        // global reads all zeros: a black sky, and the ocean's gradient as a step at the horizon.
        public float4 ShaderParams => Enabled
            ? new float4((float)Thickness, (float)(1.0 / FadeDistance), (float)SkyGradientHeight, 1f)
            : new float4(0f, 0f, (float)DefaultSkyGradientHeight, 0f);
    }
}
