using System.Collections.Generic;
using UnityEngine;
using Unseen.Core;

namespace Unseen.Audio
{
    /// <summary>
    /// Every clip the game can play, looked up by <see cref="SoundKind"/>.
    ///
    /// The gameplay contract was already settled server-side: a sound has a kind, an intensity, an
    /// occlusion and an apparent position, and the simulation decided all four. This asset is only
    /// the answer to "what does that sound like", which is why replacing the placeholder clips with
    /// recorded ones needs no code change at all - the bank is looked up by name.
    ///
    /// Build or refresh with <c>Unseen ▸ Art ▸ Build Audio Bank</c>.
    /// </summary>
    [CreateAssetMenu(menuName = "Unseen/Audio Bank", fileName = "AudioBank")]
    public sealed class AudioBank : ScriptableObject
    {
        public const string ResourcePath = "AudioBank";

        [System.Serializable]
        public sealed class Entry
        {
            public SoundKind Kind;

            [Tooltip("Variants. One is chosen at random so a run of footsteps does not machine-gun.")]
            public AudioClip[] Clips = new AudioClip[0];

            [Range(0f, 2f)] public float Volume = 1f;

            [Tooltip("Random pitch spread, so repeats of one clip do not read as one clip.")]
            [Range(0f, 0.5f)] public float PitchJitter = 0.12f;

            [Tooltip("Metres at which this sound has fallen to silence for the ear.")]
            public float MaxDistance = 40f;
        }

        public Entry[] Entries = new Entry[0];

        [Header("Surfaces")]
        [Tooltip("Footsteps on stone, tile and gravel.")]
        public AudioClip[] FootstepHard = new AudioClip[0];

        [Tooltip("Footsteps on earth and tatami.")]
        public AudioClip[] FootstepSoft = new AudioClip[0];

        [Tooltip("Footsteps on boards and rafters.")]
        public AudioClip[] FootstepWood = new AudioClip[0];

        [Tooltip("Footsteps in the river.")]
        public AudioClip[] FootstepWater = new AudioClip[0];

        [Header("Atmosphere")]
        [Tooltip("Seamless wind bed, played as a quiet loop.")]
        public AudioClip WindBed;

        [Tooltip("Occasional gusts layered over the bed.")]
        public AudioClip[] WindGusts = new AudioClip[0];

        [Tooltip("Rush of air while falling. Looped, faded in by fall speed.")]
        public AudioClip FallWind;

        private Dictionary<SoundKind, Entry> _index;

        public bool IsUsable => Entries != null && Entries.Length > 0;

        public Entry For(SoundKind kind)
        {
            if (_index == null)
            {
                _index = new Dictionary<SoundKind, Entry>(Entries.Length);
                foreach (Entry e in Entries)
                    if (e != null && e.Clips != null && e.Clips.Length > 0)
                        _index[e.Kind] = e;
            }

            return _index.TryGetValue(kind, out Entry found) ? found : null;
        }

        public static AudioClip Pick(AudioClip[] clips)
        {
            if (clips == null || clips.Length == 0) return null;
            return clips[Random.Range(0, clips.Length)];
        }

        /// <summary>
        /// Footstep set for a surface, chosen from the acoustic material the foot landed on.
        ///
        /// Reuses the acoustic data the sound model already needs rather than adding a second,
        /// parallel notion of what a surface is - if a surface is loud to the simulation, it is
        /// loud to the ear, and the two cannot drift apart.
        /// </summary>
        public AudioClip FootstepFor(AcousticMaterial surface)
        {
            if (surface == null) return Pick(FootstepHard);

            // Water is the odd one out: very low attenuation, very high footstep scale.
            if (surface.FootstepScale >= 2f) return Pick(FootstepWater);
            if (surface.Attenuation <= 0.35f) return Pick(FootstepSoft);
            if (surface.Attenuation <= 0.7f) return Pick(FootstepWood);
            return Pick(FootstepHard);
        }

        private static AudioBank _cached;
        private static bool _searched;

        public static AudioBank Load()
        {
            if (_searched) return _cached;
            _searched = true;
            _cached = Resources.Load<AudioBank>(ResourcePath);
            return _cached;
        }
    }
}
