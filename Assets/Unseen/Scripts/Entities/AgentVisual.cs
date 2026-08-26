using UnityEngine;
using Unseen.Combat;
using Unseen.Core;

namespace Unseen.Entities
{
    /// <summary>
    /// The cosmetic body of a ninja, on both the server-side agent and the client-side proxy.
    ///
    /// Purely presentational, and deliberately so: the character controller capsule, the eye anchor
    /// and the torso anchor are what perception, physics and the parkour probes read. Nothing here
    /// carries a collider, and changing the mesh cannot change how the game plays.
    /// </summary>
    public sealed class AgentVisual : MonoBehaviour
    {
        private static readonly int SpeedParam = Animator.StringToHash("Speed");
        private static readonly int AirborneParam = Animator.StringToHash("Airborne");
        private static readonly int CrouchParam = Animator.StringToHash("Crouched");
        private static readonly int ActionParam = Animator.StringToHash("Action");

        /// <summary>Action ids on the Combat layer. Must match UnseenAnimationSetup.</summary>
        private const int ActionNone = 0;
        private const int ActionGuard = 1;
        private const int ActionLight = 2;
        private const int ActionHeavy = 3;
        private const int ActionStagger = 4;
        private const int ActionTakedownAttacker = 5;
        private const int ActionTakedownVictim = 6;

        private const int CombatLayer = 1;
        private const int StanceLayer = 2;
        private const int ParkourLayer = 3;

        private static readonly int StanceParam = Animator.StringToHash("Stance");
        private static readonly int ParkourParam = Animator.StringToHash("Parkour");

        [Tooltip("Animator driven by locomotion state. Optional - the mesh renders fine without one.")]
        public Animator Rig;

        [Tooltip("Renderer whose material is swapped for the per-agent skin variant.")]
        public SkinnedMeshRenderer Body;

        [Tooltip("How quickly the reported speed follows reality, to stop the animator flickering.")]
        public float SpeedSmoothing = 8f;

        [Tooltip("How fast the combat layer fades in and out. Fast, so a swing is not late.")]
        public float ActionBlendSpeed = 9f;

        [Tooltip("How far the body sinks when crouched. The crouch pose folds the knees, which " +
                 "lifts the feet; this puts them back on the floor. Measured with " +
                 "Unseen > Art > Capture Animation Poses rather than guessed.")]
        public float CrouchBodyDrop = 0.348f;

        [Tooltip("How far the body sinks when prone. Same measurement, a much deeper fold.")]
        public float ProneBodyDrop = 0.339f;

        [Tooltip("How quickly the body settles into and out of a crouch.")]
        public float CrouchBlendSpeed = 7f;

        /// <summary>Replicated flags, for a proxy that has no local combat state.</summary>
        public ushort ProxyFlags;

        private AgentEntity _agent;
        private Vector3 _lastPosition;
        private float _speed;
        private bool _hasLastPosition;

        private Vector3 _authoredScale;
        private int _action = -1;
        private float _actionWeight;
        private float _stanceWeight;
        private float _bodyDrop;
        private int _stance = -1;
        private float _parkourWeight;
        private int _parkour = -1;
        private float _authoredLocalY;
        private bool _capturedLocalY;

        private void Awake()
        {
            _authoredScale = transform.localScale;
            if (Rig == null) Rig = GetComponentInChildren<Animator>();
            if (Body == null) Body = GetComponentInChildren<SkinnedMeshRenderer>();
            FixCullingBounds();
        }

        /// <summary>
        /// The imported FBX reports bind-pose bounds of about a centimetre, and Unity culls skinned
        /// meshes against those bounds - so the body disappeared unless the camera happened to point
        /// at its origin. From behind your own character that looks exactly like first person.
        /// </summary>
        private void FixCullingBounds()
        {
            if (Body == null) return;

            Body.updateWhenOffscreen = false; // explicit bounds are cheaper than recomputing per frame
            // Local space, so these are pre-scale metres on the model, not world metres.
            Body.localBounds = new Bounds(new Vector3(0f, 1f, 0f), new Vector3(3f, 4.5f, 3f));
        }

