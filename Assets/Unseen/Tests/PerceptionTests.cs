using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Items;
using Unseen.Perception;

namespace Unseen.Tests
{
    public sealed class VoxelInterestGridTests
    {
        [Test]
        public void QueryReturnsOnlyNearbySlots()
        {
            var buffers = new WorldBuffers(16);
            var grid = new VoxelInterestGrid(16f, buffers.Capacity);
            var results = new NativeList<int>(16, Allocator.Temp);

            try
            {
                buffers.Positions[0] = new float3(0f, 0f, 0f);
                buffers.Positions[1] = new float3(5f, 0f, 5f);
                buffers.Positions[2] = new float3(400f, 0f, 0f);
                buffers.SetCount(3);

                grid.Rebuild(buffers).Complete();
                grid.QueryRadius(float3.zero, 10f, results);

                bool foundSelf = false;
                bool foundNear = false;
                bool foundFar = false;

                for (int i = 0; i < results.Length; i++)
                {
                    if (results[i] == 0) foundSelf = true;
                    if (results[i] == 1) foundNear = true;
                    if (results[i] == 2) foundFar = true;
                }

                Assert.IsTrue(foundSelf, "the querying slot itself should be returned");
                Assert.IsTrue(foundNear, "a slot 7 m away should be returned");
                Assert.IsFalse(foundFar, "a slot 400 m away must never be returned");
            }
            finally
            {
                results.Dispose();
                grid.Dispose();
                buffers.Dispose();
            }
        }

        [Test]
        public void EmptyGridQueriesAreSafe()
        {
            var buffers = new WorldBuffers(8);
            var grid = new VoxelInterestGrid(16f, buffers.Capacity);
            var results = new NativeList<int>(4, Allocator.Temp);

            try
            {
                buffers.SetCount(0);
                grid.Rebuild(buffers).Complete();
                grid.QueryRadius(new float3(10f, 2f, -30f), 50f, results);
                Assert.AreEqual(0, results.Length);
            }
            finally
            {
                results.Dispose();
                grid.Dispose();
                buffers.Dispose();
            }
        }

        [Test]
        public void RadiusSpanningManyVoxelsStillFindsEverything()
        {
            var buffers = new WorldBuffers(64);
            var grid = new VoxelInterestGrid(8f, buffers.Capacity);
            var results = new NativeList<int>(64, Allocator.Temp);

            try
            {
                for (int i = 0; i < 40; i++)
                    buffers.Positions[i] = new float3(i * 3f, 0f, 0f);
                buffers.SetCount(40);

                grid.Rebuild(buffers).Complete();
                grid.QueryRadius(new float3(60f, 0f, 0f), 130f, results);

                Assert.AreEqual(40, results.Length, "every slot lies inside the queried span");
            }
            finally
            {
                results.Dispose();
                grid.Dispose();
                buffers.Dispose();
            }
        }
    }

    public sealed class UnseenMathTests
    {
        [Test]
        public void YawDeltaTakesTheShortWayRound()
        {
            Assert.AreEqual(20f, UnseenMath.YawDelta(350f, 10f), 0.001f);
            Assert.AreEqual(-20f, UnseenMath.YawDelta(10f, 350f), 0.001f);
            Assert.AreEqual(0f, UnseenMath.YawDelta(180f, 180f), 0.001f);
        }

        [Test]
        public void YawAndForwardAreInverses()
        {
            for (float yaw = -170f; yaw < 180f; yaw += 23f)
            {
                float3 forward = UnseenMath.YawToForward(yaw);
                Assert.AreEqual(0f, UnseenMath.YawDelta(yaw, UnseenMath.ForwardToYaw(forward)), 0.01f);
                Assert.AreEqual(1f, math.length(forward), 0.001f);
            }
        }

        [Test]
        public void FalloffIsMonotonicAndBounded()
        {
            Assert.AreEqual(1f, UnseenMath.Falloff(0f, 20f), 0.001f);
            Assert.AreEqual(0f, UnseenMath.Falloff(20f, 20f), 0.001f);
            Assert.AreEqual(0f, UnseenMath.Falloff(999f, 20f), 0.001f);

            float previous = 1.1f;
            for (float d = 0f; d <= 20f; d += 1f)
            {
                float value = UnseenMath.Falloff(d, 20f);
                Assert.LessOrEqual(value, previous, $"falloff must not rise at {d} m");
                previous = value;
            }
        }
    }

    public sealed class LootTableTests
    {
        private static LootTable BuildTable()
        {
            var table = UnityEngine.ScriptableObject.CreateInstance<LootTable>();
            table.RollsPerContainer = 2;

            var common = UnityEngine.ScriptableObject.CreateInstance<ItemDefinition>();
            common.Id = "common";
            var late = UnityEngine.ScriptableObject.CreateInstance<ItemDefinition>();
            late.Id = "late";

            table.Entries.Add(new LootTable.Entry { Item = common, Weight = 10f, MinZoneStage = 0 });
            table.Entries.Add(new LootTable.Entry { Item = late, Weight = 10f, MinZoneStage = 4 });
            return table;
        }

        [Test]
        public void SameSeedProducesSameRolls()
        {
            LootTable table = BuildTable();

            var a = new System.Random(1234);
            var b = new System.Random(1234);

            for (int i = 0; i < 20; i++)
                Assert.AreSame(table.Roll(a, 5), table.Roll(b, 5));
        }

        [Test]
        public void StageGatedEntriesAreNeverRolledEarly()
        {
            LootTable table = BuildTable();
            var random = new System.Random(99);

            for (int i = 0; i < 200; i++)
            {
                ItemDefinition rolled = table.Roll(random, 0);
                Assert.IsNotNull(rolled);
                Assert.AreEqual("common", rolled.Id);
            }
        }
    }
}
