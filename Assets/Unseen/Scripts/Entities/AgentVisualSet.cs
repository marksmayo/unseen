using UnityEngine;
using Unseen.Core;

namespace Unseen.Entities
{
    /// <summary>
    /// The ninja body used by spawned agents and client proxies. Absent, both fall back to the
    /// capsule they were built with, so the project still runs with no character art at all.
    ///
    /// Build or refresh it with <c>Unseen ▸ Art ▸ Build Ninja Character</c>.
    /// </summary>
    [CreateAssetMenu(menuName = "Unseen/Agent Visual Set", fileName = "AgentVisualSet")]
    public sealed class AgentVisualSet : ScriptableObject
    {
        public const string ResourcePath = "AgentVisualSet";

        [Tooltip("Prefab with the skinned mesh and animator. Must contain no colliders.")]
        public GameObject NinjaVisual;

        [Tooltip("Skin variants, picked per agent so a lobby is not 64 identical figures.")]
        public Material[] Skins = new Material[0];

        [Tooltip("Vertical offset applied to the visual inside the agent, in metres.")]
        public float VerticalOffset;

        private static AgentVisualSet _cached;
        private static bool _searched;

        public bool IsUsable => NinjaVisual != null;

        /// <summary>Skin for an agent, chosen deterministically from its id.</summary>
        public Material SkinFor(int id)
        {
            if (Skins == null || Skins.Length == 0) return null;
            int index = Mathf.Abs(id) % Skins.Length;
            return Skins[index];
        }

        public static AgentVisualSet Load()
        {
            if (_searched) return _cached;
            _searched = true;
            _cached = Resources.Load<AgentVisualSet>(ResourcePath);
            return _cached;
        }

        /// <summary>
        /// Instantiates the body under an agent or proxy, stripping any collider that sneaks in with
        /// the art. A collider here would quietly change line-of-sight and parkour behaviour.
        /// </summary>
        public AgentVisual Attach(Transform parent, int id)
        {
            if (NinjaVisual == null) return null;

            GameObject instance = Instantiate(NinjaVisual, parent, false);
            instance.transform.localPosition = new Vector3(0f, VerticalOffset, 0f);
            instance.transform.localRotation = Quaternion.identity;

            var colliders = instance.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++) UnseenObject.Destroy(colliders[i]);

            AgentVisual visual = instance.GetComponent<AgentVisual>();
            if (visual == null) visual = instance.AddComponent<AgentVisual>();
            visual.SetSkin(SkinFor(id));
            return visual;
        }
    }
}
