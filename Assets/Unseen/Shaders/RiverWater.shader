// Moving water.
//
// Several things stacked, none of them expensive, because the river runs the length of the map and
// is on screen from most of the town:
//
//   - Flow along the channel from two copies of the surface scrolled at different speeds. One layer
//     alone reads as a sliding photograph; two at different rates read as a surface with current.
//   - A wave field of five octaves at differing angles, used both to perturb the sample and to fake
//     a surface normal, so the moon breaks up across it instead of sitting as one smear.
//   - Depth. The bed under this is stepped - shallow shelves along each bank, a deeper middle - and
//     the shader is told where those steps are. Shallow water is paler, clearer and lets the gravel
//     show through; the middle is dark and opaque. That single cue does more for reading a river as
//     a river than any amount of surface detail.
//   - Foam where the water meets the banks, drifting along the shoreline, and on the wave crests.
//
// Lit by the main light only, deliberately. There are up to forty lanterns live at once; adding a
// clustered light loop here would multiply the shader variants for a surface that is mostly
// reflecting the moon.
Shader "Unseen/RiverWater"
{
    Properties
    {
        _BaseMap ("Surface", 2D) = "white" {}
        _ShallowColor ("Shallow", Color) = (0.20, 0.34, 0.34, 1)
        _DeepColor ("Deep", Color) = (0.03, 0.07, 0.11, 1)
        _FoamColor ("Foam", Color) = (0.78, 0.85, 0.87, 1)

        _FlowSpeed ("Flow Speed", Range(0, 1)) = 0.14
        _FlowScale ("Flow Scale", Range(0.05, 4)) = 0.6
        _Choppiness ("Choppiness", Range(0, 2)) = 0.85
        _Sparkle ("Moon Sparkle", Range(0, 8)) = 2.6
        _FoamAmount ("Crest Foam", Range(0, 2)) = 0.7
        _ShoreFoam ("Shore Foam", Range(0, 3)) = 1.2

        // The channel, so the shader knows how deep the water is under any given point. Written by
        // the generator, which is the only thing that knows where it dug the riverbed.
        _ChannelCentre ("Channel Centre X", Float) = 0
        _ChannelHalf ("Channel Half Width", Float) = 8
        _DeepHalf ("Deep Channel Half Width", Float) = 4
        _ShallowDepth ("Shelf Depth", Float) = 0.8
        _DeepDepth ("Middle Depth", Float) = 1.35
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent-10"
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

            // Blended rather than opaque, so shallow water over the shelves shows the gravel and
            // the rocks underneath it. Depth drives the alpha, so the middle of the channel stays
            // solid and a body wading in it is still hidden from the waist down.
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On

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
                float _ShoreFoam;
                float _ChannelCentre;
                float _ChannelHalf;
                float _DeepHalf;
                float _ShallowDepth;
                float _DeepDepth;
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

            // The wave field. Five octaves at differing angles and rates: three was enough to move
            // but too regular to look like water, and the repeat was visible along a straight
            // channel four hundred metres long.
            float Waves (float2 p, float t)
            {
                float w  = sin(p.x * 0.70 + t * 1.30) * 0.50;
                w += sin(p.y * 1.10 - t * 0.90) * 0.35;
                w += sin((p.x + p.y) * 0.45 + t * 1.70) * 0.28;
                w += sin((p.x * 0.6 - p.y * 1.4) * 1.90 - t * 2.30) * 0.16;
                w += sin((p.x * 1.7 + p.y * 0.3) * 3.10 + t * 3.10) * 0.09;
                return w;
            }

            // How much water stands over the bed here. The bed is stepped, so this is too - with a
            // soft shoulder at the step rather than a hard line, because a hard line in the alpha
            // would draw a stripe down the river.
            float DepthAt (float x)
            {
                float fromCentre = abs(x - _ChannelCentre);
                float step01 = smoothstep(_DeepHalf - 1.2, _DeepHalf + 1.2, fromCentre);
                float depth = lerp(_DeepDepth, _ShallowDepth, step01);

                // And it shelves out to nothing right at the bank, so the waterline is a wet edge
                // rather than a wall of water meeting the stone.
                float toBank = saturate((_ChannelHalf - fromCentre) / 1.4);
                return depth * toBank;
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

                float depth = DepthAt(input.positionWS.x);
                float deep01 = saturate(depth / max(0.05, _DeepDepth));

                // Colour by depth first, then modulate by the surface pattern. Shallow water over
                // pale gravel is a different colour from the middle of the channel, and that is the
                // cue that reads as a riverbed rather than as a painted plane.
                float3 colour = lerp(_ShallowColor.rgb, _DeepColor.rgb, deep01);
                colour *= 0.82 + 0.36 * surface;

                // Crests catch the light and go to foam. This is what gives a river its glitter
                // rather than looking like poured resin.
                float crest = saturate(wave * 0.5 + 0.5);
                // Only the sharpest crests, and sharply.
                //
                // At 0.72 with a gentle ramp, whole wave humps went to foam colour - and a wave
                // hump is five to ten metres across, so the water read as pale slabs scattered over
                // it. From a rooftop the lake looked like it had ice floes in it. Glitter is a few
                // per cent of the surface, not a third of it.
                float foam = saturate((crest - 0.88) * 9.0) * _FoamAmount;

                // And a band of foam along each bank, drifting downstream. Water meeting stone is
                // never clean, and the shoreline is where the eye looks to judge whether a body of
                // water is real.
                float fromCentre = abs(input.positionWS.x - _ChannelCentre);
                // A narrow band. At a metre and a half it read as a pale kerb running the whole
                // length of the river rather than as foam.
                float shore = saturate((fromCentre - (_ChannelHalf - 0.75)) / 0.75);
                float shoreNoise = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap,
                    float2(input.positionWS.z * 0.09 - t * 0.05, input.positionWS.x * 0.2)).r;
                foam += shore * shore * (0.45 + 0.55 * shoreNoise) * _ShoreFoam;

                foam = saturate(foam);
                colour = lerp(colour, _FoamColor.rgb, foam);

                // A perturbed normal from the wave slope, so the moon glints off moving water.
                float2 slope = float2(
                    Waves(world + float2(0.35, 0.0), t) - Waves(world - float2(0.35, 0.0), t),
                    Waves(world + float2(0.0, 0.35), t) - Waves(world - float2(0.0, 0.35), t));

                float3 normalWS = normalize(input.normalWS + float3(slope.x, 0.0, slope.y) * 0.45);

                // Seen from underneath, the surface is a ceiling. Flipping the normal keeps it lit
                // rather than rendering as a flat black lid over the player's head.
                if (!front) normalWS = -normalWS;

                Light mainLight = GetMainLight(input.shadowCoord);
                float diffuse = saturate(dot(normalWS, mainLight.direction)) * 0.5 + 0.5;

                float3 viewDir = normalize(GetWorldSpaceViewDir(input.positionWS));
                float3 halfDir = normalize(mainLight.direction + viewDir);
                float ndoth = saturate(dot(normalWS, halfDir));

                // Two speculars: a tight one for the glitter on the ripples and a broad one for the
                // sheen off the body of the water.
                // The broad term is kept very low. At any strength it stops being a sheen and
                // becomes soft white blooms sitting on the water like spilt milk.
                float sparkle = pow(ndoth, 96.0) * _Sparkle + pow(ndoth, 16.0) * _Sparkle * 0.035;

                float3 ambient = SampleSH(normalWS);
                colour *= mainLight.color * mainLight.shadowAttenuation * diffuse + ambient + 0.12;
                colour += mainLight.color * sparkle * mainLight.shadowAttenuation;

                // Underwater the surface is darker and greener, and the moon glitter belongs on
                // top of it rather than under it.
                if (!front) colour = colour * float3(0.45, 0.62, 0.6) + _DeepColor.rgb * 0.35;

                // Clear at the margins, solid in the middle. Foam is opaque wherever it appears -
                // you cannot see through froth.
                float alpha = saturate(0.42 + 0.58 * deep01);
                alpha = max(alpha, foam);

                // Grazing angles hide what is underneath, which is why a lake looks like a mirror
                // from across it and like glass from directly above.
                float grazing = 1.0 - saturate(abs(dot(normalize(input.normalWS), viewDir)));
                alpha = saturate(alpha + grazing * grazing * 0.45);

                return half4(colour, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
