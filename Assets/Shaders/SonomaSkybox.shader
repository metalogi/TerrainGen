// The atmosphere skybox: the scene's sky, from the same globals the terrain and sea are lit and
// hazed by, set as each scene's RenderSettings skybox (AtmosphereSkybox.mat). It has no
// properties of its own; everything comes from TerrainRoot's pushes, so one material serves
// every world.
//
// Along each view ray it draws SonomaSkyLight (SonomaSky.hlsl) at the camera's local up: the haze
// colour lit by the sun as the terrain's haze is lit, at the horizon, rising to the sky's ambient
// overhead. Fully hazed ground fades to exactly the horizon's colour, so the terrain meets the sky
// with no seam, and the sea reflects the same function, so its reflection agrees with the sky
// above it. Daylight is judged at the camera, as the haze judges it at each fragment: on a planet's
// night side the sky is the haze lit by the ambient alone.
//
// Times SonomaSkyFactor at the camera, which is 1 inside the atmosphere and fades to black above
// it (AtmosphereParams, the C# of the same arithmetic). The camera's altitude is taken from its
// absolute position, _WorldSpaceCameraPos + _SonomaWorldOrigin, so it holds through a rebase:
// WorldOriginSystem.ShiftOrigin re-pushes the origin in the frame it moves the camera. Per camera,
// so the Scene view's sky follows the Scene view's camera.
//
// The globals are _SonomaHaze, _SonomaSkyAmbient, _SonomaSurface and _SonomaAtmosphere (see
// SonomaSky.hlsl and the terrain shader's header), pushed by TerrainRoot every frame and from its
// OnValidate in edit mode, so the Scene view has a sky before play.
//
// No sun disc, stars or scattering towards the sun: one colour per direction, as the haze is.
Shader "Sonoma/Atmosphere Skybox"
{
    Properties { }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox"
               "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            Name "Skybox"

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "SonomaCommon.hlsl"
            // Always on here: the skybox is the sky path's.
            #define _SONOMA_SKY
            #include "SonomaSky.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 dirWS      : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // The skybox mesh is centred on the camera, so its object-space vertex is the view
            // direction, turned into world space.
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.dirWS      = mul((float3x3)UNITY_MATRIX_M, input.positionOS.xyz);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 dir = normalize(input.dirWS);
                float3 eye = _WorldSpaceCameraPos + _SonomaWorldOrigin;
                Light  ml  = GetMainLight();
                half3  sky = SonomaSkyLight(dir, SonomaUp(eye), ml) * SonomaSkyFactor(eye);
                return half4(sky, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
