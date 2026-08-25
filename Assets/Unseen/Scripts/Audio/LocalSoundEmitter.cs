using UnityEngine;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Movement;

namespace Unseen.Audio
{
    /// <summary>
    /// The sounds you make yourself.
    ///
    /// <see cref="AcousticPropagation"/> skips the listener as a source on purpose - you do not
    /// need to be told about your own noise, and modelling the path from your feet to your ears
    /// would be silly. That is right for gameplay and leaves the player in silence, so own-audio is
    /// generated here from the agent's own state instead: stride from ground speed, a landing from
    /// the airborne-to-grounded transition, a rush of air while falling.
    ///
    /// These are the sounds a player judges their own stealth by, so the volumes track the same
    /// stance and sprint scalars the simulation uses to decide how loud you actually are. Creeping
    /// has to sound like creeping.
    /// </summary>
    public sealed class LocalSoundEmitter : MonoBehaviour
    {
        [Tooltip("Master volume for your own sounds.")]
        [Range(0f, 2f)] public float Volume = 0.75f;

        [Tooltip("Speed below which no footsteps are emitted at all.")]
        public float MinimumSpeed = 0.4f;

        private AudioBank _bank;
        private UnseenConfig _config;
        private AgentEntity _agent;

        private AudioSource _feet;
        private AudioSource _body;
        private AudioSource _air;

        private float _strideTimer;
        private bool _wasAirborne;
        private float _lastHealth = 1f;
        private float _peakFall;

        public void Bind(AgentEntity agent, UnseenConfig config)
        {
            _agent = agent;
            _config = config;
            _lastHealth = agent != null ? agent.Vitals.Fraction : 1f;
        }

        private void Awake()
        {
            _bank = AudioBank.Load();
            if (_bank == null) return;

            _feet = MakeSource("Feet", loop: false);
            _body = MakeSource("Body", loop: false);
            _air = MakeSource("Air", loop: true);

            if (_bank.FallWind != null)
            {
                _air.clip = _bank.FallWind;
                _air.volume = 0f;
                _air.Play();
            }
        }

        private AudioSource MakeSource(string name, bool loop)
        {
            var host = new GameObject(name);
            host.transform.SetParent(transform, false);

            var source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = loop;

            // Your own sounds are 2D. Positioning them on your own head only produces
            // stereo-imaging artefacts as the camera swings around you in third person.
            source.spatialBlend = 0f;
            source.dopplerLevel = 0f;
            return source;
        }

        private void Update()
        {
            if (_bank == null || _agent == null || _config == null) return;
            if (!_agent.IsAlive)
            {
                if (_air != null) _air.volume = 0f;
                return;
            }

            NinjaMotor motor = _agent.Motor;
            if (motor == null) return;

            Footsteps(motor);
            Landing(motor);
            FallRush(motor);
            Hurt();
            Grapple(motor);
        }

        private void Footsteps(NinjaMotor motor)
        {
            bool grounded = _agent.Locomotion == LocomotionState.Grounded ||
                            _agent.Locomotion == LocomotionState.RafterCrawl;

            if (!grounded)
            {
                _strideTimer = 0f;
                return;
            }

            Vector3 flat = (Vector3)motor.Velocity;
            flat.y = 0f;
            float speed = flat.magnitude;

            if (speed < MinimumSpeed)
            {
                _strideTimer = 0f;
                return;
            }

            bool sprinting = (_agent.Flags & AgentFlags.Sprinting) != 0;

            // Stride scales with speed, so a creep is slow and deliberate and a sprint is a patter.
            float reference = _config.StanceSpeed(_agent.Stance, sprinting);
            float interval = _config.Audio.StrideInterval * Mathf.Clamp(reference / Mathf.Max(0.5f, speed), 0.6f, 3.5f);

            _strideTimer += Time.deltaTime;
            if (_strideTimer < interval) return;
            _strideTimer = 0f;

            AudioClip clip = SurfaceClip();
            if (clip == null || _feet == null) return;

            // Same scalars the simulation uses to decide how loud you are to others, so what you
            // hear is an honest report of how exposed you are.
            float loudness = _config.StanceLoudnessScale(_agent.Stance, sprinting);
            if (_agent.Inventory != null) loudness *= _agent.Inventory.FootstepLoudnessScale;

            _feet.pitch = Random.Range(0.92f, 1.08f);
            _feet.PlayOneShot(clip, Mathf.Clamp01(loudness * 0.5f) * Volume);
        }

