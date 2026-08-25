using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.Perception
{
    /// <summary>
    /// Builds, per observer, the set of agents that observer is allowed to know about this tick.
    /// Everything downstream - replication, bot decisions, the HUD - reads only this.
    /// An agent that is not in the set does not exist as far as that client is concerned:
    /// no transform, no packet, nothing for a memory scraper to find.
    /// </summary>
    public sealed class InterestManager : SimSystem
    {
        private struct Candidate
        {
            public int ObserverSlot;
            public int TargetSlot;
        }

        private readonly List<Candidate> _candidates = new List<Candidate>(2048);
        private NativeList<int> _queryResults;

        public override int Order => SimOrder.LineOfSight;
        public override SimRate Rate => SimRate.Base;

        public int VisiblePairsLastTick { get; private set; }
        public int CandidatePairsLastTick { get; private set; }

        protected override void OnInitialize()
        {
            Ctx.Sight = new LineOfSightService(Ctx.Config);
            Ctx.Interest = this;
            _queryResults = new NativeList<int>(128, Allocator.Persistent);

            Ctx.Entities.Unregistered += a => Ctx.Sight.Forget(a.Id);
        }

        public override void Tick(in SimFrame frame)
        {
            EntityRegistry registry = Ctx.Entities;
            LineOfSightService sight = Ctx.Sight;
            UnseenConfig cfg = Ctx.Config;

            int count = registry.Count;
            if (count == 0) return;

            sight.BeginBatch(frame.Time);
            _candidates.Clear();

            float queryRadius = math.min(
                cfg.Network.ReplicationRadius,
                cfg.Interest.MaxSightRange + cfg.Interest.NightVisionBonus);

            // Pass 1 - gather every pair worth a raycast, and queue the stale ones.
            for (int slot = 0; slot < count; slot++)
            {
                AgentEntity observer = registry.BySlot(slot);
                if (!observer.IsAlive) continue;

                Ctx.Grid.QueryRadius(observer.EyePosition, queryRadius, _queryResults);

                for (int i = 0; i < _queryResults.Length; i++)
                {
                    int targetSlot = _queryResults[i];
                    if (targetSlot == slot) continue;

                    AgentEntity target = registry.BySlot(targetSlot);
                    if (target == null || !target.IsAlive) continue;

                    if (!sight.PassesGate(observer, target, out float distance, out float confidence))
                        continue;

                    _candidates.Add(new Candidate { ObserverSlot = slot, TargetSlot = targetSlot });

                    if (!sight.IsFresh(observer.Id, target.Id))
                        sight.Enqueue(observer, target, distance, confidence);
                }
            }

            CandidatePairsLastTick = _candidates.Count;
            sight.Resolve(Ctx.Buffers);

            // Pass 2 - fold the resolved cache into per-observer knowledge.
            for (int slot = 0; slot < count; slot++)
            {
                AgentEntity observer = registry.BySlot(slot);
                if (observer.IsAlive) observer.Visible.Clear();
            }

            float linger = cfg.Interest.VisibilityLinger;
            int visiblePairs = 0;

            for (int i = 0; i < _candidates.Count; i++)
            {
                Candidate c = _candidates[i];
                AgentEntity observer = registry.BySlot(c.ObserverSlot);
                AgentEntity target = registry.BySlot(c.TargetSlot);
                if (observer == null || target == null || !observer.IsAlive || !target.IsAlive) continue;

                if (!sight.TryGet(observer.Id, target.Id, out LosResult r)) continue;

                if (r.Kind != VisibilityKind.None)
                {
                    observer.Visible.Add(new VisibleTarget
                    {
                        Id = target.Id,
                        Kind = r.Kind,
                        Position = r.SeenAt,
                        Confidence = r.Confidence,
                        LastSeenTime = r.Time
                    });

                    // Direct sight is what makes a victim "aware" and therefore immune to a takedown.
                    if ((r.Kind & VisibilityKind.Direct) != 0)
                        observer.NoteSaw(target.Id, frame.Time);

                    visiblePairs++;
                    continue;
                }

                float age = frame.Time - r.LastPositiveTime;
                if (r.LastPositiveKind == VisibilityKind.None || age > linger) continue;

                // Linger window: the target keeps its last resolved position (never a live one)
                // so a client does not pop them out of existence behind a single pillar.
                observer.Visible.Add(new VisibleTarget
                {
                    Id = target.Id,
                    Kind = r.LastPositiveKind,
                    Position = r.SeenAt,
                    Confidence = r.Confidence * math.saturate(1f - age / math.max(0.01f, linger)),
                    LastSeenTime = r.LastPositiveTime
                });
                visiblePairs++;
            }

            VisiblePairsLastTick = visiblePairs;
        }

        /// <summary>Diagnostics line for the server console and the client debug overlay.</summary>
        public string DescribeLoad()
        {
            LineOfSightService s = Ctx.Sight;
            return $"pairs {CandidatePairsLastTick} visible {VisiblePairsLastTick} " +
                   $"rays {s.RaycastsLastTick} dropped {s.PairsSkippedByBudget}";
        }

        public override void Shutdown()
        {
            if (_queryResults.IsCreated) _queryResults.Dispose();
            Ctx.Sight?.Dispose();
        }
    }
}
