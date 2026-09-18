using UnityEditor;
using UnityEngine;
using Unseen.BattleRoyale;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Net;

namespace Unseen.EditorTools
{
    /// <summary>
    /// What happens to a body when the person driving it goes away.
    ///
    /// It used to be handed to a bot, which kept the match full and was wrong in two ways at once.
    /// It rewarded the disconnect - a player losing a fight could pull the cable and have the
    /// problem solved by an AI that did not know it was losing - and it made the results table lie,
    /// because the row said "bot-014" finished fourth and no such player was ever in the match.
    /// Worse, the body kept playing: somebody could be killed by a ninja whose player left ten
    /// minutes earlier, in a game whose whole loop is working out who you are looking at.
    ///
    /// So a disconnect kills. It goes through the ordinary damage path rather than deleting the
    /// agent, because everything downstream - placement, the kill feed, the standings row, the
    /// death animation other players see - already knows how to handle a death and knows nothing
    /// about an agent that simply stops existing.
    ///
    /// Run headlessly, because it needs a real match: a registered agent, a match director holding
    /// placements, and the combat director's death path. None of that survives an EditMode test.
    /// </summary>
    public static class UnseenDisconnectTest
    {
        [MenuItem("Unseen/Test Disconnect", priority = 97)]
        public static void Run()
        {
            var host = new GameObject("DisconnectTest");

            UnseenConfig config = UnseenConfig.Default;
            int roster = config.Match.TargetEntityCount;
            config.Match.TargetEntityCount = 8;

            bool passed = true;

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260827;
                boot.Boot();

                Step(boot, 60 * 70);

                SimContext ctx = boot.Context;
                var offline = boot.Network as OfflineNetworkService;

                if (ctx.Match == null || offline == null)
                {
                    Debug.LogError("[disconnect] need a listen server with the offline transport");
                    return;
                }

                AgentEntity player = ctx.Entities.ByConnection(boot.Network.LocalConnectionId);
                if (player == null || !player.IsAlive)
                {
                    Debug.LogError("[disconnect] the local player should be alive before the test");
                    return;
                }

                AgentId id = player.Id;
                int aliveBefore = ctx.Entities.AliveCount;

                offline.Shutdown();
                Step(boot, 10);

                AgentEntity body = ctx.Entities.Get(id);

                passed &= Check("the body dies rather than carrying on",
                    body != null && !body.IsAlive);

                passed &= Check("and is not handed to a bot",
                    body != null && body.Kind != AgentKind.Bot &&
                    (body.Flags & AgentFlags.Bot) == 0);

                passed &= Check("the cause says what happened",
                    body != null && body.DeathCause == DamageKind.Disconnected);

                passed &= Check("nobody is credited with the kill",
                    body != null && body.Killer == AgentId.None);

                passed &= Check("it takes a placement like any other elimination",
                    body != null && body.Placement > 0);

                passed &= Check("and the match is one player shorter",
                    ctx.Entities.AliveCount == aliveBefore - 1);

                // Rejoining mid-round gets no body. Handing one over would be the same reward for
                // disconnecting by a longer route: leave a losing fight, come back somewhere else.
                offline.Start();
                Step(boot, 10);

                passed &= Check("and rejoining mid-round does not hand out a new one",
                    ctx.Entities.ByConnection(boot.Network.LocalConnectionId) == null);

                Debug.Log(passed ? "[disconnect] PASSED" : "[disconnect] FAILED");
            }
            finally
            {
                config.Match.TargetEntityCount = roster;
                Object.DestroyImmediate(host);
            }
        }

        private static bool Check(string what, bool ok)
        {
            Debug.Log($"[disconnect] {what}: {(ok ? "PASS" : "FAIL")}");
            return ok;
        }

        private static void Tick(UnseenBootstrap boot)
        {
            boot.Network.Poll(1f / 60f);
            boot.Simulation.Advance(1f / 60f);
        }

        private static void Step(UnseenBootstrap boot, int ticks)
        {
            for (int i = 0; i < ticks; i++) Tick(boot);
        }
    }
}