        /// <summary>Binds to an authoritative agent, so motion comes from the motor rather than deltas.</summary>
        public void Bind(AgentEntity agent)
        {
            _agent = agent;
        }

        public void SetSkin(Material material)
        {
            if (material == null || Body == null) return;
            Body.sharedMaterial = material;
        }

        private void LateUpdate()
        {
            // Belt and braces against animated scale. The clips are sanitised at import, but a
            // future clip with a scale curve would silently inflate every ninja again, and that
            // failure mode is expensive to diagnose from a screenshot.
            if (_authoredScale.sqrMagnitude > 0f && transform.localScale != _authoredScale)
                transform.localScale = _authoredScale;

            if (Rig == null) return;

            float targetSpeed;
            bool airborne;
            bool crouched;

            if (_agent != null)
            {
                targetSpeed = Mathf.Sqrt(
                    _agent.Motor != null
                        ? new Vector2(_agent.Motor.Velocity.x, _agent.Motor.Velocity.z).sqrMagnitude
                        : 0f);
                airborne = _agent.Locomotion == LocomotionState.Airborne ||
                           _agent.Locomotion == LocomotionState.Grapple ||
                           _agent.Locomotion == LocomotionState.WallClimb;
                crouched = _agent.Stance != Stance.Stand;
            }
            else
            {
                // Client proxy: infer speed from replicated movement.
                Vector3 position = transform.position;
                if (_hasLastPosition && Time.deltaTime > 0f)
                {
                    Vector3 delta = position - _lastPosition;
                    delta.y = 0f;
                    targetSpeed = delta.magnitude / Time.deltaTime;
                }
                else
                {
                    targetSpeed = 0f;
                }

                _lastPosition = position;
                _hasLastPosition = true;
                airborne = false;
                crouched = false;
            }

            _speed = Mathf.Lerp(_speed, targetSpeed, 1f - Mathf.Exp(-SpeedSmoothing * Time.deltaTime));

            Rig.SetFloat(SpeedParam, _speed);
            Rig.SetBool(AirborneParam, airborne);
            Rig.SetBool(CrouchParam, crouched);

            DriveCombatLayer();
            DriveStanceLayer();
            DriveParkourLayer();
        }

        /// <summary>
        /// Folds the body into a crouch and drops it so the feet stay on the ground.
        ///
        /// The crouch clip is rotation-only, so bending the knees lifts the feet clear of the
        /// floor; sinking the whole body by roughly the same amount puts them back. Without both
        /// halves a crouch is either a float or a squat with the feet through the boards.
        /// </summary>
        private void DriveStanceLayer()
        {
            Stance stance = _agent != null ? _agent.Stance : Stance.Stand;

            int wanted = stance == Stance.Prone ? 2 : stance == Stance.Crouch ? 1 : 0;
            if (wanted != _stance)
            {
                _stance = wanted;
                Rig.SetInteger(StanceParam, wanted);
            }

            float targetDrop = stance == Stance.Prone ? ProneBodyDrop
                : stance == Stance.Crouch ? CrouchBodyDrop
                : 0f;

            _stanceWeight = Mathf.MoveTowards(_stanceWeight, wanted == 0 ? 0f : 1f,
                CrouchBlendSpeed * Time.deltaTime);

            // The drop is interpolated separately from the layer weight so a crouch-to-prone
            // change slides between two depths instead of standing up on the way through.
            _bodyDrop = Mathf.MoveTowards(_bodyDrop, targetDrop,
                (ProneBodyDrop + CrouchBodyDrop) * CrouchBlendSpeed * Time.deltaTime);

            if (Rig.layerCount > StanceLayer) Rig.SetLayerWeight(StanceLayer, _stanceWeight);

            if (_authoredScale.sqrMagnitude <= 0f) return;

            // Captured here rather than in Awake: AgentVisualSet.Attach sets the vertical offset
            // immediately AFTER Instantiate, which is after Awake has already run, so reading it
            // there records the prefab's value and quietly discards the offset.
            if (!_capturedLocalY)
            {
                _authoredLocalY = transform.localPosition.y;
                _capturedLocalY = true;
            }

            Vector3 local = transform.localPosition;
            local.y = _authoredLocalY - _bodyDrop;
            transform.localPosition = local;
        }

