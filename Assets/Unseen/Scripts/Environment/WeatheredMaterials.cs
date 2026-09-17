using System.Collections.Generic;
using UnityEngine;

namespace Unseen.Environment
{
    /// <summary>Shared weathered variants; original material assets remain editable.</summary>
    public static class WeatheredMaterials
    {
        private static readonly Dictionary<(Material, bool, bool), Material> Cache =
            new Dictionary<(Material, bool, bool), Material>();
        private static Material _leaves;
        private static readonly Dictionary<Material, Material> Fabrics = new Dictionary<Material, Material>();

        public static Material Fabric(Material source)
        {
            if (source == null) return null;
            if (Fabrics.TryGetValue(source, out Material cached) && cached != null) return cached;
            Shader shader = Shader.Find("Unseen/WeatheredSurface");
            if (shader == null) return source;
            var material = new Material(source) { name = source.name + "_Worn", shader = shader };
            material.shaderKeywords = new string[0];
            material.SetFloat("_Fabric", 1);
            material.SetFloat("_Weathering", .32f);
            material.SetFloat("_BumpScale", .4f);
            material.SetFloat("_MossAmount", 0);
            material.SetFloat("_Plaster", 0);
            material.SetFloat("_WorldUV", 0);
            material.SetFloat("_Smoothness", .12f);
            material.SetFloat("_Metallic", 0);
            material.SetFloat("_HasMask", 0);
            Fabrics[source] = material;
            return material;
        }

        public static Material Surface(Material source, bool plaster = false, bool forest = false)
        {
            if (source == null) return null;
            var key = (source, plaster, forest);
            if (Cache.TryGetValue(key, out Material existing) && existing != null) return existing;
            Shader shader = Shader.Find("Unseen/WeatheredSurface");
            if (shader == null) return source;
            var material = new Material(source) { name = source.name + "_Weathered", shader = shader };
            material.shaderKeywords = new string[0];
            material.enableInstancing = true;
            material.SetFloat("_Weathering", forest ? .25f : .48f);
            material.SetFloat("_MossAmount", forest ? .1f : .24f);
            material.SetFloat("_Plaster", plaster ? 1 : 0);
            material.SetFloat("_WorldUV", forest ? 1 : 0);
            material.SetFloat("_Fabric", 0);
            material.SetFloat("_Metallic", 0);
            material.SetFloat("_HasMask", source.HasProperty("_MetallicGlossMap") && source.GetTexture("_MetallicGlossMap") != null ? 1 : 0);
            material.SetFloat("_Smoothness", Mathf.Min(.32f, source.GetFloat("_Smoothness")));
            if (plaster) material.SetColor("_BaseColor", new Color(.52f, .49f, .41f));
            Cache[key] = material;
            return material;
        }

        public static Material BambooLeaves(Material source)
        {
            if (_leaves != null) return _leaves;
            if (source == null) return null;
            var material = new Material(source) { name = "BambooLeafBlade", shader = Shader.Find("Unseen/WindSurface") ?? source.shader };
            material.SetFloat("_WindAmplitude", .075f);
            material.SetFloat("_WindHeight", .25f);
            material.SetFloat("_Weathering", .15f);
            material.SetFloat("_MossAmount", 0);
            material.SetTexture("_BaseMap", null);
            material.SetTexture("_BumpMap", null);
            material.DisableKeyword("_NORMALMAP");
            material.SetColor("_BaseColor", new Color(.18f, .25f, .085f));
            material.SetFloat("_Smoothness", .24f);
            material.enableInstancing = true;
            _leaves = material;
            return material;
        }
        private static Material _windCloth;
        public static Material WindCloth(Material source)
        {
            if (_windCloth != null) return _windCloth;
            _windCloth = new Material(source) { name = "Wind Noren", shader = Shader.Find("Unseen/WindSurface") ?? source.shader, enableInstancing = true };
            _windCloth.SetFloat("_WindAmplitude", .075f); _windCloth.SetFloat("_WindHeight", 1);
            _windCloth.SetFloat("_WindAnchorTop", 1); _windCloth.SetFloat("_Cull", 0);
            _windCloth.SetFloat("_Fabric", 1); _windCloth.SetFloat("_Weathering", .35f);
            return _windCloth;
        }
    }
}
