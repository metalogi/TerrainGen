// The sky light and aerial haze (M5b, _SONOMA_SKY), shared by SonomaTerrainTriplanar and, from
// ocean-a, SonomaOcean. Shared rather than copied because a sea hazed by other arithmetic than
// the coast behind it shows a seam at the waterline that no tuning removes, and two copies would
// drift.
//
// Include it from each ForwardLit pass, after Lighting.hlsl (it takes URP's Light), with
// `#pragma shader_feature_local _SONOMA_SKY` declared. Everything here is under the keyword, so a
// material without it, SampleScene's, compiles none of it. The globals are documented in
// SonomaTerrainTriplanar.shader's header.
#ifndef SONOMA_SKY_INCLUDED
#define SONOMA_SKY_INCLUDED

#if defined(_SONOMA_SKY)
    // Per scene, from TerrainRoot.PushShaderGlobals; see the header. Linear colours.
    float4 _SonomaSkyAmbient;    // rgb: ambient from straight up
    float4 _SonomaGroundAmbient; // rgb: ambient from straight down
    float4 _SonomaHaze;          // rgb: haze colour; a: density at the base surface, per metre
    float4 _SonomaSurface;       // x: SurfaceType (0 plane grid, 1 cube sphere, 2 cylinder);
                                 // y: radius; z: 1 / haze scale height, per metre

    // Altitude above the base surface of a point in absolute coordinates. SurfaceMath's
    // sphere is centred on the absolute origin, its cylinder runs along the Z axis and
    // is seen from inside, and its plane grid lies at y = 0. In float, so at Earth
    // radius it is good to about half a metre, which haze with a scale height of
    // kilometres does not notice.
    float SonomaAltitude(float3 p)
    {
        if (_SonomaSurface.x > 1.5) return _SonomaSurface.y - length(p.xy);
        if (_SonomaSurface.x > 0.5) return length(p) - _SonomaSurface.y;
        return p.y;
    }

    // Optical depth along the straight path from eye to p, both in render space, in
    // metres of air at base-surface density: the integral of exp(-h / H) over the
    // path, with the altitude h taken as linear within each of SONOMA_HAZE_STEPS
    // steps. Linear within a step, each step integrates exactly, to
    // len x (e^-a - e^-b) / (b - a) for end altitudes a and b in scale heights, and
    // that is what keeps a path that climbs out of the air -- across a ring to its far
    // side, or from orbit -- from being charged the base density all the way: the step
    // comes to about len x H / climb. A straight path across a sphere's surface dips
    // below the line between its ends, so a step this coarse undercounts grazing
    // paths over the horizon, which are hazed out regardless. The step length comes
    // from render space, where a short path keeps its precision; only the altitudes
    // need the origin added back.
    #define SONOMA_HAZE_STEPS 4
    float SonomaOpticalDepth(float3 eye, float3 p)
    {
        float invH  = _SonomaSurface.z;
        float len   = distance(eye, p) / SONOMA_HAZE_STEPS;
        float a     = max(SonomaAltitude(eye + _SonomaWorldOrigin) * invH, -20.0);
        float depth = 0.0;
        [unroll]
        for (int i = 1; i <= SONOMA_HAZE_STEPS; i++)
        {
            float3 q  = lerp(eye, p, i / (float)SONOMA_HAZE_STEPS);
            float  b  = max(SonomaAltitude(q + _SonomaWorldOrigin) * invH, -20.0);
            float  db = b - a;
            depth += len * (abs(db) > 1e-4 ? (exp(-a) - exp(-b)) / db : exp(-a));
            a = b;
        }
        return depth;
    }

    // What the haze between the eye and the ground scatters towards the eye: its
    // colour, lit by the sun where the air is in daylight and by the sky alone where it
    // is not. Daylight is judged at the fragment, from its own up, with a twilight band
    // about the terminator rather than a hard line.
    half3 SonomaHazeLight(float3 topoUp, Light ml)
    {
        half day = smoothstep(-0.1, 0.3, dot(topoUp, ml.direction));
        return _SonomaHaze.rgb * (ml.color * day + _SonomaSkyAmbient.rgb);
    }

    // A lit colour seen through the haze between the camera and positionWS (render space),
    // towards the light the haze scatters. The one composite every shader uses, so ground and
    // water fade alike.
    //
    // Aerial perspective, from the camera rendering this pass: _WorldSpaceCameraPos
    // is right here, where it is wrong for the morph, because ForwardLit is a camera
    // pass and URP binds it for one; the shadow passes are where it goes stale. At a
    // density of zero the transmittance is exactly 1 and the colour is unchanged.
    half3 SonomaApplyHaze(half3 color, float3 positionWS, float3 topoUp, Light ml)
    {
        half T = exp(-_SonomaHaze.a * SonomaOpticalDepth(_WorldSpaceCameraPos, positionWS));
        return color * T + SonomaHazeLight(topoUp, ml) * (1.0 - T);
    }
#endif

#endif // SONOMA_SKY_INCLUDED
