// The sea's waves (ocean-b, Docs/Ocean_B_Plan.md): a sum of Gerstner waves displacing the water
// grid, and the same sum, carried further, in the water's per-fragment normal. Included by
// SonomaOcean.shader alone, after SonomaCommon.hlsl, in its HLSLINCLUDE, so all three of its passes
// displace by the same code. The terrain never includes it.
//
// The globals, pushed every frame by OceanWaveGlobals (Core/Quadtree), and once more at a rebase:
//   _SonomaWaves[i]     = (K.xyz in cycles a metre, amplitude in metres); K is a float's value,
//                         rounded so when OceanWaves.Build made the table
//   _SonomaWaveExtra[i] = (wavelength, choppiness Q, shoal depth, 0)
//   _SonomaWavePhase[i] = frac(K . O - F t + Phase0), cycles, computed in double for the origin O
//   _SonomaWaveCount    = how many are in use; 0 for a flat sea or none, which draws ocean-a's sea
//   _SonomaWaveLod      = (the depth-0 vertex spacing, NominalSize(0) / (R - 1), 0, 0, 0)
//
// The phase is split where the precision is (B1). A wave's phase at an absolute point X is
// K . X - F t + Phase0, and X is kilometres or megametres from anything a float can hold to a
// millimetre. So the shader takes K . p in float, p the vertex's render-space position, which is
// metres from the camera wherever a wave is drawn, and adds the rest, pushed once a frame in double
// and narrowed. A rebase moves O by s and every p by -s, so WorldOriginSystem raises OriginShifted
// and the offsets are re-pushed from the new origin before the frame renders. A rebase then moves
// the phase at a fixed point by float rounding of K . p alone: under 1.3e-4 of a cycle at 3 km.
//
// Each wave displaces by the morphed position and fades by the effective depth (B2). The position
// is the one the geomorph has already moved, so a fully morphed child displaces the points its
// parent draws. The fade is smoothstep(4, 8, wavelength / spacing), the spacing that of the
// effective depth e = depth - k, the one number two chunks meeting at a point agree on: the same
// rule CLAUDE.md gives for the terrain's cracks. A fade by distance would agree too, but only in a
// settled tree; this one reads the depth actually drawn, in every transient state. So no wave is
// ever displaced at fewer than four vertices a wavelength, and a fully morphed child (depth d,
// k = 1) fades every wave exactly as its parent (d - 1, k = 0) does.
//
// Shallow water damps each wave by saturate(floor depth / shoal depth), the floor the morphed one
// (B5), so the longest waves flatten first towards the shore, and water of no depth does not move.
//
// The water's skirt bottoms are flagged (COLOR.b, ChunkMeshLayout.WaterSkirtFlag) and are not
// displaced: a Gerstner wave moves the water sideways, by different amounts on two sides a
// transient state apart, and a skirt slanting back to the undisplaced line closes that strip where
// one hanging straight down from a displaced edge would not.
//
// The normal (B3) takes every wave the geometry draws at its geometric fade, so the lit surface is
// the drawn one, and every wave a pixel can resolve at smoothstep(2, 4, wavelength / footprint), so
// waves keep shading the sea well past where they stop moving it. What neither carries widens the
// glint instead: the slope variance (1 - weight) (2 pi a / wavelength)^2 / 2, summed.
#ifndef SONOMA_WAVES_INCLUDED
#define SONOMA_WAVES_INCLUDED

// OceanWaves.MaxWaves.
#define SONOMA_MAX_WAVES 16

float4 _SonomaWaves[SONOMA_MAX_WAVES];
float4 _SonomaWaveExtra[SONOMA_MAX_WAVES];
float  _SonomaWavePhase[SONOMA_MAX_WAVES];
int    _SonomaWaveCount;
float4 _SonomaWaveLod;

// OceanWaves.FadeStart and FadeFull, in vertices a wavelength; the normals' pair in pixels.
#define SONOMA_WAVE_FADE_START  4.0
#define SONOMA_WAVE_FADE_FULL   8.0
#define SONOMA_WAVE_PIXEL_START 2.0
#define SONOMA_WAVE_PIXEL_FULL  4.0

int SonomaWaveCount() { return clamp(_SonomaWaveCount, 0, SONOMA_MAX_WAVES); }

// How much of wave i the geometry draws at effective depth e: OceanWaves.Fade at
// OceanWaves.Spacing(_SonomaWaveLod.x, e). HLSL's smoothstep is that function.
float SonomaWaveGeometricFade(int i, float effectiveDepth)
{
    float spacing = _SonomaWaveLod.x * exp2(-effectiveDepth);
    return smoothstep(SONOMA_WAVE_FADE_START, SONOMA_WAVE_FADE_FULL, _SonomaWaveExtra[i].x / max(spacing, 1e-20));
}

// saturate(floor depth / shoal depth), and no damping at a shoal depth of 0 (ShoalFactor 0).
float SonomaWaveShoal(int i, float floorDepth)
{
    float shoal = _SonomaWaveExtra[i].z;
    return shoal > 0.0 ? saturate(floorDepth / shoal) : 1.0;
}

