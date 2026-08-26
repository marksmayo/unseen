using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Movement;

namespace Unseen.BattleRoyale
{
    /// <summary>
    /// The infiltration phase. Agents ride a glide path over the town and steer to a landing spot;
    /// bots pick a target near loot, players steer themselves. Descent is handled here rather than
    /// in the motor so the normal locomotion state machine never has to know about gliding.
    /// </summary>
    public sealed class DeploymentSystem : SimSystem
    {
        private struct Glide
        {
            public bool Active;
            public float3 Target;

            /// <summary>Owning entity. Slots are recycled on disconnect, so the id is checked too.</summary>
            public int Owner;
        }

        private Glide[] _glides = new Glide[128];
        private float3 _flightOrigin;
        private float3 _flightDirection;

        /// <summary>Vertical descent rate under an open glider.</summary>
        public float DescentSpeed = 14f;

        public override int Order => SimOrder.Motion - 1;
        // Combat rate, not base.
        //
        // At 20 Hz the descent jumped roughly three quarters of a metre every third rendered
        // frame, which reads as violent flicker for the whole opening of the match. The glide is a
        // handful of vector operations per agent; running it at the full tick costs almost nothing
        // and is the difference between falling and strobing.
        public override SimRate Rate => SimRate.Combat;

        protected override void OnInitialize()
        {
            Ctx.Register(this);
        }

