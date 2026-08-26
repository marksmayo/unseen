using System.Collections.Generic;
using UnityEngine;
using Unseen.Audio;

namespace Unseen.Environment
{
    /// <summary>
    /// The sound of the river, and the movement of its surface.
    ///
    /// Emitters are spaced along the channel rather than parented to one point, because a river is
    /// a line source: a single AudioSource in the middle would be silent at the bridges and
    /// deafening at the centre. Each one has a large falloff radius and they overlap, which is what
    /// makes the water audible from the streets either side without being loud on the towpath.
    ///
    /// Only the handful nearest the listener are allowed to play, for the same reason the lanterns
    /// are budgeted: a river four hundred metres long does not need forty voices to be heard.
    /// </summary>
    public sealed class RiverAmbience : MonoBehaviour
    {
        [Tooltip("Metres between emitters along the channel.")]
        public float Spacing = 48f;

        [Tooltip("How far one emitter carries.")]
        public float Range = 46f;

        [Tooltip("Volume of a single emitter at its centre.")]
        [Range(0f, 1f)] public float Volume = 0.5f;

        [Tooltip("How many emitters may sound at once, nearest first.")]
        public int Budget = 4;

        [Tooltip("Seconds between re-sorts.")]
        public float Interval = 0.5f;

        [Tooltip("Metres per second the water surface appears to move.")]
        public float FlowSpeed = 0.09f;

        private readonly List<AudioSource> _sources = new List<AudioSource>(16);
        private Material _water;
        private float _next;
        private float _scroll;
        private Camera _camera;

        /// <summary>Places emitters down the channel. Called by the generator once the river exists.</summary>
        public void Configure(float centreX, float halfLength, Material water)
        {
            _water = water;

            AudioBank bank = AudioBank.Load();
            AudioClip clip = bank != null ? bank.RiverFlow : null;
            if (clip == null) return;

            int count = Mathf.Max(1, Mathf.RoundToInt(halfLength * 2f / Mathf.Max(1f, Spacing)));

            for (int i = 0; i <= count; i++)
            {
                float z = Mathf.Lerp(-halfLength, halfLength, i / (float)count);

                var host = new GameObject($"RiverVoice_{i}");
                host.transform.SetParent(transform, false);
                host.transform.position = new Vector3(centreX, 0f, z);

                var source = host.AddComponent<AudioSource>();
                source.clip = clip;
                source.loop = true;
                source.playOnAwake = false;
                source.spatialBlend = 1f;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.dopplerLevel = 0f;
                source.minDistance = 6f;
                source.maxDistance = Range;
                source.volume = Volume;

                // Started out of phase, or every emitter swells in unison and the river pulses.
                source.time = clip.length * (i / (float)(count + 1));

                _sources.Add(source);
            }

            Debug.Log($"[Unseen] river ambience: {_sources.Count} emitters, {Budget} audible at once");
        }

        private void Update()
        {
            // The surface slides even when nobody is near enough to hear it: a still river reads
            // as a painted floor the moment you look at it.
            if (_water != null)
            {
                _scroll += FlowSpeed * Time.deltaTime;
                _water.SetTextureOffset("_BaseMap", new Vector2(0f, _scroll));
                if (_water.HasProperty("_BumpMap"))
                    _water.SetTextureOffset("_BumpMap", new Vector2(0f, _scroll * 1.3f));
            }

            if (_sources.Count == 0 || Time.time < _next) return;
            _next = Time.time + Interval;

            if (_camera == null) _camera = Camera.main;
            if (_camera == null) return;

            Vector3 ear = _camera.transform.position;

            // Nearest few play, the rest stop. Sorting a dozen entries twice a second is nothing.
            _sources.Sort((a, b) =>
                (a.transform.position - ear).sqrMagnitude.CompareTo(
                    (b.transform.position - ear).sqrMagnitude));

            for (int i = 0; i < _sources.Count; i++)
            {
                AudioSource source = _sources[i];
                bool wanted = i < Budget &&
                              (source.transform.position - ear).sqrMagnitude < Range * Range;

                if (wanted && !source.isPlaying) source.Play();
                else if (!wanted && source.isPlaying) source.Stop();
            }
        }
    }
}
