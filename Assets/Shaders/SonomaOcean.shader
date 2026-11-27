// The sea (ocean-a, with its waves from ocean-b): the water grid ChunkMeshJob writes on Coast and
// Ocean chunks, a surface SeaLevel metres above the base surface along its normal
// (Docs/Ocean_Plan.md C1), displaced by the waves below. Opaque: the
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
//   TEXCOORD3.w   = the fine terrain height under the vertex, kept for the tests
//   COLOR.rg      = the floor's depth below the sea, 16 bits, d / (d + 64 m) (ChunkMeshLayout.EncodeFloor)
//   COLOR.b       = 1 on the skirt's bottom row, 0 on the grid (ChunkMeshLayout.WaterSkirtFlag)
//   TEXCOORD0.rg  = the morph target's floor depth, the parent's floor there, encoded the same way
//   The vertex layout is the terrain's; COLOR and TEXCOORD0 are its splat and morph splat there.
//
// The floor morphs on the geometry's k (ocean-b, B5): a fully morphed child's floor is its
// parent's, so the depth colour no longer pops at a swap.
//
// The waves (ocean-b): every pass morphs, then displaces the morphed vertex by
// SonomaWaveDisplace with the effective depth depth - k, the morphed floor and the skirt flag, so
// the three passes draw the same surface. Their up is the morphed normal, which at full morph is
// the parent's, so the DepthOnly pass reads NORMAL and TEXCOORD3 too. ForwardLit's normal is
// SonomaWaveNormal's, per fragment at the undisplaced position, and the glint widens by the slope
// variance the normal leaves out; DepthNormals writes the drawn surface's normal. With no waves
// (_SonomaWaveCount 0) every pass draws ocean-a's sea. See SonomaWaves.hlsl.
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
//   colour   by water depth, shallow to deep over _DepthScale metres (A5). Material colours are
//            sRGB, so choose them as such: a deep colour dark in linear goes to the haze's grey
//   diffuse  Lambert on the waves' normal, the morphed up where there are none
//   glint    Blinn-Phong, its smoothness falling from _Smoothness close to _FarSmoothness at
//            _GlintFarDistance with the log of the distance, so the sea from orbit shows a broad
//            highlight rather than a point too small to rasterise; widened further by the slope
//            variance of the waves the normal no longer resolves
//   Fresnel  Schlick, towards the sky the wave reflects: under _SONOMA_SKY a gradient by the
//            reflected ray's height above the local horizon, the lit haze at the horizon rising to
//            the sky's ambient by _SkyGradientHeight (the sine of that height), so a wave's slope
//            reads as light and dark; without it, the spherical harmonics in that direction
//   sky      under _SONOMA_SKY, the hemisphere ambient and the haze, as the terrain has them
Shader "Sonoma/Ocean"
{
    Properties
    {
        [Header(Colour by Depth)]
        _ShallowColor ("Shallow Color", Color) = (0.05, 0.35, 0.38, 1)
        _DeepColor    ("Deep Color",    Color) = (0.03, 0.16, 0.33, 1)
        _DepthScale   ("Depth Scale (m)", Float) = 20

        [Header(Glint)]
        _Smoothness       ("Smoothness, Near",   Range(0, 1)) = 0.9
        _FarSmoothness    ("Smoothness, Far",    Range(0, 1)) = 0.5
        _GlintFarDistance ("Far Distance (m)",   Float)       = 1000000

        [Header(Fresnel)]
        _FresnelStrength ("Fresnel Strength", Range(0, 1)) = 0.6
        _SkyGradientHeight ("Sky Gradient Height (sine of the elevation)", Range(0.01, 1)) = 0.35

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
            float  _SkyGradientHeight;
        CBUFFER_END

        // The morph globals and functions, the terrain's own: see SonomaCommon.hlsl.
        #include "SonomaCommon.hlsl"

        // The waves, the ocean's alone: see SonomaWaves.hlsl.
        #include "SonomaWaves.hlsl"

        // The floor's depth below the sea, from the two UNorm8 bytes ChunkMeshLayout.EncodeFloor
        // writes, r high and g low, as d / (d + 64 m). The inverse of DecodeFloor, which the tests use.
        float SonomaDecodeFloor(float4 packed)
        {
            float v = round(packed.r * 255.0) * 256.0 + round(packed.g * 255.0);
            float e = v / 65535.0;
            return 64.0 * e / (1.0 - e);
        }
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
                float4 floorFine  : COLOR;     // (floor depth, encoded; skirt flag; unused)
                float4 floorMorph : TEXCOORD0; // (the morph target's floor depth, encoded; unused)
                float4 uv2        : TEXCOORD1; // (up.xyz, sea level)
                float4 morphPos   : TEXCOORD2; // (geomorph target position.xyz, node depth)
                float4 morphNrm   : TEXCOORD3; // (geomorph target normal.xyz, fine floor height)
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;   // displaced: what is drawn, lit, shadowed and hazed
                float3 normalWS    : TEXCOORD1;   // the morphed base normal, the water's up
                float3 topoUp      : TEXCOORD2;
                float  depth       : TEXCOORD3;
                float3 wavePos     : TEXCOORD4;   // undisplaced, where the normal's waves are evaluated
                float  waveDepth   : TEXCOORD5;   // the effective depth, depth - k, for the fades
                // ONLY interpolated when URP says it is safe to; see the terrain shader.
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    float4 shadowCoord : TEXCOORD6;
                #endif
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;

                float3 positionOS = input.positionOS.xyz;
                float3 normalOS   = input.normalOS;
                float k = SonomaMorph(positionOS, normalOS, input.uv2, input.morphPos, input.morphNrm.xyz);

                // The water's depth, morphed on the geometry's k towards the parent's (B5), so it
                // agrees with the parent at a swap and with a neighbour at a shared vertex.
                o.depth     = lerp(SonomaDecodeFloor(input.floorFine), SonomaDecodeFloor(input.floorMorph), k);

                // Then the waves, on the morphed position, faded by the effective depth (B2).
                float3 geometricNormalWS;
                o.waveDepth = input.morphPos.w - k;
                o.wavePos   = SonomaWaveDisplace(positionOS, normalOS, input.morphPos.w - k, o.depth,
                                                 input.floorFine.b, geometricNormalWS);

                VertexPositionInputs vpi = GetVertexPositionInputs(positionOS);
                VertexNormalInputs   vni = GetVertexNormalInputs(normalOS);
                o.positionCS = vpi.positionCS;
                o.positionWS = vpi.positionWS;
                o.normalWS   = vni.normalWS;
                o.topoUp     = TransformObjectToWorldDir(input.uv2.xyz);
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    o.shadowCoord = GetShadowCoord(vpi);
                #endif
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 N      = normalize(input.normalWS);
                float3 topoUp = normalize(input.topoUp);

                // The waves' normal (B3), and the slope variance it leaves out, which widens the
                // glint below. With no waves the normal is left as it was, to the bit.
                float footprint = max(length(ddx(input.wavePos)), length(ddy(input.wavePos)));
                float slopeVariance;
                float3 waveN = SonomaWaveNormal(input.wavePos, N, input.waveDepth, footprint,
                                                max(input.depth, 0.0), slopeVariance);
                if (SonomaWaveCount() > 0) N = normalize(waveN);

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
                float  power  = SonomaWaveGlintPower(exp2(10.0 * smooth + 1.0), slopeVariance);
                float3 H      = normalize(ml.direction + V);
                half   spec   = pow(saturate(dot(N, H)), power) * (power + 8.0) / (8.0 * PI);
                color += light * spec * NdotL;

                // The sky: the hemisphere ambient by the local up, which a flat sea faces, and the
                // sky's colour the Fresnel term reflects at grazing angles.
                #if defined(_SONOMA_SKY)
                    half  skyView = 0.5 + 0.5 * dot(N, topoUp);
                    color += albedo * lerp(_SonomaGroundAmbient.rgb, _SonomaSkyAmbient.rgb, skyView);
                    // The sky the wave reflects, by how high above the local horizon the reflected
                    // ray climbs: the lit haze at the horizon, which the haze below also fades the
                    // far sea to, so there is no seam there, rising to the sky's ambient. A slope
                    // of a few degrees swings the reflected ray across most of that range, which is
                    // what makes waves read in a still frame (Ocean_B_Shading_Plan.md, S1). A ray
                    // reflected below the horizon, off a wave's back, takes the horizon's colour.
                    float3 R    = reflect(-V, N);
                    half   rise = smoothstep(0.0, max(_SkyGradientHeight, 1e-3), dot(R, topoUp));
                    half3  sky  = lerp(SonomaHazeLight(topoUp, ml), _SonomaSkyAmbient.rgb, rise);
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
            // morph target, TEXCOORD2, and TEXCOORD1, which the morph flattens the vertex by; and
            // for the waves, the normal and its morph target (the up they displace along), the
            // floors (the shoal damping) and the skirt flag.
            struct DAttr
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 floorFine  : COLOR;
                float4 floorMorph : TEXCOORD0;
                float4 topoElev   : TEXCOORD1;
                float4 morphPos   : TEXCOORD2;
                float4 morphNrm   : TEXCOORD3;
            };
            struct DVar  { float4 positionCS : SV_POSITION; };

            DVar DepthVert(DAttr i)
            {
                DVar o;
                float3 positionOS = i.positionOS.xyz;
                float3 normalOS   = i.normalOS;
                float k = SonomaMorph(positionOS, normalOS, i.topoElev, i.morphPos, i.morphNrm.xyz);
                float depth = lerp(SonomaDecodeFloor(i.floorFine), SonomaDecodeFloor(i.floorMorph), k);
                float3 geometricNormalWS;
                SonomaWaveDisplace(positionOS, normalOS, i.morphPos.w - k, depth, i.floorFine.b, geometricNormalWS);
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
                float4 floorFine  : COLOR;       // the floor and the skirt flag, for the waves
                float4 floorMorph : TEXCOORD0;
                float4 topoElev   : TEXCOORD1;   // the morph flattens by it; see SonomaMorphFactor
                float4 morphPos   : TEXCOORD2;
                float4 morphNrm   : TEXCOORD3;   // .w, the fine floor height, unused here
            };
            struct DNVar  { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };

            // The normal of the drawn surface, the waves the geometry displaces and no others, so
            // screen-space effects see what is drawn.
            DNVar DNVert(DNAttr i)
            {
                DNVar o;
                float3 positionOS = i.positionOS.xyz;
                float3 normalOS   = i.normalOS;
                float k = SonomaMorph(positionOS, normalOS, i.topoElev, i.morphPos, i.morphNrm.xyz);
                float depth = lerp(SonomaDecodeFloor(i.floorFine), SonomaDecodeFloor(i.floorMorph), k);
                float3 geometricNormalWS;
                SonomaWaveDisplace(positionOS, normalOS, i.morphPos.w - k, depth, i.floorFine.b, geometricNormalWS);

                o.positionCS = TransformObjectToHClip(positionOS);
                o.normalWS   = geometricNormalWS;
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
