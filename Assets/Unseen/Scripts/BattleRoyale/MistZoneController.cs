using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.BattleRoyale
{
    /// <summary>
    /// The Curse of the Shadow. A cylinder of lethal mist closes in stages, each with a hold and a
    /// close phase. Because it drives players inward and upward into paper-walled interiors, it is
    /// also the pacing lever for the whole stealth loop: the later the stage, the less darkness
    /// there is to hide in.
    /// </summary>
    public sealed class MistZoneController : SimSystem
    {
        public enum ZonePhase : byte
        {
            Waiting = 0,
            Holding = 1,
            Closing = 2,
            Final = 3
        }

        private float3 _center;
        private float3 _nextCenter;
        private float _radius;
        private float _nextRadius;
        private float _initialRadius;
        private float _phaseEnd;

        public ZonePhase Phase { get; private set; } = ZonePhase.Waiting;
        public int Stage { get; private set; }
        public float3 Center => _center;
        public float CurrentRadius => _radius;
        public float3 NextCenter => _nextCenter;
        public float NextRadius => _nextRadius;
        public float SecondsToPhaseEnd { get; private set; }

        public override int Order => SimOrder.Mist;
        public override SimRate Rate => SimRate.Base;

        protected override void OnInitialize()
        {
            Ctx.Mist = this;
        }

        /// <summary>Resets the zone for a new match, centred on the map.</summary>
        public void Begin(float3 mapCenter, float time)
        {
            UnseenConfig.MatchSection cfg = Ctx.Config.Match;

            // The first circle sits ON the map, not around it.
            //
            // It used to start at 1.15x the map radius, which on a 375 m town meant a 431 m circle
            // enclosing the whole world with 56 m to spare. Combined with the old taper, the first
            // three stages were all larger than the map and the mist did nothing at all for the
            // opening nine minutes of a twenty minute match.
            float mapRadius = Ctx.Match != null ? Ctx.Match.MapRadius : cfg.InitialZoneRadius;
            float initial = math.min(cfg.InitialZoneRadius, mapRadius);

            _center = mapCenter;
            _nextCenter = mapCenter;
            _radius = initial;
            _nextRadius = initial;
            _initialRadius = initial;
            Stage = 0;
            Phase = ZonePhase.Waiting;
            _phaseEnd = time + cfg.FirstZoneDelay;
        }

        public override void Tick(in SimFrame frame)
        {
            if (Ctx.Match == null || Ctx.Match.Phase == MatchPhase.Lobby) return;

            UnseenConfig.MatchSection cfg = Ctx.Config.Match;
            SecondsToPhaseEnd = math.max(0f, _phaseEnd - frame.Time);

            switch (Phase)
            {
                case ZonePhase.Waiting:
                case ZonePhase.Holding:
                    if (frame.Time >= _phaseEnd) BeginClosing(cfg, frame.Time);
                    break;

                case ZonePhase.Closing:
                    float remaining = math.max(0f, _phaseEnd - frame.Time);
                    float t = cfg.ZoneCloseDuration <= 0f ? 1f : 1f - remaining / cfg.ZoneCloseDuration;
                    _radius = math.lerp(RadiusForStage(cfg, Stage - 1), _nextRadius, math.saturate(t));
                    _center = math.lerp(_center, _nextCenter, math.saturate(frame.Dt * 2f));

                    if (frame.Time >= _phaseEnd)
                    {
                        _radius = _nextRadius;
                        _center = _nextCenter;
                        Phase = Stage >= cfg.ZoneStages ? ZonePhase.Final : ZonePhase.Holding;
                        _phaseEnd = frame.Time + cfg.ZoneHoldDuration;
                    }

                    break;
            }

            ApplyMistDamage(frame, cfg);
        }

        private void BeginClosing(UnseenConfig.MatchSection cfg, float now)
        {
            if (Stage >= cfg.ZoneStages)
            {
                Phase = ZonePhase.Final;
                _phaseEnd = now + cfg.ZoneHoldDuration;
                return;
            }

            Stage++;
            _nextRadius = RadiusForStage(cfg, Stage);

            // The next circle is drawn inside the current one, biased so it can favour dense
            // interior blocks rather than open courtyard.
            float maxDrift = math.max(0f, _radius - _nextRadius);
            float angle = (float)Ctx.Random.NextDouble() * math.PI * 2f;
            float drift = (float)Ctx.Random.NextDouble() * maxDrift * 0.75f;
            _nextCenter = _center + new float3(math.cos(angle) * drift, 0f, math.sin(angle) * drift);

            Phase = ZonePhase.Closing;
            _phaseEnd = now + cfg.ZoneCloseDuration;
        }

        private float RadiusForStage(UnseenConfig.MatchSection cfg, int stage)
        {
            if (stage <= 0) return _initialRadius;
            if (stage >= cfg.ZoneStages) return cfg.FinalZoneRadius;

            // Every stage has to take something away.
            //
            // The old t-squared taper removed almost nothing early - stage one of seven shrank the
            // circle by two per cent - so the first several stages passed without pressuring
            // anyone. An exponent below one front-loads the squeeze instead: on a 375 m map the
            // stages now run 285, 229, 181, 137, 96, 61, 28, and each one is a decision.
            float t = stage / (float)cfg.ZoneStages;
            float eased = math.pow(t, 0.7f);
            return math.lerp(_initialRadius, cfg.FinalZoneRadius, eased);
        }

        private void ApplyMistDamage(in SimFrame frame, UnseenConfig.MatchSection cfg)
        {
            float dps = cfg.MistDamagePerSecond * math.pow(cfg.MistDamageGrowth, math.max(0, Stage - 1));
            float dt = Ctx.Config.BaseTickInterval;
            EntityRegistry registry = Ctx.Entities;

            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity agent = registry.BySlot(i);
                if (!agent.IsAlive) continue;

                // Nobody burns while still under a glider. The drop line crosses the whole map, so
                // half the lobby is outside the first circle by definition on the way down, and
                // punishing them for the flight path they were given is not a decision they made.
                if ((agent.Flags & AgentFlags.Deployed) == 0) continue;

                float2 planar = new float2(agent.Position.x - _center.x, agent.Position.z - _center.z);
                bool outside = math.length(planar) > _radius;

                if (outside) agent.Flags |= AgentFlags.InMist;
                else agent.Flags &= ~AgentFlags.InMist;

                if (!outside) continue;

                Ctx.Combat.ApplyDamage(new DamageInfo
                {
                    Attacker = AgentId.None,
                    Victim = agent.Id,
                    Kind = DamageKind.Mist,
                    Amount = dps * dt,
                    Point = agent.TorsoPosition,
                    Direction = math.normalizesafe(new float3(planar.x, 0f, planar.y))
                });
            }
        }

        /// <summary>Nearest safe point inside the current circle, used by bot navigation.</summary>
        public float3 NearestSafePoint(float3 from, float margin = 8f)
        {
            float2 planar = new float2(from.x - _center.x, from.z - _center.z);
            float distance = math.length(planar);
            float safe = math.max(4f, _radius - margin);
            if (distance <= safe) return from;

            float2 dir = distance > 0.001f ? planar / distance : new float2(1f, 0f);
            return new float3(_center.x + dir.x * safe, from.y, _center.z + dir.y * safe);
        }

        public bool IsInside(float3 point)
        {
            float2 planar = new float2(point.x - _center.x, point.z - _center.z);
            return math.lengthsq(planar) <= _radius * _radius;
        }
    }
}
