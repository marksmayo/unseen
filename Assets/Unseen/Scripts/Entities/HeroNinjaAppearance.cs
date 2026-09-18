using UnityEngine;
namespace Unseen.Entities
{
    /// <summary>Shared palettes for the Blender-authored skinned character.</summary>
    public static class HeroNinjaAppearance
    {
        private static Material _indigo, _slate;
        public static bool IsHero(AgentVisual visual) => visual != null && visual.Body != null &&
            visual.Body.sharedMesh != null && visual.Body.sharedMesh.name.StartsWith("HeroNinja");
        public static void Apply(AgentVisual visual, Material source)
        {
            if (_indigo == null) _indigo = Resources.Load<Material>("HeroNinjaMaterial");
            if (_indigo == null) return;
            bool slate = source != null && source.name.Contains("Ash");
            if (slate && _slate == null)
            {
                _slate = new Material(_indigo) { name = "Hero ninja - slate" };
                _slate.SetColor("_AccentTint", new Color(.88f, .95f, 1.10f));
            }
            foreach (var skin in visual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (skin.sharedMesh != null && skin.sharedMesh.name.StartsWith("HeroNinja"))
                    skin.sharedMaterial = slate ? _slate : _indigo;
        }
    }
}
