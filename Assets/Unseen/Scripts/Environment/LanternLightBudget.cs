using System.Collections.Generic;
using UnityEngine;

namespace Unseen.Environment
{
    /// <summary>
    /// Keeps only the nearest lantern lights switched on.
    ///
    /// A town this size has around a thousand lanterns. Every one of them is a real light for
    /// gameplay - <see cref="Perception.StealthLightSource"/> reads its own radius and intensity
    /// and is never touched here - but a thousand real-time point lights is not something any
    /// renderer will do at frame rate. So the rendered light is budgeted: the closest N to the
    /// camera are lit, the rest are dark shells that still glow from their emissive material.
    ///
    /// This is a rendering concern only. Turning a light off here does not make anyone harder to
    /// see, which is exactly why it is safe to do.
    /// </summary>
    public sealed class LanternLightBudget : MonoBehaviour
    {
        [Tooltip("How many lantern lights may be on at once.")]
        public int Budget = 40;

        [Tooltip("Seconds between re-sorts. The player cannot outrun this at a sprint.")]
        public float Interval = 0.35f;

        [Tooltip("Beyond this range a lantern is never lit, however few are on.")]
        public float MaxRange = 85f;

        private readonly List<Light> _lights = new List<Light>(1024);
        private readonly List<int> _order = new List<int>(1024);
        private float[] _distances = new float[0];
        private float _next;
        private Camera _camera;

        /// <summary>Finds every lantern light under this object. Called once, after generation.</summary>
        public void Collect()
        {
            _lights.Clear();

            foreach (Lantern lantern in Lantern.All)
            {
                if (lantern == null) continue;
                Light light = lantern.GetComponent<Light>();
                if (light != null) _lights.Add(light);
            }

            _distances = new float[_lights.Count];
            Debug.Log($"[Unseen] lantern light budget: {_lights.Count} lanterns, {Budget} lit at once");
        }

        private void LateUpdate()
        {
            if (_lights.Count == 0 || Time.time < _next) return;
            _next = Time.time + Interval;

            if (_camera == null) _camera = Camera.main;
            if (_camera == null) return;

            Vector3 eye = _camera.transform.position;
            float maxRangeSq = MaxRange * MaxRange;

            _order.Clear();
            for (int i = 0; i < _lights.Count; i++)
            {
                Light light = _lights[i];
                if (light == null) continue;

                float d = (light.transform.position - eye).sqrMagnitude;
                _distances[i] = d;

                if (d <= maxRangeSq) _order.Add(i);
                else if (light.enabled) light.enabled = false;
            }

            // Partial ordering would be cheaper, but this runs three times a second on a list of
            // candidates already cut down by range, and clarity is worth more than the microseconds.
            _order.Sort((a, b) => _distances[a].CompareTo(_distances[b]));

            for (int rank = 0; rank < _order.Count; rank++)
            {
                Light light = _lights[_order[rank]];
                bool shouldBeOn = rank < Budget;
                if (light.enabled != shouldBeOn) light.enabled = shouldBeOn;
            }
        }
    }
}
