using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.Audio
{
    /// <summary>
    /// Raycast sound propagation. Every queued sound sphere is traced to every listener inside it;
    /// each surface on the path removes part of the signal, and what survives becomes a directional
    /// ping whose accuracy degrades with occlusion. A footstep two rooms away is a vague pull in a
    /// direction, not a marker on a map.
    /// </summary>
    public sealed class AcousticPropagation : SimSystem, IDisposable
    {
        private const int MaxHeardPerListener = 16;

        private struct Path
        {
            public int EventIndex;
            public int ListenerSlot;
            public float Distance;
            public float DirectIntensity;
        }

        private readonly List<Path> _paths = new List<Path>(512);
        private readonly List<SoundEvent> _events = new List<SoundEvent>(128);

        private NativeList<int> _queryResults;
        private NativeArray<RaycastCommand> _commands;
        private NativeArray<RaycastHit> _hits;
        private int _maxHits;

        public override int Order => SimOrder.Acoustics;
        public override SimRate Rate => SimRate.Base;

        public int PathsLastTick { get; private set; }

        /// <summary>Lifetime totals. A last-tick figure cannot tell a quiet moment from a dead system.</summary>
        public long TotalPathsTraced { get; private set; }

        public long TotalSoundsDelivered { get; private set; }

        protected override void OnInitialize()
        {
            Ctx.Acoustics = this;
            _maxHits = Mathf.Clamp(Ctx.Config.Audio.MaxOccludersPerPath, 1, 12);
            int capacity = 1024;
            _queryResults = new NativeList<int>(128, Allocator.Persistent);
            _commands = new NativeArray<RaycastCommand>(capacity, Allocator.Persistent);
            _hits = new NativeArray<RaycastHit>(capacity * _maxHits, Allocator.Persistent);
        }

        public override void Tick(in SimFrame frame)
        {
            SoundEventBus bus = Ctx.Sound;
            _events.Clear();
            _events.AddRange(bus.Queued);
            bus.Swap();

            if (_events.Count == 0)
            {
                PathsLastTick = 0;
                return;
            }

            UnseenConfig.AudioSection cfg = Ctx.Config.Audio;
            EntityRegistry registry = Ctx.Entities;
            _paths.Clear();

            // Gather source-to-listener paths that are loud enough to be worth tracing.
            for (int e = 0; e < _events.Count; e++)
            {
                SoundEvent ev = _events[e];
                if (ev.Radius <= 0.01f) continue;

                Ctx.Grid.QueryRadius(ev.Position, ev.Radius, _queryResults);
                for (int i = 0; i < _queryResults.Length; i++)
                {
                    int slot = _queryResults[i];
                    AgentEntity listener = registry.BySlot(slot);
                    if (listener == null || !listener.IsAlive) continue;
                    if (listener.Id == ev.Source) continue;

                    float3 ear = listener.EyePosition;
                    float distance = math.distance(ear, ev.Position);
                    if (distance > ev.Radius) continue;

                    float direct = math.saturate(
                        UnseenMath.Falloff(distance, ev.Radius) *
                        (0.6f + 0.4f * math.min(ev.Loudness, 3f) / 3f));

                    if (direct < cfg.AudibilityFloor) continue;
                    if (_paths.Count >= _commands.Length) break;

                    _paths.Add(new Path
                    {
                        EventIndex = e,
                        ListenerSlot = slot,
                        Distance = distance,
                        DirectIntensity = direct
                    });
                }
            }

            PathsLastTick = _paths.Count;
            TotalPathsTraced += _paths.Count;
            if (_paths.Count == 0) return;

            var parameters = new QueryParameters(
                UnseenLayers.SoundBlockers, false, QueryTriggerInteraction.Ignore, false);

            for (int i = 0; i < _paths.Count; i++)
            {
                Path p = _paths[i];
                SoundEvent ev = _events[p.EventIndex];
                float3 ear = registry.BySlot(p.ListenerSlot).EyePosition;
                float3 delta = ear - ev.Position;
                float len = math.max(math.length(delta), 1e-4f);
                _commands[i] = new RaycastCommand(ev.Position, delta / len, parameters, len - 0.05f);
            }

            // Results are strided by maxHits, so the slice has to be sized the same way.
            RaycastCommand.ScheduleBatch(
                    _commands.GetSubArray(0, _paths.Count),
                    _hits.GetSubArray(0, _paths.Count * _maxHits), 16, _maxHits, default)
                .Complete();

            for (int i = 0; i < _paths.Count; i++)
            {
                Path p = _paths[i];
                SoundEvent ev = _events[p.EventIndex];
                AgentEntity listener = registry.BySlot(p.ListenerSlot);
                if (listener == null || !listener.IsAlive) continue;

                float occlusion = 0f;
                int baseIndex = i * _maxHits;
                for (int h = 0; h < _maxHits; h++)
                {
                    RaycastHit hit = _hits[baseIndex + h];
                    if (hit.collider == null) break;
                    occlusion += AcousticMaterial.AttenuationFor(hit.collider);
                    if (occlusion >= 1f) break;
                }

                occlusion = math.saturate(occlusion);
                float intensity = p.DirectIntensity * (1f - occlusion);
                if (intensity < cfg.AudibilityFloor) continue;

                // Muffled sound arrives from roughly the right direction, not the exact one.
                float error = occlusion * cfg.MuffledPositionError;
                float3 jitter = RandomInsideSphere() * error;
                float3 apparent = ev.Position + jitter;
                float3 ear = listener.EyePosition;
                float3 dir = apparent - ear;
                float dirLen = math.length(dir);
                dir = dirLen > 1e-4f ? dir / dirLen : new float3(0f, 0f, 1f);

                TotalSoundsDelivered++;
                if (listener.Heard.Count >= MaxHeardPerListener) listener.Heard.RemoveAt(0);
                listener.Heard.Add(new HeardSound
                {
                    Source = ev.Source,
                    Kind = ev.Kind,
                    Intensity = intensity,
                    Occlusion = occlusion,
                    Direction = dir,
                    ApparentPosition = apparent,
                    Tick = frame.Tick
                });
            }
        }

        private float3 RandomInsideSphere()
        {
            System.Random r = Ctx.Random;
            float3 v;
            int guard = 0;
            do
            {
                v = new float3(
                    (float)r.NextDouble() * 2f - 1f,
                    ((float)r.NextDouble() * 2f - 1f) * 0.4f,
                    (float)r.NextDouble() * 2f - 1f);
                guard++;
            } while (math.lengthsq(v) > 1f && guard < 8);

            return v;
        }

        /// <summary>Emits a footstep for an agent, folding in stance, gear and the surface underfoot.</summary>
        public void EmitFootstep(AgentEntity agent, Collider surface, int tick)
        {
            UnseenConfig.AudioSection cfg = Ctx.Config.Audio;
            bool sprinting = (agent.Flags & AgentFlags.Sprinting) != 0;

            float loudness = cfg.FootstepLoudness * Ctx.Config.StanceLoudnessScale(agent.Stance, sprinting);
            float radius = cfg.FootstepRadius * Ctx.Config.StanceLoudnessScale(agent.Stance, sprinting);

            AcousticMaterial mat = AcousticMaterial.For(surface);
            if (mat != null)
            {
                loudness *= mat.FootstepScale;
                radius *= mat.FootstepRadiusScale;
            }

            if (agent.Inventory != null)
            {
                loudness *= agent.Inventory.FootstepLoudnessScale;
                radius *= agent.Inventory.FootstepRadiusScale;
            }

            Ctx.Sound.Emit(agent.Id, agent.Position, SoundKind.Footstep, loudness, radius, tick);
        }

        public override void Shutdown()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_queryResults.IsCreated) _queryResults.Dispose();
            if (_commands.IsCreated) _commands.Dispose();
            if (_hits.IsCreated) _hits.Dispose();
        }
    }
}