        private void Landing(NinjaMotor motor)
        {
            bool airborne = _agent.Locomotion == LocomotionState.Airborne;

            if (airborne)
            {
                _peakFall = Mathf.Min(_peakFall, motor.Velocity.y);
                _wasAirborne = true;
                return;
            }

            if (!_wasAirborne) return;
            _wasAirborne = false;

            // A step off a kerb is not a landing. Only a real drop gets the impact.
            float drop = -_peakFall;
            _peakFall = 0f;
            if (drop < 3.5f || _body == null) return;

            bool heavy = drop > 9f;
            AudioClip clip = heavy ? PickLanding(hard: true) : PickLanding(hard: false);
            if (clip == null) return;

            _body.pitch = Random.Range(0.95f, 1.05f);
            _body.PlayOneShot(clip, Mathf.Clamp01(0.35f + drop * 0.03f) * Volume);
        }

        private AudioClip PickLanding(bool hard)
        {
            AudioBank.Entry entry = _bank.For(SoundKind.Landing);
            if (entry == null || entry.Clips.Length == 0) return null;
            if (entry.Clips.Length == 1) return entry.Clips[0];
            return hard ? entry.Clips[entry.Clips.Length - 1] : entry.Clips[0];
        }

        /// <summary>Air noise that rises with fall speed. The only cue that a drop has gone bad.</summary>
        private void FallRush(NinjaMotor motor)
        {
            if (_air == null || _air.clip == null) return;

            float fall = Mathf.Max(0f, -motor.Velocity.y);
            bool falling = _agent.Locomotion == LocomotionState.Airborne && fall > 4f;

            float target = falling ? Mathf.Clamp01((fall - 4f) / 20f) * 0.6f * Volume : 0f;
            _air.volume = Mathf.MoveTowards(_air.volume, target, Time.deltaTime * 2.5f);
            _air.pitch = 0.8f + Mathf.Clamp01(fall / 30f) * 0.5f;
        }

        private void Hurt()
        {
            float health = _agent.Vitals.Fraction;
            if (health >= _lastHealth - 0.001f)
            {
                _lastHealth = health;
                return;
            }

            float lost = _lastHealth - health;
            _lastHealth = health;

            AudioBank.Entry entry = _bank.For(SoundKind.WeaponClash);
            AudioClip clip = entry != null ? AudioBank.Pick(entry.Clips) : null;
            if (clip == null || _body == null) return;

            _body.pitch = Random.Range(0.9f, 1.05f);
            _body.PlayOneShot(clip, Mathf.Clamp01(0.4f + lost * 2f) * Volume);
        }

        private void Grapple(NinjaMotor motor)
        {
            GrapplingHook hook = _agent.Hook;
            if (hook == null || !hook.JustFired) return;

            AudioBank.Entry entry = _bank.For(SoundKind.GrappleFire);
            AudioClip clip = entry != null ? AudioBank.Pick(entry.Clips) : null;
            if (clip == null || _body == null) return;

            _body.pitch = Random.Range(0.97f, 1.06f);
            _body.PlayOneShot(clip, 0.7f * Volume);
        }

        private AudioClip SurfaceClip()
        {
            if (!Physics.Raycast((Vector3)_agent.Position + Vector3.up * 0.6f, Vector3.down,
                    out RaycastHit hit, 2.2f, UnseenLayers.WorldGeometry,
                    QueryTriggerInteraction.Ignore))
                return AudioBank.Pick(_bank.FootstepHard);

            return _bank.FootstepFor(AcousticMaterial.For(hit.collider));
        }
    }
}
