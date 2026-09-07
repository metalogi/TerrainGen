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
//
// Per-frame globals:
//   _SonomaWorldOrigin (float3), from WorldOriginSystem.PushOriginToShader — added to
//   positionWS so triplanar samples on true world coordinates and stays continuous
//   through floating-origin rebases.
//
//   _SonomaMorphRanges (float4[32]), from TerrainRoot.PushMorphRanges — (start, end) per
//   depth. A global array rather than a per-chunk MaterialPropertyBlock, because a
//   property block breaks SRP batching; that is the whole reason depth travels in a
//   vertex attribute instead of a material property.
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

        // (start_d, end_d, 0, 0): the distances over which a depth-d chunk morphs onto its
        // parent. Ranges nest exactly (end_{d-1} == 2*end_d), so at any boundary between
        // depths d and d-1 the fine side is at k = 1 where the coarse side is still at
        // k = 0. See LodMath, and SonomaRevisedPlan.md section 4.4.
        #define SONOMA_MAX_MORPH_DEPTH 31
        float4 _SonomaMorphRanges[SONOMA_MAX_MORPH_DEPTH + 1];

        // Distance is measured from the UNMORPHED position. That matters twice over: it is
        // what lets every pass arrive at the same k for the same vertex, and it is what
        // makes two chunks sharing a vertex agree -- they compute the same world position
        // for it, so they compute the same distance and the same k. Per vertex, never per
        // chunk.
        //
        // Both positions are render space (world - origin), so their difference is true
        // world metres and needs no _SonomaWorldOrigin correction, unlike the triplanar
        // sampling in the fragment stage.
        float SonomaMorphFactor(float3 positionOS, float depth)
        {
            float3 wp   = TransformObjectToWorld(positionOS);
            float  dist = distance(wp, _WorldSpaceCameraPos);
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

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
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
