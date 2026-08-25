using UnityEngine;
using Unseen.Core;

namespace Unseen.Entities
{
    /// <summary>
    /// The death scene: the body drops where it fell, settles, lies there long enough to be found,
    /// then sinks away.
    ///
    /// Procedural rather than a clip or a ragdoll, deliberately. There is no death animation in the
    /// character set, and a ragdoll would mean generating bone colliders and joints from the Avatar
    /// - which is exactly the kind of rig work that has cost this project several build-and-look
    /// cycles already. A collapse about the feet, driven by the direction the killing blow came
    /// from, reads correctly at the distance a body is normally seen at in the dark, and it cannot
    /// fail on an import setting.
    ///
    /// Presentational only. The agent is already dead as far as the simulation is concerned before
    /// this runs; nothing here feeds back into gameplay.
    /// </summary>
    public sealed class AgentDeathVisual : MonoBehaviour
    {
        private enum Stage
        {
            Idle,
            Collapsing,
            Lying,
            Sinking,
            Done
        }

        [Tooltip("How long the body takes to go down.")]
        public float CollapseDuration = 0.75f;

        [Tooltip("How long the body stays where it fell. Long enough to be stumbled over.")]
        public float LingerDuration = 25f;

        [Tooltip("How long it takes to sink out of sight once the linger is up.")]
        public float SinkDuration = 3f;

        [Tooltip("How far the body sinks before it is switched off.")]
        public float SinkDepth = 2.4f;

        [Tooltip("Roughly half a ninja's thickness. How far a toppling body has to rise to lie on " +
                 "the ground rather than in it.")]
        public float BodyHalfThickness = 0.34f;

        [Tooltip("How far down to look for the floor the body should come to rest on.")]
        public float GroundSearch = 80f;

        private AgentVisual _visual;
        private Animator _animator;
        private Transform _body;
        private CharacterController _controller;

        private Stage _stage = Stage.Idle;
        private float _elapsed;
        private Quaternion _fromRotation;
        private Quaternion _toRotation;
        private Vector3 _fromPosition;
        private float _groundY;
        private bool _hasGround;
        private float _fallSpeed;
        private Vector3 _authoredPosition;
        private Quaternion _authoredRotation = Quaternion.identity;
        private bool _captured;

        /// <summary>Matches MovementSection.Gravity, so a corpse falls at the rate a body did.</summary>
        private const float Gravity = 22f;

        public bool IsPlaying => _stage != Stage.Idle && _stage != Stage.Done;

        /// <summary>
        /// Starts the collapse. <paramref name="fromAttacker"/> is the world direction the blow came
        /// from; the body falls away from it. Pass <see cref="Vector3.zero"/> when unknown.
        /// </summary>
        public void Play(Vector3 fromAttacker)
        {
            if (IsPlaying) return;

            _visual = GetComponentInChildren<AgentVisual>();
            if (_visual == null) return;

            _body = _visual.transform;
            _animator = _visual.Rig != null ? _visual.Rig : _body.GetComponentInChildren<Animator>();
            _controller = GetComponent<CharacterController>();

            // Freeze the pose. AgentVisual drives the animator and also guards the scale every
            // LateUpdate, so it has to stop before anything here can hold a rotation.
            _visual.enabled = false;
            if (_animator != null) _animator.enabled = false;

            // A corpse should not shove the living around, and the capsule would hold the body
            // upright while it tried to lie down.
            if (_controller != null) _controller.enabled = false;

            Vector3 away = fromAttacker.sqrMagnitude > 0.0001f
                ? -new Vector3(fromAttacker.x, 0f, fromAttacker.z).normalized
                : _body.forward;

            if (away.sqrMagnitude < 0.0001f) away = Vector3.forward;

            // Topple about the axis perpendicular to the fall direction, with a little twist so
            // sixty-odd bodies over a match do not all land in the same pose.
            Vector3 axis = Vector3.Cross(Vector3.up, away).normalized;
            if (axis.sqrMagnitude < 0.0001f) axis = _body.right;

            float lean = 84f + Random.Range(-6f, 6f);
            float twist = Random.Range(-14f, 14f);

            _fromRotation = _body.localRotation;
            _toRotation = Quaternion.AngleAxis(lean, _body.InverseTransformDirection(axis)) *
                          Quaternion.AngleAxis(twist, Vector3.up) *
                          _fromRotation;

            _fromPosition = _body.localPosition;

            // Captured once, before anything moves: the collapse overwrites _fromPosition as it
            // settles, so this is the only record of where the body belongs.
            if (!_captured)
            {
                _authoredPosition = _body.localPosition;
                _authoredRotation = _body.localRotation;
                _captured = true;
            }

            _elapsed = 0f;
            _fallSpeed = 0f;
            _stage = Stage.Collapsing;

            // Where the body should end up. Nothing else works this out: the agent's transform
            // stops where it died and the controller is switched off, so a ninja killed in a jump,
            // on a grapple or over a roof edge simply hung in the air at the height it was hit.
            Vector3 origin = transform.position + Vector3.up * 0.6f;
            _hasGround = Physics.Raycast(origin, Vector3.down, out RaycastHit floor, GroundSearch,
                UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore);
            _groundY = _hasGround ? floor.point.y : transform.position.y;
        }

