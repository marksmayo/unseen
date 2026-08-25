using System.Collections.Generic;
using UnityEngine;
using Unseen.Client;
using Unseen.Core;
using Unseen.Net;

namespace Unseen.Audio
{
    /// <summary>
    /// Plays what the listener was told they can hear.
    ///
    /// The server already decided audibility: <see cref="HeardSound"/> carries intensity after
    /// distance falloff, an occlusion figure, and an <em>apparent</em> position that is
    /// deliberately wrong when the path was muffled. This turns that into sound without
    /// second-guessing any of it - notably it plays at the apparent position, not the true one, so
    /// what you hear and what the HUD ping shows agree, and a muffled footstep misleads the ear
    /// exactly as much as it misleads the eye.
    ///
    /// Occlusion drives a low-pass cutoff, which is the one thing the simulation cannot express in
    /// a number and the ear reads instantly.
    /// </summary>
    public sealed class SoundRenderer : MonoBehaviour
    {
        [Tooltip("Snapshot source. Heard sounds arrive with each snapshot.")]
        public ClientNetworkView View;

        [Tooltip("Simultaneous voices. Excess sounds in one tick are dropped loudest-first.")]
        public int Voices = 24;

        [Tooltip("Master volume for world sound.")]
        [Range(0f, 2f)] public float Volume = 1f;

        private AudioBank _bank;
        private readonly List<AudioSource> _pool = new List<AudioSource>();
        private readonly List<AudioLowPassFilter> _filters = new List<AudioLowPassFilter>();
        private int _next;

        public int PlayedThisSession { get; private set; }

        private void Awake()
        {
            _bank = AudioBank.Load();
            if (_bank == null || !_bank.IsUsable)
            {
                Debug.LogWarning("[Unseen] no AudioBank in Resources; world sound is silent. " +
                                 "Run Unseen > Art > Build Audio Bank.");
                return;
            }

            for (int i = 0; i < Mathf.Max(4, Voices); i++)
            {
                var host = new GameObject($"Voice_{i}");
                host.transform.SetParent(transform, false);

                var source = host.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 1f; // fully positional
                source.rolloffMode = AudioRolloffMode.Linear;
                source.dopplerLevel = 0f; // ninjas, not jets
                source.minDistance = 1.5f;

                // A filter on a source with no clip logs "Only custom filters can be played" once
                // per voice at startup - twenty-four lines of noise in the player log. A single
                // silent sample is enough to satisfy it.
                source.clip = Silence();

                var filter = host.AddComponent<AudioLowPassFilter>();
                filter.cutoffFrequency = 22000f;

                _pool.Add(source);
                _filters.Add(filter);
            }
        }

        private static AudioClip _silence;

        private static AudioClip Silence()
        {
            if (_silence != null) return _silence;
            _silence = AudioClip.Create("unseen-silence", 1, 1, 22050, false);
            _silence.SetData(new float[1], 0);
            return _silence;
        }

        private void Start()
        {
            if (View != null) View.SnapshotApplied += OnSnapshot;
        }

        private void OnDestroy()
        {
            if (View != null) View.SnapshotApplied -= OnSnapshot;
        }

        private void OnSnapshot(SnapshotData snapshot)
        {
            if (_bank == null || snapshot == null) return;

            for (int i = 0; i < snapshot.Sounds.Count; i++)
                Play(snapshot.Sounds[i]);
        }

        private void Play(in HeardSound heard)
        {
            AudioBank.Entry entry = _bank.For(heard.Kind);
            AudioClip clip = entry != null ? AudioBank.Pick(entry.Clips) : null;

            // Footsteps pick their clip from the surface, which the entry cannot know.
            if (heard.Kind == SoundKind.Footstep)
            {
                AudioClip surfaced = SurfaceClipAt(heard.ApparentPosition);
                if (surfaced != null) clip = surfaced;
            }

            if (clip == null) return;

            AudioSource source = _pool.Count > 0 ? _pool[_next % _pool.Count] : null;
            if (source == null) return;

            // Round-robin rather than "find a free one": a stolen voice on the oldest sound is
            // less noticeable than a dropped one on the newest.
            AudioLowPassFilter filter = _filters[_next % _filters.Count];
            _next++;

            source.transform.position = (Vector3)heard.ApparentPosition;
            source.clip = clip;
            source.volume = Mathf.Clamp01(heard.Intensity) * (entry?.Volume ?? 1f) * Volume;
            source.maxDistance = entry?.MaxDistance ?? 40f;

            float jitter = entry?.PitchJitter ?? 0.1f;
            source.pitch = 1f + Random.Range(-jitter, jitter);

            // Muffled sounds lose their top end. 22 kHz is "open air", 700 Hz is "through a wall".
            filter.cutoffFrequency = Mathf.Lerp(22000f, 700f, Mathf.Clamp01(heard.Occlusion));

            source.Play();
            PlayedThisSession++;
        }

        /// <summary>Probes downward at a footstep to find what it was taken on.</summary>
        private AudioClip SurfaceClipAt(Vector3 position)
        {
            if (!Physics.Raycast(position + Vector3.up * 0.6f, Vector3.down, out RaycastHit hit, 2.5f,
                    UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                return null;

            return _bank.FootstepFor(AcousticMaterial.For(hit.collider));
        }
    }
}