        /// <summary>Puts every living agent on a fresh glide path across the map.</summary>
        public void Begin(float3 mapCenter, float mapRadius, System.Random random)
        {
            UnseenConfig.MatchSection cfg = Ctx.Config.Match;

            float angle = (float)random.NextDouble() * math.PI * 2f;
            _flightDirection = new float3(math.cos(angle), 0f, math.sin(angle));
            _flightOrigin = mapCenter + new float3(0f, cfg.GliderDeployAltitude, 0f);

            int count = Ctx.Entities.Count;
            if (_glides.Length < count) _glides = new Glide[count * 2];

            float3 lateral = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), _flightDirection));

            for (int i = 0; i < count; i++)
            {
                AgentEntity agent = Ctx.Entities.BySlot(i);
                if (!agent.IsAlive) continue;

                // Spread along a chord *through* the map, not from a point outside it. The old
                // form started at 1.1x the radius with slot 0 at offset zero, so whoever held slot 0
                // - the human, since the player connects before bots spawn - was dropped outside the
                // playable area entirely, then killed by the closing mist.
                float t = count <= 1 ? 0.5f : i / (count - 1f);
                float along = math.lerp(-mapRadius * 0.7f, mapRadius * 0.7f, t);
                float sideways = ((float)random.NextDouble() - 0.5f) * math.min(40f, mapRadius * 0.3f);
                float3 start = _flightOrigin + _flightDirection * along + lateral * sideways;

                agent.Flags &= ~AgentFlags.Deployed;
                agent.Motor?.Teleport(start);
                agent.Locomotion = LocomotionState.Airborne;

                _glides[agent.Slot] = new Glide
                {
                    Active = true,
                    Owner = agent.Id.Value,
                    Target = PickLandingSpot(mapCenter, mapRadius, random)
                };
            }
        }

        /// <summary>
        /// Scatters everyone straight onto the ground, skipping the descent. Each agent is dropped
        /// onto the first surface beneath a downward ray, and spots without standing headroom are
        /// rejected so nobody starts wedged inside a building.
        /// </summary>
        public void PlaceOnGround(float3 mapCenter, float mapRadius, System.Random random)
        {
            int count = Ctx.Entities.Count;
            if (_glides.Length < count) _glides = new Glide[count * 2];

            int placed = 0;
            for (int i = 0; i < count; i++)
            {
                AgentEntity agent = Ctx.Entities.BySlot(i);
                if (!agent.IsAlive) continue;

                float3 spot = mapCenter + new float3(0f, 1f, 0f);
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    float angle = (float)random.NextDouble() * math.PI * 2f;
                    float radius = (float)random.NextDouble() * mapRadius * 0.85f;
                    float3 candidate = mapCenter + new float3(math.cos(angle) * radius, 0f, math.sin(angle) * radius);

                    if (!Physics.Raycast(candidate + new float3(0f, 260f, 0f), Vector3.down,
                            out RaycastHit hit, 400f, UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                        continue;

                    float3 stand = (float3)hit.point + new float3(0f, 0.1f, 0f);
                    if (!ParkourProbe.HasClearance(stand, Ctx.Config.Movement.Radius, Ctx.Config.Movement.StandHeight))
                        continue;

                    spot = stand;
                    placed++;
                    break;
                }

                agent.Motor?.Teleport(spot);
                agent.Locomotion = LocomotionState.Grounded;
                agent.Flags |= AgentFlags.Deployed;
                if (agent.Slot >= 0 && agent.Slot < _glides.Length) _glides[agent.Slot] = default;
            }

            Debug.Log($"[Unseen] infiltration skipped: {placed}/{count} agents placed with clearance");
        }

        private float3 PickLandingSpot(float3 mapCenter, float mapRadius, System.Random random)
        {
            // Prefer a loot container: that is where the interesting early fights happen.
            var containers = Items.LootContainer.All;
            if (containers.Count > 0)
            {
                int index = random.Next(containers.Count);
                return containers[index].Position;
            }

            float angle = (float)random.NextDouble() * math.PI * 2f;
            float radius = (float)random.NextDouble() * mapRadius * 0.8f;
            return mapCenter + new float3(math.cos(angle) * radius, 0f, math.sin(angle) * radius);
        }

        public override void Tick(in SimFrame frame)
        {
            if (Ctx.Match == null) return;

            bool infiltrating = Ctx.Match.Phase == MatchPhase.Infiltration;
            float dt = frame.Dt;
            int count = Ctx.Entities.Count;

            for (int i = 0; i < count; i++)
            {
                AgentEntity agent = Ctx.Entities.BySlot(i);
                if (!agent.IsAlive) continue;
                if (agent.Slot >= _glides.Length) continue;

                Glide glide = _glides[agent.Slot];
                if (!glide.Active || glide.Owner != agent.Id.Value)
                {
                    // Backfilled after the drop started: it spawned on the ground, so mark it landed
                    // rather than leaving it frozen until the phase times out.
                    if (infiltrating) agent.Flags |= AgentFlags.Deployed;
                    continue;
                }

                if (!infiltrating)
                {
                    Release(agent, ref glide);
                    _glides[agent.Slot] = glide;
                    continue;
                }

                // Steering: a player's stick, or a bot's chosen landing spot.
                float3 position = agent.Position;
                float3 steer;

                if (agent.IsBot)
                {
                    float3 toTarget = UnseenMath.Horizontal(glide.Target - position);
                    steer = math.normalizesafe(toTarget);
                    agent.Yaw = UnseenMath.ForwardToYaw(steer);
                    agent.Intent = new MoveIntent { Yaw = agent.Yaw, Move = new float2(0f, 1f), Zone = GuardZone.Mid };
                }
                else
                {
                    float3 forward = UnseenMath.YawToForward(agent.Intent.Yaw);
                    float3 right = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), forward));
                    steer = math.normalizesafe(forward * agent.Intent.Move.y + right * agent.Intent.Move.x);
                }

                float3 velocity = steer * Ctx.Config.Match.GliderSpeed * 0.35f;
                velocity.y = -DescentSpeed;

                // Sweep the path rather than teleporting along it.
                //
                // The descent used to jump straight to position + velocity * dt and only look
                // straight down for ground. Moving at fifteen metres a second horizontally, that
                // put gliders through walls and in under roofs routinely, and the whole
                // infiltration phase was disabled with SkipInfiltration because of it. A capsule
                // cast over the step is the difference between flying to a landing and being
                // teleported into a building.
                float3 delta = velocity * dt;
                float travel = math.length(delta);
                float3 direction = travel > 1e-4f ? delta / travel : new float3(0f, -1f, 0f);
                float radius = Ctx.Config.Movement.Radius;

                float3 next;
                bool blocked = Physics.SphereCast(position, radius, direction, out RaycastHit sweep,
                    travel, UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore);

                if (blocked)
                {
                    // Stop short of whatever was hit, then look for a floor to stand on. Clipping
                    // a wall on the way down should drop you at its foot, not through it.
                    next = position + direction * math.max(0f, sweep.distance - 0.05f);

                    if (TryLand(next, radius, out float3 footing))
                    {
                        Land(agent, ref glide, footing, frame.Tick);
                        continue;
                    }

                    // Nothing underneath yet: shed the horizontal component and slide down the
                    // face until there is.
                    next = position + new float3(0f, velocity.y * dt, 0f);
                }
                else
                {
                    next = position + delta;

                    if (TryLand(next, radius, out float3 footing))
                    {
                        Land(agent, ref glide, footing, frame.Tick);
                        continue;
                    }
                }

                agent.Motor?.MoveDirect(next);
                _glides[agent.Slot] = glide;
            }
        }

        /// <summary>
        /// Looks for standing room beneath a point during the descent.
        ///
        /// Requires clearance for the whole body, not just a surface under the feet: the old check
        /// was a bare downward ray, which happily "landed" a ninja on the underside of an eave or
        /// inside a rafter.
        /// </summary>
        private bool TryLand(float3 point, float radius, out float3 footing)
        {
            footing = point;

            if (!Physics.Raycast(point + new float3(0f, 0.5f, 0f), Vector3.down, out RaycastHit hit,
                    3f, UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                return false;

            float3 candidate = (float3)hit.point + new float3(0f, 0.05f, 0f);
            if (!ParkourProbe.HasClearance(candidate, radius, Ctx.Config.Movement.StandHeight))
                return false;

            footing = candidate;
            return true;
        }

        private void Land(AgentEntity agent, ref Glide glide, float3 footing, int tick)
        {
            agent.Motor?.Teleport(footing);
            Release(agent, ref glide);
            _glides[agent.Slot] = glide;
            Ctx.Sound.Emit(agent.Id, footing, SoundKind.Landing, 1.2f, 20f, tick);
        }

        private static void Release(AgentEntity agent, ref Glide glide)
        {
            glide.Active = false;
            agent.Flags |= AgentFlags.Deployed;
            agent.Locomotion = LocomotionState.Grounded;
        }

        /// <summary>True while this agent is still descending under a glider.</summary>
        public bool IsGliding(AgentEntity agent)
        {
            return agent.Slot >= 0 && agent.Slot < _glides.Length &&
                   _glides[agent.Slot].Active && _glides[agent.Slot].Owner == agent.Id.Value;
        }
    }
}
