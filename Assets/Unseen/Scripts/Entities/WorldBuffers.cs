using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;

namespace Unseen.Entities
{
    /// <summary>
    /// Struct-of-arrays mirror of the agent table, refreshed at the top of every tick.
    /// This is the only view of the world the Burst jobs are allowed to touch.
    /// </summary>
    public sealed class WorldBuffers : IDisposable
    {
        public readonly int Capacity;

        public NativeArray<int> Ids;
        public NativeArray<int> Flags;
        public NativeArray<byte> Stance;
        public NativeArray<float3> Positions;
        public NativeArray<float3> EyePositions;
        public NativeArray<float3> TorsoPositions;
        public NativeArray<float3> ViewDirections;
        public NativeArray<float> Stealth;
        public NativeArray<float> Heights;

        public int Count { get; private set; }

        public WorldBuffers(int maxAgents)
        {
            Capacity = Mathf.Max(64, maxAgents + 8);
            Ids = new NativeArray<int>(Capacity, Allocator.Persistent);
            Flags = new NativeArray<int>(Capacity, Allocator.Persistent);
            Stance = new NativeArray<byte>(Capacity, Allocator.Persistent);
            Positions = new NativeArray<float3>(Capacity, Allocator.Persistent);
            EyePositions = new NativeArray<float3>(Capacity, Allocator.Persistent);
            TorsoPositions = new NativeArray<float3>(Capacity, Allocator.Persistent);
            ViewDirections = new NativeArray<float3>(Capacity, Allocator.Persistent);
            Stealth = new NativeArray<float>(Capacity, Allocator.Persistent);
            Heights = new NativeArray<float>(Capacity, Allocator.Persistent);
        }

        public void Capture(EntityRegistry registry)
        {
            int n = Mathf.Min(registry.Count, Capacity);
            Count = n;

            for (int i = 0; i < n; i++)
            {
                AgentEntity a = registry.BySlot(i);
                Ids[i] = a.Id.Value;
                Flags[i] = (int)a.Flags;
                Stance[i] = (byte)a.Stance;
                Positions[i] = a.Position;
                EyePositions[i] = a.EyePosition;
                TorsoPositions[i] = a.TorsoPosition;
                ViewDirections[i] = a.ViewDirection;
                Stealth[i] = a.StealthIndex;
                Heights[i] = a.Controller != null ? a.Controller.height : 1.8f;
            }
        }

        /// <summary>
        /// Declares how many slots are populated. Used by tests and tools that fill the arrays
        /// directly instead of going through <see cref="Capture"/>.
        /// </summary>
        public void SetCount(int count)
        {
            Count = Mathf.Clamp(count, 0, Capacity);
        }

        public bool IsAlive(int slot)
        {
            return (Flags[slot] & (int)AgentFlags.Alive) != 0;
        }

        public void Dispose()
        {
            if (Ids.IsCreated) Ids.Dispose();
            if (Flags.IsCreated) Flags.Dispose();
            if (Stance.IsCreated) Stance.Dispose();
            if (Positions.IsCreated) Positions.Dispose();
            if (EyePositions.IsCreated) EyePositions.Dispose();
            if (TorsoPositions.IsCreated) TorsoPositions.Dispose();
            if (ViewDirections.IsCreated) ViewDirections.Dispose();
            if (Stealth.IsCreated) Stealth.Dispose();
            if (Heights.IsCreated) Heights.Dispose();
        }
    }

    /// <summary>Refreshes <see cref="WorldBuffers"/> from the managed agent table. Runs first, every tick.</summary>
    public sealed class WorldBufferSystem : SimSystem
    {
        public override int Order => SimOrder.WorldBuffers;
        public override SimRate Rate => SimRate.Combat;

        public override void Tick(in SimFrame frame)
        {
            Ctx.Buffers.Capture(Ctx.Entities);
        }
    }
}
