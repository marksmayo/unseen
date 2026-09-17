Shader "Unseen/WeatheredSurface"
{
    Properties
    {
        _BaseMap("Surface", 2D) = "white" {}
        _BaseColor("Tint", Color) = (1,1,1,1)
        _BumpMap("Normal", 2D) = "bump" {}
        _MetallicGlossMap("Surface Mask", 2D) = "white" {}
        _OcclusionMap("Occlusion", 2D) = "white" {}
        _HasMask("Use Surface Mask", Float) = 0
        _BumpScale("Normal Strength", Float) = 1
        _Smoothness("Smoothness", Range(0,1)) = .2
        _Metallic("Metallic", Range(0,1)) = 0
        _Weathering("Weathering", Range(0,1)) = .6
        _MossAmount("Moss", Range(0,1)) = .15
        _Plaster("Fine Plaster", Float) = 0
        _WorldUV("World Foliage Mapping", Float) = 0
        _Fabric("Worn Fabric", Float) = 0
        _WindAmplitude("Wind amplitude", Float) = 0
        _WindHeight("Wind pin height", Float) = 1
        _WindAnchorTop("Pin top", Float) = 0
        _Cull("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Cull [_Cull]
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
        TEXTURE2D(_MetallicGlossMap); SAMPLER(sampler_MetallicGlossMap);
        TEXTURE2D(_OcclusionMap); SAMPLER(sampler_OcclusionMap);
        CBUFFER_START(UnityPerMaterial)
        float4 _BaseMap_ST, _BaseColor;
        float _BumpScale, _Smoothness, _Metallic, _Weathering, _MossAmount, _Plaster, _WorldUV, _Fabric, _Cull, _HasMask, _WindAmplitude, _WindHeight, _WindAnchorTop;
        CBUFFER_END
        float4 _UnseenWind;
        struct Attributes
        {
            float4 positionOS:POSITION;
            float3 normalOS:NORMAL;
            float4 tangentOS:TANGENT;
            float2 uv:TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        struct Varyings
        {
            float4 positionCS:SV_POSITION;
            float3 positionWS:TEXCOORD0;
            half3 normalWS:TEXCOORD1;
            half4 tangentWS:TEXCOORD2;
            float2 uv:TEXCOORD3;
            half fog:TEXCOORD4;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };
        Varyings Vert(Attributes v)
        {
            Varyings o = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_TRANSFER_INSTANCE_ID(v, o);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            float weight=saturate(v.positionOS.y/max(.01,_WindHeight));
            weight=lerp(weight,1-weight,_WindAnchorTop);
            float3 world=TransformObjectToWorld(v.positionOS.xyz);
            float wave=sin(world.x*.7+world.z*.43+_UnseenWind.w*1.8)+.35*sin(world.z*2.1+_UnseenWind.w*3.2);
            float3 sway=float3(_UnseenWind.x,0,_UnseenWind.y)*wave*weight*weight*_WindAmplitude;
            float3 displaced=v.positionOS.xyz+TransformWorldToObjectDir(sway,false);
            VertexPositionInputs p = GetVertexPositionInputs(displaced);
            o.positionWS = p.positionWS;
            o.positionCS = p.positionCS;
            o.normalWS = TransformObjectToWorldNormal(v.normalOS);
            o.tangentWS = half4(TransformObjectToWorldDir(v.tangentOS.xyz), v.tangentOS.w * GetOddNegativeScale());
            o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
            o.fog = ComputeFogFactor(p.positionCS.z);
            return o;
        }
        float Hash(float2 p) { return frac(sin(dot(p,float2(127.1,311.7)))*43758.5453); }
        float Noise(float2 p)
        {
            float2 i=floor(p), f=frac(p); f=f*f*(3-2*f);
            return lerp(lerp(Hash(i),Hash(i+float2(1,0)),f.x),
                lerp(Hash(i+float2(0,1)),Hash(i+1),f.x),f.y);
        }
        half4 Frag(Varyings i):SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(i);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            float3 p=i.positionWS;
            half3 n=normalize(i.normalWS);
            float2 vertical = abs(n.z)>abs(n.x) ? p.xy : p.zy;
            float2 uv=lerp(i.uv,vertical*float2(.42,.28),_WorldUV);
            half3 albedo=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,uv).rgb * _BaseColor.rgb;
            if (_WorldUV>.5)
            {
                // Recessed stems behind the modelled fringe, without a tiled leaf wallpaper.
                float lane=floor(vertical.x*5);
                float stalk=1-smoothstep(.22,.49,abs(frac(vertical.x*5+Noise(vertical*.3)*.13)-.5));
                float node=smoothstep(.91,.97,frac(vertical.y*1.6+Hash(float2(lane,0))));
                albedo=lerp(half3(.018,.028,.012),half3(.085,.115,.039),stalk);
                albedo*=.65+.35*Noise(float2(lane,vertical.y*.4));
                albedo+=node*stalk*half3(.013,.017,.005);
            }
            // Fabric wear follows the character's UVs rather than sliding through world noise.
            if (_Fabric>.5) { p=float3(i.uv*7,0); vertical=i.uv*7; }
            float patch=Noise(p.xz*.43 + p.y*.13);
            float grain=Noise(vertical*34);
            float runs=Noise(float2((vertical.x+Noise(vertical*.35)*.3)*5,vertical.y*.19));
            float damp=(1-smoothstep(.15,2.3,p.y)) * (.4+.6*Noise(p.xz*1.7));
            // Plaster uses mineral grain, not the paper weave on interior shoji.
            albedo=lerp(albedo,_BaseColor.rgb*(.89+grain*.18),_Plaster);
            float stain=smoothstep(.43,.83,runs)*(.18+.25*patch) + damp*.32;
            albedo *= 1-_Weathering*stain;
            albedo *= lerp(1,.77+patch*.3,_Weathering);
            if (_Fabric>.5)
            {
                half grey=dot(albedo,half3(.2126,.7152,.0722));
                albedo=lerp(albedo,grey.xxx,.48);
                albedo=lerp(albedo,half3(.18,.155,.12),smoothstep(.57,.82,patch)*.20);
            }
            float moss=_MossAmount*smoothstep(.48,.77,patch)*(damp+saturate(n.y)*.55);
            albedo=lerp(albedo,albedo*float3(.43,.57,.32),saturate(moss));
            half3 tangent=normalize(i.tangentWS.xyz);
            half3 bitangent=cross(n,tangent)*i.tangentWS.w;
            half3 normalTS=UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap,sampler_BumpMap,uv),_BumpScale*(1-_Plaster));
            // World-mapped background foliage has no matching mesh tangent basis.
            if (_WorldUV<.5 && dot(tangent,tangent)>.5)
                n=normalize(TransformTangentToWorld(normalTS,half3x3(tangent,bitangent,n)));
            InputData data=(InputData)0;
            data.positionWS=i.positionWS;
            data.normalWS=n;
            data.viewDirectionWS=GetWorldSpaceNormalizeViewDir(i.positionWS);
            data.shadowCoord=TransformWorldToShadowCoord(i.positionWS);
            data.fogCoord=i.fog;
            data.vertexLighting=VertexLighting(i.positionWS,n);
            data.bakedGI=SampleSH(n);
            data.normalizedScreenSpaceUV=GetNormalizedScreenSpaceUV(i.positionCS);
            data.shadowMask=half4(1,1,1,1);
            SurfaceData surface=(SurfaceData)0;
            surface.albedo=albedo;
            surface.metallic=_Metallic;
            surface.specular=half3(.04,.04,.04);
            float gloss=lerp(1,SAMPLE_TEXTURE2D(_MetallicGlossMap,sampler_MetallicGlossMap,uv).a,_HasMask);
            surface.smoothness=saturate(_Smoothness*gloss*(.8+patch*.3)+damp*.13*(1-_Fabric));
            surface.normalTS=half3(0,0,1);
            surface.occlusion=SAMPLE_TEXTURE2D(_OcclusionMap,sampler_OcclusionMap,uv).g;
            surface.alpha=1;
            half4 color=UniversalFragmentPBR(data,surface);
            color.rgb=MixFog(color.rgb,i.fog);
            return color;
        }
        float3 _LightDirection, _LightPosition;
        Varyings ShadowVert(Attributes v)
        {
            Varyings o=Vert(v);
            float3 lightDirection=_LightDirection;
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                lightDirection=normalize(_LightPosition-o.positionWS);
            #endif
            o.positionCS=TransformWorldToHClip(ApplyShadowBias(o.positionWS,o.normalWS,lightDirection));
            #if UNITY_REVERSED_Z
                o.positionCS.z=min(o.positionCS.z,UNITY_NEAR_CLIP_VALUE*o.positionCS.w);
            #else
                o.positionCS.z=max(o.positionCS.z,UNITY_NEAR_CLIP_VALUE*o.positionCS.w);
            #endif
            return o;
        }
        half4 DepthFrag(Varyings i):SV_Target { return 0; }
        half4 NormalFrag(Varyings i):SV_Target { return half4(normalize(i.normalWS),0); }
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
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
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ColorMask 0
            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask 0
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment NormalFrag
            #pragma multi_compile_instancing
            ENDHLSL
        }
    }
}
