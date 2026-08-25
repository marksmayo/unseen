using UnityEditor;
using UnityEngine;
using Unseen.BattleRoyale;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Kills agents, runs their death scenes to completion, starts the next match, and checks the
    /// bodies came back.
    ///
    /// This guards a bug that shipped in three builds without anyone noticing: the sink stage of
    /// the death scene calls SetActive(false) on the body, and nothing called Reset, so every agent
    /// that had ever died stayed invisible for the rest of the session. It would have presented as
    /// "the lobby slowly empties of ninjas", several matches later, with no error anywhere.
    /// </summary>
    public static class UnseenMatchCycleTest
    {
        [MenuItem("Unseen/Test Match Cycle", priority = 84)]
        public static void Run()
        {
            var host = new GameObject("MatchCycleTest");

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260824;
                boot.Boot();

                Step(boot, 240);

                MatchDirector match = boot.Simulation.GetSystem<MatchDirector>();
                if (match == null)
                {
                    Debug.LogError("[cycle] no match director");
                    return;
                }

                int bodies = CountBodies(boot, out int activeBefore);
                Debug.Log($"[cycle] before: {activeBefore}/{bodies} bodies active");

                // Kill a handful and run their death scenes right through the sink.
                int killed = 0;
                foreach (AgentEntity agent in boot.Context.Entities.All)
                {
                    if (killed >= 5) break;
                    if (!agent.IsAlive) continue;

                    var death = agent.GetComponent<AgentDeathVisual>();
                    if (death == null) continue;

                    agent.Flags &= ~AgentFlags.Alive;
                    death.Play(Vector3.forward);

                    // Straight past the linger and the sink, which is what disables the body.
                    for (int i = 0; i < 3000; i++) death.Advance(1f / 60f);
                    killed++;
                }

                CountBodies(boot, out int activeAfterDeath);
                Debug.Log($"[cycle] after {killed} deaths and full sink: {activeAfterDeath}/{bodies} active");

                bool sank = activeAfterDeath == activeBefore - killed;
                Debug.Log($"[cycle] sinking disables the body: {(sank ? "PASS" : "FAIL")}");

                // Now the next match.
                match.StartMatch(boot.Simulation.Time);
                Step(boot, 60);

                CountBodies(boot, out int activeAfterRestart);
                Debug.Log($"[cycle] after next match: {activeAfterRestart}/{bodies} active");

                bool restored = activeAfterRestart == bodies;
                Debug.Log($"[cycle] bodies restored for the next match: {(restored ? "PASS" : "FAIL")}");

                int aliveAfter = 0;
                foreach (AgentEntity agent in boot.Context.Entities.All)
                    if (agent.IsAlive)
                        aliveAfter++;

                bool revived = aliveAfter == boot.Context.Entities.Count;
                Debug.Log($"[cycle] all agents alive again: {aliveAfter}/{boot.Context.Entities.Count} " +
                          $"{(revived ? "PASS" : "FAIL")}");

                if (sank && restored && revived) Debug.Log("[cycle] PASSED");
                else Debug.LogError("[cycle] FAILED");
            }
            finally
            {
                UnseenBootstrap boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>Counts agents that own a body, and how many of those bodies are switched on.</summary>
        private static int CountBodies(UnseenBootstrap boot, out int active)
        {
            int total = 0;
            active = 0;

            foreach (AgentEntity agent in boot.Context.Entities.All)
            {
                var visual = agent.GetComponentInChildren<AgentVisual>(true);
                if (visual == null) continue;

                total++;
                if (visual.gameObject.activeInHierarchy) active++;
            }

            return total;
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
