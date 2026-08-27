using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AI;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks that nobody is spawned into water they cannot get out of.
    ///
    /// Bots are placed by picking a uniformly random point inside the map circle and raycasting
    /// down onto world geometry. The riverbed and the lake bed ARE world geometry - solid stone
    /// under one to three metres of water - so that search happily reports them as ground. The
    /// roster is topped up continuously through a match, so this is not a one-off at the drop: it
    /// keeps putting bodies in the water for the whole game.
    ///
    /// Measured two ways. The hazard rate comes from sampling the placement the same way the
    /// spawner does, thousands of times, and asking how much of the map is a trap. The outcome
    /// comes from running a match and counting bodies standing in drownable water over time. The
    /// first is precise and cheap; the second is what a player actually sees.
    /// </summary>
    public static class UnseenSpawnProbe
    {
        [MenuItem("Unseen/Probe Spawn Placement", priority = 64)]
        public static void Run()
        {
            var host = new GameObject("SpawnProbe");

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260824;
                boot.Boot();

                MapDescriptor map = boot.Map;

                // ---------------------------------------------------------- the hazard rate
                //
                // Asks the REAL spawner for placements rather than reproducing its sampling here.
                //
                // The first version of this probe copied the algorithm inline, and after the fix it
                // dutifully reported the same 10.3% as before - because it was measuring its own
                // copy of the old code. A test that re-implements what it is testing cannot fail
                // for the right reason.
                Unseen.AI.BotDirector bots = boot.Bots;

                if (bots == null)
                {
                    Debug.LogError("[spawn] no bot director to ask for placements");
                    return;
                }

                int placed = 0, inWater = 0, offMesh = 0;
                float worstDepth = 0f;

                for (int i = 0; i < 4000; i++)
                {
                    Vector3 feet = (Vector3)bots.PickSpawnPoint();
                    placed++;

                    float depth = WaterVolume.DepthAt(new float3(feet.x, feet.y, feet.z));

                    if (depth > 1f)
                    {
                        inWater++;
                        worstDepth = Mathf.Max(worstDepth, depth);
                    }

                    // And whether a bot placed there could path at all.
                    if (!NavMesh.SamplePosition(feet, out NavMeshHit _, 2f, NavMesh.AllAreas))
                        offMesh++;
                }

                float hazard = placed > 0 ? inWater / (float)placed : 0f;
                float stranded = placed > 0 ? offMesh / (float)placed : 0f;

                Debug.Log($"[spawn] {placed} placements sampled the way the spawner does");
                Debug.Log($"[spawn] {inWater} landed in water over a metre deep " +
                          $"({hazard * 100f:0.0}%, deepest {worstDepth:0.00} m)");
                Debug.Log($"[spawn] {offMesh} landed somewhere with no NavMesh under them " +
                          $"({stranded * 100f:0.0}%)");

                bool dry = hazard < 0.002f;
                bool navigable = stranded < 0.05f;

                Debug.Log($"[spawn] spawns stay out of the water: {(dry ? "PASS" : "FAIL")}");
                Debug.Log($"[spawn] spawns land somewhere a bot can path from: " +
                          $"{(navigable ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- and in a real match
                //
                // Sampled once a second for ninety seconds. Reported as the worst second rather
                // than a total, because a total is dominated by however long one stuck body sat
                // there and the question here is how many are in at once.
                //
                // Ninety rather than a hundred and eighty: three minutes of sixty-hertz simulation
                // with sixty-three bots and a NavMesh does not finish inside the batch-mode budget
                // on this machine, and a probe that gets killed reports nothing at all.
                const int Seconds = 90;

                int worstAtOnce = 0;
                int secondsWithAny = 0;

                for (int second = 0; second < Seconds; second++)
                {
                    for (int t = 0; t < 60; t++)
                    {
                        boot.Network.Poll(1f / 60f);
                        boot.Simulation.Advance(1f / 60f);
                    }

                    int wet = 0;

                    foreach (AgentEntity agent in boot.Context.Entities.All)
                    {
                        if (agent == null || !agent.IsAlive || !agent.IsBot) continue;
                        if (WaterVolume.DepthAt(agent.Position) > 1f) wet++;
                    }

                    if (wet > 0) secondsWithAny++;
                    worstAtOnce = Mathf.Max(worstAtOnce, wet);
                }

                Debug.Log($"[spawn] over {Seconds} s: at worst {worstAtOnce} bots stood in " +
                          $"drownable water at once, and some bot was in it for " +
                          $"{secondsWithAny}/{Seconds} seconds");

                bool quiet = worstAtOnce <= 2;
                Debug.Log($"[spawn] the town does not accumulate bodies in the water: " +
                          $"{(quiet ? "PASS" : "FAIL")}");

                if (dry && navigable && quiet)
                    Debug.Log("[spawn] PASSED");
                else
                    Debug.LogError("[spawn] FAILED");
            }
            finally
            {
                UnseenBootstrap boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();
                Object.DestroyImmediate(host);
            }
        }
    }
}
