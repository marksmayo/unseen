Shader "Unseen/WindSurface"
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
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "DisableBatching"="True" }
        Cull [_Cull]
        UsePass "Unseen/WeatheredSurface/ForwardLit"
        UsePass "Unseen/WeatheredSurface/ShadowCaster"
        UsePass "Unseen/WeatheredSurface/DepthOnly"
        UsePass "Unseen/WeatheredSurface/DepthNormals"
    }
}
