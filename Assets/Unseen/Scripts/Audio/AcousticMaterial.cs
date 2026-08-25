using System.Collections.Generic;
using UnityEngine;
using Unseen.Core;

namespace Unseen.Audio
{
    /// <summary>
    /// Per-surface acoustic behaviour. Attached to geometry; absent colliders fall back to a
    /// sensible default for their layer, so a greybox level still sounds like a building.
    /// </summary>
    public sealed class AcousticMaterial : MonoBehaviour
    {
        [Tooltip("Fraction of a sound removed when a path crosses this surface. 1 = silent wall.")]
        [Range(0f, 1f)] public float Attenuation = 0.55f;

        [Tooltip("Multiplier on footstep loudness for anyone walking on this surface.")]
        [Range(0f, 3f)] public float FootstepScale = 1f;

        [Tooltip("Multiplier on the audible radius of footsteps taken here. Gravel carries, tatami does not.")]
        [Range(0f, 3f)] public float FootstepRadiusScale = 1f;

        private static readonly Dictionary<Collider, AcousticMaterial> Lookup =
            new Dictionary<Collider, AcousticMaterial>(512);

        private static readonly Dictionary<Collider, float> AttenuationCache =
            new Dictionary<Collider, float>(512);

        private void Awake()
        {
            var colliders = GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                Lookup[colliders[i]] = this;
        }

        private void OnDestroy()
        {
            var colliders = GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Lookup.Remove(colliders[i]);
                AttenuationCache.Remove(colliders[i]);
            }
        }

        /// <summary>Default attenuation for geometry with no explicit material.</summary>
        public static float DefaultAttenuationForLayer(int layer)
        {
            switch (layer)
            {
                case UnseenLayers.ShojiPaper: return 0.12f; // paper hides sight, barely touches sound
                case UnseenLayers.Rafter: return 0.3f;
                case UnseenLayers.Foliage: return 0.08f;
                case UnseenLayers.Occluder: return 0.7f;
                default: return 0.55f;
            }
        }

        /// <summary>Attenuation for a collider hit by an acoustic probe. Cached per collider.</summary>
        public static float AttenuationFor(Collider collider)
        {
            if (collider == null) return 0f;

            if (AttenuationCache.TryGetValue(collider, out float cached)) return cached;

            float value;
            if (Lookup.TryGetValue(collider, out AcousticMaterial mat) && mat != null)
            {
                value = mat.Attenuation;
            }
            else
            {
                AcousticMaterial found = collider.GetComponentInParent<AcousticMaterial>();
                value = found != null ? found.Attenuation : DefaultAttenuationForLayer(collider.gameObject.layer);
                if (found != null) Lookup[collider] = found;
            }

            AttenuationCache[collider] = value;
            return value;
        }

        public static AcousticMaterial For(Collider collider)
        {
            if (collider == null) return null;
            if (Lookup.TryGetValue(collider, out AcousticMaterial mat)) return mat;

            AcousticMaterial found = collider.GetComponentInParent<AcousticMaterial>();
            if (found != null) Lookup[collider] = found;
            return found;
        }
    }
}
