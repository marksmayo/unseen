using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;

namespace Unseen.Items
{
    /// <summary>
    /// A chest, weapon rack or shrine offering. Opening one is a commitment: it makes noise and
    /// pins you in place for a moment, which is exactly when a watcher on the rafters strikes.
    /// </summary>
    public sealed class LootContainer : MonoBehaviour
    {
        private static readonly List<LootContainer> Containers = new List<LootContainer>(256);

        public LootTable Table;

        [Tooltip("Overrides the table roll count when non-zero.")]
        public int RollOverride;

        public float InteractRange = 1.8f;
        public float OpenLoudness = 1.3f;
        public float OpenRadius = 22f;

        [Tooltip("Optional visual that swaps in once the container has been emptied.")]
        public GameObject OpenedVisual;

        public bool Looted { get; private set; }
        public static IReadOnlyList<LootContainer> All => Containers;
        public float3 Position => transform.position;

        private readonly List<ItemDefinition> _contents = new List<ItemDefinition>(4);

        private void OnEnable()
        {
            EnsureRegistered();
        }

        /// <summary>Joins the container registry. Safe to call more than once.</summary>
        public void EnsureRegistered()
        {
            if (!Containers.Contains(this)) Containers.Add(this);
            if (gameObject.layer == 0) gameObject.layer = UnseenLayers.LootContainer;
        }

        private void OnDisable()
        {
            Containers.Remove(this);
        }

        /// <summary>Server-side roll, performed once at match start from the match seed.</summary>
        public void Populate(System.Random random, int zoneStage)
        {
            _contents.Clear();
            Looted = false;
            if (Table == null) return;

            int rolls = RollOverride > 0 ? RollOverride : Table.RollsPerContainer;
            for (int i = 0; i < rolls; i++)
            {
                ItemDefinition item = Table.Roll(random, zoneStage);
                if (item != null) _contents.Add(item);
            }
        }

        /// <summary>Transfers whatever the taker can carry. Returns the number of items actually taken.</summary>
        public int TakeAll(Inventory inventory)
        {
            if (Looted || inventory == null) return 0;

            int taken = 0;
            for (int i = 0; i < _contents.Count; i++)
                if (inventory.TryAdd(_contents[i])) taken++;

            _contents.Clear();
            MarkLooted();
            return taken;
        }

        public void MarkLooted()
        {
            Looted = true;
            if (OpenedVisual != null) OpenedVisual.SetActive(true);
        }

        public static LootContainer NearestUnlooted(float3 point, float maxDistance)
        {
            LootContainer best = null;
            float bestDist = maxDistance * maxDistance;

            for (int i = 0; i < Containers.Count; i++)
            {
                LootContainer c = Containers[i];
                if (c == null || c.Looted) continue;
                float d = math.distancesq(c.Position, point);
                if (d >= bestDist) continue;
                bestDist = d;
                best = c;
            }

            return best;
        }
    }
}
