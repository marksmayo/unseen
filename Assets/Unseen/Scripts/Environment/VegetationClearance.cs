using System.Collections.Generic;
using UnityEngine;

namespace Unseen.Environment
{
    /// <summary>Snapshot of architectural clearance, indexed in 16 metre cells.</summary>
    public sealed class VegetationClearance
    {
        private const float CellSize = 16f;
        private readonly Dictionary<Vector2Int, List<Bounds>> _cells = new Dictionary<Vector2Int, List<Bounds>>();

        public VegetationClearance(Transform town)
        {
            Physics.SyncTransforms();
            foreach (BoxCollider collider in town.GetComponentsInChildren<BoxCollider>())
            {
                string name = collider.name.ToLowerInvariant();
                if (name.StartsWith("bamboowall") || collider.isTrigger) continue;
                if (!name.Contains("wall") && !name.Contains("shoji") && !name.Contains("roof")) continue;
                Bounds bounds = collider.bounds;
                bounds.Expand(.12f);
                Vector2Int min = Cell(bounds.min), max = Cell(bounds.max);
                for (int x = min.x; x <= max.x; x++)
                for (int z = min.y; z <= max.y; z++)
                {
                    var key = new Vector2Int(x, z);
                    if (!_cells.TryGetValue(key, out List<Bounds> entries))
                        _cells.Add(key, entries = new List<Bounds>());
                    entries.Add(bounds);
                }
            }
        }

        public bool Overlaps(Bounds plant)
        {
            Vector2Int min = Cell(plant.min), max = Cell(plant.max);
            for (int x = min.x; x <= max.x; x++)
            for (int z = min.y; z <= max.y; z++)
            {
                if (!_cells.TryGetValue(new Vector2Int(x, z), out List<Bounds> entries)) continue;
                for (int i = 0; i < entries.Count; i++)
                    if (plant.Intersects(entries[i])) return true;
            }
            return false;
        }

        private static Vector2Int Cell(Vector3 p) =>
            new Vector2Int(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.z / CellSize));

        public static bool IsPlant(Material material)
        {
            if (material == null) return false;
            string name = material.name.ToLowerInvariant();
            return name.Contains("leaf") || name.Contains("bamboo") || name.Contains("grass") ||
                name.Contains("reed") || name.Contains("foliage");
        }
    }
}
