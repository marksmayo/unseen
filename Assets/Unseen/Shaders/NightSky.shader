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

        // The moon.
        //
        // Drawn here rather than as a billboard because a skybox is already exactly the right
        // surface for something infinitely far away: it costs no draw call, it can never clip
        // through a rooftop, and it cannot drift when the camera moves.
        _MoonDir ("Moon Direction", Vector) = (0.42, 0.34, -0.84, 0)
        _MoonSize ("Moon Angular Size", Range(0.002, 0.12)) = 0.045
        _MoonColor ("Moon Colour", Color) = (1.0, 0.97, 0.90, 1)
        _MoonBrightness ("Moon Brightness", Range(0, 8)) = 3.2
        _MoonHaloSize ("Halo Size", Range(1, 24)) = 9.0
        _MoonHaloStrength ("Halo Strength", Range(0, 2)) = 0.55
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
                float4 _MoonDir;
                float4 _MoonColor;
                float _MoonSize;
                float _MoonBrightness;
                float _MoonHaloSize;
                float _MoonHaloStrength;
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
                // ------------------------------------------------------------------ the moon
                //
                // Added before the horizon fade rather than after, so that a moon sitting low in
                // the sky is dimmed by the same haze that dims everything else down there. A disc
                // that stayed at full brightness while the sky around it went murky would read as
                // a hole punched in the fog.
                float3 moonDir = normalize(_MoonDir.xyz);
                float toMoon = dot(dir, moonDir);

                // Angular distance rather than the dot product itself: the dot flattens out near
                // the centre, which gives a disc with a soft mushy middle instead of an edge.
                float angle = acos(clamp(toMoon, -1.0, 1.0));

                // The disc. Feathered over roughly a tenth of its radius - sharp enough to read as
                // an object, soft enough not to crawl with aliasing as the camera turns.
                float edge = _MoonSize * 0.12;
                float disc = 1.0 - smoothstep(_MoonSize - edge, _MoonSize + edge, angle);

                // The halo. Moonlight scattering in the same haze the fog represents, falling off
                // sharply so it reads as glow around the moon rather than as a brightened sky.
                float halo = saturate(1.0 - angle / (_MoonSize * _MoonHaloSize));
                halo = halo * halo * halo * _MoonHaloStrength;

                sky += _MoonColor.rgb * (disc * _MoonBrightness + halo);

                float below = saturate((_HorizonLift - dir.y) / max(_HorizonSoftness, 0.001));
                sky = lerp(sky, _GroundColor.rgb, below);

                return half4(sky, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback "Skybox/Panoramic"
}
