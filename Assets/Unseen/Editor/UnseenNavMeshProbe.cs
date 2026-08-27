using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using Unseen.AI;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks that the NavMesh exists, covers the town, keeps bots out of deep water, and is
    /// actually used.
    ///
    /// Every one of those is a separate failure that the others would hide. A bake can succeed and
    /// cover two per cent of the map. It can cover the map and include the bottom of a lake. It can
    /// be perfect and go unused because nothing calls into it. So each is measured on its own, and
    /// the one that matters most is measured against a second run with the bake switched off,
    /// because a number with nothing to compare it to says nothing.
    ///
    /// Six of the seven things measured here are gates. The seventh - how much time bots spend
    /// standing in drownable water - is reported and NOT asserted on, because it could not be made
    /// to discriminate: counting drownings gave one against one, and counting wading samples
    /// flipped its ordering between runs. It is left in as a diagnostic with its own explanation
    /// rather than dressed up as a passing test.
    /// </summary>
    public static class UnseenNavMeshProbe
    {
        [MenuItem("Unseen/Probe NavMesh", priority = 62)]
        public static void Run()
        {
            int drownedWith = -1, drownedWithout = -1;
            int wadingWith = -1, wadingWithout = -1;

            bool exists = false, covers = false, keepsOut = false, fords = false;
            bool routes = false, used = false;

            // ---------------------------------------------------------------- with the bake
            var host = new GameObject("NavMeshProbe");

            try
            {
                UnseenBootstrap boot = Boot(host, navmesh: true);
                if (boot == null) return;

                Debug.Log($"[navmesh] built in {NavMeshBaker.LastBakeSeconds:0.00} s, " +
                          $"{NavMeshBaker.LastCarvedVolumes} water volume(s) carved");

                MapDescriptor map = boot.Map;
                float half = map.HalfExtent > 0f ? map.HalfExtent : map.Radius;

                // ------------------------------------------------------------ is there one at all
                exists = NavMesh.SamplePosition(map.Center + Vector3.up * 2f,
                    out NavMeshHit centre, 40f, NavMesh.AllAreas);

                Debug.Log($"[navmesh] a sample near the middle of the map: " +
                          $"{(exists ? $"hit at {centre.position}" : "nothing")}");
                Debug.Log($"[navmesh] a NavMesh exists: {(exists ? "PASS" : "FAIL")}");

                // ------------------------------------------------------------ how much of the town
                //
                // Sampled on a grid, only where there is ground to stand on, so the score is
                // "navigable where walkable" rather than "navigable including the sky".
                int ground = 0, navigable = 0;

                for (int gx = -18; gx <= 18; gx++)
                for (int gz = -18; gz <= 18; gz++)
                {
                    var at = new Vector3(gx / 18f * half * 0.92f, 80f, gz / 18f * half * 0.92f);

                    if (!Physics.Raycast(at, Vector3.down, out RaycastHit floor, 160f,
                            UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                        continue;

                    // Skip anything standing in water: that is deliberately unwalkable and is
                    // checked separately below.
                    if (WaterVolume.DepthAt(new Unity.Mathematics.float3(
                            floor.point.x, floor.point.y, floor.point.z)) > 0.3f)
                        continue;

                    ground++;

                    if (NavMesh.SamplePosition(floor.point + Vector3.up * 0.5f,
                            out NavMeshHit _, 2.5f, NavMesh.AllAreas))
                        navigable++;
                }

                float coverage = ground > 0 ? navigable / (float)ground : 0f;
                covers = coverage > 0.85f;

                Debug.Log($"[navmesh] {navigable}/{ground} dry ground samples are navigable " +
                          $"({coverage * 100f:0}%)");
                Debug.Log($"[navmesh] it covers the town: {(covers ? "PASS" : "FAIL")}");

                // ------------------------------------------------------------ deep water is out
                int deep = 0, deepNavigable = 0;

                foreach (WaterVolume water in WaterVolume.All)
                {
                    if (water == null) continue;

                    Vector2 footprint = water.DeepFootprint;
                    Vector3 at = water.transform.position;

                    for (int i = 0; i < 40; i++)
                    {
                        // Along the body's own footprint, avoiding any dry island in the middle.
                        float u = (i / 39f - 0.5f) * 1.7f;
                        float v = (((i * 7) % 40) / 39f - 0.5f) * 1.7f;

                        var sample = new Vector3(
                            at.x + u * footprint.x * 0.5f + Mathf.Sign(u) * water.InnerHalfSize.x,
                            water.SurfaceY - 0.4f,
                            at.z + v * footprint.y * 0.5f);

                        if (WaterVolume.DepthAt(new Unity.Mathematics.float3(
                                sample.x, water.SurfaceY - water.MaxDepth, sample.z)) < 1f)
                            continue;

                        deep++;

                        if (NavMesh.SamplePosition(sample, out NavMeshHit _, 1.2f,
                                NavMesh.AllAreas))
                            deepNavigable++;
                    }
                }

                keepsOut = deep > 0 && deepNavigable == 0;

                Debug.Log($"[navmesh] {deepNavigable}/{deep} samples in drownable water are " +
                          $"navigable");
                Debug.Log($"[navmesh] bots are kept out of deep water: " +
                          $"{(keepsOut ? "PASS" : "FAIL")}");

                // ------------------------------------------------------------ but not out of the
                //                                                              river's shallows
                //
                // Walling off the whole channel would be the lazy version of this, and it would
                // take a route out of the map: the shelves are knee deep and crossing there is a
                // real choice.
                WaterVolume river = null;
                foreach (WaterVolume water in WaterVolume.All)
                    if (river == null || (water != null && water.HalfSize.y > river.HalfSize.y))
                        river = water;

                if (river != null)
                {
                    int shelf = 0;

                    for (int i = 0; i < 60; i++)
                    {
                        float along = (i / 59f - 0.5f) * river.HalfSize.y * 1.6f;
                        float side = i % 2 == 0 ? 1f : -1f;
                        float across = side * river.HalfSize.x * 0.86f;

                        var sample = new Vector3(
                            river.transform.position.x + across,
                            river.SurfaceY - 0.6f,
                            river.transform.position.z + along);

                        if (NavMesh.SamplePosition(sample, out NavMeshHit _, 1.6f,
                                NavMesh.AllAreas))
                            shelf++;
                    }

                    fords = shelf > 0;
                    Debug.Log($"[navmesh] {shelf}/60 samples on the river's shallow shelves are " +
                              $"navigable");
                    Debug.Log($"[navmesh] the river can still be forded at the edges: " +
                              $"{(fords ? "PASS" : "FAIL")}");
                }

                // ------------------------------------------------------------ and a route goes round
                //
                // A path across the lake must exist and must be longer than the straight line
                // through it. Equal lengths would mean it walked over the water.
                WaterVolume lake = null;
                foreach (WaterVolume water in WaterVolume.All)
                    if (water != null && water.InnerHalfSize.x > 0f) lake = water;

                if (lake == null)
                {
                    Debug.LogError("[navmesh] no lake found to route around");
                }
                else
                {
                    Vector3 at = lake.transform.position;
                    var from = new Vector3(at.x, 0f, at.z - lake.HalfSize.y - 4f);
                    var to = new Vector3(at.x, 0f, at.z + lake.HalfSize.y + 4f);

                    routes = TryPath(from, to, out float walked, out float direct,
                        out NavMeshPathStatus status);

                    Debug.Log($"[navmesh] across the lake: {status}, {walked:0} m walked against " +
                              $"{direct:0} m straight");
                    Debug.Log($"[navmesh] a route exists and is not straight through the water: " +
                              $"{(routes ? "PASS" : "FAIL")}");
                }

                // ------------------------------------------------------------ bots use it
                Step(boot, 60 * 90);

                int withNav = 0, bots = 0;

                foreach (AgentEntity agent in boot.Context.Entities.All)
                {
                    if (agent == null || !agent.IsBot || agent.Brain == null) continue;
                    bots++;
                    if (agent.Brain.Navigator != null && agent.Brain.Navigator.UsingNavMesh)
                        withNav++;
                }

                used = bots > 0 && withNav > bots / 3;

                Debug.Log($"[navmesh] {withNav}/{bots} bots are following a NavMesh path right now");
                Debug.Log($"[navmesh] bots use it: {(used ? "PASS" : "FAIL")}");

                wadingWith = MeasureWading(boot, out drownedWith, out string causesWith);
                Debug.Log($"[navmesh] with the bake: {wadingWith} bot-samples stood in drownable " +
                          $"water; deaths were {causesWith}");
            }
            finally
            {
                Shutdown(host);
            }

            // ---------------------------------------------------------------- without the bake
            //
            // The counterfactual. "No bots drowned" is worth nothing unless bots drown without it.
            var control = new GameObject("NavMeshControl");

            try
            {
                UnseenBootstrap boot = Boot(control, navmesh: false);
                if (boot == null) return;

                // The SAME warm-up as the other run before measuring, or the two are counting
                // different windows and the comparison is meaningless.
                Step(boot, 60 * 90);

                wadingWithout = MeasureWading(boot, out drownedWithout, out string causesWithout);
                Debug.Log($"[navmesh] with no bake: {wadingWithout} bot-samples stood in drownable " +
                          $"water; deaths were {causesWithout}");
            }
            finally
            {
                Shutdown(control);
            }

            // REPORTED, not asserted. This is a diagnostic and deliberately not a gate.
            //
            // The intent was to prove the bake stops bots wading. It does not measure that
            // reliably: across four runs the pair came out 992/1044, 1117/1229, 1447/1011 and
            // 1011/1447 - the ordering flips, so the number is dominated by a handful of bots
            // parked in one spot for many seconds rather than by how often anybody walks in. A
            // threshold tuned until this went green would be a threshold measuring nothing.
            //
            // What it does say, and it is worth knowing, is that roughly one bot-second in seven
            // is still spent in drownable water WITH the bake. Deep water being unwalkable stops a
            // bot routing through it and does not stop it being in it, and the recovery steering
            // added alongside this only partly does. That is an open problem, tracked in the
            // roadmap rather than papered over here.
            Debug.Log($"[navmesh] DIAGNOSTIC, not a gate - wading samples {wadingWithout} without " +
                      $"the bake against {wadingWith} with it; drowned {drownedWithout} against " +
                      $"{drownedWith}. Too noisy to assert on: see the note in this file.");

            if (exists && covers && keepsOut && fords && routes && used)
                Debug.Log("[navmesh] PASSED");
            else
                Debug.LogError("[navmesh] FAILED");
        }

        private static bool TryPath(Vector3 from, Vector3 to, out float walked, out float direct,
            out NavMeshPathStatus status)
        {
            walked = 0f;
            direct = 0f;
            status = NavMeshPathStatus.PathInvalid;

            if (!NavMesh.SamplePosition(from, out NavMeshHit a, 30f, NavMesh.AllAreas)) return false;
            if (!NavMesh.SamplePosition(to, out NavMeshHit b, 30f, NavMesh.AllAreas)) return false;

            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path)) return false;

            status = path.status;
            direct = Vector3.Distance(a.position, b.position);

            Vector3[] corners = path.corners;
            for (int i = 1; i < corners.Length; i++)
                walked += Vector3.Distance(corners[i - 1], corners[i]);

            // Complete, and at least a tenth longer than the straight line. A path that matched the
            // straight line would have gone through the lake.
            return status == NavMeshPathStatus.PathComplete && walked > direct * 1.1f;
        }

        /// <summary>
        /// Runs the match on and counts how often a LIVING bot is stood in water deep enough to
        /// drown in, sampling once a second. Also reports what killed everyone who died.
        ///
        /// Started after the drop has finished, so a bot that came down in the lake off its glider
        /// is not counted against the pathing - it did not walk there, and no NavMesh could have
        /// stopped it.
        /// </summary>
        private static int MeasureWading(UnseenBootstrap boot, out int drowned, out string causes)
        {
            int wading = 0;
            int inRiver = 0, inLake = 0, recovering = 0, onMesh = 0, stuck = 0;
            var sample = new System.Text.StringBuilder();

            for (int second = 0; second < 120; second++)
            {
                Step(boot, 60);

                foreach (AgentEntity agent in boot.Context.Entities.All)
                {
                    if (agent == null || !agent.IsAlive || !agent.IsBot) continue;

                    float depth = WaterVolume.DepthAt(agent.Position);
                    if (depth <= 1f) continue;

                    wading++;

                    // Which body, and what the navigator thinks it is doing. Guessing at this has
                    // already cost two rounds of changes that moved the number by ten per cent.
                    bool nearRiver = false;
                    foreach (WaterVolume w in WaterVolume.All)
                        if (w != null && w.InnerHalfSize.x <= 0f &&
                            Mathf.Abs(agent.Position.x - w.transform.position.x) < w.HalfSize.x)
                            nearRiver = true;

                    if (nearRiver) inRiver++;
                    else inLake++;

                    BotNavigator nav = agent.Brain != null ? agent.Brain.Navigator : null;
                    if (nav == null) continue;

                    if (nav.Recovering) recovering++;
                    if (nav.UsingNavMesh) onMesh++;
                    if (nav.Stuck) stuck++;

                    if (sample.Length == 0)
                        sample.Append($"eg at {agent.Position} depth {depth:0.00} " +
                                      $"y {agent.Position.y:0.00}");
                }
            }

            drowned = 0;
            var tally = new System.Collections.Generic.Dictionary<DamageKind, int>();

            foreach (AgentEntity agent in boot.Context.Entities.All)
            {
                if (agent == null || agent.IsAlive) continue;

                tally.TryGetValue(agent.DeathCause, out int count);
                tally[agent.DeathCause] = count + 1;

                if (agent.DeathCause == DamageKind.Drowning) drowned++;
            }

            var text = new System.Text.StringBuilder();
            foreach (System.Collections.Generic.KeyValuePair<DamageKind, int> kv in tally)
                text.Append($"{kv.Key}:{kv.Value} ");

            causes = text.Length > 0 ? text.ToString().TrimEnd() : "none";

            Debug.Log($"[navmesh]   of {wading} wading samples: river {inRiver}, lake {inLake}; " +
                      $"recovering {recovering}, on a mesh path {onMesh}, stuck {stuck}");
            if (sample.Length > 0) Debug.Log($"[navmesh]   {sample}");

            return wading;
        }

        private static UnseenBootstrap Boot(GameObject host, bool navmesh)
        {
            UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
            boot.Mode = LaunchMode.ListenServer;
            boot.GenerateGreyboxIfEmpty = true;
            boot.BuildNavMesh = navmesh;
            boot.StatusLogInterval = 0f;
            boot.VerboseStartup = false;
            boot.Seed = 20260824;
            boot.Boot();

            if (boot.Map != null) return boot;

            Debug.LogError("[navmesh] the town would not generate");
            return null;
        }

        private static void Shutdown(GameObject host)
        {
            UnseenBootstrap boot = host.GetComponent<UnseenBootstrap>();
            if (boot != null) boot.Shutdown();
            Object.DestroyImmediate(host);
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
