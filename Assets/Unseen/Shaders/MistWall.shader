// The Curse of the Shadow: a cylinder of churning fog rendered from the inside, scaled by
// MistVisual to whatever radius the server says the circle currently is.
//
// The first version of this read as a pane of frosted glass, and there were three reasons for it.
//
// Its "Fbm" was four octaves of sin(x) * cos(y). That is not noise - it is a smooth, perfectly
// periodic ripple field, so it produced soft regular bands instead of turbulence and the whole
// surface looked machined.
//
// It sampled that pattern from the mesh's UVs. A capless tube's u runs linearly across each of its
// forty-eight quads, so anything sampled from u shows you exactly where the quads are: the
// tessellation was visible straight through the fog.
//
// And it scrolled in one direction at one speed, which reads as a texture sliding past. Fog does
// not slide, it turns over.
//
// So: real value noise, sampled in world space from cylindrical coordinates rather than UVs, two
// layers moving against each other, and the whole thing domain-warped by a third so it churns. The
// alpha now reaches zero before the geometry does at both the top and the bottom, so the rim of the
// cylinder is never a visible edge, and it thins out close to the camera so being caught by the
// wall does not paint the screen.
Shader "Unseen/MistWall"
{
    Properties
    {
        _NearColor ("Thin Colour", Color) = (0.20, 0.20, 0.30, 0.02)
        _FarColor ("Thick Colour", Color) = (0.44, 0.30, 0.58, 0.70)
        _Speed ("Churn Speed", Range(0, 2)) = 0.5
        _Density ("Density", Range(0, 4)) = 1.5
        _Scale ("Noise Scale", Range(0.005, 0.2)) = 0.035
        _Warp ("Swirl", Range(0, 8)) = 3.2
        _HeightFade ("Height Fade", Range(4, 80)) = 34
        _GroundFade ("Ground Fade", Range(0, 20)) = 5
        _NearFade ("Near Fade", Range(0, 40)) = 14
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
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _NearColor;
                float4 _FarColor;
                float _Speed;
                float _Density;
                float _Scale;
                float _Warp;
                float _HeightFade;
                float _GroundFade;
                float _NearFade;
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
            };

            Varyings vert (Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                return output;
            }

            // ------------------------------------------------------------------ noise
            //
            // Hash-based value noise. Cheap, and unlike the trigonometry it replaces it has no
            // period a viewer can find.

            float Hash (float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453123);
            }

            float ValueNoise (float2 p)
            {
                float2 cell = floor(p);
                float2 f = frac(p);

                // Smoothstep across the cell, or the noise shows its grid.
                f = f * f * (3.0 - 2.0 * f);

                float a = Hash(cell);
                float b = Hash(cell + float2(1.0, 0.0));
                float c = Hash(cell + float2(0.0, 1.0));
                float d = Hash(cell + float2(1.0, 1.0));

                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            float Fbm (float2 p)
            {
                float total = 0.0;
                float amplitude = 0.5;

                for (int i = 0; i < 5; i++)
                {
                    total += amplitude * ValueNoise(p);
                    p = p * 2.07 + 19.3;
                    amplitude *= 0.5;
                }

                return total;
            }

            half4 frag (Varyings input) : SV_Target
            {
                float t = _Time.y * _Speed;

                // Cylindrical coordinates from WORLD position, so nothing here can show the mesh.
                // The angle is unwrapped to an arc length using the cylinder's own radius, which
                // keeps the noise the same size on the ground however wide the circle currently is
                // - a pattern that stretched as the ring shrank would read as the fog zooming.
                float2 flat = input.positionWS.xz;
                float radius = max(length(flat), 0.001);
                float angle = atan2(flat.y, flat.x);

                float2 p = float2(angle * radius, input.positionWS.y) * _Scale;

                // Domain warp: the sample position is itself pushed about by noise. This is what
                // turns a scrolling texture into something that curls back on itself.
                float2 swirl = float2(
                    Fbm(p * 0.6 + float2(t * 0.13, -t * 0.09)),
                    Fbm(p * 0.6 + float2(-t * 0.11, t * 0.16) + 7.7));

                p += (swirl - 0.5) * _Warp;

                // Two layers moving against each other. One rises, which is what fog does; the
                // other drifts around the ring the other way. Neither on its own reads as churn.
                float rising = Fbm(p + float2(t * 0.35, -t * 0.75));
                float drifting = Fbm(p * 1.7 + float2(-t * 0.6, t * 0.2) + 31.4);

                float noise = saturate(rising * 0.65 + drifting * 0.5);

                // Gone before the geometry ends, at both ends. The cylinder is sixty metres tall
                // and its rim used to be a hard line across the sky.
                float top = saturate(1.0 - input.positionWS.y / max(_HeightFade, 0.01));
                float ground = saturate(input.positionWS.y / max(_GroundFade, 0.01) + 0.35);

                // And thinner close up, so walking into the wall does not fill the screen with
                // flat purple before the damage has a chance to explain itself.
                float distance = length(GetCameraPositionWS() - input.positionWS);
                float near = saturate(distance / max(_NearFade, 0.01));

                float density = saturate(noise * _Density * top * top * ground);

                float3 colour = lerp(_NearColor.rgb, _FarColor.rgb, density);
                float alpha = lerp(_NearColor.a, _FarColor.a, density) * near;

                return half4(colour, alpha);
            }
            ENDHLSL
        }
    }

    // No fallback. A silent substitution here is how the shoji silhouettes rendered as plain lit
    // paper for months while reporting the right shader name on the material.
}
