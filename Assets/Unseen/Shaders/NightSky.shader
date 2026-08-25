// Panoramic night sky that fades its lower hemisphere to darkness.
//
// A photographic HDRI carries its own landscape - trees, huts, hills - which sits above the horizon
// line and therefore cannot be hidden by widening the ground plane. Fog does not help either: URP
// never fogs the skybox. So the sky itself has to discard everything below the horizon, leaving only
// the part we actually want: stars, moon and airglow.
Shader "Unseen/NightSky"
{
    Properties
    {
        _MainTex ("Panorama (equirectangular)", 2D) = "grey" {}
        _Exposure ("Exposure", Range(0, 4)) = 1.1
        _SkyTint ("Sky Tint", Color) = (0.72, 0.80, 1.0, 1)
        _GroundColor ("Below Horizon", Color) = (0.015, 0.018, 0.03, 1)
        _HorizonSoftness ("Horizon Softness", Range(0.005, 0.8)) = 0.18
        // Fades everything below this elevation, not just below the true horizon: a photographic
        // HDRI's tree line and hills sit *above* the horizon, so clipping at y=0 leaves them intact.
        _HorizonLift ("Horizon Lift", Range(-0.3, 0.8)) = 0.26
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _SkyTint;
                float4 _GroundColor;
                float _Exposure;
                float _HorizonSoftness;
                float _HorizonLift;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 direction : TEXCOORD0;
            };

            Varyings vert (Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.direction = input.positionOS.xyz;
                return output;
            }

            half4 frag (Varyings input) : SV_Target
            {
                float3 dir = normalize(input.direction);

                // Equirectangular lookup: longitude around Y, latitude from the Y component.
                const float pi = 3.14159265;
                float2 uv = float2(
                    0.5 + atan2(dir.x, -dir.z) / (2.0 * pi),
                    0.5 + asin(clamp(dir.y, -1.0, 1.0)) / pi);

                // Explicit LOD 0: the longitude wrap makes screen-space derivatives blow up at the
                // seam, which shows as a bright vertical line straight up the sky.
                float3 sky = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv, 0).rgb * _Exposure;
                sky *= _SkyTint.rgb;

                // Everything below the lift line becomes flat darkness, with a soft band so the
                // transition reads as haze rather than a hard edge.
                float below = saturate((_HorizonLift - dir.y) / max(_HorizonSoftness, 0.001));
                sky = lerp(sky, _GroundColor.rgb, below);

                return half4(sky, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback "Skybox/Panoramic"
}
