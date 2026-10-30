// The sea (ocean-a): the water grid ChunkMeshJob writes on Coast and Ocean chunks, a flat surface
// SeaLevel metres above the base surface along its normal (Docs/Ocean_Plan.md C1). Opaque: the
// terrain under it is not drawn on an Ocean chunk at all (Docs/Ocean_A_Plan.md, A2), and on a Coast
// chunk it is hidden. The water's own submesh, drawn with the terrain's geomorph so it morphs by the
// same k as the ground beside it.
//
// Per-vertex inputs from ChunkMeshJob (A8):
//   POSITION      = the base surface point offset by the sea level, chunk-local
//   NORMAL        = the base surface normal, the water's up
//   TEXCOORD1.xyz = the same up; TEXCOORD1.w = the sea level, as the terrain's elevation
//   TEXCOORD2.xyz = geomorph target: the parent's water vertex; TEXCOORD2.w = the node depth
//   TEXCOORD3.xyz = the geomorph target's normal, the parent's base normal
//   TEXCOORD3.w   = the floor: the fine terrain height under the vertex, for the colour by depth
//   COLOR, TEXCOORD0 are zero and unread; the vertex layout is the terrain's.
//
// The floor does not morph (the parent's floor is not in the vertex), so the depth colour pops by
// what the parent's floor differs from the child's at every swap. ocean-b morphs it (Q6).
//
// No ShadowCaster pass (C5). Opaque water over an unseen floor has nothing beneath it to shadow,
// and a flat sea casts on nothing above it. It receives shadows: ForwardLit takes the main light's
// cascades exactly as the terrain does, per fragment, with the fade (see the terrain shader's
// comments above its Frag, and ShaderShadowTests, which checks both shaders).
//
// The morph and the sky are the terrain's, from the same includes: SonomaCommon.hlsl and, under
// _SONOMA_SKY, SonomaSky.hlsl. The globals they read are documented in SonomaTerrainTriplanar's
// header. Every pass reads TEXCOORD1 and TEXCOORD2, since the morph flattens each vertex by its up
// and elevation before measuring its distance (LodVertical).
//
// Shading, per fragment:
//   colour   by water depth, shallow to deep over _DepthScale metres (A5)
//   diffuse  Lambert on the morphed up
//   glint    Blinn-Phong, its smoothness falling from _Smoothness close to _FarSmoothness at
//            _GlintFarDistance with the log of the distance, so the sea from orbit shows a broad
//            highlight rather than a point too small to rasterise
//   Fresnel  Schlick, towards the sky's ambient at grazing angles
//   sky      under _SONOMA_SKY, the hemisphere ambient and the haze, as the terrain has them
Shader "Sonoma/Ocean"
{
    Properties
    {
        [Header(Colour by Depth)]
        _ShallowColor ("Shallow Color", Color) = (0.05, 0.35, 0.38, 1)
        _DeepColor    ("Deep Color",    Color) = (0.02, 0.07, 0.15, 1)
        _DepthScale   ("Depth Scale (m)", Float) = 20

        [Header(Glint)]
        _Smoothness       ("Smoothness, Near",   Range(0, 1)) = 0.9
        _FarSmoothness    ("Smoothness, Far",    Range(0, 1)) = 0.4
        _GlintFarDistance ("Far Distance (m)",   Float)       = 1000000

        [Header(Fresnel)]
        _FresnelStrength ("Fresnel Strength", Range(0, 1)) = 0.6

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

        // SRP Batcher: every pass must share this exact layout.
        CBUFFER_START(UnityPerMaterial)
            float4 _ShallowColor;
            float4 _DeepColor;
            float  _DepthScale;
            float  _Smoothness;
            float  _FarSmoothness;
            float  _GlintFarDistance;
            float  _FresnelStrength;
        CBUFFER_END

        // The morph globals and functions, the terrain's own: see SonomaCommon.hlsl.
        #include "SonomaCommon.hlsl"
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            // The terrain's main-light shadow keywords exactly, which must match URP's own
            // Lit.shader; see the terrain shader and ShaderShadowTests.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            // Sky light and haze. Local and shader_feature, as the terrain's.
            #pragma shader_feature_local _SONOMA_SKY

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            #include "SonomaSky.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 uv2        : TEXCOORD1; // (up.xyz, sea level)
                float4 morphPos   : TEXCOORD2; // (geomorph target position.xyz, node depth)
                float4 morphNrm   : TEXCOORD3; // (geomorph target normal.xyz, floor height)
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 topoUp      : TEXCOORD2;
                float  depth       : TEXCOORD3;
                // ONLY interpolated when URP says it is safe to; see the terrain shader.
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    float4 shadowCoord : TEXCOORD4;
                #endif
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;

                float3 positionOS = input.positionOS.xyz;
                float3 normalOS   = input.normalOS;
                SonomaMorph(positionOS, normalOS, input.uv2, input.morphPos, input.morphNrm.xyz);

                VertexPositionInputs vpi = GetVertexPositionInputs(positionOS);
                VertexNormalInputs   vni = GetVertexNormalInputs(normalOS);
                o.positionCS = vpi.positionCS;
                o.positionWS = vpi.positionWS;
                o.normalWS   = vni.normalWS;
                o.topoUp     = TransformObjectToWorldDir(input.uv2.xyz);
                // The sea level less the floor beneath it. Neither morphs: the sea is the same at
                // every depth, and the floor is the fine one (see the header).
                o.depth      = input.uv2.w - input.morphNrm.w;
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    o.shadowCoord = GetShadowCoord(vpi);
                #endif
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 N      = normalize(input.normalWS);
                float3 topoUp = normalize(input.topoUp);

                // Colour by depth (A5). A coast vertex whose floor stands above the sea draws water
                // of no depth, which the terrain then covers.
                float  depth  = max(input.depth, 0.0);
                half3  albedo = lerp(_ShallowColor.rgb, _DeepColor.rgb, 1.0 - exp(-depth / max(_DepthScale, 1e-3)));

                // The shadow coordinate per fragment under cascades, and the fade overload, exactly
                // as the terrain resolves them and for the same reasons.
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    float4 shadowCoord = input.shadowCoord;
                #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                    float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #else
                    float4 shadowCoord = float4(0, 0, 0, 0);
                #endif
                Light  ml    = GetMainLight(shadowCoord, input.positionWS, half4(1, 1, 1, 1));
                half3  light = ml.color * (ml.distanceAttenuation * ml.shadowAttenuation);
                half   NdotL = saturate(dot(N, ml.direction));
                half3  color = albedo * light * NdotL;

                // The glint. Its smoothness falls with the log of the distance, from _Smoothness at
                // a metre to _FarSmoothness at _GlintFarDistance, so the highlight widens as the
                // sea recedes rather than shrinking below a pixel. Blinn-Phong, normalised so a
                // broad highlight is dimmer rather than brighter.
                float3 toEye  = _WorldSpaceCameraPos - input.positionWS;
                float  dist   = length(toEye);
                float3 V      = toEye / max(dist, 1e-5);
                float  far    = saturate(log2(max(dist, 1.0)) / log2(max(_GlintFarDistance, 2.0)));
                float  smooth = lerp(_Smoothness, _FarSmoothness, far);
                float  power  = exp2(10.0 * smooth + 1.0);
                float3 H      = normalize(ml.direction + V);
                half   spec   = pow(saturate(dot(N, H)), power) * (power + 8.0) / (8.0 * PI);
                color += light * spec * NdotL;

                // The sky: the hemisphere ambient by the local up, which a flat sea faces, and the
                // sky's colour the Fresnel term reflects at grazing angles.
                #if defined(_SONOMA_SKY)
                    half  skyView = 0.5 + 0.5 * dot(N, topoUp);
                    color += albedo * lerp(_SonomaGroundAmbient.rgb, _SonomaSkyAmbient.rgb, skyView);
                    half3 sky = _SonomaSkyAmbient.rgb;
                #else
                    color += albedo * SampleSH(N) * 0.5;
                    half3 sky = SampleSH(reflect(-V, N));
                #endif
                half fresnel = _FresnelStrength * pow(1.0 - saturate(dot(N, V)), 5.0);
                color = lerp(color, sky, fresnel);

                #if defined(_SONOMA_SKY)
                    // Aerial perspective, the same composite as the terrain's, so the sea and the
                    // coast behind it fade alike; see SonomaSky.hlsl.
                    color = SonomaApplyHaze(color, input.positionWS, topoUp, ml);
                #endif

                return half4(color, 1);
            }
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

            // The depth prepass moves its vertices exactly as ForwardLit does, so it needs the
            // morph target, TEXCOORD2, and TEXCOORD1, which the morph flattens the vertex by.
            struct DAttr { float4 positionOS : POSITION; float4 topoElev : TEXCOORD1; float4 morphPos : TEXCOORD2; };
            struct DVar  { float4 positionCS : SV_POSITION; };

            DVar DepthVert(DAttr i)
            {
                DVar o;
                float3 positionOS = i.positionOS.xyz;
                SonomaMorphPosition(positionOS, i.topoElev, i.morphPos);
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
                float4 topoElev   : TEXCOORD1;   // the morph flattens by it; see SonomaMorphFactor
                float4 morphPos   : TEXCOORD2;
                float4 morphNrm   : TEXCOORD3;   // .w, the floor, unused here
            };
            struct DNVar  { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };

            DNVar DNVert(DNAttr i)
            {
                DNVar o;
                float3 positionOS = i.positionOS.xyz;
                float3 normalOS   = i.normalOS;
                SonomaMorph(positionOS, normalOS, i.topoElev, i.morphPos, i.morphNrm.xyz);

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
