using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Unseen.Perception
{
    /// <summary>
    /// A light the stealth system knows about. Every lantern, brazier and window shaft carries one.
    /// Extinguishing it is a gameplay action: the shadow it was holding back expands immediately.
    /// </summary>
    public sealed class StealthLightSource : MonoBehaviour
    {
        private static readonly List<StealthLightSource> Sources = new List<StealthLightSource>(256);

        [Tooltip("Radius at which this source no longer contributes to exposure.")]
        public float Radius = 9f;

        [Tooltip("Exposure contributed at the source. 1.0 fully reveals an agent standing in it.")]
        [Range(0f, 4f)] public float Intensity = 1f;

        [Tooltip("Optional visual light driven in lockstep with the gameplay state.")]
        public Light Visual;

        [Tooltip("Moonlight and other sources that cannot be put out.")]
        public bool Indestructible;

        [SerializeField] private bool _extinguished;

        public static IReadOnlyList<StealthLightSource> All => Sources;

        public bool Extinguished => _extinguished;
        public float3 Position => transform.position;

        private float _baseVisualIntensity = -1f;

        private void OnEnable()
        {
            EnsureRegistered();
        }

        /// <summary>Joins the light registry. Safe to call more than once.</summary>
        public void EnsureRegistered()
        {
            if (!Sources.Contains(this)) Sources.Add(this);
            if (Visual == null) Visual = GetComponent<Light>();
            if (Visual != null && _baseVisualIntensity < 0f) _baseVisualIntensity = Visual.intensity;
            ApplyVisual();
        }

        private void OnDisable()
        {
            Sources.Remove(this);
        }

        public void SetExtinguished(bool value)
        {
            if (Indestructible && value) return;
            if (_extinguished == value) return;
            _extinguished = value;
            ApplyVisual();
        }

        private void ApplyVisual()
        {
            if (Visual == null) return;
            if (_baseVisualIntensity < 0f) _baseVisualIntensity = Visual.intensity;
            Visual.enabled = !_extinguished;
            Visual.intensity = _extinguished ? 0f : _baseVisualIntensity;
        }

        /// <summary>Unoccluded exposure this source delivers at a point, before any raycast.</summary>
        public float ExposureAt(float3 point)
        {
            if (_extinguished) return 0f;
            float d = math.distance(point, Position);
            if (d >= Radius) return 0f;
            float t = 1f - d / Radius;
            return Intensity * t * t;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = _extinguished ? new Color(0.2f, 0.2f, 0.3f, 0.5f) : new Color(1f, 0.85f, 0.4f, 0.35f);
            Gizmos.DrawWireSphere(transform.position, Radius);
        }
#endif
    }
}
