using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.Perception
{
    public struct LosResult
    {
        public VisibilityKind Kind;

        /// <summary>Where the observer resolved the target, or the last known point for a stale entry.</summary>
        public float3 SeenAt;

        /// <summary>Simulation time this result was produced.</summary>
        public float Time;

        /// <summary>0..1 how well the target is resolved. Silhouettes are always low.</summary>
        public float Confidence;

        /// <summary>Simulation time of the last positive sighting, used for the visibility linger window.</summary>
        public float LastPositiveTime;

        public VisibilityKind LastPositiveKind;
    }

    /// <summary>
    /// Server-authoritative visibility. Every pair goes through three gates:
    /// range (scaled by the target stealth index), view frustum, then a batched physics raycast.
    /// Two raycast passes run per tick: opaque geometry decides "can I see anything at all",
    /// shoji paper decides "do I see a person or just a shape".
    /// </summary>
    public sealed class LineOfSightService : IDisposable
    {
        private struct Pending
        {
            public long Key;
            public int ObserverSlot;
            public int TargetSlot;
            public float3 Origin;
            public float3 Target;
            public float Distance;
            public float Confidence;
        }

        private readonly UnseenConfig _config;
        private readonly int _budget;

        private readonly Dictionary<long, LosResult> _cache;
        private readonly List<Pending> _pending;

        private NativeArray<RaycastCommand> _commands;
        private NativeArray<RaycastHit> _hits;

        private float _time;

        public int PairsTestedLastTick { get; private set; }
        public int PairsSkippedByBudget { get; private set; }
        public int RaycastsLastTick { get; private set; }

        /// <summary>Maximum distance at which a shoji screen still resolves a readable silhouette.</summary>
        public float SilhouetteRange = 16f;

        public LineOfSightService(UnseenConfig config)
        {
            _config = config;
            _budget = Mathf.Max(64, config.Interest.LosRaycastBudget);
            _cache = new Dictionary<long, LosResult>(1024);
            _pending = new List<Pending>(_budget);
            _commands = new NativeArray<RaycastCommand>(_budget, Allocator.Persistent);
            _hits = new NativeArray<RaycastHit>(_budget, Allocator.Persistent);
        }

        public static long PairKey(AgentId observer, AgentId target)
        {
            return ((long)observer.Value << 32) | (uint)target.Value;
        }

        public void BeginBatch(float time)
        {
            _time = time;
            _pending.Clear();
            PairsTestedLastTick = 0;
            PairsSkippedByBudget = 0;
            RaycastsLastTick = 0;
        }

        /// <summary>Effective sight range for one observer against one target, after stealth and night vision.</summary>
        public float EffectiveRange(AgentEntity observer, AgentEntity target)
        {
            float range = _config.Interest.MaxSightRange;
            if (observer.Inventory != null && observer.Inventory.HasNightVision)
                range += _config.Interest.NightVisionBonus;

            float hidden = math.saturate(target.StealthIndex);
            return range * math.max(0.08f, 1f - hidden * _config.Stealth.StealthRangeScale);
        }

        /// <summary>Range inside which the awareness cone widens; see PassesGate.</summary>
        private const float PointBlankRange = 2.5f;

        /// <summary>
        /// Cosine of the half-angle of the widened point-blank cone: 240 degrees of awareness,
        /// leaving a 120 degree blind arc behind. Wider than CombatSection.TakedownRearArc (110),
        /// so a legal takedown angle is always one perception genuinely misses.
        /// </summary>
        private const float PointBlankCos = -0.5f;

        /// <summary>Cheap gate: range plus view frustum. Returns false for pairs that never need a raycast.</summary>
        public bool PassesGate(AgentEntity observer, AgentEntity target, out float distance, out float confidence)
        {
            distance = 0f;
            confidence = 0f;

            float3 delta = target.TorsoPosition - observer.EyePosition;
            float distSq = math.lengthsq(delta);
            float range = EffectiveRange(observer, target);
            if (distSq > range * range) return false;

            distance = math.sqrt(math.max(distSq, 1e-6f));
            float3 dir = delta / distance;

            float3 view = observer.ViewDirection;
            float cosLimit = math.cos(_config.Interest.ViewFieldOfView * 0.5f * UnseenMath.Deg2Rad);
            float dot = math.dot(view, dir);

            // Point-blank contact WIDENS the cone rather than removing it.
            //
            // This used to be "anything within 2.5 m is noticed, whatever you are facing", which
            // reads as sensible on its own and is fatal in combination with the takedown rules: a
            // takedown has to happen inside 1.6 m, so every possible victim was already inside the
            // point-blank radius, was therefore always seeing their attacker, and was therefore
            // never takedown-eligible. The feature could not fire, and never had.
            //
            // A rear blind arc is the whole premise of a silent kill. It is kept wider than
            // CombatSection.TakedownRearArc on purpose, so the two rules cannot contradict each
            // other again: everything the takedown accepts is inside what perception misses.
            float effectiveCos = distance <= PointBlankRange ? PointBlankCos : cosLimit;
            if (dot < effectiveCos) return false;

            float pitchDelta = math.abs(math.degrees(math.asin(math.clamp(dir.y, -1f, 1f))) - (-observer.Pitch));
            if (pitchDelta > _config.Interest.ViewPitchTolerance && distance > PointBlankRange) return false;

            float rangeTerm = 1f - math.saturate(distance / math.max(1f, range));
            float centering = math.saturate((dot - cosLimit) / math.max(0.001f, 1f - cosLimit));
            confidence = math.saturate(0.35f + 0.5f * rangeTerm + 0.25f * centering);
            return true;
        }

        /// <summary>True when a cached result is still inside its lifetime.</summary>
        public bool IsFresh(AgentId observer, AgentId target)
        {
            return _cache.TryGetValue(PairKey(observer, target), out LosResult r) &&
                   _time - r.Time <= _config.Interest.LosCacheLifetime;
        }

        public bool TryGet(AgentId observer, AgentId target, out LosResult result)
        {
            return _cache.TryGetValue(PairKey(observer, target), out result);
        }

        /// <summary>Queues a pair for this tick raycast batch. Silently drops the pair once the budget is spent.</summary>
        public void Enqueue(AgentEntity observer, AgentEntity target, float distance, float confidence)
        {
            PairsTestedLastTick++;
            if (_pending.Count >= _budget)
            {
                PairsSkippedByBudget++;
                return;
            }

            _pending.Add(new Pending
            {
                Key = PairKey(observer.Id, target.Id),
                ObserverSlot = observer.Slot,
                TargetSlot = target.Slot,
                Origin = observer.EyePosition,
                Target = target.TorsoPosition,
                Distance = distance,
                Confidence = confidence
            });
        }

        /// <summary>Runs the batched raycasts and folds the results into the cache.</summary>
        public void Resolve(WorldBuffers buffers)
        {
            int n = _pending.Count;
            if (n == 0) return;

            // Pass 1 - opaque geometry. Anything hit here means the target is simply not there.
            var opaqueParams = new QueryParameters(
                (1 << UnseenLayers.Default) | (1 << UnseenLayers.Occluder),
                false, QueryTriggerInteraction.Ignore, false);

            for (int i = 0; i < n; i++)
            {
                Pending p = _pending[i];
                float3 dir = (p.Target - p.Origin) / math.max(p.Distance, 1e-4f);
                _commands[i] = new RaycastCommand(p.Origin, dir, opaqueParams, p.Distance - 0.05f);
            }

            // ScheduleBatch walks the whole array, so slice it to the commands actually written -
            // otherwise every spare slot becomes a junk raycast.
            RaycastCommand.ScheduleBatch(_commands.GetSubArray(0, n), _hits.GetSubArray(0, n), 32, 1, default)
                .Complete();
            RaycastsLastTick += n;

            // Pass 2 - paper only, for the pairs that survived. A paper wall downgrades a
            // person to an anonymous silhouette instead of hiding them outright.
            var paperParams = new QueryParameters(
                1 << UnseenLayers.ShojiPaper, false, QueryTriggerInteraction.Ignore, false);

            int paperCount = 0;
            var survivors = new NativeArray<int>(n, Allocator.Temp);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    if (_hits[i].collider != null)
                    {
                        Store(_pending[i], VisibilityKind.None, 0f);
                        continue;
                    }

                    survivors[paperCount] = i;
                    Pending p = _pending[i];
                    float3 dir = (p.Target - p.Origin) / math.max(p.Distance, 1e-4f);
                    _commands[paperCount] = new RaycastCommand(p.Origin, dir, paperParams, p.Distance - 0.05f);
                    paperCount++;
                }

                if (paperCount > 0)
                {
                    RaycastCommand.ScheduleBatch(
                            _commands.GetSubArray(0, paperCount),
                            _hits.GetSubArray(0, paperCount), 32, 1, default)
                        .Complete();
                    RaycastsLastTick += paperCount;

                    for (int i = 0; i < paperCount; i++)
                    {
                        Pending p = _pending[survivors[i]];
                        bool paper = _hits[i].collider != null;

                        if (!paper)
                        {
                            Store(p, VisibilityKind.Direct, p.Confidence);
                            continue;
                        }

                        // A silhouette needs backlight and proximity: a ninja standing in an unlit
                        // room behind paper prints nothing on the screen.
                        float exposure = 1f - buffers.Stealth[p.TargetSlot];
                        bool readable = p.Distance <= SilhouetteRange && exposure > 0.35f;
                        Store(p, readable ? VisibilityKind.Silhouette : VisibilityKind.None,
                            readable ? math.min(0.35f, p.Confidence * exposure) : 0f);
                    }
                }
            }
            finally
            {
                survivors.Dispose();
            }

            _pending.Clear();
        }

        private void Store(Pending p, VisibilityKind kind, float confidence)
        {
            if (!_cache.TryGetValue(p.Key, out LosResult old))
            {
                old.LastPositiveTime = float.NegativeInfinity;
                old.SeenAt = p.Target;
            }

            bool positive = kind != VisibilityKind.None;

            _cache[p.Key] = new LosResult
            {
                Kind = kind,
                // A lost target freezes at its last known point. The server never leaks a
                // position the observer has not actually earned.
                SeenAt = positive ? p.Target : old.SeenAt,
                Time = _time,
                Confidence = confidence,
                LastPositiveTime = positive ? _time : old.LastPositiveTime,
                LastPositiveKind = positive ? kind : old.LastPositiveKind
            };
        }

        /// <summary>Drops every cached pair that mentions this entity. Called on death and disconnect.</summary>
        public void Forget(AgentId id)
        {
            var doomed = new List<long>();
            foreach (KeyValuePair<long, LosResult> kv in _cache)
            {
                int observer = (int)(kv.Key >> 32);
                int target = (int)(kv.Key & 0xFFFFFFFF);
                if (observer == id.Value || target == id.Value) doomed.Add(kv.Key);
            }

            for (int i = 0; i < doomed.Count; i++) _cache.Remove(doomed[i]);
        }

        public void Clear()
        {
            _cache.Clear();
            _pending.Clear();
        }

        public void Dispose()
        {
            if (_commands.IsCreated) _commands.Dispose();
            if (_hits.IsCreated) _hits.Dispose();
            _cache.Clear();
        }
    }
}
