using System.Collections.Generic;
using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using Unseen.AI;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Measures whether the bots actually go anywhere.
    ///
    /// "Running back and forth over the same metre" is a specific, measurable claim: a bot doing it
    /// covers a lot of ground and ends up where it started. So the numbers that matter are the ratio
    /// of net displacement to path length, how much of the map each bot visits, and how much height
    /// it ever gains - a town full of climbable roofs and pagodas is not being explored by anything
    /// that never leaves y=0.
    ///
    /// It also counts how often a bot's state machine reacts to something it heard, because a bot
    /// that hunts by ear is the whole point of the acoustic model and it would be easy for that
    /// wiring to be present and never fire.
    /// </summary>
    public static class UnseenBotRoamProbe
    {
        private const float Seconds = 90f;

        [MenuItem("Unseen/Diagnose Bot Roaming", priority = 91)]
        public static void Run()
        {
            var host = new GameObject("BotRoamProbe");

            UnseenConfig config = UnseenConfig.Default;
            int roster = config.Match.TargetEntityCount;
            config.Match.TargetEntityCount = 24;

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260824;
                boot.Boot();

                // Past the drop, so nobody is still gliding.
                for (int i = 0; i < 60 * 70; i++)
                {
                    boot.Network.Poll(1f / 60f);
                    boot.Simulation.Advance(1f / 60f);
                }

                var tracked = new List<AgentEntity>(32);
                foreach (AgentEntity agent in boot.Context.Entities.All)
                    if (agent != null && agent.IsAlive && agent.IsBot) tracked.Add(agent);

                int n = tracked.Count;
                if (n == 0)
                {
                    Debug.LogError("[roam] no live bots to measure");
                    return;
                }

                var start = new float3[n];
                var previous = new float3[n];
                var pathLength = new float[n];
                var maxHeight = new float[n];
                var minHeight = new float[n];
                var visited = new HashSet<long>[n];
                var heardStates = new int[n];

                for (int i = 0; i < n; i++)
                {
                    start[i] = tracked[i].Position;
                    previous[i] = start[i];
                    maxHeight[i] = start[i].y;
                    minHeight[i] = start[i].y;
                    visited[i] = new HashSet<long>();
                }

                var stateTicks = new Dictionary<string, int>(8);
                var actionTicks = new Dictionary<string, int>(16);
                float destSum = 0f;
                int destCount = 0;

                int ticks = (int)(Seconds * 60f);

                for (int t = 0; t < ticks; t++)
                {
                    boot.Network.Poll(1f / 60f);
                    boot.Simulation.Advance(1f / 60f);

                    for (int i = 0; i < n; i++)
                    {
                        AgentEntity agent = tracked[i];
                        if (agent == null || !agent.IsAlive) continue;

                        float3 at = agent.Position;
                        pathLength[i] += math.distance(at, previous[i]);
                        previous[i] = at;

                        if (at.y > maxHeight[i]) maxHeight[i] = at.y;
                        if (at.y < minHeight[i]) minHeight[i] = at.y;

                        // A 20 m grid: how many distinct squares of the town this bot set foot in.
                        long cell = ((long)Mathf.FloorToInt(at.x / 20f) << 32) ^
                                    (uint)Mathf.FloorToInt(at.z / 20f);
                        visited[i].Add(cell);

                        BotBrain brain = agent.GetComponent<BotBrain>();
                        if (brain == null) continue;

                        string state = brain.Describe();
                        if (state.StartsWith("Investigate")) heardStates[i]++;

                        stateTicks.TryGetValue(brain.State.ToString(), out int sc);
                        stateTicks[brain.State.ToString()] = sc + 1;

                        actionTicks.TryGetValue(brain.Action.ToString(), out int ac);
                        actionTicks[brain.Action.ToString()] = ac + 1;

                        // How far the bot is being asked to go. A roaming bot handed destinations
                        // twelve metres away is doing laps of a courtyard by design, not by bug.
                        float want = math.distance(
                            UnseenMath.Horizontal(agent.Position),
                            UnseenMath.Horizontal(brain.Blackboard.PatrolDestination));
                        if (brain.Blackboard.HasPatrolDestination)
                        {
                            destSum += want;
                            destCount++;
                        }
                    }
                }

                float worstRatio = 1f;
                float totalRatio = 0f;
                int stuck = 0;
                float totalCells = 0f;
                float bestClimb = 0f;
                int climbed = 0;
                int investigated = 0;
                int alive = 0;

                for (int i = 0; i < n; i++)
                {
                    AgentEntity agent = tracked[i];
                    if (agent == null || !agent.IsAlive) continue;
                    alive++;

                    float net = math.distance(agent.Position, start[i]);
                    float ratio = pathLength[i] > 1f ? net / pathLength[i] : 0f;

                    totalRatio += ratio;
                    if (ratio < worstRatio) worstRatio = ratio;

                    // Pacing is walking a long way without going anywhere - covering ground while
                    // staying in one place. Measured by area covered, not by net displacement:
                    // a bot that roams a long loop and comes back past its start has explored
                    // properly and would score badly on displacement alone.
                    if (pathLength[i] > 40f && visited[i].Count <= 2) stuck++;

                    totalCells += visited[i].Count;

                    float climb = maxHeight[i] - minHeight[i];
                    if (climb > bestClimb) bestClimb = climb;
                    if (climb > 3f) climbed++;

                    if (heardStates[i] > 0) investigated++;
                }

                if (alive == 0)
                {
                    Debug.LogError("[roam] every bot died during the sample");
                    return;
                }

                float totalPath = 0f;
                for (int i = 0; i < n; i++) totalPath += pathLength[i];

                Debug.Log($"[roam] {alive} bots over {Seconds:0} s");
                Debug.Log($"[roam] mean path walked {totalPath / alive:0.0} m per bot");
                Debug.Log($"[roam] mean destination distance {(destCount > 0 ? destSum / destCount : 0f):0.0} m");

                foreach (KeyValuePair<string, int> kv in stateTicks)
                    Debug.Log($"[roam] state {kv.Key}: {100f * kv.Value / (ticks * (float)alive):0}% of ticks");

                foreach (KeyValuePair<string, int> kv in actionTicks)
                    Debug.Log($"[roam] action {kv.Key}: {100f * kv.Value / (ticks * (float)alive):0}% of ticks");

                Debug.Log($"[roam] mean net/path ratio {totalRatio / alive:0.00} " +
                          $"(worst {worstRatio:0.00}); 1.00 is a straight line, 0.00 is pacing");
                Debug.Log($"[roam] pacing on the spot: {stuck}/{alive} bots");
                Debug.Log($"[roam] mean distinct 20 m cells visited: {totalCells / alive:0.0}");
                Debug.Log($"[roam] gained more than 3 m of height: {climbed}/{alive} " +
                          $"(best climb {bestClimb:0.0} m)");
                Debug.Log($"[roam] reacted to something heard: {investigated}/{alive} bots");

                bool notPacing = stuck <= alive / 10;
                bool exploring = totalCells / alive >= 6f;
                bool vertical = climbed >= alive / 5;
                bool listening = investigated >= alive / 4;

                Debug.Log($"[roam] bots are not pacing on the spot: {(notPacing ? "PASS" : "FAIL")}");
                Debug.Log($"[roam] bots cover ground: {(exploring ? "PASS" : "FAIL")}");
                Debug.Log($"[roam] bots use the vertical: {(vertical ? "PASS" : "FAIL")}");
                Debug.Log($"[roam] bots hunt by ear: {(listening ? "PASS" : "FAIL")}");

                if (notPacing && exploring && vertical && listening)
                    Debug.Log("[roam] PASSED");
                else
                    Debug.LogError("[roam] FAILED");
            }
            finally
            {
                config.Match.TargetEntityCount = roster;

                UnseenBootstrap boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();
                Object.DestroyImmediate(host);
            }
        }
    }
}
