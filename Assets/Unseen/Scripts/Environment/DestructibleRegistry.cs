using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Items;

namespace Unseen.Environment
{
    public enum WorldEventKind : byte
    {
        ShojiSliced = 0,
        ShojiBroken = 1,
        LanternExtinguished = 2,
        SmokeSpawned = 3,
        ContainerOpened = 4
    }

    /// <summary>A world state change worth telling nearby clients about.</summary>
    public struct WorldEvent
    {
        public WorldEventKind Kind;
        public ushort TargetId;
        public float3 Position;
        public float Radius;
        public float Duration;
        public int Tick;
    }

    /// <summary>
    /// Stable identity for every destructible in the level, plus the outbound event queue.
    /// Ids are derived by sorting on position so server and client agree without any handshake.
    /// </summary>
    public sealed class DestructibleRegistry
    {
        private readonly List<ShojiPanel> _panels = new List<ShojiPanel>(512);
        private readonly List<Lantern> _lanterns = new List<Lantern>(256);
        private readonly List<LootContainer> _containers = new List<LootContainer>(256);

        private readonly Dictionary<ShojiPanel, ushort> _panelIds = new Dictionary<ShojiPanel, ushort>();
        private readonly Dictionary<Lantern, ushort> _lanternIds = new Dictionary<Lantern, ushort>();
        private readonly Dictionary<LootContainer, ushort> _containerIds = new Dictionary<LootContainer, ushort>();

        private readonly List<WorldEvent> _events = new List<WorldEvent>(64);

        public IReadOnlyList<WorldEvent> PendingEvents => _events;

        /// <summary>Snapshots the scene and assigns deterministic ids. Call once the level is loaded.</summary>
        public void BuildIndex()
        {
            _panels.Clear();
            _lanterns.Clear();
            _containers.Clear();
            _panelIds.Clear();
            _lanternIds.Clear();
            _containerIds.Clear();

            _panels.AddRange(ShojiPanel.All);
            _lanterns.AddRange(Lantern.All);
            _containers.AddRange(LootContainer.All);

            _panels.Sort(ComparePanels);
            _lanterns.Sort(CompareLanterns);
            _containers.Sort(CompareContainers);

            for (int i = 0; i < _panels.Count; i++) _panelIds[_panels[i]] = (ushort)i;
            for (int i = 0; i < _lanterns.Count; i++) _lanternIds[_lanterns[i]] = (ushort)i;
            for (int i = 0; i < _containers.Count; i++) _containerIds[_containers[i]] = (ushort)i;
        }

        private static int ComparePanels(ShojiPanel a, ShojiPanel b) => CompareByPosition(a.Position, b.Position);
        private static int CompareLanterns(Lantern a, Lantern b) => CompareByPosition(a.Position, b.Position);
        private static int CompareContainers(LootContainer a, LootContainer b) => CompareByPosition(a.Position, b.Position);

        private static int CompareByPosition(float3 a, float3 b)
        {
            int c = Quantise(a.x).CompareTo(Quantise(b.x));
            if (c != 0) return c;
            c = Quantise(a.y).CompareTo(Quantise(b.y));
            if (c != 0) return c;
            return Quantise(a.z).CompareTo(Quantise(b.z));
        }

        private static int Quantise(float v) => Mathf.RoundToInt(v * 100f);

        public ushort IdOf(ShojiPanel panel) => _panelIds.TryGetValue(panel, out ushort id) ? id : ushort.MaxValue;
        public ushort IdOf(Lantern lantern) => _lanternIds.TryGetValue(lantern, out ushort id) ? id : ushort.MaxValue;
        public ushort IdOf(LootContainer container) => _containerIds.TryGetValue(container, out ushort id) ? id : ushort.MaxValue;

        public ShojiPanel PanelById(ushort id) => id < _panels.Count ? _panels[id] : null;
        public Lantern LanternById(ushort id) => id < _lanterns.Count ? _lanterns[id] : null;
        public LootContainer ContainerById(ushort id) => id < _containers.Count ? _containers[id] : null;

        public void Raise(WorldEvent e)
        {
            _events.Add(e);
        }

        public void Raise(WorldEventKind kind, ushort target, float3 position, int tick, float radius = 0f, float duration = 0f)
        {
            _events.Add(new WorldEvent
            {
                Kind = kind,
                TargetId = target,
                Position = position,
                Radius = radius,
                Duration = duration,
                Tick = tick
            });
        }

        /// <summary>Called by the replication system once the queue has been fanned out.</summary>
        public void ClearEvents()
        {
            _events.Clear();
        }

        /// <summary>Applies a replicated world event on a client.</summary>
        public void ApplyEvent(in WorldEvent e, GameObject smokePrefab)
        {
            switch (e.Kind)
            {
                case WorldEventKind.ShojiSliced:
                    PanelById(e.TargetId)?.Slice();
                    break;
                case WorldEventKind.ShojiBroken:
                    PanelById(e.TargetId)?.Break();
                    break;
                case WorldEventKind.LanternExtinguished:
                    LanternById(e.TargetId)?.Extinguish(999f);
                    break;
                case WorldEventKind.SmokeSpawned:
                    SmokeCloud.Spawn(smokePrefab, e.Position, e.Radius, e.Duration);
                    break;
                case WorldEventKind.ContainerOpened:
                    ContainerById(e.TargetId)?.MarkLooted();
                    break;
            }
        }

        public string Describe()
        {
            return $"shoji {_panels.Count} lanterns {_lanterns.Count} containers {_containers.Count}";
        }
    }
}
