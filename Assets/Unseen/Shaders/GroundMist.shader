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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _Tint;
                float _Density;
                float _Speed;
                float _Scale;
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

            half4 frag (Varyings input) : SV_Target
            {
                float t = _Time.y;

                // World-space sampling, so neighbouring panels do not show a seam where they
                // overlap and a panel does not slide its pattern along as it is placed.
                float2 world = input.positionWS.xz * _Scale;

                float a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap,
                    world + float2(t * _Speed, t * _Speed * 0.6)).r;
                float b = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap,
                    world * 1.7 - float2(t * _Speed * 0.8, t * _Speed * 1.3)).r;

                // Flattened toward the middle of its range. At full contrast the two layers beat
                // against each other into bright curdled patches, which read as spilled paint
                // rather than as air.
                float noise = 0.45 + 0.55 * saturate(a * 0.6 + b * 0.6);

                // Soft radial falloff across the panel, so a quad has no visible edge. Square-
                // rooted rather than squared: squaring concentrates the alpha into a small bright
                // core in the middle of each panel, which is the blob it used to look like. The
                // root spreads it, so a panel is a wide faint haze that happens to have an edge
                // somewhere you cannot find.
                float2 centred = input.uv * 2.0 - 1.0;
                float edge = saturate(1.0 - dot(centred, centred));
                edge = sqrt(edge);

                // And gone entirely close to the camera. A panel you walk into should thin out
                // rather than paint the whole screen grey.
                float distance = length(GetCameraPositionWS() - input.positionWS);
                float near = saturate(distance / max(0.01, _NearFade));

                float alpha = noise * edge * near * _Density * _Tint.a;

                return half4(_Tint.rgb, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
