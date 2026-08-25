using System.Collections.Generic;
using UnityEngine;

namespace Unseen.Items
{
    /// <summary>
    /// Weighted loot definition. Rolls are made with the match seed so a replay or a re-simulation
    /// of the same match produces the same containers.
    /// </summary>
    [CreateAssetMenu(menuName = "Unseen/Loot Table", fileName = "LootTable")]
    public sealed class LootTable : ScriptableObject
    {
        [System.Serializable]
        public struct Entry
        {
            public ItemDefinition Item;

            [Min(0f)] public float Weight;

            [Tooltip("Never rolled before this stage of the match. Late gear stays late.")]
            public int MinZoneStage;
        }

        public List<Entry> Entries = new List<Entry>();

        [Tooltip("How many items a container of this table yields.")]
        [Min(1)] public int RollsPerContainer = 2;

        public ItemDefinition Roll(System.Random random, int zoneStage)
        {
            float total = 0f;
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry e = Entries[i];
                if (e.Item == null || e.MinZoneStage > zoneStage) continue;
                total += Mathf.Max(0f, e.Weight);
            }

            if (total <= 0f) return null;

            float pick = (float)random.NextDouble() * total;
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry e = Entries[i];
                if (e.Item == null || e.MinZoneStage > zoneStage) continue;
                pick -= Mathf.Max(0f, e.Weight);
                if (pick <= 0f) return e.Item;
            }

            return null;
        }
    }
}