// The phase as OceanWaves.ShaderPhase computes it, in [0, 1): K . p in float, plus the offset.
float SonomaWavePhase(int i, float3 positionWS)
{
    return frac(dot(_SonomaWaves[i].xyz, positionWS) + _SonomaWavePhase[i]);
}

// Wave i's direction across the sea, K projected onto the tangent plane and divided by |K|: a unit
// vector where the wave runs along the surface, shrinking smoothly to nothing where it runs along
// the up, so the wave only bobs there rather than turning (B4, the poles).
float3 SonomaWaveTangent(int i, float3 up)
{
    float3 K = _SonomaWaves[i].xyz;
    return (K - dot(K, up) * up) * _SonomaWaveExtra[i].x;
}

// Moves a morphed water vertex by the waves the geometry draws there, in object space, and returns
// its undisplaced render-space position, for the fragment's normal.
//   normalOS        the morphed base normal: the water's up, as the parent has it at full morph
//   effectiveDepth  morphPosition.w - k
//   floorDepth      the morphed floor depth below the sea, for the shoal damping
//   skirtFlag       COLOR.b: 1 on the skirt's bottom row, which is never displaced
//   geometricNormalWS  the normal of the drawn surface, for DepthNormals: the unit base normal
//                      exactly when nothing displaces, so a flat sea writes ocean-a's normals
float3 SonomaWaveDisplace(inout float3 positionOS, float3 normalOS, float effectiveDepth,
                          float floorDepth, float skirtFlag, out float3 geometricNormalWS)
{
    float3 positionWS = TransformObjectToWorld(positionOS);
    float3 up = TransformObjectToWorldNormal(normalOS);   // normalised

    float3 offset   = float3(0, 0, 0);
    float3 slope    = float3(0, 0, 0);
    float  stretch  = 0.0;
    float  keep     = 1.0 - step(0.5, skirtFlag);
    int    count    = SonomaWaveCount();

    [loop]
    for (int i = 0; i < count; i++)
    {
        float a = _SonomaWaves[i].w * SonomaWaveGeometricFade(i, effectiveDepth) * SonomaWaveShoal(i, floorDepth);
        if (a <= 0.0) continue;

        float  s, c;
        sincos(TWO_PI * SonomaWavePhase(i, positionWS), s, c);
        float3 t = SonomaWaveTangent(i, up);
        float  q = _SonomaWaveExtra[i].y;
        float  w = TWO_PI / _SonomaWaveExtra[i].x;       // radians a metre

        // Height a cos, and the Gerstner sideways move towards the crest, -Q a sin along t.
        offset  += up * (a * c) - t * (q * a * s);
        // The normal of the sum: up (1 - sum Q a w cos) + sum a w sin t.
        slope   += t * (a * w * s);
        stretch += q * a * w * c;
    }

    positionOS += TransformWorldToObjectDir(offset * keep, false);
    geometricNormalWS = up * (1.0 - stretch) + slope;
    return positionWS;
}

// The water's normal at a fragment (B3), from its undisplaced position: every wave at the larger of
// its geometric fade and its fade by the pixel footprint, with the shoal damping. slopeVariance is
// what that leaves out, for the glint. With no waves, up exactly and a variance of 0.
float3 SonomaWaveNormal(float3 positionWS, float3 up, float effectiveDepth, float footprint,
                        float floorDepth, out float slopeVariance)
{
    float3 slope   = float3(0, 0, 0);
    float  stretch = 0.0;
    slopeVariance  = 0.0;
    int    count   = SonomaWaveCount();

    [loop]
    for (int i = 0; i < count; i++)
    {
        float lambda = _SonomaWaveExtra[i].x;
        float a      = _SonomaWaves[i].w * SonomaWaveShoal(i, floorDepth);
        if (a <= 0.0) continue;

        float weight = max(SonomaWaveGeometricFade(i, effectiveDepth),
                           smoothstep(SONOMA_WAVE_PIXEL_START, SONOMA_WAVE_PIXEL_FULL, lambda / max(footprint, 1e-6)));
        float w      = TWO_PI / lambda;
        float steep  = a * w;
        slopeVariance += (1.0 - weight) * steep * steep * 0.5;

        float aw = steep * weight;
        if (aw <= 0.0) continue;
        float s, c;
        sincos(TWO_PI * SonomaWavePhase(i, positionWS), s, c);
        slope   += SonomaWaveTangent(i, up) * (aw * s);
        stretch += _SonomaWaveExtra[i].y * aw * c;
    }
    return up * (1.0 - stretch) + slope;
}

// A Blinn-Phong power widened by a slope variance: the lobe's own variance, 2 / (power + 2) as a
// Beckmann roughness squared, plus the waves' (Toksvig's idea). Written so a variance of 0 returns
// the power to the bit.
float SonomaWaveGlintPower(float power, float slopeVariance)
{
    float t = slopeVariance * (power + 2.0) * 0.5;
    return power - (power + 2.0) * t / (1.0 + t);
}

#endif // SONOMA_WAVES_INCLUDED
