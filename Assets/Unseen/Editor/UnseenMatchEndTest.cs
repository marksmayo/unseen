using UnityEditor;
using UnityEngine;
using Unseen.BattleRoyale;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Net;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Forces a match to its conclusion and checks the result reaches a client.
    ///
    /// Playing a match out honestly would take ten minutes of simulated time and roughly
    /// twenty-five of real time, which is not a test anyone will run. Killing everyone but one
    /// agent reaches the same state in a second and exercises the part that matters: the winner is
    /// decided, the phase advances, and the placement, kill count and countdown arrive in the
    /// snapshot rather than only existing on the server.
    /// </summary>
    public static class UnseenMatchEndTest
    {
        [MenuItem("Unseen/Test Match End", priority = 86)]
        public static void Run()
        {
            var host = new GameObject("MatchEndTest");

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260824;
                boot.Boot();

                Step(boot, 300);

                MatchDirector match = boot.Simulation.GetSystem<MatchDirector>();
                AgentEntity local = null;
                foreach (AgentEntity agent in boot.Context.Entities.All)
                    if (agent.ConnectionId >= 0)
                        local = agent;

                if (match == null || local == null)
                {
                    Debug.LogError("[end] need a match director and a local player");
                    return;
                }

                // Eliminate everyone else so the local player is last standing. Kills are not
                // forced: the counter is owned by the combat director and only it may write it, so
                // the test reports whatever the server holds and checks that the same number is
                // what reaches the client.
                int killed = 0;
                foreach (AgentEntity agent in boot.Context.Entities.All)
                {
                    if (agent == local || !agent.IsAlive) continue;
                    agent.Flags &= ~AgentFlags.Alive;
                    match.NotifyDeath(agent, local);
                    killed++;
                }

                Debug.Log($"[end] eliminated {killed}, leaving {local.DisplayName} alive");

                Step(boot, 120);

                Debug.Log($"[end] phase {match.Phase}, winner {match.Winner}, " +
                          $"local placement {local.Placement}, kills {local.Kills}");

                bool ended = match.Phase == MatchPhase.PostMatch;
                bool crowned = match.Winner == local.Id;

                // And the part that actually matters: did any of it reach the client?
                SnapshotData snapshot = Snapshot(boot);
                bool replicated = snapshot != null &&
                                  snapshot.Winner == local.Id &&
                                  snapshot.SelfKills == local.Kills &&
                                  (MatchPhase)snapshot.MatchPhase == MatchPhase.PostMatch;

                if (snapshot != null)
                    Debug.Log($"[end] snapshot: phase {(MatchPhase)snapshot.MatchPhase}, " +
                              $"winner {snapshot.Winner}, placement {snapshot.SelfPlacement}, " +
                              $"kills {snapshot.SelfKills}, countdown {snapshot.PhaseSecondsRemaining:0.0}s");
                else
                    Debug.LogWarning("[end] no snapshot reached the client view");

                Debug.Log($"[end] match ended:        {(ended ? "PASS" : "FAIL")}");
                Debug.Log($"[end] winner is the player:{(crowned ? "PASS" : "FAIL")}");
                Debug.Log($"[end] result replicated:  {(replicated ? "PASS" : "FAIL")}");

                // The next match must follow, or the loop dead-ends where it used to.
                Step(boot, Mathf.RoundToInt((match.PostMatchDuration + 2f) * 60f));
                bool looped = match.Phase != MatchPhase.PostMatch && match.MatchNumber >= 2;
                Debug.Log($"[end] rolled into match {match.MatchNumber} ({match.Phase}): " +
                          $"{(looped ? "PASS" : "FAIL")}");

                if (ended && crowned && replicated && looped) Debug.Log("[end] PASSED");
                else Debug.LogError("[end] FAILED");
            }
            finally
            {
                UnseenBootstrap boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>Most recent snapshot the client view decoded, if the rig built one.</summary>
        private static SnapshotData Snapshot(UnseenBootstrap boot)
        {
            var view = boot.GetComponentInChildren<Client.ClientNetworkView>();
            return view != null ? view.Latest : null;
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
