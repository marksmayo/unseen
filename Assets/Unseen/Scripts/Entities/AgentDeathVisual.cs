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
            Evaporating,
            Done
        }

        [Tooltip("How long the body takes to go down.")]
        public float CollapseDuration = 0.75f;

        [Tooltip("How long the body stays where it fell. Long enough to be stumbled over.")]
        public float LingerDuration = 10f;

        [Tooltip("How long the body takes to come apart into mist and disperse.")]
        public float EvaporateDuration = 5f;

        [Tooltip("How many puffs of mist a body comes apart into.")]
        public int PuffCount = 9;

        [Tooltip("How far the puffs rise over the course of the dispersal, in metres.")]
        public float PuffRise = 2.6f;

        [Tooltip("How much wider the puffs spread as they go, as a multiplier.")]
        public float PuffSpread = 2.8f;

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
        private Vector3 _authoredScale = Vector3.one;
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
                _authoredScale = _body.localScale;
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
                        _stage = Stage.Evaporating;
                        _elapsed = 0f;
                        _fromPosition = _body.localPosition;
                        BeginEvaporating();
                    }

                    break;

                case Stage.Evaporating:
                {
                    float t = Mathf.Clamp01(_elapsed / Mathf.Max(0.01f, EvaporateDuration));
                    Evaporate(t);

                    if (t >= 1f)
                    {
                        _stage = Stage.Done;
                        _body.gameObject.SetActive(false);
                        ClearPuffs();
                    }

                    break;
                }
            }
        }

        // ------------------------------------------------------------------ evaporation

        private Transform[] _puffs;
        private Vector3[] _puffFrom;
        private Vector3[] _puffDrift;
        private float[] _puffPhase;
        private Material _mist;

        /// <summary>
        /// Cuts the body into a handful of mist puffs, ready to disperse.
        ///
        /// Replaces the body sinking into the ground, which was always a placeholder and read as
        /// the corpse falling through the floor - which, on a rooftop, is exactly what it looked
        /// like. A body that comes apart into mist also says something the sinking never did: this
        /// is a game about people who are not supposed to leave anything behind.
        ///
        /// The puffs get their own material INSTANCE rather than a property block. The ground mist
        /// shader declares its tint and density inside UnityPerMaterial, and the SRP batcher
        /// silently ignores per-renderer overrides of those - a lesson this project has already
        /// paid for once. One material per corpse, destroyed with it.
        /// </summary>
        private void BeginEvaporating()
        {
            Environment.GreyboxMaterialSet set = Environment.GreyboxMaterialSet.Load();
            Material source = set != null ? set.GroundMist : null;

            // No mist material means no puffs. The body still disappears on schedule, because a
            // corpse that stayed forever because an art asset was missing would be worse.
            if (source == null) return;

            _mist = new Material(source) { name = "DeathMist" };

            int count = Mathf.Clamp(PuffCount, 1, 24);

            _puffs = new Transform[count];
            _puffFrom = new Vector3[count];
            _puffDrift = new Vector3[count];
            _puffPhase = new float[count];

            // Sized off the body itself, so a puff cloud is the shape of the thing that made it.
            Bounds bounds = BodyBounds();
            float span = Mathf.Max(0.5f, bounds.size.magnitude * 0.4f);

            for (int i = 0; i < count; i++)
            {
                var puff = new GameObject($"DeathMist_{i}");
                Transform t = puff.transform;

                t.SetParent(_body.parent, false);

                // Spread along the body rather than a sphere around its middle: a corpse is lying
                // down, and a ball of fog over it reads as a smoke grenade instead.
                Vector3 along = _body.right * ((i / (float)(count - 1) - 0.5f) * bounds.size.magnitude * 0.7f);

                Vector3 jitter = new Vector3(
                    Random.Range(-0.3f, 0.3f),
                    Random.Range(-0.1f, 0.35f),
                    Random.Range(-0.3f, 0.3f));

                t.position = bounds.center + along + jitter;
                t.rotation = Quaternion.Euler(90f, Random.Range(0f, 360f), 0f);
                t.localScale = new Vector3(span, span, 1f);

                puff.AddComponent<MeshFilter>().sharedMesh = PuffQuad();

                var renderer = puff.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = _mist;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                _puffs[i] = t;
                _puffFrom[i] = t.position;

                // Each one drifts its own way, mostly upward. Fog that all went the same way
                // would read as one object being translated.
                _puffDrift[i] = new Vector3(
                    Random.Range(-0.35f, 0.35f), 1f, Random.Range(-0.35f, 0.35f)).normalized;

                _puffPhase[i] = Random.Range(0f, 6.28f);
            }
        }

        /// <summary>
        /// One frame of dispersal, from 0 to 1.
        ///
        /// The body goes first and quickly - it is hidden behind the puffs by a third of the way
        /// through, and holding it any longer means watching a corpse shrink, which looks like a
        /// bug rather than an effect. The mist then has the rest of the time to itself.
        /// </summary>
        private void Evaporate(float t)
        {
            // Sinking a little and settling flatter as it goes, so it is dispersing rather than
            // being deleted. Switched off once the puffs have thickened enough to cover it.
            if (_body.gameObject.activeSelf)
            {
                float collapse = Mathf.Clamp01(t / 0.32f);

                _body.localPosition = _fromPosition + Vector3.down * (0.45f * collapse);
                _body.localScale = new Vector3(
                    _authoredScale.x,
                    _authoredScale.y * (1f - 0.55f * collapse),
                    _authoredScale.z);

                if (collapse >= 1f) _body.gameObject.SetActive(false);
            }

            if (_puffs == null || _mist == null) return;

            // Rising, spreading, and thinning. The density curve peaks early: mist coming off a
            // body should be at its thickest just after it comes apart, not halfway through.
            float thickness = Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI);
            thickness *= thickness < 1f ? Mathf.Lerp(1.4f, 0.9f, t) : 1f;

            if (_mist.HasProperty("_Density"))
                _mist.SetFloat("_Density", 0.46f * thickness);

            for (int i = 0; i < _puffs.Length; i++)
            {
                Transform puff = _puffs[i];
                if (puff == null) continue;

                // Eased out, so they slow as they thin instead of sailing off.
                float rise = 1f - (1f - t) * (1f - t);

                // A slow curl, which is the difference between rising mist and a rising quad.
                float curl = Mathf.Sin(_puffPhase[i] + t * 3.1f) * 0.28f * t;

                puff.position = _puffFrom[i]
                                + _puffDrift[i] * (PuffRise * rise)
                                + new Vector3(curl, 0f, -curl);

                float grow = Mathf.Lerp(1f, PuffSpread, rise);
                puff.localScale = new Vector3(_puffScale * grow, _puffScale * grow, 1f);
            }
        }

        private float _puffScale = 1f;

        private void ClearPuffs()
        {
            if (_puffs != null)
            {
                for (int i = 0; i < _puffs.Length; i++)
                    if (_puffs[i] != null) UnseenObject.Destroy(_puffs[i].gameObject);

                _puffs = null;
            }

            if (_mist == null) return;

            UnseenObject.Destroy(_mist);
            _mist = null;
        }

        /// <summary>Where the body actually is, from its renderers rather than its transform.</summary>
        private Bounds BodyBounds()
        {
            var renderers = _body.GetComponentsInChildren<Renderer>(true);

            if (renderers.Length == 0)
                return new Bounds(_body.position + Vector3.up * 0.3f, Vector3.one);

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            _puffScale = Mathf.Max(0.5f, bounds.size.magnitude * 0.4f);
            return bounds;
        }

        private static Mesh _puffQuad;

        /// <summary>A unit quad, shared by every puff in the level.</summary>
        private static Mesh PuffQuad()
        {
            if (_puffQuad != null) return _puffQuad;

            _puffQuad = new Mesh { name = "DeathMistQuad" };

            _puffQuad.SetVertices(new System.Collections.Generic.List<Vector3>
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f)
            });

            _puffQuad.SetUVs(0, new System.Collections.Generic.List<Vector2>
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)
            });

            _puffQuad.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            _puffQuad.RecalculateNormals();
            _puffQuad.RecalculateBounds();

            return _puffQuad;
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
            // The puffs go first: they are parented to the body's parent, not the body, so
            // switching the body back on would leave the last death's mist hanging in the air
            // around a living agent.
            ClearPuffs();

            if (_body != null)
            {
                _body.gameObject.SetActive(true);
                _body.localRotation = _authoredRotation;
                _body.localPosition = _authoredPosition;

                // And the scale. The dispersal squashes the body on its way out, and an agent that
                // came back two thirds of its proper height would be a subtle, lasting mess - this
                // component has already shipped a bug of exactly that shape once, when the sink
                // stage disabled the body and nothing restored it.
                _body.localScale = _authoredScale;
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
