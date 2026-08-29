using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;
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

            /// <summary>Set once the descent has clipped something; steering stops after that.</summary>
            public bool Grazed;
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

        /// <summary>
        /// Parks everyone at the drop altitude while the lobby fills.
        ///
        /// Agents are spawned onto the ground and the match does not start until the roster is
        /// full, so for those seconds the player stood in the street looking at the town - and then
        /// got snatched into the sky the instant the match began. That reads as a glitch because it
        /// is one: the game showed a position it had not decided on yet.
        ///
        /// Parking at the altitude Begin will use means the drop starts where the waiting ended.
        /// Begin still spreads everyone along its chord, but that is a sideways move at the same
        /// height, so there is nothing to see.
        ///
        /// Safe to call every lobby tick - it only touches agents that are not already parked, so
        /// somebody who joins late is lifted without disturbing anybody who is already up there.
        /// </summary>
        public void Park(float3 mapCenter)
        {
            float altitude = Ctx.Config.Match.GliderDeployAltitude;
            int count = Ctx.Entities.Count;

            for (int i = 0; i < count; i++)
            {
                AgentEntity agent = Ctx.Entities.BySlot(i);
                if (agent == null || !agent.IsAlive) continue;

                // Already up and waiting.
                if (agent.Locomotion == LocomotionState.Airborne &&
                    agent.Position.y >= altitude - 1f) continue;

                agent.Flags &= ~AgentFlags.Deployed;

                // Fanned out a little so a full lobby is not one body inside another.
                float spread = count <= 1 ? 0f : 6f;
                float angle = count <= 1 ? 0f : i / (float)count * math.PI * 2f;

                agent.Motor?.Teleport(mapCenter + new float3(
                    math.cos(angle) * spread, altitude, math.sin(angle) * spread));

                agent.Locomotion = LocomotionState.Airborne;

                // Off for the same reason it is off during the descent: the controller would
                // depenetrate against nothing and start falling.
                if (agent.Controller != null) agent.Controller.enabled = false;
            }
        }

        /// <summary>
        /// Whether a point is comfortably inside the first mist ring.
        ///
        /// The margin is metres of walking room, not a safety factor: landing one metre inside a
        /// ring that is about to close is the same problem as landing outside it.
        /// </summary>
        private bool InsideFirstRing(float3 point, float3 mapCenter, float mapRadius)
        {
            float ring = Ctx.Mist != null && Ctx.Mist.CurrentRadius > 1f
                ? Ctx.Mist.CurrentRadius
                : math.min(Ctx.Config.Match.InitialZoneRadius, mapRadius);

            float room = math.max(20f, ring - 40f);
            return math.distancesq(point.xz, mapCenter.xz) <= room * room;
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

            // The chord is shuffled before it is handed out.
            //
            // Position along it was slot order, and slot 0 is always the human because the player
            // connects before the bots spawn - so the player was posted to one extreme end of the
            // flight path every single match, 260 m from the middle of a 372 m map, while the bots
            // arranged themselves symmetrically inward. A fixed worst seat is worse than a random
            // one even when the average is identical.
            var order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;

            for (int i = count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }

            // And the chord is kept inside the first ring rather than scaled to the map.
            //
            // At 0.7 of the map radius the ends of the chord sat within a few metres of the mist,
            // so anybody who did not steer hard inward spent the descent drifting toward ground
            // that was about to become lethal. Bounded by the ring, the whole flight path is
            // survivable wherever you let go.
            float ringRoom = Ctx.Mist != null && Ctx.Mist.CurrentRadius > 1f
                ? Ctx.Mist.CurrentRadius
                : math.min(cfg.InitialZoneRadius, mapRadius);

            float chord = math.min(mapRadius * 0.6f, math.max(30f, ringRoom - 120f));

            for (int i = 0; i < count; i++)
            {
                AgentEntity agent = Ctx.Entities.BySlot(i);
                if (!agent.IsAlive) continue;

                // Spread along a chord *through* the map, not from a point outside it. The old
                // form started at 1.1x the radius with slot 0 at offset zero, so whoever held slot 0
                // - the human, since the player connects before bots spawn - was dropped outside the
                // playable area entirely, then killed by the closing mist.
                float t = count <= 1 ? 0.5f : order[i] / (count - 1f);
                float along = math.lerp(-chord, chord, t);
                float sideways = ((float)random.NextDouble() - 0.5f) * math.min(40f, mapRadius * 0.3f);
                float3 start = _flightOrigin + _flightDirection * along + lateral * sideways;

                // Nudged off the water before anybody is put there.
                //
                // A glider that never steers comes straight down, so the start column is the
                // landing spot for anyone who lets go - and the human is exactly the player who
                // might. Rejecting wet landing TARGETS did nothing for them, because they never
                // fly to their target.
                for (int nudge = 0; nudge < 8 && WaterVolume.OverWater(start.x, start.z); nudge++)
                    start += lateral * 18f;

                agent.Flags &= ~AgentFlags.Deployed;
                agent.Motor?.Teleport(start);
                agent.Locomotion = LocomotionState.Airborne;

                // The controller is switched off for the whole descent, not per step.
                //
                // The glide owns the transform while it is open, and a live CharacterController
                // depenetrates against whatever it finds on its own schedule - so writing a
                // position each tick and letting the controller argue with it produced exactly the
                // judder that shows up when a glider passes close to a roof. The sweep in Tick is
                // already doing the collision work the controller would have done.
                if (agent.Controller != null) agent.Controller.enabled = false;

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

                    // Not in the river or the lake. The same rule the glide path follows: landing
                    // in water costs a player the opening, because wading is slow and loud and the
                    // banks hide the streets. Ten tries is plenty when water is a small share of
                    // the map, and the fallback below is the map centre rather than a wet spot.
                    if (WaterVolume.OverWater(candidate.x, candidate.z)) continue;

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

            UnseenLog.Info($"[Unseen] infiltration skipped: {placed}/{count} agents placed with clearance");
        }

        /// <summary>
        /// Somewhere to aim for: spread around the town, and not in the water.
        ///
        /// Two changes from the version that only picked a loot container. Containers are still the
        /// draw - that is where the interesting early fights happen - but taking one every time put
        /// everybody in a compound courtyard, and now that the street stacks are containers too the
        /// bias got stronger rather than weaker. So a third of drops aim at open ground instead,
        /// which is what makes a drop feel like a choice of where to go rather than a shuttle to
        /// the nearest chest.
        ///
        /// And nothing lands in the river or the castle lake. Coming down in water costs a player
        /// the whole opening: wading is slow and loud, the banks hide the streets, and bots were
        /// getting stuck in the lake outright. A candidate over water is thrown away and another
        /// tried, rather than nudged to the nearest shore - a nudge would pile everybody who rolled
        /// the river onto the same few metres of towpath.
        /// </summary>
        private float3 PickLandingSpot(float3 mapCenter, float mapRadius, System.Random random)
        {
            var containers = Items.LootContainer.All;
            float3 fallback = mapCenter;

            // Twelve tries. Water is a small share of the map, so this effectively never runs out;
            // the cap is only here so that a map somebody floods entirely cannot hang the drop.
            for (int attempt = 0; attempt < 12; attempt++)
            {
                float3 candidate;

                if (containers.Count > 0 && random.NextDouble() < 0.66)
                {
                    Items.LootContainer c = containers[random.Next(containers.Count)];
                    if (c == null) continue;
                    candidate = c.Position;
                }
                else
                {
                    float angle = (float)random.NextDouble() * math.PI * 2f;
                    float radius = (float)random.NextDouble() * mapRadius * 0.8f;
                    candidate = mapCenter +
                                new float3(math.cos(angle) * radius, 0f, math.sin(angle) * radius);
                }

                if (attempt == 0) fallback = candidate;
                if (WaterVolume.OverWater(candidate.x, candidate.z)) continue;

                // Inside the first mist ring, with room to spare.
                //
                // The ring is a circle and the town is a square, so the corners of the map lie
                // outside it - about 519 m out on a town whose first ring is 372 m. Containers are
                // spread over the whole square, so aiming at one could drop a player into a corner
                // that was already lethal, which is exactly the "landed and then died to the mist"
                // that has no counterplay: you cannot walk 150 m before the damage kills you.
                if (!InsideFirstRing(candidate, mapCenter, mapRadius)) continue;

                return candidate;
            }

            return fallback;
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

                float3 velocity = glide.Grazed
                    ? float3.zero
                    : steer * Ctx.Config.Match.GliderSpeed * 0.35f;
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
                    // Once something has been clipped, the horizontal steering is given up for the
                    // rest of the descent. Re-testing it every tick made the glider alternate
                    // between blocked and clear against a roof edge and shudder down the seam.
                    glide.Grazed = true;

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

            // Handing the body back to the motor.
            if (agent.Controller != null) agent.Controller.enabled = true;
        }

        /// <summary>True while this agent is still descending under a glider.</summary>
        public bool IsGliding(AgentEntity agent)
        {
            return agent.Slot >= 0 && agent.Slot < _glides.Length &&
                   _glides[agent.Slot].Active && _glides[agent.Slot].Owner == agent.Id.Value;
        }
    }
}
