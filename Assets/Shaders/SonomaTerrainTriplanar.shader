// Triplanar terrain shader with topology-aware elevation/slope blending.
//
// Per-vertex inputs from QuadtreeManager.BuildMesh:
//   uv2.xyz = topology-up unit vector (the surface normal at h=0, in world space)
//   uv2.w   = elevation scalar (height value × HeightScale, in world units)
//
// Per-frame global from WorldOriginSystem.PushOriginToShader:
//   _SonomaWorldOrigin (float3) — added to positionWS so triplanar samples on true world
//   coordinates and stays continuous through floating-origin rebases.
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

        // Per-frame global (NOT in UnityPerMaterial — set via Shader.SetGlobalVector).
        float3 _SonomaWorldOrigin;
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float4 uv2        : TEXCOORD1; // (topoUp.xyz, elevation)
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 topoUp      : TEXCOORD2;
                float  elevation   : TEXCOORD3;
                float4 shadowCoord : TEXCOORD4;
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
                VertexPositionInputs vpi = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs   vni = GetVertexNormalInputs(input.normalOS);
                o.positionCS  = vpi.positionCS;
                o.positionWS  = vpi.positionWS;
                o.normalWS    = vni.normalWS;
                // Chunk transforms are unrotated in this project, but TransformObjectToWorldDir
                // is the safe form if a parent ever introduces rotation.
                o.topoUp      = TransformObjectToWorldDir(input.uv2.xyz);
                o.elevation   = input.uv2.w;
                o.shadowCoord = GetShadowCoord(vpi);
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 N     = normalize(input.normalWS);
                float3 absN  = abs(N);
                float3 wpTri = input.positionWS + _SonomaWorldOrigin;

                half3 lo  = _LowColor.rgb   * SampleTriplanar(_LowTex,   sampler_LowTex,   wpTri, absN, _LowScale);
                half3 mid = _MidColor.rgb   * SampleTriplanar(_MidTex,   sampler_MidTex,   wpTri, absN, _MidScale);
                half3 hi  = _HighColor.rgb  * SampleTriplanar(_HighTex,  sampler_HighTex,  wpTri, absN, _HighScale);
                half3 clf = _CliffColor.rgb * SampleTriplanar(_CliffTex, sampler_CliffTex, wpTri, absN, _CliffScale);

                float halfBlend = _BandBlend * 0.5;
                float t1 = smoothstep(_LowToMid  - halfBlend, _LowToMid  + halfBlend, input.elevation);
                float t2 = smoothstep(_MidToHigh - halfBlend, _MidToHigh + halfBlend, input.elevation);
                half3 band = lerp(lerp(lo, mid, t1), hi, t2);

                float3 topoUp = normalize(input.topoUp);
                float  slope  = saturate(1.0 - dot(N, topoUp));
                float  cliffB = smoothstep(_SlopeThreshold, _SlopeThreshold + _SlopeBlend, slope);
                half3  albedo = lerp(band, clf, cliffB);

                Light  ml      = GetMainLight(input.shadowCoord);
                half   NdotL   = saturate(dot(N, ml.direction));
                half3  direct  = albedo * ml.color * (ml.distanceAttenuation * ml.shadowAttenuation) * NdotL;
                half3  ambient = albedo * SampleSH(N) * 0.5;

                return half4(direct + ambient, 1);
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

            struct ShadowAttr { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct ShadowVar  { float4 positionCS : SV_POSITION; };

            ShadowVar ShadowVert(ShadowAttr input)
            {
                ShadowVar o;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);

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

            struct DAttr { float4 positionOS : POSITION; };
            struct DVar  { float4 positionCS : SV_POSITION; };

            DVar DepthVert(DAttr i)
            {
                DVar o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
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

            struct DNAttr { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct DNVar  { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };

            DNVar DNVert(DNAttr i)
            {
                DNVar o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.normalWS   = TransformObjectToWorldNormal(i.normalOS);
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
