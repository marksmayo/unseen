using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AI;
using Unseen.BattleRoyale;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.AI
{
    /// <summary>
    /// Owns the bot population: keeps the lobby topped up to 64 entities and throttles how often
    /// each brain thinks.
    ///
    /// Who sits in which body is PlayerSeatSystem's business, not this class's. It lived here while
    /// a disconnect produced a bot, which made a kind of sense; once leaving cost a player their
    /// round instead, a class for steering AI had quietly become the one deciding whether a human
    /// was in the match.
    ///
    /// The perception aggregation that decides those tick rates runs as a Burst job over the
    /// interest sets, so the cost of having 63 bots watching each other stays off the main thread.
    /// </summary>
    public sealed class BotDirector : SimSystem
    {
        private const int MaxSpawnsPerTick = 4;
        private const int MaxPairs = 2048;

        private AgentSpawner _spawner;
        private float3 _mapCenter;
        private float _mapRadius = 300f;

        private readonly Dictionary<int, float> _nextThink = new Dictionary<int, float>(64);
        private readonly List<int> _pairBot = new List<int>(MaxPairs);

        private NativeArray<float3> _botPositions;
        private NativeArray<float3> _targetPositions;
        private NativeArray<float> _confidence;
        private NativeArray<float> _pairScores;
        private float[] _pressure = new float[128];

        public override int Order => SimOrder.BotThink;
        public override SimRate Rate => SimRate.Combat;

        public int ThinksLastTick { get; private set; }
        public int PairsScoredLastTick { get; private set; }
        public int BotsSpawnedTotal { get; private set; }

        protected override void OnInitialize()
        {
            Ctx.Bots = this;
            _botPositions = new NativeArray<float3>(MaxPairs, Allocator.Persistent);
            _targetPositions = new NativeArray<float3>(MaxPairs, Allocator.Persistent);
            _confidence = new NativeArray<float>(MaxPairs, Allocator.Persistent);
            _pairScores = new NativeArray<float>(MaxPairs, Allocator.Persistent);
        }

        public void Configure(AgentSpawner spawner, float3 mapCenter, float mapRadius)
        {
            _spawner = spawner;
            _mapCenter = mapCenter;
            _mapRadius = math.max(30f, mapRadius);
        }

        public override void Tick(in SimFrame frame)
        {
            if (!Ctx.Net.IsServer) return;

            if (frame.IsBaseTick)
            {
                MaintainPopulation();
                ScorePressure();
            }

            ThinkBots(frame);
        }

        // ---------------------------------------------------------------- population

        /// <summary>Tops the match up with bots so a half-empty queue still plays like a full lobby.</summary>
        private void MaintainPopulation()
        {
            if (_spawner == null) return;

            UnseenConfig.MatchSection cfg = Ctx.Config.Match;
            int target = cfg.TargetEntityCount;
            int maxBots = Mathf.RoundToInt(target * Mathf.Clamp01(cfg.MaxBotFraction));

            int spawned = 0;
            while (Ctx.Entities.Count < target && Ctx.Entities.BotCount < maxBots && spawned < MaxSpawnsPerTick)
            {
                BotsSpawnedTotal++;
                float3 position = RandomGroundPoint();
                _spawner.Spawn(AgentKind.Bot, -1, position, $"bot-{BotsSpawnedTotal:000}");
                spawned++;
            }
        }

        /// <summary>
        /// Somewhere to put a body: on the ground, out of the water, and somewhere it can path from.
        ///
        /// Two things were wrong with this and both put bots in the lake.
        ///
        /// It only asked for GROUND, and a riverbed is ground - solid stone under a metre and a half
        /// of water, which a downward ray reports as a perfectly good floor. Sampled four thousand
        /// times, 10.3% of placements came down in water deep enough to drown in. The roster is
        /// topped up all match, so that is not a one-off at the drop: at worst thirteen bots were
        /// stood in drownable water at once, and some bot was in it for 176 of 180 seconds. It also
        /// explains a number I could not shift by making the water unwalkable - they were not
        /// walking in, they were being put there.
        ///
        /// And the radius was uniform rather than area-uniform, which piles samples into the middle
        /// of the map: picking r evenly gives a density proportional to 1/r. The middle of this map
        /// is now a lake, so the bias aimed the bug straight at it. The square root fixes the
        /// distribution and is the whole difference between an even scatter and a crowd at the
        /// centre.
        /// </summary>
        /// <summary>
        /// Public so a probe can test the REAL placement rather than a copy of it.
        ///
        /// The first version of the spawn probe re-implemented this sampling inline and reported the
        /// same 10.3% hazard rate after the fix as before it - because it was measuring its own copy
        /// of the old algorithm. A test that reproduces the code under test proves nothing about the
        /// code under test.
        /// </summary>
        public float3 PickSpawnPoint() => RandomGroundPoint();

        private float3 RandomGroundPoint()
        {
            float3 fallback = default;
            bool haveFallback = false;

            for (int attempt = 0; attempt < 16; attempt++)
            {
                float angle = (float)Ctx.Random.NextDouble() * math.PI * 2f;

                // Area-uniform, not radius-uniform.
                float radius = math.sqrt((float)Ctx.Random.NextDouble()) * _mapRadius;

                float3 candidate = _mapCenter + new float3(math.cos(angle) * radius, 0f, math.sin(angle) * radius);

                if (!Physics.Raycast(candidate + new float3(0f, 300f, 0f), Vector3.down, out RaycastHit hit, 600f,
                        UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                    continue;

                float3 feet = (float3)hit.point + new float3(0f, 0.2f, 0f);

                // Never in water a body could drown in. Ankle deep is fine and is a legitimate
                // place to be standing.
                if (Environment.WaterVolume.DepthAt(feet) > 0.5f) continue;

                // Keep the first dry spot in hand, so a map with no NavMesh still gets an answer.
                if (!haveFallback)
                {
                    fallback = feet;
                    haveFallback = true;
                }

                // And somewhere the navigator can actually start from. This also excludes deep
                // water a second time over, since that is carved out of the mesh - but the reason
                // to ask is the general one: a body dropped somewhere unreachable can only ever
                // whisker-steer, and will spend its whole life doing it.
                if (NavMesh.SamplePosition(feet, out NavMeshHit _, 2f, NavMesh.AllAreas))
                    return feet;
            }

            if (haveFallback) return fallback;

            return _mapCenter + new float3(0f, 1f, 0f);
        }

        /// <summary>
        /// A bot whose body an arriving human can take over. Public because the seating decision
        /// belongs to PlayerSeatSystem, while which bot is expendable is a question about bots.
        /// </summary>
        public AgentEntity PickBotToReplace()
        {
            // Prefer a living bot that is not currently in a fight, so nobody inherits a losing
            // clash they never started.
            AgentEntity fallback = null;

            for (int i = 0; i < Ctx.Entities.Count; i++)
            {
                AgentEntity a = Ctx.Entities.BySlot(i);
                if (!a.IsBot) continue;

                if (a.IsAlive && !a.IsHot) return a;
                if (fallback == null) fallback = a;
            }

            return fallback;
        }

        // ---------------------------------------------------------------- perception pressure

        [BurstCompile]
        private struct PressureJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> BotPositions;
            [ReadOnly] public NativeArray<float3> TargetPositions;
            [ReadOnly] public NativeArray<float> Confidence;
            [WriteOnly] public NativeArray<float> Scores;

            public void Execute(int index)
            {
                float distance = math.max(1f, math.distance(BotPositions[index], TargetPositions[index]));
                Scores[index] = Confidence[index] * (40f / distance);
            }
        }

        /// <summary>
        /// Aggregates each bot perceived pressure off the main thread. The result drives the tick
        /// tier: a bot with someone breathing down its neck thinks often, a bot alone in the fog
        /// barely thinks at all.
        /// </summary>
        private void ScorePressure()
        {
            EntityRegistry registry = Ctx.Entities;
            int count = registry.Count;
            if (_pressure.Length < count) _pressure = new float[count * 2];
            System.Array.Clear(_pressure, 0, _pressure.Length);

            _pairBot.Clear();
            int pairs = 0;

            for (int i = 0; i < count && pairs < MaxPairs; i++)
            {
                AgentEntity bot = registry.BySlot(i);
                if (!bot.IsBot || !bot.IsAlive) continue;

                for (int v = 0; v < bot.Visible.Count && pairs < MaxPairs; v++)
                {
                    VisibleTarget target = bot.Visible[v];
                    _botPositions[pairs] = bot.Position;
                    _targetPositions[pairs] = target.Position;
                    _confidence[pairs] = target.Confidence;
                    _pairBot.Add(i);
                    pairs++;
                }
            }

            PairsScoredLastTick = pairs;
            if (pairs == 0) return;

            var job = new PressureJob
            {
                BotPositions = _botPositions,
                TargetPositions = _targetPositions,
                Confidence = _confidence,
                Scores = _pairScores
            };

            job.Schedule(pairs, 32).Complete();

            for (int i = 0; i < pairs; i++)
            {
                int slot = _pairBot[i];
                if (slot < _pressure.Length) _pressure[slot] += _pairScores[i];
            }

            for (int i = 0; i < count; i++)
            {
                AgentEntity bot = registry.BySlot(i);
                if (bot.Brain != null) bot.Brain.ThreatScore = _pressure[i];
            }
        }

        // ---------------------------------------------------------------- think scheduling

        private void ThinkBots(in SimFrame frame)
        {
            UnseenConfig.BotSection cfg = Ctx.Config.Bots;
            EntityRegistry registry = Ctx.Entities;
            int thinks = 0;

            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity bot = registry.BySlot(i);
                if (!bot.IsBot || !bot.IsAlive || bot.Brain == null || !bot.Brain.enabled) continue;

                int key = bot.Id.Value;
                float due = _nextThink.TryGetValue(key, out float t) ? t : 0f;
                if (frame.Time < due) continue;

                float rate = RateFor(bot, cfg);
                _nextThink[key] = frame.Time + 1f / math.max(1f, rate);

                bot.Brain.Think(Ctx, frame.Time, 1f / math.max(1f, rate));
                thinks++;
            }

            ThinksLastTick = thinks;
        }

        private float RateFor(AgentEntity bot, UnseenConfig.BotSection cfg)
        {
            if (bot.IsHot || bot.Brain.State == BotState.Combat) return cfg.CombatTickRate;

            bool alert = bot.Brain.ThreatScore > 0.1f ||
                         bot.Brain.State == BotState.Investigate ||
                         bot.Brain.State == BotState.Ambush ||
                         bot.Brain.State == BotState.Flee ||
                         (bot.Flags & AgentFlags.InMist) != 0;

            return alert ? cfg.AlertTickRate : cfg.IdleTickRate;
        }

        /// <summary>Diagnostics line for the server console.</summary>
        public string Describe()
        {
            return $"bots {Ctx.Entities.BotCount} thinks/tick {ThinksLastTick} pairs {PairsScoredLastTick}";
        }

        public override void Shutdown()
        {

            if (_botPositions.IsCreated) _botPositions.Dispose();
            if (_targetPositions.IsCreated) _targetPositions.Dispose();
            if (_confidence.IsCreated) _confidence.Dispose();
            if (_pairScores.IsCreated) _pairScores.Dispose();
        }
    }
}
