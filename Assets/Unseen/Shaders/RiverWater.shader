// Moving water.
//
// Two copies of the same texture scrolled at different speeds and scales, which is the oldest
// trick there is for flow and still the cheapest: one layer alone reads as a sliding photograph,
// two at different rates read as a surface with current in it.
//
// Lit by the main light only, deliberately. The river runs the length of the map and there are up
// to forty lanterns live at once; adding a clustered light loop here would multiply the shader
// variants for a surface that is mostly reflecting the moon.
Shader "Unseen/RiverWater"
{
    Properties
    {
        _BaseMap ("Surface", 2D) = "white" {}
        _ShallowColor ("Shallow", Color) = (0.16, 0.28, 0.30, 0.92)
        _DeepColor ("Deep", Color) = (0.04, 0.09, 0.13, 1)
        _FoamColor ("Foam", Color) = (0.72, 0.80, 0.82, 1)

        _FlowSpeed ("Flow Speed", Range(0, 1)) = 0.14
        _FlowScale ("Flow Scale", Range(0.05, 4)) = 0.6
        _Choppiness ("Choppiness", Range(0, 2)) = 0.85
        _Sparkle ("Moon Sparkle", Range(0, 8)) = 2.6
        _FoamAmount ("Foam", Range(0, 2)) = 0.7
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry+1"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "WaterForward"
            Tags { "LightMode" = "UniversalForward" }

            // Two-sided. You can stand in this river, and crouching in the deep middle puts your
            // eyes under the surface - with back faces culled the water disappeared entirely from
            // down there, which read as the river flickering out as you looked around.
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _ShallowColor;
                float4 _DeepColor;
                float4 _FoamColor;
                float _FlowSpeed;
                float _FlowScale;
                float _Choppiness;
                float _Sparkle;
                float _FoamAmount;
            CBUFFER_END

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
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
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

            // A cheap wave field, used to perturb the sample and to fake a surface normal. World
            // space rather than UV space, so the pattern does not stretch with the mesh.
            float Waves (float2 p, float t)
            {
                float w = sin(p.x * 0.7 + t * 1.3) * 0.5;
                w += sin(p.y * 1.1 - t * 0.9) * 0.35;
                w += sin((p.x + p.y) * 0.45 + t * 1.7) * 0.28;
                return w;
            }

            half4 frag (Varyings input, bool front : SV_IsFrontFace) : SV_Target
            {
                float t = _Time.y;

                // The river runs along Z, so the flow does too.
                float2 world = input.positionWS.xz * _FlowScale;
                float wave = Waves(world, t) * _Choppiness;

                float2 uvA = world * 0.12 + float2(wave * 0.03, -t * _FlowSpeed);
                float2 uvB = world * 0.27 + float2(-wave * 0.02, -t * _FlowSpeed * 1.7);

                float3 layerA = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvA).rgb;
                float3 layerB = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvB).rgb;
                float surface = saturate(dot(layerA * 0.6 + layerB * 0.4, float3(0.33, 0.33, 0.33)));

                float3 colour = lerp(_DeepColor.rgb, _ShallowColor.rgb, surface);

                // Crests catch the light and go to foam. This is what gives a river its glitter
                // rather than looking like poured resin.
                float crest = saturate(wave * 0.5 + 0.5);
                float foam = saturate((crest - 0.72) * 4.0) * _FoamAmount;
                colour = lerp(colour, _FoamColor.rgb, foam);

                // A perturbed normal from the wave slope, so the moon glints off moving water.
                float2 slope = float2(
                    Waves(world + float2(0.35, 0.0), t) - Waves(world - float2(0.35, 0.0), t),
                    Waves(world + float2(0.0, 0.35), t) - Waves(world - float2(0.0, 0.35), t));

                float3 normalWS = normalize(input.normalWS + float3(slope.x, 0.0, slope.y) * 0.35);

                // Seen from underneath, the surface is a ceiling. Flipping the normal keeps it lit
                // rather than rendering as a flat black lid over the player's head.
                if (!front) normalWS = -normalWS;

                Light mainLight = GetMainLight(input.shadowCoord);
                float diffuse = saturate(dot(normalWS, mainLight.direction)) * 0.5 + 0.5;

                float3 viewDir = normalize(GetWorldSpaceViewDir(input.positionWS));
                float3 halfDir = normalize(mainLight.direction + viewDir);
                float sparkle = pow(saturate(dot(normalWS, halfDir)), 96.0) * _Sparkle;

                float3 ambient = SampleSH(normalWS);
                colour *= mainLight.color * mainLight.shadowAttenuation * diffuse + ambient + 0.12;
                colour += mainLight.color * sparkle * mainLight.shadowAttenuation;

                // Underwater the surface is darker and greener, and the moon glitter belongs on
                // top of it rather than under it.
                if (!front) colour = colour * float3(0.45, 0.62, 0.6) + _DeepColor.rgb * 0.35;

                return half4(colour, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/Lit"
}
