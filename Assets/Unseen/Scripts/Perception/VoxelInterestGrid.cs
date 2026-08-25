using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.Perception
{
    /// <summary>
    /// Uniform 3D voxel hash over every agent. This is the gate in front of every expensive
    /// query in the game: line of sight, acoustics, combat pockets and replication all start here,
    /// so an entity that is far away costs nothing and is never mentioned to a distant client.
    /// </summary>
    public sealed class VoxelInterestGrid : IDisposable
    {
        private readonly float _voxelSize;
        private readonly float _inverseVoxelSize;
        private NativeParallelMultiHashMap<int, int> _cells;
        private NativeArray<int3> _coords;

        public VoxelInterestGrid(float voxelSize, int capacity)
        {
            _voxelSize = Mathf.Max(1f, voxelSize);
            _inverseVoxelSize = 1f / _voxelSize;
            _cells = new NativeParallelMultiHashMap<int, int>(Mathf.Max(64, capacity * 4), Allocator.Persistent);
            _coords = new NativeArray<int3>(Mathf.Max(64, capacity), Allocator.Persistent);
        }

        public float VoxelSize => _voxelSize;
        public int Count { get; private set; }

        [BurstCompile]
        private struct HashJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public float InverseVoxelSize;
            [WriteOnly] public NativeArray<int3> Coords;
            public NativeParallelMultiHashMap<int, int>.ParallelWriter Cells;

            public void Execute(int index)
            {
                int3 c = (int3)math.floor(Positions[index] * InverseVoxelSize);
                Coords[index] = c;
                Cells.Add(CellHash(c), index);
            }
        }

        public static int CellHash(int3 c)
        {
            unchecked
            {
                return (int)math.hash(c);
            }
        }

        /// <summary>Rebuilds the grid from the current world buffers. Cheap enough to run every base tick.</summary>
        public JobHandle Rebuild(WorldBuffers buffers, JobHandle dependency = default)
        {
            Count = buffers.Count;
            _cells.Clear();
            if (Count == 0) return dependency;

            var job = new HashJob
            {
                Positions = buffers.Positions,
                InverseVoxelSize = _inverseVoxelSize,
                Coords = _coords,
                Cells = _cells.AsParallelWriter()
            };

            return job.Schedule(Count, 16, dependency);
        }

        public int3 CoordOf(int slot) => _coords[slot];

        /// <summary>Appends every agent slot whose voxel overlaps the sphere. Results are unsorted and may exceed the radius.</summary>
        public void QueryRadius(float3 center, float radius, NativeList<int> results)
        {
            results.Clear();
            if (Count == 0) return;

            int3 min = (int3)math.floor((center - radius) * _inverseVoxelSize);
            int3 max = (int3)math.floor((center + radius) * _inverseVoxelSize);

            for (int x = min.x; x <= max.x; x++)
            for (int y = min.y; y <= max.y; y++)
            for (int z = min.z; z <= max.z; z++)
            {
                int hash = CellHash(new int3(x, y, z));
                if (!_cells.TryGetFirstValue(hash, out int slot, out NativeParallelMultiHashMapIterator<int> it))
                    continue;

                do
                {
                    results.Add(slot);
                } while (_cells.TryGetNextValue(out slot, ref it));
            }
        }

        public void Dispose()
        {
            if (_cells.IsCreated) _cells.Dispose();
            if (_coords.IsCreated) _coords.Dispose();
        }
    }

    /// <summary>Rebuilds the interest grid at the base spatial rate.</summary>
    public sealed class InterestGridSystem : SimSystem
    {
        public override int Order => SimOrder.InterestGrid;
        public override SimRate Rate => SimRate.Base;

        protected override void OnInitialize()
        {
            Ctx.Grid = new VoxelInterestGrid(Ctx.Config.Interest.VoxelSize, Ctx.Buffers.Capacity);
        }

        public override void Tick(in SimFrame frame)
        {
            Ctx.Grid.Rebuild(Ctx.Buffers).Complete();
        }
    }
}
