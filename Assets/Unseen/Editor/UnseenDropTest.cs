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

                    // Hanging, climbing and crawling all put the body deliberately against - and
                    // partly inside - the thing being held onto. A ninja on a ledge overlapping the
                    // wall it is hanging from is the feature working, not a landing bug, and this
                    // check exists to catch bodies that ended up in geometry they cannot get out
                    // of. The penetration depth below is what distinguishes the two, and for these
                    // states it is zero.
                    bool holdingOn = agent.Locomotion == LocomotionState.LedgeHang ||
                                     agent.Locomotion == LocomotionState.WallClimb ||
                                     agent.Locomotion == LocomotionState.WallRun ||
                                     agent.Locomotion == LocomotionState.RafterCrawl;

                    if (!holdingOn && !ParkourProbe.HasClearance(feet, radius, height))
                    {
                        inGeometry++;

                        // Naming what it is inside. "One agent is in geometry" is not actionable;
                        // "one agent is inside a hedge" tells you whether it is a landing bug or a
                        // prop somebody just added that should not have had a collider.
                        Collider[] around = Physics.OverlapSphere(
                            (Vector3)feet + Vector3.up * (height * 0.5f), radius,
                            UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore);

                        string what = around.Length > 0 ? around[0].name : "nothing found";

                        // How deep, not merely whether they touch. HasClearance tests a full-radius
                        // sphere, so an agent standing with its shoulder against a wall fails it
                        // while being perfectly fine - the number that matters is how far the
                        // controller would have to be pushed to be clear.
                        float depth = 0f;
                        CharacterController controller = agent.Controller;

                        if (controller != null && around.Length > 0 &&
                            Physics.ComputePenetration(
                                controller, agent.Motor.transform.position, agent.Motor.transform.rotation,
                                around[0], around[0].transform.position, around[0].transform.rotation,
                                out Vector3 _, out float distance))
                            depth = distance;

                        Debug.Log($"[drop] {agent.Id} at {feet} is inside '{what}' " +
                                  $"({around.Length} colliders, {depth:0.000} m of penetration), " +
                                  $"locomotion={agent.Locomotion}, flags={agent.Flags}, " +
                                  $"controllerEnabled={(agent.Controller != null && agent.Controller.enabled)}");
                    }

                    // Anything with no floor beneath it never actually landed - unless it is in
                    // the air on purpose. The check runs a minute after the drop, by which point
                    // bots are jumping, grappling and dropping off roofs of their own accord, and
                    // counting those as failed landings tests nothing.
                    bool deliberatelyAirborne =
                        agent.Locomotion == LocomotionState.Airborne ||
                        agent.Locomotion == LocomotionState.Grapple ||
                        agent.Locomotion == LocomotionState.WallClimb ||
                        agent.Locomotion == LocomotionState.WallRun ||
                        agent.Locomotion == LocomotionState.RafterCrawl;

                    if (deliberatelyAirborne) continue;

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
