using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using Unseen.BattleRoyale;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Movement;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Runs a real glider drop and checks where sixty-four ninjas actually end up.
    ///
    /// The descent was disabled for weeks behind <c>SkipInfiltration</c> because it teleported
    /// along its path and put people inside buildings. "It looks better now" is not a standard that
    /// would have caught that, so this asserts the two things that matter: everybody lands, and
    /// nobody lands somewhere a body does not fit.
    /// </summary>
    public static class UnseenDropTest
    {
        [MenuItem("Unseen/Test Glider Drop", priority = 85)]
        public static void Run()
        {
            var host = new GameObject("DropTest");

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260824;
                boot.Boot();

                UnseenConfig config = boot.Context.Config;
                if (config.Match.SkipInfiltration)
                {
                    Debug.LogError("[drop] SkipInfiltration is on; there is no drop to test");
                    return;
                }

                MatchDirector match = boot.Simulation.GetSystem<MatchDirector>();
                float radius = config.Movement.Radius;
                float height = config.Movement.StandHeight;

                // Long enough for the whole descent plus a little settling.
                int ticks = Mathf.RoundToInt((config.Match.InfiltrationDuration + 12f) * 60f);
                int embeddedDuringDescent = 0;

                for (int i = 0; i < ticks; i++)
                {
                    boot.Network.Poll(1f / 60f);
                    boot.Simulation.Advance(1f / 60f);

                    // Sample mid-descent too: a glider that passes through a wall and comes out the
                    // far side would still land legally.
                    if (i % 30 != 0) continue;

                    foreach (AgentEntity agent in boot.Context.Entities.All)
                    {
                        if (!agent.IsAlive) continue;
                        if ((agent.Flags & AgentFlags.Deployed) != 0) continue;

                        Vector3 centre = (Vector3)agent.Position + Vector3.up * (height * 0.5f);
                        if (Physics.OverlapSphere(centre, radius * 0.8f, UnseenLayers.WorldGeometry,
                                QueryTriggerInteraction.Ignore).Length > 0)
                            embeddedDuringDescent++;
                    }
                }

                int total = 0;
                int landed = 0;
                int inGeometry = 0;
                int floating = 0;
                float lowest = float.MaxValue;
                float highest = float.MinValue;

                foreach (AgentEntity agent in boot.Context.Entities.All)
                {
                    if (!agent.IsAlive) continue;
                    total++;

                    if ((agent.Flags & AgentFlags.Deployed) != 0) landed++;

                    float3 feet = agent.Position;
                    lowest = math.min(lowest, feet.y);
                    highest = math.max(highest, feet.y);

                    if (!ParkourProbe.HasClearance(feet, radius, height)) inGeometry++;

                    // Anything with no floor within a couple of metres never actually landed.
                    if (!Physics.Raycast((Vector3)feet + Vector3.up * 0.4f, Vector3.down, 2.5f,
                            UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                        floating++;
                }

                Debug.Log($"[drop] phase {match.Phase} after {ticks / 60f:0} s");
                Debug.Log($"[drop] landed {landed}/{total}   feet y {lowest:0.0} to {highest:0.0} m");
                Debug.Log($"[drop] inside geometry on landing: {inGeometry}");
                Debug.Log($"[drop] with no ground beneath:     {floating}");
                Debug.Log($"[drop] embedded samples mid-descent: {embeddedDuringDescent}");

                bool allLanded = landed == total && total > 0;
                bool clean = inGeometry == 0;
                bool grounded = floating == 0;

                Debug.Log($"[drop] everyone landed:      {(allLanded ? "PASS" : "FAIL")}");
                Debug.Log($"[drop] nobody inside a wall: {(clean ? "PASS" : "FAIL")}");
                Debug.Log($"[drop] everybody has floor:  {(grounded ? "PASS" : "FAIL")}");

                if (allLanded && clean && grounded) Debug.Log("[drop] PASSED");
                else Debug.LogError("[drop] FAILED");
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
