using System.Collections.Generic;
using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Items;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Walks the whole loot chain, because "the boxes do nothing" can break in six places and
    /// reading the code proves none of them.
    ///
    /// The chain is: the generator creates containers, the match director rolls contents into
    /// them, the player presses interact, the combat director finds the nearest unlooted one
    /// within reach, and the inventory accepts what comes out. A break anywhere reads identically
    /// from inside the game - you press the key and nothing happens.
    ///
    /// So each link is measured separately, and the last test is the one that matters: an agent
    /// standing at a box, pressing the real button, through the real systems.
    /// </summary>
    public static class UnseenLootProbe
    {
        /// <summary>The reach CombatDirector.HandleInteract actually uses.</summary>
        private const float InteractReach = 2.2f;

        [MenuItem("Unseen/Probe Loot", priority = 69)]
        public static void Run()
        {
            var host = new GameObject("LootProbe");

            UnseenConfig config = UnseenConfig.Default;
            int roster = config.Match.TargetEntityCount;
            config.Match.TargetEntityCount = 4;

            try
            {
                var boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.BuildNavMesh = false;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260828;
                boot.Boot();

                Step(boot, 120);
                Physics.SyncTransforms();

                // ------------------------------------------------------------------ 1. they exist
                IReadOnlyList<LootContainer> all = LootContainer.All;
                bool anyExist = all.Count > 0;

                Debug.Log($"[loot] {all.Count} containers registered in the town");
                Debug.Log($"[loot] containers exist: {(anyExist ? "PASS" : "FAIL")}");

                if (!anyExist) { Debug.LogError("[loot] FAILED"); return; }

                // ------------------------------------------------------------------ 2. they have contents
                //
                // Measured by emptying a sample into a throwaway inventory, because the contents
                // list is private and TakeAll is the only honest way to ask.
                var scratch = new GameObject("Scratch");
                int sampled = 0, itemsFound = 0, emptyBoxes = 0;
                int stride = Mathf.Max(1, all.Count / 40);

                for (int i = 0; i < all.Count && sampled < 40; i += stride)
                {
                    LootContainer c = all[i];
                    if (c == null || c.Looted) continue;

                    var inv = scratch.AddComponent<Inventory>();
                    int took = c.TakeAll(inv);

                    sampled++;
                    itemsFound += took;
                    if (took == 0) emptyBoxes++;

                    Object.DestroyImmediate(inv);
                }

                Object.DestroyImmediate(scratch);

                float perBox = sampled == 0 ? 0f : itemsFound / (float)sampled;
                bool stocked = perBox > 0.5f;

                Debug.Log($"[loot] {sampled} boxes opened, {itemsFound} items out, " +
                          $"{perBox:0.00} per box, {emptyBoxes} came out empty");
                Debug.Log($"[loot] the boxes have something in them: {(stocked ? "PASS" : "FAIL")}");

                // ------------------------------------------------------------------ 3. a player can reach one
                //
                // A chest inside a walled compound is a chest most players never open. This asks
                // how far a body standing in the street is from the nearest one.
                float reach = NearestFromStreets(out float worst);

                Debug.Log($"[loot] mean distance from a street position to the nearest container: " +
                          $"{reach:0.0} m (worst sampled {worst:0.0} m)");

                // ------------------------------------------------------------------ 4. the button works
                //
                // The test that matters. A live agent, teleported to a real container, pressing
                // the real interact bit, driven through the real simulation.
                LootContainer target = FirstUnlooted(all);

                if (target == null)
                {
                    Debug.LogError("[loot] every container was already looted - cannot test the button");
                    return;
                }

                AgentEntity agent = null;
                foreach (AgentEntity a in boot.Context.Entities.All)
                    if (a != null && a.IsAlive) { agent = a; break; }

                if (agent == null) { Debug.LogError("[loot] no live agent"); return; }
                if (agent.Brain != null) agent.Brain.enabled = false;

                int before = CountItems(agent.Inventory);

                // Stood just short of the reach, not on top of it.
                agent.Motor.Teleport(target.Position + new float3(0.9f, 0f, 0f));
                Physics.SyncTransforms();
                Step(boot, 4);

                float standoff = math.distance(agent.TorsoPosition, target.Position);

                Press(boot, agent, 12);

                int after = CountItems(agent.Inventory);
                bool opened = target.Looted;
                bool gained = after > before;

                Debug.Log($"[loot] agent torso {standoff:0.00} m from the box " +
                          $"(HandleInteract reaches {InteractReach:0.0} m)");
                Debug.Log($"[loot] pressing interact: container looted = {opened}, " +
                          $"inventory {before} -> {after} items");
                Debug.Log($"[loot] the interact button empties a box: {(opened ? "PASS" : "FAIL")}");
                Debug.Log($"[loot] and the player keeps what was in it: {(gained ? "PASS" : "FAIL")}");

                // ------------------------------------------------------------------ 5. counterfactual
                //
                // Without this the test above proves nothing: an inventory that gains items no
                // matter what would pass it.
                LootContainer second = FirstUnlooted(all);
                bool nothingFromNowhere = true;

                if (second != null)
                {
                    int baseline = CountItems(agent.Inventory);

                    // Stood well out of reach of anything, and press.
                    agent.Motor.Teleport(second.Position + new float3(0f, 0f, 40f));
                    Physics.SyncTransforms();
                    Step(boot, 4);
                    Press(boot, agent, 12);

                    int nowHeld = CountItems(agent.Inventory);
                    nothingFromNowhere = nowHeld == baseline && !second.Looted;

                    Debug.Log($"[loot] pressing interact 40 m from any box: " +
                              $"looted = {second.Looted}, inventory {baseline} -> {nowHeld}");
                }

                Debug.Log($"[loot] interact does nothing when there is no box: " +
                          $"{(nothingFromNowhere ? "PASS" : "FAIL")}");

                if (anyExist && stocked && opened && gained && nothingFromNowhere)
                    Debug.Log("[loot] PASSED");
                else
                    Debug.LogError("[loot] FAILED");
            }
            finally
            {
                config.Match.TargetEntityCount = roster;

                var boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();

                Object.DestroyImmediate(host);
            }
        }

        private static LootContainer FirstUnlooted(IReadOnlyList<LootContainer> all)
        {
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && !all[i].Looted) return all[i];

            return null;
        }

        /// <summary>Holds the interact bit down for a few ticks, then lets it go.</summary>
        private static void Press(UnseenBootstrap boot, AgentEntity agent, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                var intent = new MoveIntent { Sequence = (uint)(1000 + i), Interact = true };
                agent.Intent = intent;

                boot.Network.Poll(1f / 60f);
                boot.Simulation.Advance(1f / 60f);

                agent.Intent = intent;
            }
        }

        private static int CountItems(Inventory inv)
        {
            if (inv == null) return 0;

            int n = inv.Weapon != null ? 1 : 0;
            n += inv.Gear.Count;
            for (int i = 0; i < inv.Utility.Count; i++) n += inv.Utility[i].Count;
            return n;
        }

        /// <summary>
        /// How far the nearest container is from open street, averaged over a scatter of points.
        /// Sampled on ground the probe finds rather than at a fixed height, and only where a body
        /// actually fits - otherwise this measures distances from inside a wall.
        /// </summary>
        private static float NearestFromStreets(out float worst)
        {
            worst = 0f;
            float total = 0f;
            int taken = 0;
            var random = new System.Random(4242);

            for (int attempt = 0; attempt < 4000 && taken < 200; attempt++)
            {
                var at = new Vector3(
                    (float)(random.NextDouble() * 2f - 1f) * 300f, 80f,
                    (float)(random.NextDouble() * 2f - 1f) * 300f);

                if (!Physics.Raycast(at, Vector3.down, out RaycastHit hit, 200f)) continue;
                if (hit.point.y > 4f) continue;
                if (Physics.CheckCapsule(hit.point + Vector3.up * 0.5f,
                        hit.point + Vector3.up * 1.7f, 0.45f)) continue;

                LootContainer near = LootContainer.NearestUnlooted(hit.point, 500f);
                if (near == null) continue;

                float d = math.distance((float3)hit.point, near.Position);
                total += d;
                worst = Mathf.Max(worst, d);
                taken++;
            }

            return taken == 0 ? -1f : total / taken;
        }

        private static void Step(UnseenBootstrap boot, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                boot.Network.Poll(1f / 60f);
                boot.Simulation.Advance(1f / 60f);
            }
        }
    }
}
