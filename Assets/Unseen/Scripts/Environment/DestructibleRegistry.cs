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
        ContainerOpened = 4,

        /// <summary>A lantern lit again. Appended, never renumbered: this travels as a byte.</summary>
        LanternRelit = 5
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

            // Filtered rather than copied wholesale, because the static registries can contain
            // objects that no longer exist.
            //
            // A panel joins the list in OnEnable and leaves it in OnDisable, and neither runs in
            // edit mode - which is why the generator calls EnsureRegistered directly. There is no
            // matching call for the other direction, so a tool that builds a town and throws it
            // away leaves the destroyed panels behind, and the next index sorts a list of corpses.
            // The comparison then dereferences one and the whole sort throws.
            //
            // Cheap to be robust about, and it protects the running game too: anything destroyed
            // at runtime between one index and the next would do exactly the same thing.
            for (int i = 0; i < ShojiPanel.All.Count; i++)
                if (ShojiPanel.All[i] != null) _panels.Add(ShojiPanel.All[i]);

            for (int i = 0; i < Lantern.All.Count; i++)
                if (Lantern.All[i] != null) _lanterns.Add(Lantern.All[i]);

            for (int i = 0; i < LootContainer.All.Count; i++)
                if (LootContainer.All[i] != null) _containers.Add(LootContainer.All[i]);

            _panels.Sort(ComparePanels);
            _lanterns.Sort(CompareLanterns);
            _containers.Sort(CompareContainers);

            for (int i = 0; i < _panels.Count; i++) _panelIds[_panels[i]] = (ushort)i;
            for (int i = 0; i < _lanterns.Count; i++) _lanternIds[_lanterns[i]] = (ushort)i;
            for (int i = 0; i < _containers.Count; i++) _containerIds[_containers[i]] = (ushort)i;

            WarnAboutInseparablePairs();
        }

        /// <summary>
        /// Complains if any two destructibles sort equal.
        ///
        /// At that point the numbering is decided by whatever the sort left behind, and the sort is
        /// not a stable one - so two machines building the same town need not agree, and nothing
        /// downstream would ever say so. The server would break panel 47 and the client would open
        /// a different wall: a free look into a room, in a game about not being seen, reported by
        /// whoever noticed as walls falling apart on their own.
        ///
        /// A warning rather than a refusal. It is a fault in the level rather than in the code that
        /// numbers it, and a town with one coincident pair is still playable - but it must not pass
        /// in silence, because the symptom is so far from the cause.
        /// </summary>
        private void WarnAboutInseparablePairs()
        {
            int pairs = 0;

            for (int i = 1; i < _panels.Count; i++)
                if (ComparePanels(_panels[i - 1], _panels[i]) == 0) pairs++;

            for (int i = 1; i < _lanterns.Count; i++)
                if (CompareLanterns(_lanterns[i - 1], _lanterns[i]) == 0) pairs++;

            for (int i = 1; i < _containers.Count; i++)
                if (CompareContainers(_containers[i - 1], _containers[i]) == 0) pairs++;

            if (pairs == 0) return;

            Debug.LogWarning(
                $"[Unseen] {pairs} pair(s) of destructibles occupy the same pose. Their ids are " +
                "decided by an unstable sort, so a server and a client may not agree on which is " +
                "which, and a world event could break the wrong one.");
        }

        private static int ComparePanels(ShojiPanel a, ShojiPanel b) =>
            CompareByPose(a.Position, a.transform.forward, b.Position, b.transform.forward);

        private static int CompareLanterns(Lantern a, Lantern b) =>
            CompareByPose(a.Position, a.transform.forward, b.Position, b.transform.forward);

        private static int CompareContainers(LootContainer a, LootContainer b) =>
            CompareByPose(a.Position, a.transform.forward, b.Position, b.transform.forward);

        /// <summary>
        /// Orders two destructibles the same way on every machine, by where they are and which way
        /// they face.
        ///
        /// Position alone is not enough, and the town proves it: around a hundred shoji panels per
        /// seed share a centre to the nearest centimetre, because a doorway has a panel on each
        /// face of the wall and both are centred on the same point. For every one of those the
        /// comparison returned "equal", and the sort behind it is not a stable one - so the order
        /// of a tied pair was whatever the sort happened to leave.
        ///
        /// It agreed between two builds anyway, which is exactly what made it dangerous: it agrees
        /// as long as both sides feed the sort an identically ordered list, and a dedicated server
        /// does not build the same objects as a client. The first time the two lists differed, a
        /// hundred panels would have silently swapped identities, and the server saying "panel 47
        /// is broken" would have opened a different wall on somebody's screen - a free look into a
        /// room, in a game about not being seen, reported as walls falling apart on their own.
        ///
        /// Facing separates the pair because the two panels of a doorway look opposite ways. It is
        /// as deterministic as the position: both come from the same generated transform.
        /// </summary>
        private static int CompareByPose(float3 aPosition, float3 aForward,
            float3 bPosition, float3 bForward)
        {
            int c = Quantise(aPosition.x).CompareTo(Quantise(bPosition.x));
            if (c != 0) return c;
            c = Quantise(aPosition.y).CompareTo(Quantise(bPosition.y));
            if (c != 0) return c;
            c = Quantise(aPosition.z).CompareTo(Quantise(bPosition.z));
            if (c != 0) return c;

            // Coarser than the position, deliberately. A facing is a direction rather than a place,
            // and quantising it to a hundredth would make the order depend on floating-point noise
            // in a normal that ought to be identical on both sides but need not be bit-for-bit.
            c = QuantiseDirection(aForward.x).CompareTo(QuantiseDirection(bForward.x));
            if (c != 0) return c;
            c = QuantiseDirection(aForward.y).CompareTo(QuantiseDirection(bForward.y));
            if (c != 0) return c;
            return QuantiseDirection(aForward.z).CompareTo(QuantiseDirection(bForward.z));
        }

        private static int Quantise(float v) => Mathf.RoundToInt(v * 100f);

        private static int QuantiseDirection(float v) => Mathf.RoundToInt(v * 64f);

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
                case WorldEventKind.LanternRelit:
                    LanternById(e.TargetId)?.Relight();
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
