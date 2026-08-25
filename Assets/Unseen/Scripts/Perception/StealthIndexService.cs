using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.Perception
{
    /// <summary>
    /// Computes each agent stealth index (0 exposed .. 1 swallowed by shadow) on the server.
    /// Exposure is the sum of every unoccluded light reaching the agent torso; stance, motion
    /// and smoke then modify it. Clients are told their own value only - never anyone else's.
    /// </summary>
    public sealed class StealthIndexService : SimSystem, IDisposable
    {
        private const int MaxLightsPerAgent = 4;

        private struct Probe
        {
            public int Slot;
            public float Exposure;
        }

        private readonly List<Probe> _probes = new List<Probe>(256);
        private readonly List<StealthLightSource> _nearest = new List<StealthLightSource>(MaxLightsPerAgent);
        private readonly List<StealthLightSource> _candidates = new List<StealthLightSource>(32);
        private readonly StealthLightGrid _grid = new StealthLightGrid();

        /// <summary>Lights in the spatial index, for diagnostics.</summary>
        public int IndexedLights => _grid.LightCount;

        private NativeArray<RaycastCommand> _commands;
        private NativeArray<RaycastHit> _hits;
        private float[] _exposure = new float[64];

        public override int Order => SimOrder.Stealth;
        public override SimRate Rate => SimRate.Base;

        public int LightProbesLastTick { get; private set; }

        protected override void OnInitialize()
        {
            int capacity = Ctx.Buffers.Capacity * MaxLightsPerAgent * Ctx.Config.Stealth.LightSamples;
            _commands = new NativeArray<RaycastCommand>(capacity, Allocator.Persistent);
            _hits = new NativeArray<RaycastHit>(capacity, Allocator.Persistent);
            Ctx.Stealth = this;
        }

        public override void Tick(in SimFrame frame)
        {
            EntityRegistry registry = Ctx.Entities;
            int count = registry.Count;
            if (count == 0) return;

            if (_exposure.Length < count) Array.Resize(ref _exposure, count * 2);
            Array.Clear(_exposure, 0, count);
            _probes.Clear();

            UnseenConfig.StealthSection cfg = Ctx.Config.Stealth;
            int samples = Mathf.Clamp(cfg.LightSamples, 1, 4);
            var mask = new QueryParameters(UnseenLayers.LightBlockers, false, QueryTriggerInteraction.Ignore, false);

            int commandCount = 0;

            for (int slot = 0; slot < count; slot++)
            {
                AgentEntity agent = registry.BySlot(slot);
                if (!agent.IsAlive) continue;

                CollectNearestLights(agent.TorsoPosition);
                for (int li = 0; li < _nearest.Count; li++)
                {
                    StealthLightSource light = _nearest[li];
                    float raw = light.ExposureAt(agent.TorsoPosition);
                    if (raw <= 0.001f) continue;

                    for (int s = 0; s < samples; s++)
                    {
                        if (commandCount >= _commands.Length) break;

                        float3 origin = SamplePoint(agent, s, samples);
                        float3 delta = light.Position - origin;
                        float dist = math.length(delta);
                        if (dist < 0.05f) continue;

                        _commands[commandCount] = new RaycastCommand(origin, delta / dist, mask, dist - 0.05f);
                        _probes.Add(new Probe { Slot = slot, Exposure = raw / samples });
                        commandCount++;
                    }
                }
            }

            LightProbesLastTick = commandCount;

            if (commandCount > 0)
            {
                RaycastCommand.ScheduleBatch(
                        _commands.GetSubArray(0, commandCount),
                        _hits.GetSubArray(0, commandCount), 32, 1, default)
                    .Complete();
                for (int i = 0; i < commandCount; i++)
                {
                    if (_hits[i].collider != null) continue; // shadowed - this sample contributes nothing
                    Probe p = _probes[i];
                    _exposure[p.Slot] += p.Exposure;
                }
            }

            float smoothing = 1f - Mathf.Exp(-frame.Dt * Ctx.Config.Network.BaseTickRate / Mathf.Max(0.01f, cfg.SmoothingTime));
            smoothing = Mathf.Clamp01(smoothing);

            for (int slot = 0; slot < count; slot++)
            {
                AgentEntity agent = registry.BySlot(slot);
                if (!agent.IsAlive) continue;
                float target = Evaluate(agent, _exposure[slot], cfg);
                agent.StealthIndex = Mathf.Lerp(agent.StealthIndex, target, smoothing);
            }
        }

        private float Evaluate(AgentEntity agent, float exposure, UnseenConfig.StealthSection cfg)
        {
            float hidden = cfg.AmbientHiddenFloor * math.saturate(1f - exposure);

            switch (agent.Stance)
            {
                case Stance.Crouch:
                    hidden += cfg.CrouchBonus;
                    break;
                case Stance.Prone:
                    hidden += cfg.ProneBonus;
                    break;
            }

            if ((agent.Flags & AgentFlags.Sprinting) != 0) hidden -= cfg.SprintPenalty;
            if ((agent.Flags & AgentFlags.Smoked) != 0) hidden += cfg.SmokeBonus;
            if (agent.Locomotion == LocomotionState.Grapple) hidden -= 0.1f;

            if (agent.Inventory != null) hidden += agent.Inventory.StealthBonus;

            return math.saturate(hidden);
        }

        private float3 SamplePoint(AgentEntity agent, int index, int samples)
        {
            float3 torso = agent.TorsoPosition;
            if (samples <= 1) return torso;

            float height = agent.Controller != null ? agent.Controller.height : 1.8f;
            switch (index)
            {
                case 0: return torso;
                case 1: return agent.Position + new float3(0f, height * 0.92f, 0f);
                case 2: return agent.Position + new float3(0f, height * 0.15f, 0f);
                default:
                    float3 right = math.cross(new float3(0f, 1f, 0f), agent.Forward);
                    return torso + right * 0.28f;
            }
        }

        private void CollectNearestLights(float3 point)
        {
            _nearest.Clear();

            // Cell lookup rather than a scan of every light in the world. The scan cost
            // O(agents x lights) per base tick, which measured 204 ms against 10-19 ms for every
            // other system once the map carried thirteen hundred lanterns.
            _grid.EnsureBuilt();
            _candidates.Clear();
            _grid.Query(point, _candidates);

            List<StealthLightSource> all = _candidates;
            float worst = float.MaxValue;

            for (int i = 0; i < all.Count; i++)
            {
                StealthLightSource light = all[i];
                if (light == null || light.Extinguished) continue;

                float d = math.distancesq(light.Position, point);
                if (d > light.Radius * light.Radius) continue;

                if (_nearest.Count < MaxLightsPerAgent)
                {
                    _nearest.Add(light);
                    if (d < worst) worst = d;
                    continue;
                }

                // Replace the furthest entry when a closer light shows up.
                int furthest = 0;
                float furthestDist = -1f;
                for (int j = 0; j < _nearest.Count; j++)
                {
                    float dj = math.distancesq(_nearest[j].Position, point);
                    if (dj > furthestDist)
                    {
                        furthestDist = dj;
                        furthest = j;
                    }
                }

                if (d < furthestDist) _nearest[furthest] = light;
            }
        }

        /// <summary>True when this agent is dark enough to be functionally invisible at range.</summary>
        public bool IsConcealed(AgentEntity agent)
        {
            return agent.StealthIndex >= Ctx.Config.Stealth.ConcealedThreshold;
        }

        public override void Shutdown()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_commands.IsCreated) _commands.Dispose();
            if (_hits.IsCreated) _hits.Dispose();
        }
    }
}
