using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.Movement
{
    /// <summary>
    /// Server-authoritative locomotion for one ninja. Clients send intent, this produces motion.
    /// The same code path drives bots, so a bot cannot move in a way a player could not.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class NinjaMotor : MonoBehaviour
    {
        [Tooltip("Where the grapple rope originates. Falls back to the chest.")]
        public Transform HandAnchor;

        private AgentEntity _agent;
        private CharacterController _cc;
        private GrapplingHook _hook;

        private float3 _velocity;
        private float _wallTimer;
        private float _strideDistance;
        private float _coyoteTime;
        private int _wallSide;
        private float3 _wallNormal;
        private LedgeHit _ledge;
        private Collider _groundCollider;
        private float _peakFallSpeed;

        /// <summary>Downward speed applied while grounded, enough to hold slopes and steps.</summary>
        private const float GroundStickSpeed = -2f;

        /// <summary>Longest a single reel may run before the hook lets go regardless.</summary>
        private const float MaxReelDuration = 3f;

        private float _grappleTime;
        private bool _jumpHeld;
        private bool _grappleHeld;
        private bool _jumpPressed;
        private bool _grapplePressed;

        private float _lockElapsed;
        private float _lockDuration;
        private float3 _lockStart;
        private float3 _lockTarget;
        private float _lockYawStart;
        private float _lockYawTarget;
        private LocomotionState _lockExitState;

        public float3 Velocity => _velocity;
        public bool IsGrounded { get; private set; }
        public Collider GroundCollider => _groundCollider;

        public float3 HandPosition => HandAnchor != null ? (float3)HandAnchor.position : _agent != null ? _agent.TorsoPosition : (float3)transform.position;

        public void Bind(AgentEntity agent)
        {
            _agent = agent;
            _cc = GetComponent<CharacterController>();
            _hook = GetComponent<GrapplingHook>();
        }

        /// <summary>
        /// Hands motion to a fixed animation for a fixed time - the mantle, and the silent takedown.
        /// This is the motion-warping hook: the target transform is authoritative, the animation
        /// is told to hit it.
        /// </summary>
        public void BeginMotionWarp(float3 targetPosition, float targetYaw, float duration, LocomotionState exitState = LocomotionState.Grounded)
        {
            _lockStart = transform.position;
            _lockTarget = targetPosition;
            _lockYawStart = _agent.Yaw;
            _lockYawTarget = targetYaw;
            _lockDuration = math.max(0.05f, duration);
            _lockElapsed = 0f;
            _lockExitState = exitState;
            _velocity = float3.zero;
            _agent.Locomotion = LocomotionState.Locked;
        }

        public bool IsWarping => _agent != null && _agent.Locomotion == LocomotionState.Locked;

        public float WarpProgress => _lockDuration <= 0f ? 1f : math.saturate(_lockElapsed / _lockDuration);

        /// <summary>
        /// Moves the body without disturbing the controller or clearing velocity.
        ///
        /// Teleport switches the CharacterController off and on again, which is right for a real
        /// jump across the map and wrong sixty times a second: PhysX re-registers the controller on
        /// every toggle, and a glider driven that way visibly stutters all the way down. A
        /// CharacterController tracks transform.position perfectly well for small steps.
        /// </summary>
        public void MoveDirect(float3 position)
        {
            transform.position = position;
        }

        public void Teleport(float3 position)
        {
            _cc.enabled = false;
            transform.position = position;
            _cc.enabled = true;
            _velocity = float3.zero;
            _strideDistance = 0f;
            _peakFallSpeed = 0f;
        }

        public void AddImpulse(float3 impulse)
        {
            _velocity += impulse;
            if (_agent.Locomotion == LocomotionState.Grounded) _agent.Locomotion = LocomotionState.Airborne;
        }

        public void Simulate(SimContext ctx, float dt, int tick, float time)
        {
            if (_agent == null || !_agent.IsAlive) return;

            UnseenConfig cfg = ctx.Config;
            _hook?.Tick(dt, HandPosition);

            if (_agent.Locomotion == LocomotionState.Locked)
            {
                TickWarp(dt);
                return;
            }

            MoveIntent intent = _agent.Intent;
            _agent.Yaw = intent.Yaw;
            _agent.Pitch = math.clamp(intent.Pitch, -80f, 80f);
            transform.rotation = Quaternion.Euler(0f, _agent.Yaw, 0f);

            // Rising-edge detection happens before the state switch so that an early return from a
            // state handler cannot leave a button latched on.
            _jumpPressed = intent.Jump && !_jumpHeld;
            _grapplePressed = intent.Grapple && !_grappleHeld;
            _jumpHeld = intent.Jump;
            _grappleHeld = intent.Grapple;

            UpdateStance(intent, cfg);
            UpdateFlags(intent);

            switch (_agent.Locomotion)
            {
                case LocomotionState.Grounded:
                    TickGrounded(ctx, intent, cfg, dt, tick);
                    break;
                case LocomotionState.Airborne:
                    TickAirborne(ctx, intent, cfg, dt, tick, time);
                    break;
                case LocomotionState.WallClimb:
                    TickWallClimb(ctx, intent, cfg, dt);
                    break;
                case LocomotionState.WallRun:
                    TickWallRun(ctx, intent, cfg, dt, tick);
                    break;
                case LocomotionState.LedgeHang:
                    TickLedgeHang(ctx, intent, cfg, dt);
                    break;
                case LocomotionState.RafterCrawl:
                    TickRafter(ctx, intent, cfg, dt, tick);
                    break;
                case LocomotionState.Grapple:
                    TickGrapple(ctx, intent, cfg, dt, tick);
                    break;
                case LocomotionState.Slide:
                    TickSlide(ctx, intent, cfg, dt, tick);
                    break;
            }
        }

        // ---------------------------------------------------------------- states

        private void TickGrounded(SimContext ctx, in MoveIntent intent, UnseenConfig cfg, float dt, int tick)
        {
            float3 wish = WishDirection(intent);

            // How much water is standing on the ground under us. Zero almost everywhere; the river
            // is the only place it is not.
            float wade = Unseen.Environment.WaterVolume.DepthAt(_agent.Position);

            bool sprinting = intent.Sprint && _agent.Stance == Stance.Stand &&
                             math.lengthsq(wish) > 0.01f &&
                             wade <= cfg.Movement.WadeSprintDepth;

            float speed = cfg.StanceSpeed(_agent.Stance, sprinting);

            // Wading. The river is meant to be a decision - cover and a route that costs you your
            // legs - and it is not one if you cross it at walking pace.
            if (wade > 0.02f)
            {
                float t = math.saturate(wade / math.max(0.05f, cfg.Movement.WadeFullDepth));
                speed *= math.lerp(1f, cfg.Movement.WadeSlowest, t);
            }

            float3 planar = UnseenMath.Horizontal(_velocity);
            planar = math.lerp(planar, wish * speed, math.saturate(cfg.Movement.Acceleration * dt));

            // A fixed downward stick force while grounded, NOT accumulated gravity: the previous
            // form kept the old negative value and added gravity again every tick, so simply
            // standing still built up hundreds of metres per second of fall speed. The controller
            // hid it - until you stepped off a ledge and died instantly.
            float vertical = IsGrounded
                ? GroundStickSpeed
                : math.max(_velocity.y + cfg.Movement.Gravity * dt, cfg.Movement.TerminalVelocity);

            _velocity = new float3(planar.x, vertical, planar.z);

            if (TryStartGrapple(ctx, intent, cfg, tick)) return;

            if (_jumpPressed)
            {
                if (TryMantle(cfg, intent)) return;
                if (TryGrabRafter(cfg)) return;

                _velocity.y = cfg.Movement.JumpVelocity;
                _agent.Locomotion = LocomotionState.Airborne;
                ctx.Sound.Emit(_agent.Id, _agent.Position, SoundKind.Vault, 0.8f, 18f, tick);
            }
            else if (intent.Crouch && sprinting)
            {
                _agent.Locomotion = LocomotionState.Slide;
            }

            ApplyMotion(ctx, dt, tick, emitFootsteps: true);

            if (!IsGrounded && _coyoteTime <= 0f) _agent.Locomotion = LocomotionState.Airborne;
        }

        private void TickAirborne(SimContext ctx, in MoveIntent intent, UnseenConfig cfg, float dt, int tick, float time)
        {
            float3 wish = WishDirection(intent);
            float3 planar = UnseenMath.Horizontal(_velocity);
            float speed = cfg.StanceSpeed(Stance.Stand, intent.Sprint);
            planar = math.lerp(planar, wish * speed, math.saturate(cfg.Movement.AirAcceleration * dt));

            _velocity = new float3(planar.x, math.max(_velocity.y + cfg.Movement.Gravity * dt, cfg.Movement.TerminalVelocity), planar.z);
            _peakFallSpeed = math.min(_peakFallSpeed, _velocity.y);

            if (TryStartGrapple(ctx, intent, cfg, tick)) return;

            // Wall contact while falling forward converts into a climb or a run.
            float3 chest = _agent.TorsoPosition;
            if (math.lengthsq(wish) > 0.05f && ParkourProbe.FindWall(chest, wish, cfg.Movement.Radius + 0.4f, out RaycastHit wall))
            {
                _wallNormal = wall.normal;
                _wallTimer = 0f;
                _agent.Locomotion = LocomotionState.WallClimb;
                return;
            }

            int side = ParkourProbe.FindWallRunSide(chest, _agent.Forward, cfg.Movement.Radius + 0.5f, out RaycastHit runWall);
            if (side != 0 && math.lengthsq(wish) > 0.2f && _velocity.y < 1f)
            {
                _wallSide = side;
                _wallNormal = runWall.normal;
                _wallTimer = 0f;
                _agent.Locomotion = LocomotionState.WallRun;
                return;
            }

            if (_velocity.y < 0f && TryGrabLedge(cfg)) return;

            ApplyMotion(ctx, dt, tick, emitFootsteps: false);

            if (IsGrounded)
            {
                Land(ctx, cfg, tick, time);
            }
        }

        private void TickWallClimb(SimContext ctx, in MoveIntent intent, UnseenConfig cfg, float dt)
        {
            _wallTimer += dt;

            float3 into = -_wallNormal;
            _velocity = new float3(into.x * 1.2f, cfg.Movement.WallClimbSpeed, into.z * 1.2f);

            if (_jumpPressed)
            {
                _velocity = _wallNormal * 4.5f + new float3(0f, cfg.Movement.JumpVelocity * 0.85f, 0f);
                _agent.Locomotion = LocomotionState.Airborne;
                return;
            }

            if (_wallTimer > cfg.Movement.WallClimbDuration || intent.Crouch)
            {
                _velocity = _wallNormal * 1.5f;
                _agent.Locomotion = LocomotionState.Airborne;
                return;
            }

            // Reaching the top of the wall turns into a mantle rather than a hop into the void.
            if (!ParkourProbe.FindWall(_agent.TorsoPosition, into, cfg.Movement.Radius + 0.5f, out _))
            {
                if (TryMantle(cfg, intent)) return;
                _agent.Locomotion = LocomotionState.Airborne;
                return;
            }

            ApplyMotion(ctx, dt, 0, emitFootsteps: false);
        }

        private void TickWallRun(SimContext ctx, in MoveIntent intent, UnseenConfig cfg, float dt, int tick)
        {
            _wallTimer += dt;

            float3 up = new float3(0f, 1f, 0f);
            float3 tangent = math.normalizesafe(math.cross(_wallNormal, up)) * -_wallSide;
            if (math.dot(tangent, _agent.Forward) < 0f) tangent = -tangent;

            _velocity = tangent * cfg.Movement.WallRunSpeed + new float3(0f, cfg.Movement.Gravity * dt * 0.25f, 0f);

            if (_jumpPressed)
            {
                _velocity = _wallNormal * 5.5f + new float3(0f, cfg.Movement.JumpVelocity, 0f);
                _agent.Locomotion = LocomotionState.Airborne;
                ctx.Sound.Emit(_agent.Id, _agent.Position, SoundKind.Vault, 1.1f, 22f, tick);
                return;
            }

            if (_wallTimer > cfg.Movement.WallRunDuration ||
                ParkourProbe.FindWallRunSide(_agent.TorsoPosition, _agent.Forward, cfg.Movement.Radius + 0.6f, out _) == 0)
            {
                _agent.Locomotion = LocomotionState.Airborne;
                return;
            }

            ApplyMotion(ctx, dt, tick, emitFootsteps: true);
        }

        private void TickLedgeHang(SimContext ctx, in MoveIntent intent, UnseenConfig cfg, float dt)
        {
            _velocity = float3.zero;

            if (_jumpPressed && TryMantle(cfg, intent)) return;

            if (intent.Crouch)
            {
                _agent.Locomotion = LocomotionState.Airborne;
                _velocity = new float3(0f, -1f, 0f);
                return;
            }

            // Shimmy: slide along the lip without letting go.
            float3 up = new float3(0f, 1f, 0f);
            float3 along = math.normalizesafe(math.cross(_ledge.WallNormal, up));
            float3 shift = along * intent.Move.x * 1.2f * dt;
            if (math.lengthsq(shift) > 0f)
            {
                _cc.Move(shift);
                _ledge.GrabPoint += shift;
            }
        }

        private void TickRafter(SimContext ctx, in MoveIntent intent, UnseenConfig cfg, float dt, int tick)
        {
            float3 wish = WishDirection(intent);
            _velocity = wish * cfg.Movement.RafterSpeed;

            if (intent.Crouch || !ParkourProbe.FindRafter(_agent.EyePosition, 1.2f, out _))
            {
                _agent.Locomotion = LocomotionState.Airborne;
                _velocity.y = -0.5f;
                return;
            }

            if (_jumpPressed)
            {
                _velocity = wish * cfg.Movement.WalkSpeed;
                _velocity.y = -1f;
                _agent.Locomotion = LocomotionState.Airborne;
                return;
            }

            ApplyMotion(ctx, dt, tick, emitFootsteps: true, footstepScale: 0.35f);
        }

        private void TickGrapple(SimContext ctx, in MoveIntent intent, UnseenConfig cfg, float dt, int tick)
        {
            if (_hook == null || !_hook.Attached)
            {
                _agent.Locomotion = LocomotionState.Airborne;
                return;
            }

            float3 reel = _hook.ReelVelocity(_agent.Position, cfg.Movement.GrappleReelSpeed, out bool arrived);
            _velocity = reel;
            _grappleTime += dt;

            // Hard timeout as well as an arrival test. Arrival depends on the reel actually
            // closing the distance, which depends on the geometry cooperating; a rope that is
            // still paying out after this long is stuck, and the player must not be stuck with it.
            bool timedOut = _grappleTime > MaxReelDuration;

            if (arrived || timedOut || _jumpPressed)
            {
                _hook.Release(cfg.Movement.GrappleCooldown);
                _agent.Flags &= ~AgentFlags.Grappling;
                _agent.Locomotion = LocomotionState.Airborne;

                // Carry a little of the reel through the release, so arriving at an eave throws
                // you onto it rather than stopping you dead in the air beside it.
                _velocity = reel * 0.4f;
                if (arrived) _velocity.y = math.max(_velocity.y, 1.5f);
                return;
            }

            ApplyMotion(ctx, dt, tick, emitFootsteps: false);
        }

        private void TickSlide(SimContext ctx, in MoveIntent intent, UnseenConfig cfg, float dt, int tick)
        {
            float3 planar = UnseenMath.Horizontal(_velocity);
            planar = math.lerp(planar, float3.zero, math.saturate(3.5f * dt));
            _velocity = new float3(planar.x, cfg.Movement.Gravity * dt, planar.z);

            if (math.length(planar) < cfg.Movement.CrouchSpeed || !intent.Crouch)
                _agent.Locomotion = LocomotionState.Grounded;

            ApplyMotion(ctx, dt, tick, emitFootsteps: false);
        }

        // ---------------------------------------------------------------- helpers

        private bool TryStartGrapple(SimContext ctx, in MoveIntent intent, UnseenConfig cfg, int tick)
        {
            if (_hook == null || !_grapplePressed) return false;

            float3 aim = _agent.ViewDirection;
            if (!_hook.TryFire(_agent.EyePosition, aim, cfg.Movement.GrappleRange)) return false;

            _agent.Locomotion = LocomotionState.Grapple;
            _agent.Flags |= AgentFlags.Grappling;
            _grappleTime = 0f;

            // The rope is quiet in the open and loud when someone is close enough to hear tile
            // shift under a hook. Escaping is silent; sneaking up with it is not.
            bool enemyNear = false;
            for (int i = 0; i < _agent.Visible.Count; i++)
            {
                if (math.distancesq(_agent.Visible[i].Position, _agent.Position) < 20f * 20f)
                {
                    enemyNear = true;
                    break;
                }
            }

            float loudness = enemyNear ? cfg.Movement.GrappleNoiseLoudness : 0.5f;
            ctx.Sound.Emit(_agent.Id, _agent.Position, SoundKind.GrappleFire, loudness, loudness * 14f, tick);
            return true;
        }

        private bool TryMantle(UnseenConfig cfg, in MoveIntent intent)
        {
            float3 forward = _agent.Forward;
            if (!ParkourProbe.FindLedge(_agent.Position, forward, cfg.Movement.LedgeGrabReach + 0.5f,
                    0.4f, cfg.Movement.StandHeight + 0.9f, out LedgeHit ledge))
                return false;

            float3 target = ledge.Top + forward * 0.35f;
            if (!ParkourProbe.HasClearance(target, cfg.Movement.Radius, cfg.StanceHeight(_agent.Stance)))
                return false;

            BeginMotionWarp(target, _agent.Yaw, cfg.Movement.MantleDuration);
            return true;
        }

        private bool TryGrabLedge(UnseenConfig cfg)
        {
            float3 forward = _agent.Forward;
            if (!ParkourProbe.FindLedge(_agent.Position, forward, cfg.Movement.LedgeGrabReach,
                    cfg.Movement.StandHeight * 0.6f, cfg.Movement.StandHeight + 1.2f, out LedgeHit ledge))
                return false;

            _ledge = ledge;
            _velocity = float3.zero;
            _agent.Locomotion = LocomotionState.LedgeHang;

            float3 hangPosition = ledge.GrabPoint - new float3(0f, cfg.Movement.StandHeight * 0.95f, 0f);
            Teleport(hangPosition);
            return true;
        }

        private bool TryGrabRafter(UnseenConfig cfg)
        {
            if (!ParkourProbe.FindRafter(_agent.EyePosition, 1.6f, out RaycastHit hit)) return false;

            _agent.Locomotion = LocomotionState.RafterCrawl;
            _velocity = float3.zero;
            Teleport(new float3(hit.point.x, hit.point.y - cfg.Movement.StandHeight * 0.9f, hit.point.z));
            return true;
        }

        private void UpdateStance(in MoveIntent intent, UnseenConfig cfg)
        {
            // Prone beats crouch: holding both should put you as low as you asked to go.
            Stance desired = intent.Prone ? Stance.Prone
                : intent.Crouch ? Stance.Crouch
                : Stance.Stand;

            // Refuse to rise into a low ceiling, at whichever height was asked for. Checked
            // against the height being stood up INTO, so crawling out from under a rafter can
            // still reach a crouch even when standing is blocked.
            if (desired != _agent.Stance && cfg.StanceHeight(desired) > cfg.StanceHeight(_agent.Stance))
            {
                if (!ParkourProbe.HasClearance(_agent.Position, cfg.Movement.Radius,
                        cfg.StanceHeight(desired)))
                {
                    desired = cfg.StanceHeight(Stance.Crouch) > cfg.StanceHeight(_agent.Stance) &&
                              ParkourProbe.HasClearance(_agent.Position, cfg.Movement.Radius,
                                  cfg.StanceHeight(Stance.Crouch))
                        ? Stance.Crouch
                        : _agent.Stance;
                }
            }

            if (desired == _agent.Stance) return;

            _agent.Stance = desired;
            float height = cfg.StanceHeight(desired);
            _cc.height = height;
            _cc.center = new Vector3(0f, height * 0.5f, 0f);
        }

        private void UpdateFlags(in MoveIntent intent)
        {
            if (intent.Sprint && _agent.Stance == Stance.Stand && math.lengthsq(intent.Move) > 0.05f)
                _agent.Flags |= AgentFlags.Sprinting;
            else
                _agent.Flags &= ~AgentFlags.Sprinting;

            if (_agent.Stance != Stance.Stand) _agent.Flags |= AgentFlags.Crouched;
            else _agent.Flags &= ~AgentFlags.Crouched;
        }

        private float3 WishDirection(in MoveIntent intent)
        {
            float3 forward = UnseenMath.YawToForward(intent.Yaw);
            float3 right = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), forward));
            float3 wish = right * intent.Move.x + forward * intent.Move.y;
            float len = math.length(wish);
            return len > 1f ? wish / len : wish;
        }

        private void ApplyMotion(SimContext ctx, float dt, int tick, bool emitFootsteps, float footstepScale = 1f)
        {
            float3 before = transform.position;
            _cc.Move(_velocity * dt);
            float3 after = transform.position;

            IsGrounded = _cc.isGrounded;
            if (IsGrounded)
            {
                _coyoteTime = 0.12f;
                if (ParkourProbe.ProbeGround(after, _cc.radius, 0.4f, out RaycastHit ground))
                    _groundCollider = ground.collider;
            }
            else
            {
                _coyoteTime = math.max(0f, _coyoteTime - dt);
            }

            if (!emitFootsteps) return;

            float3 travelled = UnseenMath.Horizontal(after - before);
            _strideDistance += math.length(travelled);

            float speed = math.max(0.5f, math.length(UnseenMath.Horizontal(_velocity)));
            float stride = speed * ctx.Config.Audio.StrideInterval;
            if (_strideDistance < stride) return;

            _strideDistance = 0f;
            if (footstepScale <= 0.01f) return;

            ctx.Acoustics.EmitFootstep(_agent, _groundCollider, tick);
        }

        private void Land(SimContext ctx, UnseenConfig cfg, int tick, float time)
        {
            float impact = -_peakFallSpeed;
            _peakFallSpeed = 0f;
            _agent.Locomotion = LocomotionState.Grounded;

            if (impact < 3f) return;

            float loudness = math.saturate(impact / 12f) * 2.4f;
            if (_agent.Stance != Stance.Stand) loudness *= 0.5f;
            if (_agent.Inventory != null) loudness *= _agent.Inventory.FootstepLoudnessScale;

            ctx.Sound.Emit(_agent.Id, _agent.Position, SoundKind.Landing, loudness, loudness * 16f, tick);

            if (-impact < cfg.Movement.FallDamageThreshold)
            {
                float excess = math.abs(-impact - cfg.Movement.FallDamageThreshold);
                ctx.Combat?.ApplyDamage(new DamageInfo
                {
                    Attacker = AgentId.None,
                    Victim = _agent.Id,
                    Kind = DamageKind.Fall,
                    Amount = excess * cfg.Movement.FallDamagePerUnit,
                    Point = _agent.Position,
                    Direction = new float3(0f, -1f, 0f)
                });
            }
        }

        private void TickWarp(float dt)
        {
            _lockElapsed += dt;
            float t = math.saturate(_lockElapsed / _lockDuration);
            float eased = t * t * (3f - 2f * t);

            float3 position = math.lerp(_lockStart, _lockTarget, eased);
            _cc.enabled = false;
            transform.position = position;
            _cc.enabled = true;

            _agent.Yaw = _lockYawStart + UnseenMath.YawDelta(_lockYawStart, _lockYawTarget) * eased;

            if (t >= 1f)
            {
                _agent.Locomotion = _lockExitState;
                _velocity = float3.zero;
                _peakFallSpeed = 0f;
            }
        }
    }
}
