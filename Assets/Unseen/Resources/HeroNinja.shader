Shader "Unseen/HeroNinja"
{
    Properties { [MainColor] _BaseColor("Visibility tint", Color)=(1,1,1,1)
        _AccentTint("Cloth palette", Color)=(1,1,1,1) }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Cull Back
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        CBUFFER_START(UnityPerMaterial)
        half4 _AccentTint;
        half4 _BaseColor;
        CBUFFER_END
        struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; float4 tangentOS:TANGENT; float2 uv:TEXCOORD0; float2 surface:TEXCOORD1; half4 color:COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
        struct Varyings { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; half3 normalWS:TEXCOORD1; half4 tangentWS:TEXCOORD2; float2 uv:TEXCOORD3; float2 surface:TEXCOORD4; half4 color:COLOR; half fog:TEXCOORD5; UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO };
        Varyings Vert(Attributes v)
        {
            Varyings o=(Varyings)0;UNITY_SETUP_INSTANCE_ID(v);UNITY_TRANSFER_INSTANCE_ID(v,o);UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            VertexPositionInputs p=GetVertexPositionInputs(v.positionOS.xyz);o.positionCS=p.positionCS;o.positionWS=p.positionWS;
            o.normalWS=TransformObjectToWorldNormal(v.normalOS);o.tangentWS=half4(TransformObjectToWorldDir(v.tangentOS.xyz),v.tangentOS.w*GetOddNegativeScale());
            o.uv=v.uv;o.surface=v.surface;o.color=v.color;o.fog=ComputeFogFactor(p.positionCS.z);return o;
        }
        half4 Frag(Varyings i):SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(i);UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            half cloth=1-step(.5,i.surface.x);half leather=step(.5,i.surface.x)*(1-step(1.5,i.surface.x));
            half metal=step(1.5,i.surface.x)*(1-step(2.5,i.surface.x));half eye=step(3.5,i.surface.x);
            float2 weaveUV=i.uv*420;
            float filter=1-saturate(max(length(ddx(weaveUV)),length(ddy(weaveUV))));
            half weave=sin(weaveUV.x*6.283185)*sin(weaveUV.y*6.283185)*filter;
            half3 n=normalize(i.normalWS);half3 tangent=normalize(i.tangentWS.xyz);half3 bitangent=cross(n,tangent)*i.tangentWS.w;
            n=normalize(n+(tangent*sin(weaveUV.x*6.283185)+bitangent*sin(weaveUV.y*6.283185))*.055*cloth*filter);
            InputData data=(InputData)0;data.positionWS=i.positionWS;data.normalWS=n;data.viewDirectionWS=GetWorldSpaceNormalizeViewDir(i.positionWS);
            data.shadowCoord=TransformWorldToShadowCoord(i.positionWS);data.fogCoord=i.fog;data.vertexLighting=VertexLighting(i.positionWS,n);
            data.bakedGI=SampleSH(n);data.normalizedScreenSpaceUV=GetNormalizedScreenSpaceUV(i.positionCS);data.shadowMask=half4(1,1,1,1);
            SurfaceData surface=(SurfaceData)0;
            surface.albedo=i.color.rgb*_BaseColor.rgb*lerp(half3(1,1,1),_AccentTint.rgb,i.surface.y)*(1+weave*.035*cloth);
            surface.metallic=metal*.55;surface.specular=half3(.04,.04,.04);
            surface.smoothness=.17*cloth+.38*leather+.58*metal+.40*step(2.5,i.surface.x)+.23*eye;
            surface.normalTS=half3(0,0,1);surface.occlusion=1;surface.alpha=1;
            half4 color=UniversalFragmentPBR(data,surface);color.rgb=MixFog(color.rgb,i.fog);return color;
        }
        float3 _LightDirection,_LightPosition;
        Varyings ShadowVert(Attributes v)
        {
            Varyings o=Vert(v);float3 direction=_LightDirection;
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
            direction=normalize(_LightPosition-o.positionWS);
            #endif
            o.positionCS=TransformWorldToHClip(ApplyShadowBias(o.positionWS,o.normalWS,direction));
            #if UNITY_REVERSED_Z
            o.positionCS.z=min(o.positionCS.z,UNITY_NEAR_CLIP_VALUE*o.positionCS.w);
            #else
            o.positionCS.z=max(o.positionCS.z,UNITY_NEAR_CLIP_VALUE*o.positionCS.w);
            #endif
            return o;
        }
        half4 DepthFrag(Varyings i):SV_Target{return 0;}
        half4 NormalFrag(Varyings i):SV_Target{return half4(normalize(i.normalWS),0);}
        ENDHLSL
        Pass
        {
            Name "ForwardLit" Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster" Tags { "LightMode"="ShadowCaster" } ZWrite On ColorMask 0
            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly" Tags { "LightMode"="DepthOnly" } ZWrite On ColorMask 0
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals" Tags { "LightMode"="DepthNormals" } ZWrite On
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment NormalFrag
            #pragma multi_compile_instancing
            ENDHLSL
        }
    }
}
