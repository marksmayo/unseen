// Low-lying mist that sits in the streets.
//
// Deliberately NOT the mist-wall shader. That one is purple and fades by height because it is the
// boundary of the world, and the boundary has to be unmistakable: if the haze in a courtyard looked
// like the thing that kills you, the one piece of information a player most needs to read at a
// glance would be the one made ambiguous. This is grey-blue, thin, and drifts.
//
// Drawn as flat panels scattered near the ground, so the geometry is trivial and all the work is in
// the alpha: a soft radial falloff turns a quad into a patch with no edges, and two layers of the
// surface texture scrolled against each other make it move without a particle system.
Shader "Unseen/GroundMist"
{
    Properties
    {
        _BaseMap ("Noise", 2D) = "white" {}
        _Tint ("Tint", Color) = (0.52, 0.58, 0.70, 1)
        _Density ("Density", Range(0, 2)) = 0.42
        _Speed ("Drift Speed", Range(0, 0.5)) = 0.02
        _Scale ("Noise Scale", Range(0.01, 1)) = 0.06

        // Metres from the camera over which the patch fades out, so walking into a panel thins it
        // rather than painting the whole screen grey. ShaderLab has no Tooltip attribute.
        _NearFade ("Near Fade", Range(0, 30)) = 7

        // Metres of depth difference over which the panel fades out against whatever is behind it.
        // This is what stops a quad drawing a hard line where it cuts a wall or the street.
        _SoftFade ("Soft Depth Fade", Range(0, 8)) = 1.6

        // Metres above the panel's own origin over which density falls away. Ground fog is thick at
        // the ankles and thin at the chest; a uniform slab is the thing that reads as a sheet.
        _HeightFalloff ("Height Falloff", Range(0, 12)) = 3.5

        // How much light the mist picks up. Fog beside a lantern is not the same colour as fog in
        // an alley, and a constant tint is what makes a lantern look painted onto the haze.
        _LightResponse ("Light Response", Range(0, 3)) = 0.5
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
            Name "GroundMist"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _FORWARD_PLUS
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _Tint;
                float _Density;
                float _Speed;
                float _Scale;
                float _NearFade;
                float _SoftFade;
                float _HeightFalloff;
                float _LightResponse;
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
                float2 uv : TEXCOORD1;
                float4 screenPos : TEXCOORD2;
                float heightAbove : TEXCOORD3;
                float fogCoord : TEXCOORD4;
                float3 normalWS : TEXCOORD5;
            };

            Varyings vert (Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.uv = input.uv;
                output.screenPos = ComputeScreenPos(positions.positionCS);

                // Height above this panel's own origin, not above the world. A flat panel sits at
                // its origin and stays dense; an upright one spans its own height and thins toward
                // the top, which is the same falloff doing the right thing for both without the
                // generator having to tell the shader which kind it built.
                float originY = unity_ObjectToWorld._m13;
                output.heightAbove = positions.positionWS.y - originY;

                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.fogCoord = ComputeFogFactor(positions.positionCS.z);
                return output;
            }

            half4 frag (Varyings input) : SV_Target
            {
                float t = _Time.y;

                // World-space sampling, so neighbouring panels do not show a seam where they
                // overlap and a panel does not slide its pattern along as it is placed.
                //
                // Projected three ways and blended by facing, not taken from XZ alone. Sampling
                // only XZ is right for a panel lying flat, but an upright one does not change its
                // X or Z as you move up it - so every vertical column got one noise value, the
                // pattern smeared into vertical stripes, and a soft bank of haze rendered as a
                // flat curtain with a visible top edge. A flat panel's normal is ±Y, so it still
                // samples XZ exactly as before and nothing about the streets changes.
                float3 blend = abs(normalize(input.normalWS));
                blend /= max(1e-4, blend.x + blend.y + blend.z);

                float2 planeX = input.positionWS.zy * _Scale;
                float2 planeY = input.positionWS.xz * _Scale;
                float2 planeZ = input.positionWS.xy * _Scale;

                float2 driftA = float2(t * _Speed, t * _Speed * 0.6);
                float2 driftB = -float2(t * _Speed * 0.8, t * _Speed * 1.3);

                float a =
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, planeX + driftA).r * blend.x +
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, planeY + driftA).r * blend.y +
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, planeZ + driftA).r * blend.z;

                float b =
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, planeX * 1.7 + driftB).r * blend.x +
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, planeY * 1.7 + driftB).r * blend.y +
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, planeZ * 1.7 + driftB).r * blend.z;

                // Flattened toward the middle of its range. At full contrast the two layers beat
                // against each other into bright curdled patches, which read as spilled paint
                // rather than as air.
                float noise = 0.45 + 0.55 * saturate(a * 0.6 + b * 0.6);

                // Soft radial falloff across the panel, so a quad has no visible edge.
                //
                // Smoothstepped on radius rather than square-rooted on radius squared. The root
                // was chosen to avoid concentrating alpha into a bright core, and it does - but it
                // holds the alpha near full across most of the panel and then runs out near the
                // rim, so a large upright panel presents a broad even sheet with a findable
                // boundary. That is what reads as a wall of fog standing in the street rather than
                // as air. This profile is zero well before the geometry ends and has no flat top,
                // so a panel has a centre and no edge at all - and because it is smooth at both
                // ends it does not bring back the blob either.
                float2 centred = input.uv * 2.0 - 1.0;
                float radius = saturate(1.0 - length(centred));
                float edge = radius * radius * (3.0 - 2.0 * radius);

                // And gone entirely close to the camera. A panel you walk into should thin out
                // rather than paint the whole screen grey.
                float distance = length(GetCameraPositionWS() - input.positionWS);
                float near = saturate(distance / max(0.01, _NearFade));

                // Soft against whatever is behind it.
                //
                // This is the one that matters. Without it a quad meeting a wall, the street or the
                // river surface draws a hard straight line at the intersection, and in motion that
                // line sweeps across the view and reads as a transparent box sliding through the
                // town. Comparing the panel's own depth against the depth already in the buffer and
                // fading over the last couple of metres turns the seam into a gradient.
                float2 screenUV = input.screenPos.xy / max(1e-5, input.screenPos.w);
                float sceneDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                float ownDepth = input.screenPos.w;
                float soft = saturate((sceneDepth - ownDepth) / max(0.01, _SoftFade));

                // Thick at the bottom, thin at the top.
                float height = exp(-max(0.0, input.heightAbove) / max(0.01, _HeightFalloff));

                float alpha = noise * edge * near * soft * height * _Density * _Tint.a;

                // Lit, rather than a constant tint.
                //
                // Ambient plus the moon gives the base, and every lantern in range adds its own
                // colour with distance attenuation - so fog in a lit courtyard is warm, fog in an
                // alley stays blue, and a lantern glows through a bank of it instead of sitting on
                // top of it. No normals here: mist has no surface, so each light contributes its
                // colour flat, which is also what stops a panel showing its own facing.
                half3 lighting = SampleSH(half3(0, 1, 0));

                Light mainLight = GetMainLight();
                lighting += mainLight.color * mainLight.distanceAttenuation;

                #if defined(_ADDITIONAL_LIGHTS)
                    // LIGHT_LOOP_BEGIN expands, under Forward+, to code that reads a variable
                    // named inputData for the cluster lookup. It has to exist and carry the screen
                    // UV and world position or the lantern lights never reach the fog.
                    InputData inputData = (InputData)0;
                    inputData.normalizedScreenSpaceUV = screenUV;
                    inputData.positionWS = input.positionWS;

                    uint lightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(lightCount)
                        Light extra = GetAdditionalLight(lightIndex, input.positionWS);
                        lighting += extra.color * extra.distanceAttenuation;
                    LIGHT_LOOP_END
                #endif

                // Bounded, because the sum is not.
                //
                // Ambient plus the moon plus every lantern in range adds up to well over one
                // wherever lanterns cluster, and multiplying the tint by that turned the river
                // into a milky white veil - brighter fog is not better lit fog, it is just fog you
                // cannot see through. Saturating first means light can only ever tint the mist and
                // lift it by a bounded amount, so a lantern colours the air near it and a dark
                // alley stays the tint it was authored as.
                half3 lit = saturate(lighting * _LightResponse);
                half3 colour = _Tint.rgb * (0.85 + 0.55 * lit);

                // Into the distance fog, so far mist belongs to the same air as everything else.
                colour = MixFog(colour, input.fogCoord);

                return half4(colour, alpha);
            }
            ENDHLSL
        }

        // Shadow casting, for the smoke bomb.
        //
        // Off for the town mist - the renderer decides per object, and 800 panels in the shadow
        // atlas would cost a great deal for haze nobody expects to cast anything. On for smoke,
        // where a cloud that darkens the lantern-lit street it lands in makes the cover it gives
        // legible to everyone, not just to the stealth maths.
        //
        // The alpha is dithered into a clip rather than blended: a shadow map holds depth, not
        // coverage, so a soft edge has to be expressed as a varying density of holes. Screen-door
        // transparency in the shadow pass is the standard trick and it is why a thin edge of smoke
        // casts a thin shadow instead of a solid one.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex shadowVert
            #pragma fragment shadowFrag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _Tint;
                float _Density;
                float _Speed;
                float _Scale;
                float _NearFade;
                float _SoftFade;
                float _HeightFalloff;
                float _LightResponse;
            CBUFFER_END

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float heightAbove : TEXCOORD2;
                float3 normalWS : TEXCOORD3;
            };

            ShadowVaryings shadowVert (ShadowAttributes input)
            {
                ShadowVaryings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.uv = input.uv;
                output.heightAbove = positions.positionWS.y - unity_ObjectToWorld._m13;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 shadowFrag (ShadowVaryings input) : SV_Target
            {
                float t = _Time.y;

                // Same triplanar blend as the forward pass. It has to be the same or the shadow a
                // puff casts stops matching the puff you can see.
                float3 blend = abs(normalize(input.normalWS));
                blend /= max(1e-4, blend.x + blend.y + blend.z);

                float2 planeX = input.positionWS.zy * _Scale;
                float2 planeY = input.positionWS.xz * _Scale;
                float2 planeZ = input.positionWS.xy * _Scale;

                float2 driftA = float2(t * _Speed, t * _Speed * 0.6);
                float2 driftB = -float2(t * _Speed * 0.8, t * _Speed * 1.3);

                float a =
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, planeX + driftA).r * blend.x +
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, planeY + driftA).r * blend.y +
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, planeZ + driftA).r * blend.z;

                float b =
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, planeX * 1.7 + driftB).r * blend.x +
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, planeY * 1.7 + driftB).r * blend.y +
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, planeZ * 1.7 + driftB).r * blend.z;

                float noise = 0.45 + 0.55 * saturate(a * 0.6 + b * 0.6);

                float2 centred = input.uv * 2.0 - 1.0;
                float edge = sqrt(saturate(1.0 - dot(centred, centred)));
                float height = exp(-max(0.0, input.heightAbove) / max(0.01, _HeightFalloff));

                float alpha = noise * edge * height * _Density * _Tint.a;

                // A 4x4 ordered dither. The fragment survives only where the pattern falls under
                // the local alpha, so density becomes the proportion of the shadow map it fills.
                float2 pixel = fmod(input.positionCS.xy, 4.0);
                const float threshold[16] =
                {
                    0.0625, 0.5625, 0.1875, 0.6875,
                    0.8125, 0.3125, 0.9375, 0.4375,
                    0.2500, 0.7500, 0.1250, 0.6250,
                    1.0000, 0.5000, 0.8750, 0.3750
                };

                clip(alpha - threshold[(int)(pixel.y) * 4 + (int)(pixel.x)]);
                return 0;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
