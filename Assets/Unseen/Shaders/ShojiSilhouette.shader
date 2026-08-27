// Paper screen: lit, shadow-receiving, and able to print silhouettes.
//
// The client only ever knows about silhouette contacts the server chose to send, so this shader
// cannot draw a shape for someone the interest manager decided you have not earned. It renders a
// deliberately low-fidelity blob: a soft radial darkening with no gear, no facing and no identity.
//
// Lighting is real, and that matters more here than anywhere else in the town: a shoji screen is
// the surface lantern light falls on, and the surface a body between you and the lamp casts a
// shadow onto. An unlit version of this shader lost both, and the paper walls went flat.
//
// Lit double-sided on purpose - paper is thin, so a lamp behind a screen should brighten the face
// you are looking at rather than leaving it in shadow.
Shader "Unseen/ShojiSilhouette"
{
    Properties
    {
        _BaseMap ("Paper Texture", 2D) = "white" {}
        _BaseColor ("Paper Colour", Color) = (0.86, 0.83, 0.72, 0.92)
        _SilhouetteColor ("Silhouette Colour", Color) = (0.06, 0.05, 0.08, 1)
        _Radius ("Silhouette Radius", Range(0.2, 4)) = 1.1
        _Softness ("Silhouette Softness", Range(0.01, 2)) = 0.75
        _SliceAmount ("Slice Amount", Range(0, 1)) = 0
        _Grain ("Paper Grain", Range(0, 1)) = 0.25
        _Translucency ("Translucency", Range(0, 2)) = 0.85
        _AmbientFloor ("Ambient Floor", Range(0, 1)) = 0.18
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "ShojiForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            // Lighting and shadow variants. Without these the panel compiles and renders, but
            // GetMainLight returns an unshadowed light and every additional light is ignored -
            // which is how a lantern ends up casting nothing onto a paper wall.
            // Kept deliberately narrow. Every keyword here multiplies the variant count, and this
            // shader is on five thousand panels: the full URP lit set took long enough to compile
            // that a batch render timed out. Screen-space main-light shadows and reflection probe
            // blending are dropped because paper needs neither.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            #define UNSEEN_MAX_SILHOUETTES 8

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float4 _SilhouetteColor;
                float _Radius;
                float _Softness;
                float _SliceAmount;
                float _Grain;
                float _Translucency;
                float _AmbientFloor;
            CBUFFER_END

            // Written once per frame by ShojiSilhouetteFeeder: xyz = world position, w = strength.
            float4 _UnseenSilhouettes[UNSEEN_MAX_SILHOUETTES];
            float _UnseenSilhouetteCount;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float4 shadowCoord : TEXCOORD3;
            };

            Varyings vert (Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;
                output.shadowCoord = TransformWorldToShadowCoord(positions.positionWS);
                return output;
            }

            // Cheap value noise so the paper does not read as flat plastic.
            float PaperGrain (float2 uv)
            {
                float2 p = floor(uv * 220.0);
                return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
            }

            // Paper transmits: light hitting either face brightens the sheet, so the wrap term uses
            // the absolute dot rather than a clamped one.
            float PaperWrap (float3 normalWS, float3 lightDir)
            {
                float direct = saturate(dot(normalWS, lightDir));
                float through = saturate(-dot(normalWS, lightDir)) * _Translucency;
                return direct + through;
            }

            half4 frag (Varyings input) : SV_Target
            {
                float grain = lerp(1.0, 0.86 + 0.14 * PaperGrain(input.uv), _Grain);

                // Sample the same paper albedo the rest of the town uses, so a screen that can
                // print a silhouette still looks like the screens that cannot.
                float2 uv = input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                float3 paper = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).rgb;
                float3 albedo = _BaseColor.rgb * paper * grain;

                float3 normalWS = normalize(input.normalWS);

                // Moonlight, with its shadow. This is what makes a body between the moon and the
                // screen read on the paper.
                Light mainLight = GetMainLight(input.shadowCoord);
                float3 lighting = mainLight.color * mainLight.shadowAttenuation *
                                  PaperWrap(normalWS, mainLight.direction);

                // Every lantern in range, each with its own shadow. A shoji lit from inside by one
                // lamp and outside by another is the whole point of the surface.
                //
                // LIGHT_LOOP_BEGIN expands to code that reads a local named exactly "inputData"
                // for the clustered light loop, so it has to exist even though this shader does
                // not otherwise use URP's InputData. Omitting it fails to compile, and the
                // Fallback at the bottom of this file then silently swaps in an unlit shader -
                // which is how a broken paper wall passes for a working one.
                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                uint pixelLightCount = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light light = GetAdditionalLight(lightIndex, input.positionWS, half4(1, 1, 1, 1));
                    lighting += light.color * light.distanceAttenuation * light.shadowAttenuation *
                                PaperWrap(normalWS, light.direction);
                LIGHT_LOOP_END

                float3 ambient = SampleSH(normalWS) + _AmbientFloor;
                float3 colour = albedo * (lighting + ambient);
                float alpha = _BaseColor.a;

                // Accumulate every silhouette that reaches this fragment.
                float shade = 0.0;
                int count = (int)min(_UnseenSilhouetteCount, (float)UNSEEN_MAX_SILHOUETTES);
                for (int i = 0; i < count; i++)
                {
                    float4 entry = _UnseenSilhouettes[i];
                    float distance = length(input.positionWS - entry.xyz);
                    float blob = 1.0 - smoothstep(_Radius, _Radius + _Softness, distance);
                    shade = max(shade, blob * entry.w);
                }

                colour = lerp(colour, _SilhouetteColor.rgb, saturate(shade));
                alpha = lerp(alpha, min(1.0, alpha + 0.35), saturate(shade));

                // A sliced panel fades out around the cut so the opening reads as a hole.
                // A torn hole rather than a clean fade.
                //
                // The slice used to be a radial gradient, which reads as the panel becoming
                // gradually transparent - paper cut with a blade tears, and the edge of the tear is
                // ragged and slightly brighter where the fibres have pulled. The noise is derived
                // from the UV so it is stable per panel and costs nothing.
                float2 p = input.uv * 9.0;
                float ragged = frac(sin(dot(floor(p), float2(12.9898, 78.233))) * 43758.5453);
                ragged = lerp(0.82, 1.18, ragged);

                float cut = saturate(_SliceAmount * ragged - length(input.uv - 0.5) * 1.2);
                float hole = saturate(cut * 3.2);

                // The rim of the tear: pulled fibres catch the light.
                float rim = saturate(1.0 - abs(hole - 0.45) * 6.0) * step(0.02, _SliceAmount);
                colour += rim * 0.22;

                alpha *= 1.0 - hole;

                return half4(colour, alpha);
            }
            ENDHLSL
        }

        // Shadow caster, so a paper screen throws its own soft shape onto the floor inside.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            float4 ShadowVert (ShadowAttributes input) : SV_POSITION
            {
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, _LightDirection));

                #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                return positionCS;
            }

            half4 ShadowFrag () : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    // No fallback. This shader's own comments warn twice that a Fallback silently substitutes an
    // unlit shader when the subshader fails to compile, and that "is how a broken paper wall passes
    // for a working one" - and then it kept the Fallback anyway. A magenta wall is a bug report; a
    // quietly unlit one is a feature that looks fine and does nothing.
    Fallback Off
}
