Shader "Unseen/CombatAccent"
{
    Properties { _BaseColor("Tint",Color)=(1,1,1,1) }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Blend SrcAlpha One ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct A {float4 vertex:POSITION;float4 color:COLOR;float2 uv:TEXCOORD0;};
            struct V {float4 vertex:SV_POSITION;float4 color:COLOR;float2 uv:TEXCOORD0;};
            CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            CBUFFER_END
            V Vert(A v){V o;o.vertex=TransformObjectToHClip(v.vertex.xyz);o.color=v.color*_BaseColor;o.uv=v.uv;return o;}
            half4 Frag(V i):SV_Target {half edge=saturate(1-abs(i.uv.y*2-1));return half4(i.color.rgb,i.color.a*edge*edge);}
            ENDHLSL
        }
    }
}
