using UnityEngine;

namespace Unseen.Audio
{
    /// <summary>
    /// The wind. A quiet bed that never stops, with gusts over the top of it every so often.
    ///
    /// Louder up high and in the open, quieter down in a street or under a bridge, which does a
    /// little atmospheric work for free: the roofs are where you are exposed, and they are also
    /// where you can hear the weather. It doubles as a noise floor - a game this quiet makes every
    /// footstep sound like a footstep in a recording booth without one.
    /// </summary>
    public sealed class AmbientWind : MonoBehaviour
    {
        [Tooltip("Volume of the constant bed at ground level.")]
        [Range(0f, 1f)] public float BedVolume = 0.16f;

        [Tooltip("Extra bed volume when high up and unsheltered.")]
        [Range(0f, 1f)] public float ExposedBonus = 0.22f;

        [Tooltip("Height above ground at which the wind is considered fully exposed.")]
        public float ExposureHeight = 12f;

        [Tooltip("Average seconds between gusts.")]
        public float GustInterval = 21f;

        [Range(0f, 1f)] public float GustVolume = 0.3f;

        private AudioBank _bank;
        private AudioSource _bed;
        private AudioSource _gust;
        private float _nextGust;
        private float _exposure;

        private void Awake()
        {
            _bank = AudioBank.Load();
            if (_bank == null || _bank.WindBed == null) return;

            _bed = Make("WindBed");
            _bed.clip = _bank.WindBed;
            _bed.loop = true;
            _bed.volume = BedVolume;
            _bed.Play();

            _gust = Make("WindGust");
            _nextGust = Time.time + Random.Range(GustInterval * 0.3f, GustInterval);
        }

        private AudioSource Make(string name)
        {
            var host = new GameObject(name);
            host.transform.SetParent(transform, false);

            var source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;

            // 2D: weather has no position, and panning it would make the sky swing when you turn.
            source.spatialBlend = 0f;
            source.dopplerLevel = 0f;
            return source;
        }

        private void Update()
        {
            if (_bed == null) return;

            // Exposure is height above whatever is directly below, not absolute altitude: a
            // rooftop is exposed, and the bottom of a deep river channel is not, even though both
            // can sit at a similar world Y.
            float target = 0f;
            if (Physics.Raycast(transform.position, Vector3.down, out RaycastHit hit, 60f,
                    Core.UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                target = Mathf.Clamp01(hit.distance / Mathf.Max(1f, ExposureHeight));

            // Under a roof, the wind drops away.
            if (Physics.Raycast(transform.position, Vector3.up, out RaycastHit _, 20f,
                    Core.UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                target *= 0.25f;

            _exposure = Mathf.Lerp(_exposure, target, Time.deltaTime * 0.7f);
            _bed.volume = BedVolume + ExposedBonus * _exposure;

            if (Time.time < _nextGust || _gust == null) return;

            AudioClip clip = AudioBank.Pick(_bank.WindGusts);
            _nextGust = Time.time + Random.Range(GustInterval * 0.5f, GustInterval * 1.6f);
            if (clip == null) return;

            _gust.pitch = Random.Range(0.85f, 1.15f);
            _gust.PlayOneShot(clip, GustVolume * (0.4f + 0.6f * _exposure));
        }
    }
}
