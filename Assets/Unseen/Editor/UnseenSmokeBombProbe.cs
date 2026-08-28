using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;
using Unseen.Items;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks that throwing a smoke bomb does something you can see.
    ///
    /// The volume worked all along: the cloud registered, agents inside it were flagged, and the
    /// perception maths read the flag. What it did not have was any geometry, because Spawn takes
    /// a prefab, no smoke prefab exists in the project, and the field that would hold one is empty
    /// in the scene. So the fallback path built a bare GameObject and the bomb was invisible.
    ///
    /// That failure is worth a test of its own precisely because it cannot be found by testing the
    /// mechanic. Every gameplay assertion passed while the item read as broken from the only place
    /// that matters, which is behind the eyes of whoever threw it.
    /// </summary>
    public static class UnseenSmokeBombProbe
    {
        [MenuItem("Unseen/Probe Smoke Bomb", priority = 70)]
        public static void Run()
        {
            var host = new GameObject("SmokeBombProbe");

            UnseenConfig config = UnseenConfig.Default;
            int roster = config.Match.TargetEntityCount;
            bool skipped = config.Match.SkipInfiltration;
            config.Match.TargetEntityCount = 4;

            // Straight onto the ground, no glider.
            //
            // DeploymentSystem overwrites agent.Intent every tick while a body is still coming
            // down, so a probe that writes an intent during infiltration is writing into a field
            // that is about to be replaced. That is what made the throw look like it did nothing
            // on two runs of this probe.
            config.Match.SkipInfiltration = true;

            try
            {
                SmokeCloud.BuildVisuals = true;

                var boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.BuildNavMesh = false;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260828;
                boot.Boot();

                Step(boot, 120);

                AgentEntity agent = null;
                foreach (AgentEntity a in boot.Context.Entities.All)
                    if (a != null && a.IsAlive) { agent = a; break; }

                if (agent == null) { Debug.LogError("[bomb] no live agent"); return; }
                if (agent.Brain != null) agent.Brain.enabled = false;

                // ------------------------------------------------------------------ 1. it is issued
                bool carries = agent.Inventory != null &&
                               agent.Inventory.HasUtility(UtilityEffect.SmokeBomb);

                Debug.Log($"[bomb] agent starts carrying a smoke bomb: {(carries ? "PASS" : "FAIL")}");

                // ------------------------------------------------------------------ 2. nothing yet
                //
                // The counterfactual, taken before the throw rather than after: if clouds already
                // existed, every assertion below would pass without the bomb doing anything.
                int cloudsBefore = SmokeCloud.All.Count;
                bool cleanStart = cloudsBefore == 0;

                Debug.Log($"[bomb] {cloudsBefore} clouds in the world before throwing: " +
                          $"{(cleanStart ? "PASS" : "FAIL")}");

                // ------------------------------------------------------------------ 3. throwing it
                byte slot = FindSmokeSlot(agent.Inventory);

                if (slot == 0) { Debug.LogError("[bomb] no smoke in any utility slot"); return; }

                // Held for eight ticks, not two.
                //
                // CombatDirector skips a cold agent on every tick that is not a base tick, so a
                // button held for two sixty-hertz ticks can fall entirely between two twenty-hertz
                // combat updates and never be seen. That is what made this read as "throwing does
                // nothing" on the first run of this probe.
                //
                // Holding it longer cannot throw two bombs: HandleUtility fires on the change from
                // the previous slot, so the first base tick throws one and the rest see no change.
                for (int i = 0; i < 8; i++)
                {
                    agent.Intent = new MoveIntent { Sequence = (uint)(500 + i), UseUtility = slot };
                    boot.Network.Poll(1f / 60f);
                    boot.Simulation.Advance(1f / 60f);
                }

                agent.Intent = new MoveIntent { Sequence = 600 };
                Step(boot, 30);

                // Where it stopped, if it stopped. Whether the item left the inventory separates
                // "the handler never ran" from "the handler ran and produced nothing", and the
                // scene count separates "no object was made" from "an object was made and never
                // registered itself".
                bool stillCarries = agent.Inventory.HasUtility(UtilityEffect.SmokeBomb);
                var inScene = Object.FindObjectsByType<SmokeCloud>(FindObjectsSortMode.None);

                Debug.Log($"[bomb] after the press: still carrying smoke = {stillCarries}, " +
                          $"SmokeCloud.All = {SmokeCloud.All.Count}, " +
                          $"objects in scene = {inScene.Length}, " +
                          $"agent hot = {agent.IsHot}, alive = {agent.IsAlive}");

                // Exactly one, not merely at least one. A listen server used to make two clouds
                // for one bomb and nothing noticed, because "more than none" was the whole test.
                bool spawned = SmokeCloud.All.Count == cloudsBefore + 1;
                Debug.Log($"[bomb] throwing spawns a cloud: {(spawned ? "PASS" : "FAIL")} " +
                          $"({SmokeCloud.All.Count} in the world)");

                if (!spawned) { Debug.LogError("[bomb] FAILED"); return; }

                SmokeCloud cloud = null;
                for (int i = 0; i < SmokeCloud.All.Count; i++)
                    if (SmokeCloud.All[i] != null) { cloud = SmokeCloud.All[i]; break; }

                // Let it finish expanding before measuring how big it looks, or this reads the
                // radius at nought seconds old and calls a working cloud too small.
                for (int i = 0; i < 60; i++) cloud.Advance(1f / 60f);

                // ------------------------------------------------------------------ 4. you can SEE it
                //
                // The whole point of this probe. A cloud with no renderers is the bug that was
                // shipped: mechanically perfect, completely invisible.
                var renderers = cloud.GetComponentsInChildren<Renderer>();
                int visible = 0;
                float area = 0f;

                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] == null || !renderers[i].enabled) continue;
                    if (renderers[i].sharedMaterial == null) continue;

                    visible++;
                    Vector3 size = renderers[i].bounds.size;
                    area = Mathf.Max(area, size.x * size.y);
                }

                bool hasVisual = visible > 0;
                bool bigEnough = area > 1f;

                Debug.Log($"[bomb] cloud carries {visible} enabled renderer(s) with a material, " +
                          $"largest panel {area:0.0} m^2, radius {cloud.CurrentRadius:0.0} m");
                Debug.Log($"[bomb] a thrown bomb is visible: {(hasVisual ? "PASS" : "FAIL")}");
                Debug.Log($"[bomb] and big enough to read as cover: {(bigEnough ? "PASS" : "FAIL")}");

                // ------------------------------------------------------------------ 5. it is cover
                bool coversItsOwnCentre = SmokeCloud.Covers(cloud.transform.position);

                float3 outside = (float3)cloud.transform.position +
                                 new float3(cloud.Radius * 4f, 0f, 0f);
                bool doesNotCoverFarAway = !SmokeCloud.Covers(outside);

                Debug.Log($"[bomb] covers its own centre: {coversItsOwnCentre}, " +
                          $"covers a point {cloud.Radius * 4f:0.0} m away: {!doesNotCoverFarAway}");
                Debug.Log($"[bomb] the cloud is cover where it is and nowhere else: " +
                          $"{(coversItsOwnCentre && doesNotCoverFarAway ? "PASS" : "FAIL")}");

                // ------------------------------------------------------------------ 6. it clears
                float duration = cloud.Duration;

                // Aged by hand. Update does not run outside play mode, so stepping the simulation
                // alone would leave every cloud frozen at nought seconds old for ever.
                for (int i = 0; i < Mathf.CeilToInt((duration + 2f) * 60f); i++)
                {
                    for (int c = SmokeCloud.All.Count - 1; c >= 0; c--)
                        SmokeCloud.All[c]?.Advance(1f / 60f);

                    boot.Network.Poll(1f / 60f);
                    boot.Simulation.Advance(1f / 60f);
                }

                bool cleared = SmokeCloud.All.Count == 0;
                Debug.Log($"[bomb] after {duration + 2f:0} s there are {SmokeCloud.All.Count} " +
                          $"clouds left: {(cleared ? "PASS" : "FAIL")}");

                if (carries && cleanStart && spawned && hasVisual && bigEnough &&
                    coversItsOwnCentre && doesNotCoverFarAway && cleared)
                    Debug.Log("[bomb] PASSED");
                else
                    Debug.LogError("[bomb] FAILED");
            }
            finally
            {
                config.Match.TargetEntityCount = roster;
                config.Match.SkipInfiltration = skipped;

                var boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();

                Object.DestroyImmediate(host);
            }
        }

        private static byte FindSmokeSlot(Inventory inventory)
        {
            if (inventory == null) return 0;

            for (int i = 0; i < inventory.Utility.Count; i++)
            {
                ItemDefinition item = inventory.Utility[i].Item;
                if (item != null && item.Effect == UtilityEffect.SmokeBomb) return (byte)(i + 1);
            }

            return 0;
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
