using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;

namespace Unseen.Perception
{
    /// <summary>
    /// Flushes birds and bolts animals when somebody moves badly near them.
    ///
    /// This is the cheapest information-leak mechanic in the game and the most readable one. A
    /// footstep tells a listener where you were; a flushed bird tells them the same thing but
    /// louder, from higher up, and it fires even when the ground underfoot is quiet. It makes
    /// gardens and tree-lined streets into places where speed genuinely costs you something, which
    /// they otherwise were not - the foliage had no collision and no consequence.
    ///
    /// How close you get before it happens is scaled by the stance loudness the acoustic model
    /// already uses, so the rule a player learns for footsteps transfers here without being taught
    /// twice: crouch to get close, sprint and clear the street.
    ///
    /// Cost is kept down by a uniform grid over the critters, the same shape as the light grid.
    /// Iterating every critter for every agent would be sixty-four times a couple of hundred every
    /// base tick for a mechanic that only ever fires within a few metres.
    /// </summary>
    public sealed class CritterStartleSystem : SimSystem
    {
        public override int Order => SimOrder.Mist - 20;
        public override SimRate Rate => SimRate.Base;

        /// <summary>Cell size of the lookup grid, in metres. A little over the widest startle.</summary>
        private const float CellSize = 16f;

        /// <summary>Below this speed a body is not disturbing anything.</summary>
        private const float MovingSpeedSq = 0.6f;

        private readonly Dictionary<long, List<Critter>> _grid = new Dictionary<long, List<Critter>>(512);
        private readonly List<Critter> _flushed = new List<Critter>(16);
        private int _indexedCount = -1;

        /// <summary>Critters startled since boot. Watched by the tests.</summary>
        public int Startles { get; private set; }

        public override void Tick(in SimFrame frame)
        {
            UnseenConfig cfg = Ctx.Config;
            if (Critter.All.Count == 0) return;

            // The grid is rebuilt only when the population changes, which is at generation time and
            // never again: critters return to a fixed perch, so their cells are stable.
            if (_indexedCount != Critter.All.Count) BuildIndex();

            EntityRegistry registry = Ctx.Entities;

            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity agent = registry.BySlot(i);
                if (agent == null || !agent.IsAlive || agent.Motor == null) continue;
                if (agent.IsLocked) continue;

                // A body standing still disturbs nothing, however close it is.
                float3 velocity = agent.Motor.Velocity;
                velocity.y = 0f;
                if (math.lengthsq(velocity) < MovingSpeedSq) continue;

                bool sprinting = agent.Intent.Sprint && agent.Stance == Stance.Stand;
                float loudness = cfg.StanceLoudnessScale(agent.Stance, sprinting);

                Startle(agent, loudness, frame);
            }

            // Advancing after the sweep, so a critter flushed this tick starts moving this tick.
            for (int i = 0; i < Critter.All.Count; i++)
            {
                Critter critter = Critter.All[i];
                if (critter != null) critter.Advance(frame.Dt);
            }
        }

        private void Startle(AgentEntity agent, float loudness, in SimFrame frame)
        {
            float3 at = agent.Position;
            int cx = Mathf.FloorToInt(at.x / CellSize);
            int cz = Mathf.FloorToInt(at.z / CellSize);

            _flushed.Clear();

            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!_grid.TryGetValue(Key(cx + dx, cz + dz), out List<Critter> cell)) continue;

                for (int i = 0; i < cell.Count; i++)
                {
                    Critter critter = cell[i];
                    if (critter == null || !critter.IsSettled) continue;

                    // Scaled by how loudly this body is moving: the same number that decides how
                    // far a footstep carries decides how close you can get to a bird.
                    float radius = critter.StartleRadius * loudness;

                    Vector3 perch = critter.transform.position;
                    float dxp = perch.x - at.x;
                    float dzp = perch.z - at.z;
                    float dyp = perch.y - at.y;

                    if (dxp * dxp + dzp * dzp + dyp * dyp > radius * radius) continue;

                    _flushed.Add(critter);
                }
            }

            for (int i = 0; i < _flushed.Count; i++)
            {
                Critter critter = _flushed[i];
                if (!critter.Flush(at)) continue;

                Startles++;

                UnseenConfig.CritterSection cfg = Ctx.Config.Critters;
                bool bird = critter.Kind == Critter.Species.Bird;

                // Attributed to NOBODY, not to the agent who caused it.
                //
                // The acoustic model refuses to deliver a sound to the agent it names as the
                // source, which is right for footsteps - you do not need telling that you are
                // walking - and exactly wrong here. Credited to the player, the one person in the
                // match who could not hear the bird go up was the player who flushed it, and the
                // whole mechanic is that YOU hear it and know you have just announced yourself.
                //
                // The bird made the noise. It is the bird's sound.
                Ctx.Sound.Emit(AgentId.None, critter.transform.position,
                    bird ? SoundKind.BirdFlush : SoundKind.AnimalScatter,
                    bird ? cfg.BirdLoudness : cfg.AnimalLoudness,
                    bird ? cfg.BirdRadius : cfg.AnimalRadius,
                    frame.Tick);
            }
        }

        private void BuildIndex()
        {
            _grid.Clear();
            _indexedCount = Critter.All.Count;

            for (int i = 0; i < Critter.All.Count; i++)
            {
                Critter critter = Critter.All[i];
                if (critter == null) continue;

                Vector3 at = critter.transform.position;
                long key = Key(Mathf.FloorToInt(at.x / CellSize), Mathf.FloorToInt(at.z / CellSize));

                if (!_grid.TryGetValue(key, out List<Critter> cell))
                {
                    cell = new List<Critter>(8);
                    _grid[key] = cell;
                }

                cell.Add(critter);
            }

            Debug.Log($"[Unseen] critter grid: {_indexedCount} critters across {_grid.Count} cells " +
                      $"of {CellSize:0} m");
        }

        private static long Key(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }
    }
}
