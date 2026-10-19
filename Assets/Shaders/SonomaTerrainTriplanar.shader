// Triplanar terrain shader with topology-aware elevation/slope blending, and the LOD
// geomorph.
//
// Per-vertex inputs from ChunkMeshJob:
//   TEXCOORD1.xyz = topology-up unit vector (the surface normal at h=0, in world space)
//   TEXCOORD1.w   = elevation scalar (height value × HeightScale, in world units)
//   TEXCOORD2.xyz = geomorph target position, chunk-local like POSITION
//   TEXCOORD2.w   = the chunk quadtree depth, which indexes the morph range array
//   TEXCOORD3.xyz = geomorph target normal
//   TEXCOORD3.w   = geomorph target elevation, blended against TEXCOORD1.w
//   COLOR         = biome weights, UNorm8 x 4, one per biome slot (Biomes.Pack)
//   TEXCOORD0     = geomorph target biome weights, UNorm8 x 4, blended against COLOR on the
//                   morph's k for the reason the elevation is: without them the colour pops at
//                   the swap where the geometry does not. TEXCOORD0 was a float2 uv nothing
//                   read; the two splats took its eight bytes, so the vertex is still 80.
//
// Biome colours are a feature keyword, _SONOMA_BIOMES, local and shader_feature: a material
// that enables it is what keeps the variant in a build, and a global keyword would leak
// between scenes in the Editor. TerrainBiomes.mat enables it for the stress scenes;
// TerrainTriplaner.mat, SampleScene's, does not, so SampleScene never compiles the path.
// Under it the ground colour is the weights' blend of _SonomaBiomeColors and the elevation
// bands are unused; the cliff overlay stays. Only ForwardLit reads colour, so only it changes.
//
// The sky is a second feature keyword of the same kind, _SONOMA_SKY, on in TerrainBiomes.mat
// and off in SampleScene's material (M5b). Under it ForwardLit takes its ambient from the
// local up -- sky colour from above, ground colour from below, by the hemisphere a tilted
// surface faces -- rather than from world-space SH, whose up is world Y and so is the local up
// only at a planet's north pole. And it adds aerial haze: the colour fades towards the light
// the air scatters, by the transmittance of an exponential atmosphere along the view ray. That
// is the cue that separates one ridge from the ridge behind it, which a directional light
// alone shows only near the terminator.
//
// Per-frame globals:
//   _SonomaWorldOrigin (float3), from WorldOriginSystem.PushOriginToShader — added to
//   positionWS so triplanar samples on true world coordinates and stays continuous
//   through floating-origin rebases.
//
//   PRECISION LIMIT, deliberately not fixed (Docs/M4_FloatingOrigin_Plan.md C4). The sum is
//   a float the size of the world, so at Earth radius it is quantised to 0.5 m: every
//   fragment within half a metre samples the same texel, and the pattern jumps by up to
//   0.25 m each time the origin rebases. Invisible today only because TerrainTriplaner.mat
//   has no textures, so every sample is the default white. The fix, when textures are real:
//   reduce the origin modulo a large power of two, in double, before it is pushed, so the
//   sum stays small. That tiles seamlessly only if every triplanar period (1 / _*Scale)
//   divides the modulus, so texture periods must be powers of two metres -- the current
//   0.05 scales are 20 m periods and would not qualify.
//
//   _SonomaMorphRanges (float4[32]), from TerrainRoot.PushShaderGlobals — (start, end) per
//   depth. A global array rather than a per-chunk MaterialPropertyBlock, because a
//   property block breaks SRP batching; that is the whole reason depth travels in a
//   vertex attribute instead of a material property.
//
//   _SonomaBiomeColors (float4[4]), from TerrainRoot.PushShaderGlobals — one linear colour
//   per biome slot, from the BiomeDefinition assets. A global for the reason the morph ranges
//   are; written into a material instead it would, in the Editor, permanently edit the .mat
//   asset from play mode.
//
//   _SonomaSkyAmbient, _SonomaGroundAmbient, _SonomaHaze and _SonomaSurface (float4 each),
//   from TerrainRoot.PushShaderGlobals out of the scene's TerrainSettings: the ambient from
//   straight up and straight down, the haze colour and its density at the base surface, and
//   the surface's type, radius and the haze's inverse scale height. Read only under
//   _SONOMA_SKY. Globals because they are per scene and the material is shared by four.
//
//   _SonomaViewPosition (float3), from TerrainRoot.PushShaderGlobals — the position the
//   morph measures distance from, in render space, and the same one LodSelector selected
//   with this frame; WorldOriginSystem.ShiftOrigin moves it with the world when a rebase
//   lands after the push. NOT _WorldSpaceCameraPos: URP restores that for the main-light
//   shadow pass but not for the additional-lights one, so a scene with a shadow-casting
//   point or spot light and no shadowed directional light renders the terrain's shadow
//   caster against a stale camera and morphs it by a different k than ForwardLit used.
//
// The morph runs in ALL FOUR passes. Morphing only ForwardLit leaves shadows and the
// depth prepass on unmorphed geometry, which presents as shadow acne and depth-test
// dropouts — symptoms that look nothing like the cause.
//
// Layer model: 3 elevation bands (Low/Mid/High) blended by elevation thresholds, plus a
// Cliff overlay driven by slope = 1 − dot(worldNormal, topologyUp). Each layer is
// (color × triplanar texture sample), so default white textures + tinted color = solid color.
Shader "Sonoma/TerrainTriplanar"
{
    Properties
    {
        [Header(Low Band)]
        _LowColor  ("Low Color",   Color) = (0.30, 0.45, 0.20, 1)
        _LowTex    ("Low Texture", 2D)    = "white" {}
        _LowScale  ("Low Triplanar Scale", Float) = 0.05

        [Header(Mid Band)]
        _MidColor  ("Mid Color",   Color) = (0.55, 0.45, 0.30, 1)
        _MidTex    ("Mid Texture", 2D)    = "white" {}
        _MidScale  ("Mid Triplanar Scale", Float) = 0.05

        [Header(High Band)]
        _HighColor ("High Color",   Color) = (0.95, 0.95, 0.95, 1)
        _HighTex   ("High Texture", 2D)    = "white" {}
        _HighScale ("High Triplanar Scale", Float) = 0.05

        [Header(Cliff)]
        _CliffColor ("Cliff Color",   Color) = (0.40, 0.35, 0.30, 1)
        _CliffTex   ("Cliff Texture", 2D)    = "white" {}
        _CliffScale ("Cliff Triplanar Scale", Float) = 0.05

        [Header(Elevation Bands)]
        _LowToMid  ("Low to Mid Elevation",  Float) = 10.0
        _MidToHigh ("Mid to High Elevation", Float) = 30.0
        _BandBlend ("Band Blend Width",      Float) = 4.0

        [Header(Slope)]
        _SlopeThreshold ("Slope Threshold", Range(0, 1))   = 0.55
        _SlopeBlend     ("Slope Blend Width", Range(0, 0.5)) = 0.15

        [Header(Triplanar)]
        _TriplanarSharpness ("Triplanar Sharpness", Range(1, 16)) = 4

        [Header(Biomes)]
        [Toggle(_SONOMA_BIOMES)] _SonomaBiomes ("Biome Colours", Float) = 0

        [Header(Sky)]
        [Toggle(_SONOMA_SKY)] _SonomaSky ("Sky Light and Haze", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType"      = "Opaque"
            "RenderPipeline"  = "UniversalPipeline"
            "Queue"           = "Geometry"
            "IgnoreProjector" = "True"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_LowTex);   SAMPLER(sampler_LowTex);
        TEXTURE2D(_MidTex);   SAMPLER(sampler_MidTex);
        TEXTURE2D(_HighTex);  SAMPLER(sampler_HighTex);
        TEXTURE2D(_CliffTex); SAMPLER(sampler_CliffTex);

        // SRP Batcher: every pass must share this exact layout.
        CBUFFER_START(UnityPerMaterial)
            float4 _LowTex_ST;
            float4 _MidTex_ST;
            float4 _HighTex_ST;
            float4 _CliffTex_ST;
            float4 _LowColor;
            float4 _MidColor;
            float4 _HighColor;
            float4 _CliffColor;
            float  _LowScale;
            float  _MidScale;
            float  _HighScale;
            float  _CliffScale;
            float  _LowToMid;
            float  _MidToHigh;
            float  _BandBlend;
            float  _SlopeThreshold;
            float  _SlopeBlend;
            float  _TriplanarSharpness;
        CBUFFER_END

        // Per-frame globals (NOT in UnityPerMaterial -- set via Shader.SetGlobal*).
        float3 _SonomaWorldOrigin;

        // One linear colour per biome slot; see the header. Read only under _SONOMA_BIOMES.
        float4 _SonomaBiomeColors[4];

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
        // correction and like every chunk transform.
        float3 _SonomaViewPosition;

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
        float SonomaMorphFactor(float3 positionOS, float depth)
        {
            float3 wp   = TransformObjectToWorld(positionOS);
            float  dist = distance(wp, _SonomaViewPosition);
            float2 r    = _SonomaMorphRanges[clamp((int)depth, 0, SONOMA_MAX_MORPH_DEPTH)].xy;
            return saturate((dist - r.x) / max(r.y - r.x, 1e-5));
        }

        // Returns the factor it used, so a caller that has more than position and normal to
        // blend -- ForwardLit, with its elevation -- does not recompute it.
        float SonomaMorph(inout float3 positionOS, inout float3 normalOS,
                          float4 morphPosition, float3 morphNormal)
        {
            float k    = SonomaMorphFactor(positionOS, morphPosition.w);
            positionOS = lerp(positionOS, morphPosition.xyz, k);
            normalOS   = lerp(normalOS,   morphNormal,       k);
            // Deliberately not normalized here: the fragment stages normalize what they
            // receive, and a lerp of two unit normals is only short, never wrong.
            return k;
        }

        // Position only, for the depth prepass, which has no normal to morph. Its position
        // must still match the other passes exactly, or the depth test rejects the lit
        // geometry it is supposed to accept.
        void SonomaMorphPosition(inout float3 positionOS, float4 morphPosition)
        {
            positionOS = lerp(positionOS, morphPosition.xyz,
                              SonomaMorphFactor(positionOS, morphPosition.w));
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            // These must match URP's own Lit.shader for the installed URP version. A
            // keyword the pipeline enables but the shader does not declare does not warn --
            // the shader simply gets the variant with that keyword OFF. Missing
            // _SHADOWS_SOFT_LOW/MEDIUM/HIGH (Unity 6 replaced the single _SHADOWS_SOFT with
            // a quality tier) is why this shader rendered single-tap hard shadows no matter
            // what Soft Shadows quality the URP asset asked for.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            // Biome colours. Local and shader_feature on purpose; see the header.
            #pragma shader_feature_local _SONOMA_BIOMES
            // Sky light and haze, likewise.
            #pragma shader_feature_local _SONOMA_SKY

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

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
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                #if defined(_SONOMA_BIOMES)
                    float4 splat      : COLOR;     // biome weights
                    float4 morphSplat : TEXCOORD0; // geomorph target biome weights
                #endif
                float4 uv2        : TEXCOORD1; // (topoUp.xyz, elevation)
                float4 morphPos   : TEXCOORD2; // (geomorph target position.xyz, node depth)
                float4 morphNrm   : TEXCOORD3; // (geomorph target normal.xyz, target elevation)
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 topoUp      : TEXCOORD2;
                float  elevation   : TEXCOORD3;
                // ONLY interpolated when URP says it is safe to. Under
                // _MAIN_LIGHT_SHADOWS_CASCADE the shadow coord is not an interpolatable
                // quantity: TransformWorldToShadowCoord picks a cascade with
                // ComputeCascadeIndex, a hard step across the four camera-centred split
                // spheres, and each cascade has its own matrix into its own quadrant of the
                // shadow atlas. Interpolating across a triangle whose vertices fall in
                // different cascades samples a point between two unrelated atlas tiles.
                // See the note above Frag.
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    float4 shadowCoord : TEXCOORD4;
                #endif
                #if defined(_SONOMA_BIOMES)
                    float4 splat       : TEXCOORD5;
                #endif
            };

            half3 SampleTriplanar(TEXTURE2D_PARAM(tex, smp), float3 wp, float3 absN, float scale)
            {
                half3 cx = SAMPLE_TEXTURE2D(tex, smp, wp.yz * scale).rgb;
                half3 cy = SAMPLE_TEXTURE2D(tex, smp, wp.xz * scale).rgb;
                half3 cz = SAMPLE_TEXTURE2D(tex, smp, wp.xy * scale).rgb;
                float3 w = pow(absN, _TriplanarSharpness);
                w /= max(w.x + w.y + w.z, 1e-5);
                return cx * w.x + cy * w.y + cz * w.z;
            }

            Varyings Vert(Attributes input)
            {
                Varyings o;

                float3 positionOS = input.positionOS.xyz;
                float3 normalOS   = input.normalOS;
                float  k = SonomaMorph(positionOS, normalOS, input.morphPos, input.morphNrm.xyz);

                VertexPositionInputs vpi = GetVertexPositionInputs(positionOS);
                VertexNormalInputs   vni = GetVertexNormalInputs(normalOS);
                o.positionCS  = vpi.positionCS;
                o.positionWS  = vpi.positionWS;
                o.normalWS    = vni.normalWS;
                // Chunk transforms are unrotated in this project, but TransformObjectToWorldDir
                // is the safe form if a parent ever introduces rotation.
                o.topoUp      = TransformObjectToWorldDir(input.uv2.xyz);
                // Elevation morphs with everything else, on the same k. The bands are
                // driven by this value, so leaving it on the fine elevation while the
                // geometry moves to the coarse one shifts the band blend at exactly the
                // moment the parent takes over -- a colour seam where there is no
                // geometric one.
                o.elevation   = lerp(input.uv2.w, input.morphNrm.w, k);
                #if defined(_SONOMA_BIOMES)
                    // The biome weights morph on the same k, towards the parent's at the even
                    // vertex, for the elevation's reason.
                    o.splat   = lerp(input.splat, input.morphSplat, k);
                #endif
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    o.shadowCoord = GetShadowCoord(vpi);
                #endif
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 N     = normalize(input.normalWS);
                float3 absN  = abs(N);
                // Quantised to 0.5 m at Earth radius; see PRECISION LIMIT in the header.
                float3 wpTri = input.positionWS + _SonomaWorldOrigin;

                half3 lo  = _LowColor.rgb   * SampleTriplanar(_LowTex,   sampler_LowTex,   wpTri, absN, _LowScale);
                half3 mid = _MidColor.rgb   * SampleTriplanar(_MidTex,   sampler_MidTex,   wpTri, absN, _MidScale);
                half3 hi  = _HighColor.rgb  * SampleTriplanar(_HighTex,  sampler_HighTex,  wpTri, absN, _HighScale);
                half3 clf = _CliffColor.rgb * SampleTriplanar(_CliffTex, sampler_CliffTex, wpTri, absN, _CliffScale);

                #if defined(_SONOMA_BIOMES)
                    // The biome colours blended by the vertex's weights, renormalised: they
                    // arrive as bytes summing to within two of 255, and interpolation across a
                    // triangle keeps them non-negative but not summing to one. A vertex with no
                    // weight at all -- a terrain with this material and no biomes -- falls back
                    // to the low band's colour rather than black. The low band's texture stays as
                    // the triplanar detail, white until textures are real (M7).
                    float4 s   = max(input.splat, 0);
                    float  sum = s.x + s.y + s.z + s.w;
                    half3  biome = (s.x * _SonomaBiomeColors[0].rgb + s.y * _SonomaBiomeColors[1].rgb +
                                    s.z * _SonomaBiomeColors[2].rgb + s.w * _SonomaBiomeColors[3].rgb)
                                   / max(sum, 1e-5);
                    half3  band = sum > 1e-3
                        ? biome * SampleTriplanar(_LowTex, sampler_LowTex, wpTri, absN, _LowScale)
                        : lo;
                #else
                    float halfBlend = _BandBlend * 0.5;
                    float t1 = smoothstep(_LowToMid  - halfBlend, _LowToMid  + halfBlend, input.elevation);
                    float t2 = smoothstep(_MidToHigh - halfBlend, _MidToHigh + halfBlend, input.elevation);
                    half3 band = lerp(lerp(lo, mid, t1), hi, t2);
                #endif

                float3 topoUp = normalize(input.topoUp);
                float  slope  = saturate(1.0 - dot(N, topoUp));
                float  cliffB = smoothstep(_SlopeThreshold, _SlopeThreshold + _SlopeBlend, slope);
                half3  albedo = lerp(band, clf, cliffB);

                // Shadow coordinate, resolved exactly the way URP's own InitializeInputData
                // does. The cheap per-vertex path is valid only for a single non-cascaded
                // shadow map (and for screen-space shadows, which are already in NDC);
                // REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR is URP's name for that
                // condition, and it is deliberately NOT defined for
                // _MAIN_LIGHT_SHADOWS_CASCADE. Computing it per vertex regardless -- which
                // is what this shader did -- puts a ring of triangles at every cascade split
                // sphere whose interpolated coord lands in the wrong atlas tile, so the
                // depth compare fails and the ring reads as a thin black line. Concentric,
                // and it follows the camera, because the split spheres do.
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    float4 shadowCoord = input.shadowCoord;
                #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                    float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #else
                    float4 shadowCoord = float4(0, 0, 0, 0);
                #endif

                // The three-argument overload, not GetMainLight(shadowCoord): only this one
                // applies GetMainLightShadowFade(positionWS), which fades the shadow out
                // over the cascade border and to nothing at the shadow distance. Without it
                // shadows terminate at a hard circle at _ShadowDistance -- another
                // camera-following ring, on top of the cascade ones. shadowMask is 1: this
                // project bakes nothing, and CALCULATE_BAKED_SHADOWS is not defined here.
                Light  ml      = GetMainLight(shadowCoord, input.positionWS, half4(1, 1, 1, 1));
                half   NdotL   = saturate(dot(N, ml.direction));
                half3  direct  = albedo * ml.color * (ml.distanceAttenuation * ml.shadowAttenuation) * NdotL;

                #if defined(_SONOMA_SKY)
                    // The sky by the local up: a surface tilted from it sees the sky in proportion
                    // (1 + cos tilt) / 2 and the ground in the rest.
                    half  skyView = 0.5 + 0.5 * dot(N, topoUp);
                    half3 ambient = albedo * lerp(_SonomaGroundAmbient.rgb, _SonomaSkyAmbient.rgb, skyView);
                    half3 color   = direct + ambient;

                    // Aerial perspective, from the camera rendering this pass: _WorldSpaceCameraPos
                    // is right here, where it is wrong for the morph, because ForwardLit is a camera
                    // pass and URP binds it for one; the shadow passes are where it goes stale. At a
                    // density of zero the transmittance is exactly 1 and the colour is unchanged.
                    half T = exp(-_SonomaHaze.a * SonomaOpticalDepth(_WorldSpaceCameraPos, input.positionWS));
                    color  = color * T + SonomaHazeLight(topoUp, ml) * (1.0 - T);
                #else
                    half3 ambient = albedo * SampleSH(N) * 0.5;
                    half3 color   = direct + ambient;
                #endif

                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ColorMask 0
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag

            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttr
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 morphPos   : TEXCOORD2;
                float4 morphNrm   : TEXCOORD3;   // .w unused here; the layout is shared
            };
            struct ShadowVar  { float4 positionCS : SV_POSITION; };

            ShadowVar ShadowVert(ShadowAttr input)
            {
                float3 positionOS = input.positionOS.xyz;
                float3 normalOS   = input.normalOS;
                SonomaMorph(positionOS, normalOS, input.morphPos, input.morphNrm.xyz);

                ShadowVar o;
                float3 positionWS = TransformObjectToWorld(positionOS);
                float3 normalWS   = TransformObjectToWorldNormal(normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDir = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDir = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDir));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.positionCS = positionCS;
                return o;
            }

            half4 ShadowFrag(ShadowVar i) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ColorMask 0
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex   DepthVert
            #pragma fragment DepthFrag

            // POSITION alone was enough before the morph; the depth prepass now has to
            // move its vertices exactly as ForwardLit does, so it needs TEXCOORD2 too.
            struct DAttr { float4 positionOS : POSITION; float4 morphPos : TEXCOORD2; };
            struct DVar  { float4 positionCS : SV_POSITION; };

            DVar DepthVert(DAttr i)
            {
                DVar o;
                float3 positionOS = i.positionOS.xyz;
                SonomaMorphPosition(positionOS, i.morphPos);
                o.positionCS = TransformObjectToHClip(positionOS);
                return o;
            }
            half4 DepthFrag(DVar i) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex   DNVert
            #pragma fragment DNFrag

            struct DNAttr
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 morphPos   : TEXCOORD2;
                float4 morphNrm   : TEXCOORD3;   // .w unused here; the layout is shared
            };
            struct DNVar  { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };

            DNVar DNVert(DNAttr i)
            {
                DNVar o;
                float3 positionOS = i.positionOS.xyz;
                float3 normalOS   = i.normalOS;
                SonomaMorph(positionOS, normalOS, i.morphPos, i.morphNrm.xyz);

                o.positionCS = TransformObjectToHClip(positionOS);
                o.normalWS   = TransformObjectToWorldNormal(normalOS);
                return o;
            }
            half4 DNFrag(DNVar i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                return half4(n * 0.5 + 0.5, 1);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
