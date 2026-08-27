using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.Combat
{
    /// <summary>
    /// Thrown steel: shuriken in flight, what they hit, and where they end up.
    ///
    /// Ranged attacks are dangerous in a game built on not being seen, because a weapon you can use
    /// from cover without moving undoes most of what the stealth model is for. Three things keep it
    /// honest, and all three were in the brief:
    ///
    ///   - You start with ONE. It is a decision, not a rate of fire.
    ///   - A miss does not disappear. The blade lands on the ground where it fell and anybody can
    ///     pick it up, including the person you threw it at - so a miss can arm your target.
    ///   - It whistles the whole way. Throwing across a courtyard draws a line in sound from you to
    ///     wherever it lands, which is the loudest thing you can do short of breaking a lantern.
    ///
    /// And a two-second floor between throws, so a lucky pickup run cannot turn into a machine gun.
    ///
    /// Runs at the combat rate rather than the base rate: a blade at thirty-four metres a second
    /// covers half a metre per tick at 60 Hz and nearly two at 20 Hz, which is wider than a body.
    /// </summary>
    public sealed class ShurikenSystem : SimSystem
    {
        public override int Order => SimOrder.Combat + 5;
        public override SimRate Rate => SimRate.Combat;

        private struct Blade
        {
            public AgentId Owner;
            public float3 Position;
            public float3 Velocity;
            public float Age;
            public float NextWhistle;
            public Transform Visual;
        }

        private readonly List<Blade> _blades = new List<Blade>(32);
        private readonly Dictionary<int, float> _nextThrow = new Dictionary<int, float>(64);
        private readonly Dictionary<int, bool> _throwHeld = new Dictionary<int, bool>(64);

        /// <summary>Blades currently in the air. Watched by the tests.</summary>
        public int InFlight => _blades.Count;

        /// <summary>Throws since boot, and how many of those hit somebody.</summary>
        public int Thrown { get; private set; }
        public int Hits { get; private set; }

        public override void Tick(in SimFrame frame)
        {
            UnseenConfig.ShurikenSection cfg = Ctx.Config.Shuriken;
            if (!cfg.Enabled) return;

            CollectThrows(cfg, frame);
            Advance(cfg, frame);
            Collect(cfg, frame);
        }

        /// <summary>Clears every blade and cooldown. Called when a match restarts.</summary>
        public void Reset()
        {
            for (int i = 0; i < _blades.Count; i++)
                if (_blades[i].Visual != null) Object.Destroy(_blades[i].Visual.gameObject);

            _blades.Clear();
            _nextThrow.Clear();
            _throwHeld.Clear();
            ShurikenPickup.ClearAll();
            Thrown = 0;
            Hits = 0;
        }

        private void CollectThrows(UnseenConfig.ShurikenSection cfg, in SimFrame frame)
        {
            EntityRegistry registry = Ctx.Entities;

            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity agent = registry.BySlot(i);
                if (agent == null || !agent.IsAlive || agent.IsLocked) continue;

                int id = agent.Id.Value;
                bool wants = agent.Intent.Throw;

                _throwHeld.TryGetValue(id, out bool held);
                _throwHeld[id] = wants;

                // Rising edge only. Holding the button is one throw, not a stream of them.
                if (!wants || held) continue;
                if (agent.Shuriken <= 0) continue;

                _nextThrow.TryGetValue(id, out float ready);
                if (frame.Time < ready) continue;

                _nextThrow[id] = frame.Time + cfg.Cooldown;
                agent.Shuriken--;
                Thrown++;

                var blade = new Blade
                {
                    Owner = agent.Id,
                    Position = agent.EyePosition + agent.ViewDirection * 0.4f,
                    Velocity = agent.ViewDirection * cfg.Speed,
                    Age = 0f,
                    NextWhistle = 0f,
                    Visual = ShurikenPickup.CreateBlade()
                };

                _blades.Add(blade);

                // The throw itself is heard, separately from the whistle: a body moving hard enough
                // to sling steel is not quiet.
                Ctx.Sound.Emit(agent.Id, agent.Position, SoundKind.WeaponSwing,
                    cfg.ThrowLoudness, cfg.ThrowRadius, frame.Tick);
            }
        }

        private void Advance(UnseenConfig.ShurikenSection cfg, in SimFrame frame)
        {
            float dt = frame.Dt;

            for (int i = _blades.Count - 1; i >= 0; i--)
            {
                Blade blade = _blades[i];

                blade.Age += dt;
                blade.Velocity.y -= cfg.Drop * dt;

                float3 from = blade.Position;
                float3 step = blade.Velocity * dt;
                float distance = math.length(step);
                float3 to = from + step;

                if (blade.Age > cfg.Lifetime)
                {
                    Land(cfg, blade, to, frame);
                    _blades.RemoveAt(i);
                    continue;
                }

                // Bodies first, then the world, so a blade that would pass through both hits the
                // person rather than the wall behind them.
                if (distance > 0.0001f &&
                    TryHitAgent(cfg, blade, from, math.normalize(step), distance, frame))
                {
                    if (blade.Visual != null) Object.Destroy(blade.Visual.gameObject);
                    _blades.RemoveAt(i);
                    continue;
                }

                if (distance > 0.0001f &&
                    Physics.Raycast(from, math.normalize(step), out RaycastHit world, distance,
                        UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                {
                    Ctx.Sound.Emit(blade.Owner, world.point, SoundKind.ShurikenHit,
                        cfg.HitLoudness, cfg.HitRadius, frame.Tick);

                    Land(cfg, blade, (float3)world.point + (float3)world.normal * 0.06f, frame);
                    _blades.RemoveAt(i);
                    continue;
                }

                blade.Position = to;

                if (blade.Visual != null)
                {
                    blade.Visual.position = to;

                    // Spinning about its own flight axis, which is what a thrown star does and what
                    // makes it readable in the air.
                    blade.Visual.rotation = Quaternion.LookRotation(
                        math.normalizesafe(blade.Velocity, new float3(0f, 0f, 1f)), Vector3.up) *
                        Quaternion.Euler(0f, 0f, blade.Age * cfg.SpinDegreesPerSecond);
                }

                // The whistle, repeatedly, from wherever it currently is - so it draws a line
                // through the air rather than a point at the thrower.
                if (frame.Time >= blade.NextWhistle)
                {
                    blade.NextWhistle = frame.Time + cfg.WhistleInterval;
                    Ctx.Sound.Emit(blade.Owner, blade.Position, SoundKind.ShurikenWhistle,
                        cfg.WhistleLoudness, cfg.WhistleRadius, frame.Tick);
                }

                _blades[i] = blade;
            }
        }

        private bool TryHitAgent(UnseenConfig.ShurikenSection cfg, in Blade blade, float3 from,
            float3 direction, float distance, in SimFrame frame)
        {
            EntityRegistry registry = Ctx.Entities;
            AgentEntity best = null;
            float bestT = float.MaxValue;

            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity agent = registry.BySlot(i);
                if (agent == null || !agent.IsAlive) continue;
                if (agent.Id == blade.Owner) continue;

                // Against the whole BODY, as a vertical segment from ankles to head, rather than
                // a sphere on the torso.
                //
                // A torso sphere reads as a miss for shots that obviously connect. Thrown flat from
                // the eye at 1.7 m, a blade arrives at eight metres having dropped fifteen
                // centimetres - so it passes about half a metre above a torso point at 0.95 m and
                // sails through the target's chest without touching the sphere.
                float height = Ctx.Config.StanceHeight(agent.Stance);
                float3 low = agent.Position + new float3(0f, 0.25f, 0f);
                float3 high = agent.Position + new float3(0f, math.max(0.4f, height - 0.15f), 0f);

                if (!SegmentsClose(from, direction, distance, low, high,
                        cfg.HitRadiusMetres, out float along))
                    continue;

                if (along >= bestT) continue;
                bestT = along;
                best = agent;
            }

            if (best == null) return false;

            Ctx.Combat.ApplyDamage(new DamageInfo
            {
                Attacker = blade.Owner,
                Victim = best.Id,
                Kind = DamageKind.Thrown,
                Amount = cfg.Damage,
                Point = best.TorsoPosition,
                Direction = direction
            });

            Ctx.Sound.Emit(blade.Owner, best.TorsoPosition, SoundKind.ShurikenHit,
                cfg.HitLoudness, cfg.HitRadius, frame.Tick);

            Hits++;
            return true;
        }

        /// <summary>
        /// Closest approach between the blade's path this tick and a standing body, treated as a
        /// vertical segment. Returns how far along the path that approach happens.
        ///
        /// Sampled rather than solved. The exact segment-to-segment distance is a short piece of
        /// algebra that is easy to get subtly wrong, and at these speeds and sizes a dozen samples
        /// along the path is both obviously correct and cheaper to read.
        /// </summary>
        private static bool SegmentsClose(float3 from, float3 direction, float distance,
            float3 low, float3 high, float radius, out float along)
        {
            along = 0f;

            const int samples = 12;
            float best = radius * radius;
            bool found = false;

            for (int i = 0; i <= samples; i++)
            {
                float t = distance * i / samples;
                float3 point = from + direction * t;

                // Nearest point on the body segment to this point on the path.
                float3 axis = high - low;
                float length = math.lengthsq(axis);
                float k = length > 0.0001f ? math.saturate(math.dot(point - low, axis) / length) : 0f;
                float3 onBody = low + axis * k;

                float gap = math.distancesq(point, onBody);
                if (gap >= best) continue;

                best = gap;
                along = t;
                found = true;
            }

            return found;
        }

        /// <summary>Drops a blade on the ground where anyone can pick it up.</summary>
        private void Land(UnseenConfig.ShurikenSection cfg, in Blade blade, float3 at,
            in SimFrame frame)
        {
            if (blade.Visual != null) Object.Destroy(blade.Visual.gameObject);

            float3 resting = at;

            // Settle it onto whatever is underneath, so a blade that clips a roof edge is not left
            // floating half a metre above it.
            if (Physics.Raycast((Vector3)at + Vector3.up * 0.5f, Vector3.down,
                    out RaycastHit ground, 4f, UnseenLayers.WorldGeometry,
                    QueryTriggerInteraction.Ignore))
                resting = (float3)ground.point + new float3(0f, 0.04f, 0f);

            ShurikenPickup.Drop(resting, frame.Time + cfg.PickupDelay);
        }

        private void Collect(UnseenConfig.ShurikenSection cfg, in SimFrame frame)
        {
            EntityRegistry registry = Ctx.Entities;

            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity agent = registry.BySlot(i);
                if (agent == null || !agent.IsAlive) continue;
                if (agent.Shuriken >= cfg.MaxCarried) continue;

                if (!ShurikenPickup.TryTake(agent.Position, cfg.PickupRadius, frame.Time)) continue;

                agent.Shuriken++;
                Ctx.Sound.Emit(agent.Id, agent.Position, SoundKind.LootContainer,
                    0.3f, 8f, frame.Tick);
            }
        }
    }
}
