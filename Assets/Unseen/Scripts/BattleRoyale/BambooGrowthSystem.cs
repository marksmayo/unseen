using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;

namespace Unseen.BattleRoyale
{
    /// <summary>
    /// Grows the spirit forest and holds everyone inside it.
    ///
    /// The forest is the visible body of the shrinking boundary. It used to grow inward from the
    /// rampart on a schedule of its own while the mist closed on a different one, which meant the
    /// two were never in the same place: the mist would be a hundred metres inside the bamboo, so
    /// the wall the player was actually being killed by was an invisible circle and the bamboo was
    /// scenery nobody could reach. Now the ring rides the mist. What closes on you is a wall of
    /// bamboo, and the mist is the band of poison in front of it.
    ///
    /// The forest sits a margin OUTSIDE the mist line rather than on it, so there is still a strip
    /// of mist you can step into and take damage in. Without that margin the bamboo would seal the
    /// mist off entirely and the damage would never fire.
    ///
    /// Position is a pure function of the mist and of match time, so a client that joins late, a
    /// server that hitches, and a headless test all agree on where the wall is without replicating
    /// anything.
    /// </summary>
    public sealed class BambooGrowthSystem : SimSystem
    {
        public override int Order => SimOrder.Mist + 5;
        public override SimRate Rate => SimRate.Base;

        private BambooForest _forest;
        private float3 _centre;
        private float _ring;
        private float _matchStart;
        private bool _running;

        private readonly Dictionary<int, float> _lastRustle = new Dictionary<int, float>(64);

        /// <summary>How far in front of the wall the culms stand, and so are audible.</summary>
        private const float RustleReach = 1.6f;

        /// <summary>How far from the centre the bamboo now stands.</summary>
        public float InnerEdge => _forest != null && _forest.IsGrown ? _forest.InnerEdge : float.MaxValue;

        /// <summary>Metres the forest has taken from the map edge. Zero before it starts.</summary>
        public float Depth => _forest != null && _forest.IsGrown
            ? math.max(0f, _ring - _forest.InnerEdge)
            : 0f;

        private MistZoneController _mist;

        /// <summary>
        /// Wires the forest to the boundary it follows.
        ///
        /// The mist is passed in rather than looked up, because the simulation's service registry
        /// holds services and not systems, and a system reaching sideways into the system list to
        /// find another one is the kind of coupling that quietly breaks when the order changes.
        /// </summary>
        public void Configure(BambooForest forest, float3 centre, float ringRadius,
            MistZoneController mist)
        {
            _forest = forest;
            _centre = centre;
            _ring = ringRadius;
            _mist = mist;
        }

        /// <summary>Restarts growth. Called when a match begins, so every match grows its own.</summary>
        public void Begin(float now)
        {
            _matchStart = now;
            _running = true;
            _lastRustle.Clear();
            _forest?.Hide();
        }

        public void Stop()
        {
            _running = false;
            _forest?.Hide();
        }

        public override void Tick(in SimFrame frame)
        {
            UnseenConfig.BambooSection cfg = Ctx.Config.Bamboo;
            if (!cfg.Enabled || !_running || _forest == null) return;
            if (Ctx.Match == null || Ctx.Match.Phase == MatchPhase.Lobby)
            {
                _forest.Hide();
                return;
            }

            float elapsed = frame.Time - _matchStart;

            // Dormant for the first stretch of the match. The town has to be worth exploring before
            // anything starts taking it away.
            if (elapsed < cfg.FirstGrowth)
            {
                _forest.Hide();
                return;
            }

            // Shoots for the first minute, rising to a full wall. After that it is simply the
            // boundary, and it goes where the boundary goes.
            float risen = math.saturate((elapsed - cfg.FirstGrowth) /
                                        math.max(0.01f, cfg.FirstBandDuration));

            float3 centre = _centre;
            float radius = _ring;

            if (_mist != null && _mist.CurrentRadius > 0f)
            {
                centre = _mist.Center;
                radius = _mist.CurrentRadius + cfg.MistMargin;
            }

            // Never outside the rampart: a ring of bamboo standing beyond the wall is invisible and
            // pointless, and at the start of a match the mist is the whole map.
            radius = math.min(radius, _ring);

            _forest.SetRing(centre, radius, risen);

            if (!_forest.IsGrown) return;

            HoldAgentsInside(cfg, frame);
        }

        /// <summary>
        /// Pushes anyone the forest has closed over back towards the middle.
        ///
        /// The collider stops a player walking in, but it cannot stop the bamboo closing around
        /// somebody who was already standing there - a collider that moves does not shove a
        /// character controller. So the wall does the shoving itself, and does it on the server
        /// where a client cannot decline.
        /// </summary>
        private void HoldAgentsInside(UnseenConfig.BambooSection cfg, in SimFrame frame)
        {
            float edge = _forest.InnerEdge;
            float3 centre = _forest.Centre;
            EntityRegistry registry = Ctx.Entities;

            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity agent = registry.BySlot(i);
                if (agent == null || !agent.IsAlive || agent.Motor == null) continue;

                float3 offset = agent.Position - centre;
                offset.y = 0f;

                float reach = math.length(offset);

                // Two thresholds, because the bamboo is two things. The culms stand over a metre
                // proud of the wall behind them, so brushing into them is heard well before
                // anything stops you - which is the point of cover you cannot use quietly.
                if (reach > edge - RustleReach) Rustle(cfg, agent, frame);
                if (reach <= edge - 0.35f) continue;

                float limit = edge - 0.35f;
                float3 inward = math.normalizesafe(offset, new float3(1f, 0f, 0f));

                // Move at the push speed, or straight to the face if that is nearer: a body deep
                // inside the wall after a teleport should not spend ten seconds oozing out of it.
                float target = math.max(limit, reach - math.max(cfg.PushSpeed * frame.Dt,
                                                                reach - limit));

                float3 corrected = centre + inward * target;
                agent.Motor.MoveDirect(new float3(corrected.x, agent.Position.y, corrected.z));
            }
        }

        /// <summary>
        /// A body against the bamboo is heard. Rate-limited per agent so pressing into it is a
        /// sound rather than a siren, and emitted through the normal acoustic model so it occludes,
        /// muffles and misleads exactly like every other noise in the game.
        /// </summary>
        private void Rustle(UnseenConfig.BambooSection cfg, AgentEntity agent, in SimFrame frame)
        {
            if (_lastRustle.TryGetValue(agent.Id.Value, out float last) &&
                frame.Time - last < cfg.RustleInterval)
                return;

            _lastRustle[agent.Id.Value] = frame.Time;
            Ctx.Sound.Emit(agent.Id, agent.Position, SoundKind.BambooRustle,
                cfg.RustleLoudness, cfg.RustleRadius, frame.Tick);
        }
    }
}
