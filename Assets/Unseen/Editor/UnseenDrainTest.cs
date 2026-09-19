using UnityEditor;
using UnityEngine;
using Unseen.BattleRoyale;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Net;

namespace Unseen.EditorTools
{
    /// <summary>
    /// A server told to stop in the middle of a match.
    ///
    /// The rule is that a deploy must not be something the players notice. A rolling update asks
    /// every server in the fleet to go, one at a time, and a server that obeys immediately ends a
    /// match for sixty-four people - the update arriving as a crash, from their side.
    ///
    /// The unit tests around ServerLifecycle prove the rule. This proves the wiring, in a running
    /// match, which is where it would actually be wrong: a lifecycle nothing consults is a lifecycle
    /// that changes nothing.
    /// </summary>
    public static class UnseenDrainTest
    {
        [MenuItem("Unseen/Test Drain", priority = 92)]
        public static void Run()
        {
            var host = new GameObject("DrainTest");
            bool passed = true;

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.BuildNavMesh = false;
                boot.Seed = 20260827;
                boot.Boot();

                Step(boot, 60 * 70);

                SimContext ctx = boot.Context;
                var life = ctx.Get<ServerLifecycle>();

                passed &= Check("the server publishes a lifecycle at all", life != null);
                if (life == null) { Debug.Log("[drain] FAILED"); return; }

                passed &= Check("a running server is healthy", life.IsHealthy(boot.Simulation.Time));
                passed &= Check("and takes players", life.AcceptsPlayers);

                int aliveBefore = ctx.Entities.AliveCount;
                bool matchRunning = ctx.Match.Phase != MatchPhase.Lobby &&
                                    ctx.Match.Phase != MatchPhase.PostMatch;

                passed &= Check("with a match actually in progress", matchRunning);

                // The deploy arrives.
                life.Drain(boot.Simulation.Time);
                Step(boot, 120);

                passed &= Check("it knows it is going", life.IsDraining);

                // And the match carries on regardless, which is the entire point.
                passed &= Check("the match keeps running",
                    ctx.Match.Phase != MatchPhase.Lobby && ctx.Match.Phase != MatchPhase.PostMatch);

                passed &= Check("and nobody was dropped",
                    ctx.Entities.AliveCount >= aliveBefore - 2);

                passed &= Check("but no new player would be seated", !life.AcceptsPlayers);

                // A body arriving now is turned away rather than loaded into a server that is
                // about to go.
                var offline = boot.Network as OfflineNetworkService;
                if (offline != null)
                {
                    AgentEntity local = ctx.Entities.ByConnection(boot.Network.LocalConnectionId);
                    if (local != null) offline.Shutdown();

                    Step(boot, 30);
                    offline.Start();
                    Step(boot, 30);

                    passed &= Check("a fresh arrival is not given a seat while draining",
                        ctx.Entities.ByConnection(boot.Network.LocalConnectionId) == null ||
                        ctx.Entities.ByConnection(boot.Network.LocalConnectionId) == local);
                }

                Debug.Log(passed ? "[drain] PASSED" : "[drain] FAILED");
            }
            finally
            {
                Object.DestroyImmediate(host);

                foreach (MapDescriptor map in Object.FindObjectsByType<MapDescriptor>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (map != null) Object.DestroyImmediate(map.gameObject);

                GameObject town = GameObject.Find("GreyboxTown");
                if (town != null) Object.DestroyImmediate(town);
            }
        }

        private static bool Check(string what, bool ok)
        {
            Debug.Log($"[drain] {what}: {(ok ? "PASS" : "FAIL")}");
            return ok;
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
