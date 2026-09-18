using System.Collections.Generic;
using Unity.Mathematics;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;

namespace Unseen.Perception
{
    /// <summary>
    /// Holding your breath, and what happens when you cannot any longer.
    ///
    /// The river is the best hiding place in the town: lying prone in the deep middle puts a body
    /// entirely under the surface, invisible from the bank and from the bridges. That was a free
    /// hiding place with no cost attached, which is the one thing a stealth game cannot afford - a
    /// player who can simply wait somewhere unreachable is a player nobody has to deal with.
    ///
    /// So it has a clock. Thirty seconds of air, then the body starts fighting for it, and that
    /// fight is LOUD: the choking goes out through the same acoustic model as a footstep, so
    /// somebody on the towpath hears exactly where you are. Fifteen seconds after that it starts
    /// killing you.
    ///
    /// The gap between the two is the whole design. The noise is the warning and the penalty at
    /// once - you are given time to surface, but only by giving your position away.
    /// </summary>
    public sealed class DrowningSystem : SimSystem
    {
        public override int Order => SimOrder.Mist - 15;
        public override SimRate Rate => SimRate.Base;

        private readonly Dictionary<int, float> _submergedFor = new Dictionary<int, float>(64);
        private readonly Dictionary<int, float> _lastChoke = new Dictionary<int, float>(64);

        /// <summary>Agents currently under the surface. Watched by the tests.</summary>
        public int Submerged { get; private set; }

        /// <summary>Agents past the point of holding their breath.</summary>
        public int Choking { get; private set; }

        public override void Tick(in SimFrame frame)
        {
            UnseenConfig.WaterSection cfg = Ctx.Config.Water;
            if (!cfg.Drowning) return;

            EntityRegistry registry = Ctx.Entities;
            float dt = Ctx.Config.BaseTickInterval;

            Submerged = 0;
            Choking = 0;

            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity agent = registry.BySlot(i);
                if (agent == null || !agent.IsAlive)
                {
                    if (agent != null) _submergedFor.Remove(agent.Id.Value);
                    continue;
                }

                // Crossing the waterline is a separate question from drowning, and asked here
                // because this loop is already paying to sample the water for all sixty-four
                // bodies once a tick. A second system asking the same geometry the same question
                // would double a cost that is measured in raycasts.
                TrackWaterline(cfg, agent, frame);

                // The eye, not the feet. Standing chest-deep is not drowning; it is the head going
                // under that starts the clock, which is why going prone in the deep channel does
                // and crouching on the shelf does not.
                bool under = WaterVolume.IsUnder(agent.EyePosition);

                // Replicated, so everybody else can see the bubbles. Set from the same test that
                // drives the breath clock, so what is drawn and what is fatal cannot disagree.
                if (under) agent.Flags |= AgentFlags.Submerged;
                else agent.Flags &= ~AgentFlags.Submerged;

                if (!under)
                {
                    // Surfacing clears it outright. Breath recovery could be modelled, but a
                    // partial reset would mean a player who bobbed up for a moment drowning anyway,
                    // and being killed by a rule you appeared to satisfy is the worst kind of rule.
                    _submergedFor.Remove(agent.Id.Value);
                    continue;
                }

                Submerged++;

                _submergedFor.TryGetValue(agent.Id.Value, out float held);
                held += dt;
                _submergedFor[agent.Id.Value] = held;

                if (held < cfg.HoldBreathSeconds) continue;

                Choking++;
                Choke(cfg, agent, frame);

                if (held < cfg.DrownAfterSeconds) continue;

                Ctx.Combat.ApplyDamage(new DamageInfo
                {
                    Attacker = AgentId.None,
                    Victim = agent.Id,
                    Kind = DamageKind.Drowning,
                    Amount = cfg.DrownDamagePerSecond * dt,
                    Point = agent.TorsoPosition,
                    Direction = new float3(0f, 1f, 0f)
                });
            }
        }

        /// <summary>
        /// The sound of somebody out of air. Rate-limited per agent, and emitted through the normal
        /// acoustic model so it occludes and misleads exactly like every other noise in the game.
        /// </summary>
        private void Choke(UnseenConfig.WaterSection cfg, AgentEntity agent, in SimFrame frame)
        {
            if (_lastChoke.TryGetValue(agent.Id.Value, out float last) &&
                frame.Time - last < cfg.ChokeInterval)
                return;

            _lastChoke[agent.Id.Value] = frame.Time;
            Ctx.Sound.Emit(agent.Id, agent.EyePosition, SoundKind.Choking,
                cfg.ChokeLoudness, cfg.ChokeRadius, frame.Tick);
        }

        /// <summary>Which bodies were in the water last tick, so a crossing can be noticed.</summary>
        private readonly HashSet<int> _inWater = new HashSet<int>();

        private readonly Dictionary<int, float> _lastSplash = new Dictionary<int, float>(64);

        /// <summary>
        /// A splash when a body crosses the waterline, in either direction.
        ///
        /// Emitted into the acoustic model rather than played as a sound effect, so it occludes,
        /// misleads and gives a position away exactly like every other noise in the game. That is
        /// the point of it: the river is cover from sight and the opposite of cover from sound, and
        /// crossing one should be a choice between being seen and being heard.
        ///
        /// Louder the faster you were going. Wading in carefully and jumping in are different acts
        /// and should not sound the same, and a player who has understood that has been given
        /// something to do with the understanding.
        /// </summary>
        private void TrackWaterline(UnseenConfig.WaterSection cfg, AgentEntity agent, in SimFrame frame)
        {
            bool wet = WaterVolume.DepthAt(agent.Position) > cfg.SplashDepth;
            bool wasWet = _inWater.Contains(agent.Id.Value);

            if (wet == wasWet) return;

            if (wet) _inWater.Add(agent.Id.Value);
            else _inWater.Remove(agent.Id.Value);

            // Rate limited, because the waterline is a line and a body standing on it in a current
            // will cross it over and over. Without this, treading the shallows would ring like an
            // alarm and give away a position the player never moved from.
            if (_lastSplash.TryGetValue(agent.Id.Value, out float last) &&
                frame.Time - last < cfg.SplashInterval)
                return;

            _lastSplash[agent.Id.Value] = frame.Time;

            float speed = agent.Motor != null
                ? math.length(new float2(agent.Motor.Velocity.x, agent.Motor.Velocity.z))
                : 0f;

            float loudness = cfg.SplashLoudness *
                             (1f + cfg.SplashSpeedBoost * math.saturate(speed / 8f));

            Ctx.Sound.Emit(agent.Id, agent.Position, SoundKind.Splash,
                loudness, cfg.SplashRadius, frame.Tick);
        }

        /// <summary>How long this agent has been under, in seconds. Zero on the surface.</summary>
        public float HeldBreath(AgentId id)
        {
            return _submergedFor.TryGetValue(id.Value, out float held) ? held : 0f;
        }

        /// <summary>Clears every breath clock. Called when a match restarts.</summary>
        public void Reset()
        {
            _submergedFor.Clear();
            _lastChoke.Clear();

            // The waterline memory too, or the first tick of a new match reads every body standing
            // on dry land as having just climbed out of the river.
            _inWater.Clear();
            _lastSplash.Clear();

            Submerged = 0;
            Choking = 0;
        }
    }
}