        /// <summary>
        /// Climbing, wall running and hanging, driven straight off the locomotion state.
        ///
        /// Full body: none of these are things the legs and the arms can disagree about, and until
        /// now every one of them was mimed in the airborne pose.
        /// </summary>
        private void DriveParkourLayer()
        {
            if (Rig.layerCount <= ParkourLayer) return;

            LocomotionState state = _agent != null ? _agent.Locomotion : LocomotionState.Grounded;

            int wanted;
            switch (state)
            {
                case LocomotionState.WallClimb: wanted = 1; break;
                case LocomotionState.WallRun: wanted = 2; break;
                case LocomotionState.RafterCrawl:
                case LocomotionState.Grapple: wanted = 3; break;
                default: wanted = 0; break;
            }

            if (wanted != _parkour)
            {
                _parkour = wanted;
                Rig.SetInteger(ParkourParam, wanted);
            }

            _parkourWeight = Mathf.MoveTowards(_parkourWeight, wanted == 0 ? 0f : 1f,
                ActionBlendSpeed * Time.deltaTime);
            Rig.SetLayerWeight(ParkourLayer, _parkourWeight);
        }

        /// <summary>
        /// Picks the combat action and fades the override layer in behind it.
        ///
        /// The layer weight is driven here rather than left at one with an empty pass-through
        /// state: an empty state on an override layer writes the rig's bind pose over the
        /// locomotion layer, which reads as the ninja snapping to a T-pose between swings.
        ///
        /// Priority is deliberate. A takedown owns the body outright, a stagger interrupts
        /// whatever you were doing, a swing beats a raised guard, and the guard is the resting
        /// case. That ordering is the same one CombatDirector resolves in.
        /// </summary>
        private void DriveCombatLayer()
        {
            int action = ResolveAction();
            if (action != _action)
            {
                _action = action;
                Rig.SetInteger(ActionParam, action);
            }

            float target = action == ActionNone ? 0f : 1f;
            _actionWeight = Mathf.MoveTowards(_actionWeight, target, ActionBlendSpeed * Time.deltaTime);

            if (Rig.layerCount > CombatLayer) Rig.SetLayerWeight(CombatLayer, _actionWeight);
        }

        private int ResolveAction()
        {
            // A local agent has the full combat state. A proxy only has the replicated flags, so
            // it can show a guard, a flinch and a takedown but not which swing is in flight.
            if (_agent == null || _agent.Melee == null) return ResolveFromFlags(ProxyFlags);

            AgentCombat melee = _agent.Melee;

            if (melee.IsTakedownVictim) return ActionTakedownVictim;
            if (melee.TakedownTarget.IsValid) return ActionTakedownAttacker;

            // Stagger is read from the flags rather than the timer: the timer is in simulation
            // time, which the visual has no honest access to, and the server mirrors it onto the
            // flags for exactly this reason.
            if ((_agent.Flags & AgentFlags.Staggered) != 0) return ActionStagger;

            if (melee.Phase != AttackPhase.Idle)
                return melee.Heavy ? ActionHeavy : ActionLight;

            return melee.Guarding ? ActionGuard : ActionNone;
        }

        private static int ResolveFromFlags(ushort raw)
        {
            var flags = (AgentFlags)raw;
            if ((flags & AgentFlags.Takedown) != 0) return ActionTakedownVictim;
            if ((flags & AgentFlags.Staggered) != 0) return ActionStagger;
            if ((flags & AgentFlags.Guarding) != 0) return ActionGuard;
            return ActionNone;
        }
    }
}
