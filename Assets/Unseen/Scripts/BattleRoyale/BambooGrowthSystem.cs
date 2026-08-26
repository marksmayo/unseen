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
    /// The mist and the bamboo squeeze the map in different ways on purpose. The mist is a line you
    /// can cross, at a price - running through it to reach better ground is a real decision. The
    /// bamboo is not negotiable: it takes the ground away and will not give it back, so the map
    /// genuinely shrinks rather than merely becoming expensive at the edges.
    ///
    /// Growth is a pure function of match time, so a client that joins late, a server that hitches,
    /// and a headless test all agree on how deep the forest is without replicating anything.
    /// </summary>
    public sealed class BambooGrowthSystem : SimSystem
    {
        public override int Order => SimOrder.Mist - 5;
        public override SimRate Rate => SimRate.Base;

        private BambooForest _forest;
        private float3 _centre;
        private float _ring;
        private float _matchStart;
        private bool _running;

        private readonly Dictionary<int, float> _lastRustle = new Dictionary<int, float>(64);

        /// <summary>How far in front of the solid mass the culms stand, and so are audible.</summary>
        private const float RustleReach = 1.6f;

        /// <summary>Metres the forest has taken from the map edge. Zero before it starts.</summary>
        public float Depth => _forest != null ? _forest.Depth : 0f;

        /// <summary>How far from the centre the bamboo now stands.</summary>
        public float InnerEdge => _forest != null && _forest.IsGrown ? _forest.InnerEdge : float.MaxValue;

        public void Configure(BambooForest forest, float3 centre, float ringRadius)
        {
            _forest = forest;
            _centre = centre;
            _ring = ringRadius;
        }

        /// <summary>Restarts growth. Called when a match begins, so every match grows its own.</summary>
        public void Begin(float now)
        {
            _matchStart = now;
            _running = true;
            _lastRustle.Clear();
            _forest?.SetDepth(0f, 0f);
        }

        public void Stop()
        {
            _running = false;
            _forest?.SetDepth(0f, 0f);
        }

        public override void Tick(in SimFrame frame)
        {
            UnseenConfig.BambooSection cfg = Ctx.Config.Bamboo;
            if (!cfg.Enabled || !_running || _forest == null) return;
            if (Ctx.Match == null || Ctx.Match.Phase == MatchPhase.Lobby) return;

            float elapsed = frame.Time - _matchStart;
            Advance(cfg, elapsed);

            if (!_forest.IsGrown) return;

            HoldAgentsInside(cfg, frame);
        }

        /// <summary>
        /// Works out how deep the forest is at this moment.
        ///
        /// The first band is slow - a minute of shoots coming up in front of the wall - and every
        /// band after it is quick. That shape is the point: the forest announces itself, and then
        /// it starts eating.
        /// </summary>
        private void Advance(UnseenConfig.BambooSection cfg, float elapsed)
        {
            float since = elapsed - cfg.FirstGrowth;
            if (since <= 0f)
            {
                _forest.SetDepth(0f, 0f);
                return;
            }

            float first = Mathf.Max(0.01f, cfg.FirstBandDuration);

            if (since < first)
            {
                // The first band: one metre deep, rising over the whole minute.
                _forest.SetDepth(cfg.BandDepth, since / first);
                return;
            }

            float later = Mathf.Max(0.01f, cfg.BandDuration);
            float afterFirst = since - first;

            int completeBands = 1 + Mathf.FloorToInt(afterFirst / later);
            float intoBand = (afterFirst % later) / later;

            float depth = completeBands * cfg.BandDepth + intoBand * cfg.BandDepth;
            _forest.SetDepth(depth, 1f);
        }

        /// <summary>
        /// Pushes anyone the forest has grown over back towards the middle.
        ///
        /// The collider stops a player walking in, but it cannot stop the bamboo growing around
        /// somebody who was already standing there - a collider that changes size does not shove a
        /// character controller. So the growing edge does the shoving itself, and does it on the
        /// server where a client cannot decline.
        /// </summary>
        private void HoldAgentsInside(UnseenConfig.BambooSection cfg, in SimFrame frame)
        {
            float edge = _forest.InnerEdge;
            EntityRegistry registry = Ctx.Entities;

            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity agent = registry.BySlot(i);
                if (agent == null || !agent.IsAlive || agent.Motor == null) continue;

                // The forest is a square ring, so the test is a square one: the deepest axis wins.
                float3 offset = agent.Position - _centre;
                float reach = math.max(math.abs(offset.x), math.abs(offset.z));

                // Two thresholds, because the bamboo is two things. The culms stand over a metre
                // proud of the solid mass, so brushing into them is heard well before anything
                // stops you - which is the point of cover you cannot use quietly.
                if (reach > edge - RustleReach) Rustle(cfg, agent, frame);
                if (reach <= edge - 0.35f) continue;

                float3 corrected = agent.Position;
                float push = cfg.PushSpeed * frame.Dt;

                if (math.abs(offset.x) > edge - 0.35f)
                    corrected.x = _centre.x + math.sign(offset.x) *
                        math.max(0f, math.abs(offset.x) - math.max(push, math.abs(offset.x) - (edge - 0.35f)));

                if (math.abs(offset.z) > edge - 0.35f)
                    corrected.z = _centre.z + math.sign(offset.z) *
                        math.max(0f, math.abs(offset.z) - math.max(push, math.abs(offset.z) - (edge - 0.35f)));

                agent.Motor.MoveDirect(corrected);
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