        private void LateUpdate()
        {
            Advance(Time.deltaTime);
        }

        /// <summary>
        /// Steps the sequence by hand.
        ///
        /// Public so a tool can drive it: MonoBehaviour callbacks do not run outside play mode, so
        /// a headless capture of the collapse would otherwise be impossible to take - and a death
        /// scene nobody can look at is a death scene nobody can check.
        /// </summary>
        public void Advance(float dt)
        {
            if (_stage == Stage.Idle || _stage == Stage.Done || _body == null) return;

            _elapsed += dt;

            switch (_stage)
            {
                case Stage.Collapsing:
                {
                    float t = Mathf.Clamp01(_elapsed / Mathf.Max(0.01f, CollapseDuration));

                    // Accelerating fall, then a small settle at the end, so it lands rather than
                    // easing politely into place.
                    float fall = t * t;
                    float settle = t > 0.82f ? Mathf.Sin((t - 0.82f) / 0.18f * Mathf.PI) * 0.045f : 0f;

                    _body.localRotation = Quaternion.SlerpUnclamped(
                        _fromRotation, _toRotation, fall + settle);

                    // The pivot is at the feet, so a body rotated flat has its centre line
                    // exactly at ground level - half of it underground. Resting height is the
                    // floor plus roughly half a body's thickness, reached as the topple completes.
                    SettleOnto(_groundY + BodyHalfThickness * fall, dt);

                    if (t >= 1f)
                    {
                        _stage = Stage.Lying;
                        _elapsed = 0f;
                    }

                    break;
                }

                case Stage.Lying:
                    // Keep settling: a body that died in mid-air is still falling when the topple
                    // finishes, and it has further to go than the collapse lasts.
                    SettleOnto(_groundY + BodyHalfThickness, dt);

                    if (_elapsed >= LingerDuration)
                    {
                        _stage = Stage.Sinking;
                        _elapsed = 0f;
                        _fromPosition = _body.localPosition;
                    }

                    break;

                case Stage.Sinking:
                {
                    float t = Mathf.Clamp01(_elapsed / Mathf.Max(0.01f, SinkDuration));
                    _body.localPosition = _fromPosition + Vector3.down * (SinkDepth * t * t);

                    if (t >= 1f)
                    {
                        _stage = Stage.Done;
                        _body.gameObject.SetActive(false);
                    }

                    break;
                }
            }
        }

        /// <summary>
        /// Drives the body toward a resting height, falling under gravity if it is above it.
        ///
        /// Falls rather than lerps because the distance is unknown: killed on the ground this is a
        /// few centimetres of settle, killed off a pagoda balcony it is a twenty metre drop, and a
        /// fixed-duration interpolation would make one of those two look absurd.
        /// </summary>
        private void SettleOnto(float restY, float dt)
        {
            if (!_hasGround) return;

            Vector3 world = _body.position;

            if (world.y > restY + 0.01f)
            {
                _fallSpeed += Gravity * dt;
                world.y = Mathf.Max(restY, world.y - _fallSpeed * dt);
            }
            else
            {
                world.y = restY;
                _fallSpeed = 0f;
            }

            _body.position = world;
            _fromPosition = _body.localPosition;
        }

        /// <summary>
        /// Puts the body back on its feet for the next match.
        ///
        /// Restores the authored local transform, not the last one the collapse left behind:
        /// _fromPosition is rewritten every frame while the body settles and sinks, so reusing it
        /// would revive the ninja two metres underground. And the body is re-enabled because the
        /// sink stage switches it off - without this, every agent that has ever died stays
        /// invisible for the rest of the session, and a few matches in the lobby is empty.
        /// </summary>
        public void Reset()
        {
            if (_body != null)
            {
                _body.gameObject.SetActive(true);
                _body.localRotation = _authoredRotation;
                _body.localPosition = _authoredPosition;
            }

            if (_visual != null) _visual.enabled = true;
            if (_animator != null) _animator.enabled = true;
            if (_controller != null) _controller.enabled = true;

            _stage = Stage.Idle;
            _elapsed = 0f;
            _fallSpeed = 0f;
            _hasGround = false;
        }
    }
}
