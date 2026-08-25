using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Unseen.Perception
{
    /// <summary>
    /// Spatial index over the lights that affect the stealth index.
    ///
    /// <see cref="StealthIndexService"/> used to scan every light in the world once per agent per
    /// base tick. That was fine at ninety-four lanterns and became the single most expensive thing
    /// in the simulation at thirteen hundred: measured at 204 ms per tick, against 10-19 ms for
    /// every other system combined. The scan is O(agents x lights) and the map grew the second
    /// term fourteen-fold.
    ///
    /// Lanterns never move, so the index is built once and only rebuilt when the light count
    /// changes. Each light is inserted into every cell its sphere of influence overlaps, which
    /// means a lookup only ever has to read the one cell the query point falls in - no neighbour
    /// ring, no radius arithmetic at query time.
    ///
    /// Positions are cached in the cell entries deliberately: <c>StealthLightSource.Position</c>
    /// reads <c>transform.position</c>, and a quarter of a million native transform reads per tick
    /// was a meaningful share of the cost on its own.
    /// </summary>
    public sealed class StealthLightGrid
    {
        private struct Entry
        {
            public float3 Position;
            public float RadiusSq;
            public StealthLightSource Source;
        }

        /// <summary>
        /// Cell edge in metres. Comfortably larger than a lantern's reach, so a light lands in a
        /// handful of cells rather than a swathe of them.
        /// </summary>
        private const float CellSize = 16f;

        private readonly Dictionary<long, List<Entry>> _cells = new Dictionary<long, List<Entry>>(512);
        private int _builtFrom = -1;

        public int LightCount { get; private set; }
        public int CellCount => _cells.Count;

        /// <summary>Rebuilds if the set of lights has changed size since the last build.</summary>
        public void EnsureBuilt()
        {
            IReadOnlyList<StealthLightSource> all = StealthLightSource.All;
            if (_builtFrom == all.Count) return;

            Build(all);
        }

        /// <summary>Forces a rebuild, e.g. after a level is regenerated in place.</summary>
        public void Invalidate()
        {
            _builtFrom = -1;
        }

        private void Build(IReadOnlyList<StealthLightSource> all)
        {
            _cells.Clear();
            LightCount = 0;

            for (int i = 0; i < all.Count; i++)
            {
                StealthLightSource light = all[i];
                if (light == null) continue;

                float3 position = light.Position;
                float radius = math.max(0.1f, light.Radius);

                var entry = new Entry
                {
                    Position = position,
                    RadiusSq = radius * radius,
                    Source = light
                };

                // Insert into every cell the light can reach, so a query reads one cell.
                int3 min = CellOf(position - radius);
                int3 max = CellOf(position + radius);

                for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                for (int z = min.z; z <= max.z; z++)
                {
                    long key = Key(new int3(x, y, z));
                    if (!_cells.TryGetValue(key, out List<Entry> bucket))
                    {
                        bucket = new List<Entry>(8);
                        _cells[key] = bucket;
                    }

                    bucket.Add(entry);
                }

                LightCount++;
            }

            _builtFrom = all.Count;
            Debug.Log($"[Unseen] stealth light grid: {LightCount} lights across {_cells.Count} cells " +
                      $"of {CellSize:0} m");
        }

        /// <summary>
        /// Every light whose sphere contains the point, appended to <paramref name="into"/>.
        /// Extinguished lights are skipped here rather than at build time, so dousing a lantern
        /// takes effect immediately without a rebuild.
        /// </summary>
        public void Query(float3 point, List<StealthLightSource> into)
        {
            if (!_cells.TryGetValue(Key(CellOf(point)), out List<Entry> bucket)) return;

            for (int i = 0; i < bucket.Count; i++)
            {
                Entry entry = bucket[i];
                if (entry.Source == null || entry.Source.Extinguished) continue;
                if (math.distancesq(entry.Position, point) > entry.RadiusSq) continue;
                into.Add(entry.Source);
            }
        }

        private static int3 CellOf(float3 position)
        {
            return new int3(
                (int)math.floor(position.x / CellSize),
                (int)math.floor(position.y / CellSize),
                (int)math.floor(position.z / CellSize));
        }

        /// <summary>
        /// Packs a cell coordinate into one key. Twenty-one bits per axis covers a map roughly
        /// sixteen thousand kilometres across, which is ample.
        /// </summary>
        private static long Key(int3 cell)
        {
            long x = (uint)(cell.x + 0x100000) & 0x1FFFFF;
            long y = (uint)(cell.y + 0x100000) & 0x1FFFFF;
            long z = (uint)(cell.z + 0x100000) & 0x1FFFFF;
            return (x << 42) | (y << 21) | z;
        }
    }
}
