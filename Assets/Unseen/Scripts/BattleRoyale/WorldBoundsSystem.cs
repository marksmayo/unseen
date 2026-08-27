using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.BattleRoyale
{
    /// <summary>
    /// Keeps every agent inside the world. The rampart around the town is the thing a player
    /// actually sees and bumps into, but a wall alone is not enough to rely on: the glider drop
    /// teleports, grapples and mantles move an agent without a sweep, and a physics escape only
    /// has to happen once for someone to end up falling forever off the edge of the map.
    ///
    /// So the wall is the affordance and this is the guarantee. It runs on the server after motion
    /// has integrated, which means it covers bots and humans identically and cannot be bypassed by
    /// a client that mispredicts or lies.
    /// </summary>
    public sealed class WorldBoundsSystem : SimSystem
    {
        public override int Order => SimOrder.Motion + 10;
        public override SimRate Rate => SimRate.Combat;

        private float3 _center;
        private float _radius = 200f;
        private float _halfExtent;
        private float _floorY = -40f;
        private float _ceilingY = 120f;

        /// <summary>Corrections applied since boot. Non-zero means something is escaping.</summary>
        public int Corrections { get; private set; }

        public void Configure(MapDescriptor map)
        {
            if (map == null) return;

            _center = map.Center;

            // Slightly inside the descriptor bounds, so an agent is stopped by the clamp before it
            // reaches the visual edge of the ground plane rather than hanging over it.
            _radius = math.max(8f, map.Radius - 2f);

            // A square town gets a square clamp. With a circular one the corners of the map were
            // fenced off a hundred metres short of the rampart, which meant an invisible wall
            // standing in an open street and most of the spirit forest permanently out of reach.
            _halfExtent = map.HalfExtent > 0f ? math.max(8f, map.HalfExtent - 2f) : 0f;
            _floorY = map.FloorY - 6f;
            _ceilingY = map.CeilingY + 40f;
        }

        public override void Tick(in SimFrame frame)
        {
            EntityRegistry registry = Ctx.Entities;
            float radiusSq = _radius * _radius;

            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity agent = registry.BySlot(i);
                if (agent == null || !agent.IsAlive || agent.Motor == null) continue;

                float3 position = agent.Position;
                float3 offset = position - _center;
                offset.y = 0f;

                bool corrected = false;

                if (_halfExtent > 0f)
                {
                    // Per-axis, so the boundary is the wall the player can see rather than a
                    // circle inscribed in it.
                    float x = math.clamp(offset.x, -_halfExtent, _halfExtent);
                    float z = math.clamp(offset.z, -_halfExtent, _halfExtent);

                    if (x != offset.x || z != offset.z)
                    {
                        position = new float3(_center.x + x, position.y, _center.z + z);
                        corrected = true;
                    }
                }
                else if (math.lengthsq(offset) > radiusSq)
                {
                    float3 inward = math.normalizesafe(offset, new float3(1f, 0f, 0f));
                    float3 clamped = _center + inward * _radius;
                    position = new float3(clamped.x, position.y, clamped.z);
                    corrected = true;
                }

                // Below the sewer floor there is nothing to land on, so a fall there never ends.
                //
                // Put them back on the GROUND rather than at the floor plane. Clamping the height
                // alone stops the endless fall but leaves the body hanging in the void with nothing
                // under it, which is a different soft-lock rather than a fix - the drop test found
                // exactly that, an agent parked on the clamp at minus seventeen metres.
                // Only for bodies that have LANDED. Someone still under a glider is below the
                // floor because the drop line starts there, and teleporting them onto the nearest
                // roof mid-descent put thirty-nine of them inside geometry - a worse outcome than
                // the one being fixed. They just get lifted.
                bool deployed = (agent.Flags & AgentFlags.Deployed) != 0;

                if (position.y < _floorY && !deployed)
                {
                    position.y = _floorY + 2f;
                    corrected = true;
                }
                else if (position.y < _floorY)
                {
                    if (Physics.Raycast(new Vector3(position.x, _ceilingY, position.z),
                            Vector3.down, out RaycastHit ground, _ceilingY - _floorY + 20f,
                            UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                        position = new float3(position.x, ground.point.y + 0.2f, position.z);
                    else
                        position = new float3(_center.x, _floorY + 2f, _center.z);

                    corrected = true;
                }
                else if (position.y > _ceilingY)
                {
                    position.y = _ceilingY;
                    corrected = true;
                }

                if (!corrected) continue;

                agent.Motor.Teleport(position);
                Corrections++;
            }
        }
    }
}
