// The geomorph, shared by every shader that draws on the terrain's nodes: SonomaTerrainTriplanar
// and, from ocean-a, SonomaOcean. Shared rather than copied because the two must arrive at the
// same k for a vertex at the same depth and distance. Two copies of this code would drift, and
// a water surface morphing by another k than the ground beside it opens a gap at every LOD
// boundary along the coast.
//
// Include it from the shader's HLSLINCLUDE, after Core.hlsl and after the shader's own
// UnityPerMaterial block. It needs nothing but Core.hlsl, so every pass can use it. The globals
// it declares are documented in SonomaTerrainTriplanar.shader's header.
#ifndef SONOMA_COMMON_INCLUDED
#define SONOMA_COMMON_INCLUDED

// Per-frame globals (NOT in UnityPerMaterial -- set via Shader.SetGlobal*).
float3 _SonomaWorldOrigin;

// (start_d, end_d, 0, 0): the distances over which a depth-d chunk morphs onto its
// parent. Ranges nest exactly (end_{d-1} == 2*end_d), so at any boundary between
// depths d and d-1 the fine side is at k = 1 where the coarse side is still at
// k = 0. See LodMath, and SonomaRevisedPlan.md section 4.4.
// LodMath.Create refuses a MaxDepth above what NodeId can address (30), so no chunk
// can reach this index. The clamp is the belt to that brace: it bounds the read, but
// it does NOT degrade gracefully -- slot 31's range is a few centimetres wide, so a
// chunk landing there would be pinned at k = 1 and drawn permanently morphed onto
// its parent, not drawn unmorphed.
#define SONOMA_MAX_MORPH_DEPTH 31
float4 _SonomaMorphRanges[SONOMA_MAX_MORPH_DEPTH + 1];

// Render space (world - origin), like positionWS before the _SonomaWorldOrigin
// correction and like every chunk transform. Flattened, as the LOD sees the camera.
float3 _SonomaViewPosition;

// (Flatten, Anchor, 0, 0); see the header and LodVertical.
float4 _SonomaLodVertical;

// Distance is measured from the UNMORPHED position. That matters twice over: it is
// what lets every pass arrive at the same k for the same vertex, and it is what
// makes two chunks sharing a vertex agree -- they compute the same world position
// for it, so they compute the same distance and the same k. Per vertex, never per
// chunk.
//
// Both positions are render space, so their difference is true world metres and
// needs no _SonomaWorldOrigin correction, unlike the triplanar sampling in the
// fragment stage.
//
// _SonomaViewPosition rather than _WorldSpaceCameraPos, and that is not a style
// choice. URP sets _WorldSpaceCameraPos for the main-light shadow pass explicitly
// (MainLightShadowCasterPass calls ShadowUtils.SetCameraPosition, commented "not set
// for passes executed before normal rendering") and AdditionalLightsShadowCasterPass
// does not. So with a shadow-casting point or spot light and no shadowed directional
// light, the ShadowCaster pass reads whatever was last bound -- a stale frame, or
// another camera -- and morphs the shadow geometry by a different k than ForwardLit
// and DepthOnly used. That is the same shadow-acne-along-LOD-boundaries failure the
// "morph in every pass" rule exists to prevent, arrived at from the other side.
// TerrainRoot pushes this once a frame from the camera it also selected with, so all
// four passes agree by construction rather than by URP's pass order.
//
// The vertex is flattened before the distance is taken, by the map LodVertical gives
// the camera and LodMath.NodeDistance the bounding spheres: moved along its topology
// up by Flatten x (elevation - Anchor), with the UNMORPHED elevation, TEXCOORD1.w, for
// the reason the position is unmorphed. topoUp and elevation are attributes, the same
// bits in every chunk that shares the vertex, so the move is too. topoUp goes through
// TransformObjectToWorldDir, as ForwardLit's does: chunk transforms are unrotated, so
// that is the identity today, and the move stays right if a parent ever rotates them.
//
// The anchor moves every point, the camera included, by about Flatten x Envelope -- 68 km
// on Earth -- so both ends of the distance sit that far from the render-space origin and
// carry float rounding of a few millimetres. Across the morph windows that is a k error
// under 1e-3: sub-millimetre motion, at a shared vertex or anywhere else.
float SonomaMorphFactor(float3 positionOS, float4 topoElevation, float depth)
{
    float3 up   = TransformObjectToWorldDir(topoElevation.xyz);
    float3 wp   = TransformObjectToWorld(positionOS)
                - up * (_SonomaLodVertical.x * (topoElevation.w - _SonomaLodVertical.y));
    float  dist = distance(wp, _SonomaViewPosition);
    float2 r    = _SonomaMorphRanges[clamp((int)depth, 0, SONOMA_MAX_MORPH_DEPTH)].xy;
    return saturate((dist - r.x) / max(r.y - r.x, 1e-5));
}

// Returns the factor it used, so a caller that has more than position and normal to
// blend -- ForwardLit, with its elevation -- does not recompute it.
float SonomaMorph(inout float3 positionOS, inout float3 normalOS, float4 topoElevation,
                  float4 morphPosition, float3 morphNormal)
{
    float k    = SonomaMorphFactor(positionOS, topoElevation, morphPosition.w);
    positionOS = lerp(positionOS, morphPosition.xyz, k);
    normalOS   = lerp(normalOS,   morphNormal,       k);
    // Deliberately not normalized here: the fragment stages normalize what they
    // receive, and a lerp of two unit normals is only short, never wrong.
    return k;
}

// Position only, for the depth prepass, which has no normal to morph. Its position
// must still match the other passes exactly, or the depth test rejects the lit
// geometry it is supposed to accept.
void SonomaMorphPosition(inout float3 positionOS, float4 topoElevation, float4 morphPosition)
{
    positionOS = lerp(positionOS, morphPosition.xyz,
                      SonomaMorphFactor(positionOS, topoElevation, morphPosition.w));
}

#endif // SONOMA_COMMON_INCLUDED
