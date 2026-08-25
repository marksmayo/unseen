// The Curse of the Shadow. A cylinder of animated fog rendered from the inside, scaled by
// MistVisual to whatever radius the server says the circle currently is.
Shader "Unseen/MistWall"
{
    Properties
    {
        _NearColor ("Inner Colour", Color) = (0.16, 0.15, 0.26, 0.03)
        _FarColor ("Outer Colour", Color) = (0.40, 0.26, 0.55, 0.62)
        _Speed ("Drift Speed", Range(0, 2)) = 0.35
        _Density ("Density", Range(0, 4)) = 1.4
        _HeightFade ("Height Fade", Range(0, 60)) = 24
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+10"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "MistForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Front

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _NearColor;
                float4 _FarColor;
                float _Speed;
                float _Density;
                float _HeightFade;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
            };

            Varyings vert (Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.uv = input.uv;
                return output;
            }

            float Fbm (float2 p)
            {
                float total = 0.0;
                float amplitude = 0.5;
                for (int i = 0; i < 4; i++)
                {
                    total += amplitude * (sin(p.x) * cos(p.y * 1.3) * 0.5 + 0.5);
                    p *= 2.03;
                    amplitude *= 0.5;
                }

                return total;
            }

            half4 frag (Varyings input) : SV_Target
            {
                float t = _Time.y * _Speed;
                float2 sample = float2(input.uv.x * 18.0 + t, input.positionWS.y * 0.35 - t * 0.6);
                float noise = Fbm(sample);

                float heightFade = saturate(1.0 - input.positionWS.y / max(_HeightFade, 0.01));
                float density = saturate(noise * _Density * (0.4 + 0.6 * heightFade));

                float3 colour = lerp(_NearColor.rgb, _FarColor.rgb, density);
                float alpha = lerp(_NearColor.a, _FarColor.a, density);

                return half4(colour, alpha);
            }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/Unlit"
}
