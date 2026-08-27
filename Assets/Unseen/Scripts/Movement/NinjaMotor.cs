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

            // Last word on where the body is allowed to be.
            //
            // A character controller cannot sweep its way out of something it is already inside, so
            // any state that places the body directly - a grapple reeling to an anchor on an eave,
            // a mantle, a warp, a spawn - can leave it embedded and it stays embedded for good.
            // This is the guarantee behind those affordances, the same way the world bounds clamp
            // sits behind the rampart, and it costs nothing in the overwhelming case where the body
            // overlaps nothing at all.
            PushOutOfGeometry();
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

            // Look for a lip to get over on EVERY tick of the climb, not only at the moment the
            // wall runs out.
            //
            // This is the reported bug. A roof with a parapet on it never stops being wall in front
            // of the chest, so the old check never fired; the climb simply timed out and dropped
            // the player into Airborne hard against the face of the wall, apparently standing in
            // mid-air until they crouched and fell. There was no way over a ledge at all.
            //
            // Held forward is the input, as asked: pressing up against a lip scrambles over it.
            if (intent.Move.y > 0.1f && TryMantleFromClimb(cfg)) return;

            if (_wallTimer > cfg.Movement.WallClimbDuration || intent.Crouch)
            {
                // One last attempt at the top before giving up, so a climb that runs out of stamina
                // exactly at the lip finishes the move rather than dropping you down the wall.
                if (TryMantleFromClimb(cfg)) return;

                _velocity = _wallNormal * 1.5f;
                _agent.Locomotion = LocomotionState.Airborne;
                return;
            }

            // Reaching the top of the wall turns into a mantle rather than a hop into the void.
            if (!ParkourProbe.FindWall(_agent.TorsoPosition, into, cfg.Movement.Radius + 0.5f, out _))
            {
                if (TryMantleFromClimb(cfg)) return;
                if (TryGrabLedge(cfg)) return;
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

            // Jump OR forward. Hanging from a lip and pushing the stick towards it is the most
            // natural way anyone tries to climb up, and requiring a jump press there is a rule
            // nobody guesses.
            if ((_jumpPressed || intent.Move.y > 0.1f) && TryMantle(cfg, intent)) return;

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

        /// <summary>
        /// Shoves the controller out of anything it has ended up inside.
        ///
        /// Uses the physics engine's own penetration solver rather than a guess at a direction:
        /// ComputePenetration reports the shortest way out of each overlapping collider, which is
        /// the only answer that works for a corner where two walls meet.
        /// </summary>
        private void PushOutOfGeometry()
        {
            if (_cc == null) return;

            Transform self = _cc.transform;
            Vector3 position = self.position;

            Collider[] overlapping = Physics.OverlapCapsule(
                position + Vector3.up * _cc.radius,
                position + Vector3.up * math.max(_cc.radius, _cc.height - _cc.radius),
                _cc.radius, UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore);

            if (overlapping.Length == 0) return;

            for (int i = 0; i < overlapping.Length; i++)
            {
                Collider other = overlapping[i];
                if (other == null || other == _cc) continue;

                if (!Physics.ComputePenetration(_cc, position, self.rotation,
                        other, other.transform.position, other.transform.rotation,
                        out Vector3 direction, out float distance))
                    continue;

                // Never straight down.
                //
                // The shortest way out of a collider is sometimes downward, and the spirit forest's
                // wall now reaches sixteen metres below ground so it fills the river channel. A
                // body caught in it on the drop was being resolved down through the world and
                // ending up at minus seventeen metres with nothing under it. Out of a wall means
                // sideways or up; if the only way out is down, staying put is better than falling
                // out of the map.
                if (direction.y < 0f) direction.y = 0f;
                if (direction.sqrMagnitude < 0.0001f) continue;

                position += direction.normalized * (distance + 0.02f);
            }

            MoveDirect(position);
        }

        /// <summary>Longest a single reel sweep may travel, in metres. Under the thinnest wall.</summary>
        private const float MaxReelStep = 0.2f;

        /// <summary>Cap on sub-steps, so a pathological reel speed cannot stall the tick.</summary>
        private const int MaxReelSubSteps = 8;

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

                // Arriving at an anchor on an eave used to leave the player hanging in the air
                // beside the roof with nothing to do but fall. If there is a lip within reach of
                // where the rope has put them, they take hold of it.
                if (arrived)
                {
                    _hook.Release(cfg.Movement.GrappleCooldown);
                    _agent.Flags &= ~AgentFlags.Grappling;

                    if (TryGrabLedge(cfg)) return;

                    _agent.Locomotion = LocomotionState.Airborne;
                    PushOutOfGeometry();
                    return;
                }

                return;
            }

            // Sub-stepped, because the reel is fast.
            //
            // A single sweep of the whole frame's travel can pass clean through a thin wall: at
            // reel speed a sixtieth of a second is most of the thickness of a compound wall, and
            // the controller's own skin does the rest. A bot grappling for a roof was found wedged
            // a fifth of a metre inside one. Splitting the move into steps no longer than a fifth
            // of a metre keeps every sweep well under the thinnest geometry in the town, and it
            // costs a couple of extra sweeps only while somebody is actually on a rope.
            float travel = math.length(reel) * dt;
            int steps = math.clamp((int)math.ceil(travel / MaxReelStep), 1, MaxReelSubSteps);
            float slice = dt / steps;

            for (int i = 0; i < steps; i++) ApplyMotion(ctx, slice, tick, emitFootsteps: false);
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

            if (!FindStandableTop(cfg, ledge, forward, out float3 target)) return false;

            BeginMotionWarp(target, _agent.Yaw, cfg.Movement.MantleDuration);
            return true;
        }

        /// <summary>
        /// A mantle attempted from part-way up a wall.
        ///
        /// <see cref="TryMantle"/> probes forward from the FEET, which is right when you are
        /// standing in front of something waist-high and useless when you are flat against a wall
        /// with the lip somewhere above your head: the ray goes into the wall and the ledge search
        /// never starts. This one sweeps the probe up the body - feet, waist, chest, eyes - and
        /// takes the first lip it finds within reach.
        /// </summary>
        /// <summary>
        /// Finds a spot on TOP of a ledge that a body can actually stand on.
        ///
        /// The obvious target - the lip plus a step forward - lands past the far edge of anything
        /// thin, and a compound wall is thin. That was the reported bug, and it does not look like
        /// an overshoot from the player's seat: the mantle plays, you arrive on the wall, and a
        /// tick later you are falling. The trace reads WallClimb, Locked, Grounded at 3.55 m,
        /// Airborne, ground.
        ///
        /// So the step forward is tried longest-first and each candidate has to have floor under
        /// it, which lands a thick roof deep and a thin parapet right on its centre line.
        /// </summary>
        private bool FindStandableTop(UnseenConfig cfg, in LedgeHit ledge, float3 forward,
            out float3 target)
        {
            target = default;
            // Including zero and a step BACK.
            //
            // ledge.Top is already a quarter of a metre in from the near face - that is where the
            // probe that found it looked down - so on a compound wall three tenths of a metre thick
            // every forward offset lands past the far edge and finds no floor. The mantle then
            // never fired at all, and the climb ratcheted up and fell out of itself forever at two
            // and a half metres: the limbo that was reported.
            float[] steps = { 0.45f, 0.3f, 0.18f, 0.08f, 0f, -0.1f };

            for (int i = 0; i < steps.Length; i++)
            {
                float3 candidate = ledge.Top + forward * steps[i] + new float3(0f, 0.05f, 0f);

                // Floor directly under it, close enough to be the same surface as the lip.
                if (!Physics.Raycast(candidate + new float3(0f, 0.6f, 0f), Vector3.down,
                        out RaycastHit floor, 1.1f, UnseenLayers.WorldGeometry,
                        QueryTriggerInteraction.Ignore))
                    continue;

                if (Vector3.Dot(floor.normal, Vector3.up) < 0.6f) continue;

                float3 stand = new float3(candidate.x, floor.point.y + 0.03f, candidate.z);
                if (!ParkourProbe.HasClearance(stand, cfg.Movement.Radius,
                        cfg.StanceHeight(_agent.Stance)))
                    continue;

                target = stand;
                return true;
            }

            return false;
        }

        private bool TryMantleFromClimb(UnseenConfig cfg)
        {
            float3 forward = -_wallNormal;
            float stand = cfg.Movement.StandHeight;

            // Four heights up the body. A lip level with the eyes is reachable; one two metres
            // above them is a different wall.
            float[] offsets = { 0f, stand * 0.45f, stand * 0.8f, stand * 1.15f };

            for (int i = 0; i < offsets.Length; i++)
            {
                float3 from = _agent.Position + new float3(0f, offsets[i], 0f);

                if (!ParkourProbe.FindLedge(from, forward, cfg.Movement.LedgeGrabReach + 0.6f,
                        0.25f, stand + 1.1f, out LedgeHit ledge))
                    continue;

                if (!FindStandableTop(cfg, ledge, forward, out float3 target)) continue;

                BeginMotionWarp(target, _agent.Yaw, cfg.Movement.MantleDuration);
                return true;
            }

            return false;
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

            // Move the eye and torso anchors with the stance.
            //
            // They were created once at STANDING height and never touched again, so a prone ninja
            // was seen from - and saw from - a point one and a seventh metres above its own back.
            // That is not a cosmetic error: EyePosition is what the perception system traces
            // sightlines from and to, so lying down behind a wall gave no benefit whatsoever, and
            // the drowning check believed a body on the riverbed still had its head in the air.
            if (_agent.EyeAnchor != null)
                _agent.EyeAnchor.localPosition =
                    new Vector3(0f, height + cfg.Movement.EyeOffset, 0f);

            if (_agent.TorsoAnchor != null)
                _agent.TorsoAnchor.localPosition = new Vector3(0f, height * 0.55f, 0f);
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
