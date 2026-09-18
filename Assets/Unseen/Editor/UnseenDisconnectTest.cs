using System;
using UnityEditor;
using UnityEngine;
using Unseen.BattleRoyale;
using Unseen.Combat;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Net;

namespace Unseen.EditorTools
{
    /// <summary>
    /// What happens to a body when the person driving it goes away.
    ///
    /// It used to be handed to a bot, which kept the match full and was wrong three times over. It
    /// rewarded the disconnect - a player losing a fight could pull the cable and have the problem
    /// taken over by an AI that did not know it was losing. It made the results table lie, because
    /// the row read "bot-014 finished fourth" about a player who was never in the match under that
    /// name. And the body kept playing, so somebody could be stalked and killed by a ninja whose
    /// player left ten minutes earlier - in a game whose entire loop is working out who you are
    /// looking at.
    ///
    /// Now the body is simply abandoned: it stands where it was left, alive and killable, for a
    /// minute. Come back inside that and it is yours again. Leave it and it dies. Let somebody find
    /// it first and it dies the way anything else does, with their name on it.
    ///
    /// Three scenarios, three separate matches, because a body can only die once and each of these
    /// is a different death. Run headlessly: all of it needs a registered agent, a match director
    /// holding placements, and the combat director's death path.
    /// </summary>
    public static class UnseenDisconnectTest
    {
        [MenuItem("Unseen/Test Disconnect", priority = 97)]
        public static void Run()
        {
            bool passed = true;

            passed &= Scenario("comes back", ComesBackInsideTheWindow);
            passed &= Scenario("never comes back", NeverComesBack);
            passed &= Scenario("found first", SomebodyFindsTheBodyFirst);

            Debug.Log(passed ? "[disconnect] PASSED" : "[disconnect] FAILED");
        }

        /// <summary>
        /// A minute of a bad hotel connection should not end somebody's game, so the body waits.
        /// Without this the grace period buys nothing: there would be no way to use it.
        /// </summary>
        private static bool ComesBackInsideTheWindow(UnseenBootstrap boot, OfflineNetworkService net)
        {
            SimContext ctx = boot.Context;

            AgentEntity player = ctx.Entities.ByConnection(boot.Network.LocalConnectionId);
            if (player == null || !player.IsAlive) return Fail("the local player should start alive");

            net.Shutdown();
            Step(boot, 30);

            bool ok = Check("the body is left standing rather than killed at once", player.IsAlive);

            ok &= Check("and is not handed to a bot",
                player.Kind != AgentKind.Bot && (player.Flags & AgentFlags.Bot) == 0);

            // Left idle rather than mid-stride. A body still running on the last input its player
            // sent would sprint off a roof on their behalf, which is a strange thing to come back
            // to and a stranger thing to be killed by.
            ok &= Check("standing still, not running on somebody's last input",
                Unity.Mathematics.math.lengthsq(player.Intent.Move) < 1e-6f);

            net.Start();
            Step(boot, 10);

            ok &= Check("rejoining inside the window gets the same body back",
                ctx.Entities.ByConnection(boot.Network.LocalConnectionId) == player);

            ok &= Check("and it is still alive to come back to", player.IsAlive);

            return ok;
        }

        private static bool NeverComesBack(UnseenBootstrap boot, OfflineNetworkService net)
        {
            SimContext ctx = boot.Context;

            AgentEntity player = ctx.Entities.ByConnection(boot.Network.LocalConnectionId);
            if (player == null || !player.IsAlive) return Fail("the local player should start alive");

            int aliveBefore = ctx.Entities.AliveCount;

            net.Shutdown();
            Step(boot, (int)(60f * PlayerSeatSystem.GraceSeconds) + 180);

            bool ok = Check("a body nobody comes back for dies when the window closes", !player.IsAlive);

            ok &= Check("the cause says what happened", player.DeathCause == DamageKind.Disconnected);
            ok &= Check("nobody is credited with the kill", player.Killer == AgentId.None);
            ok &= Check("it takes a placement like any other elimination", player.Placement > 0);
            ok &= Check("and the match is one player shorter", ctx.Entities.AliveCount == aliveBefore - 1);

            // Past the window there is nothing to come back to. Rejoining mid-round never hands out
            // a fresh body, or leaving and returning would be the same escape by a longer route.
            net.Start();
            Step(boot, 10);

            ok &= Check("rejoining after it does not hand out a new one",
                ctx.Entities.ByConnection(boot.Network.LocalConnectionId) == null);

            return ok;
        }

        /// <summary>
        /// The body is vulnerable for the whole minute, and if somebody finds it, bad luck.
        ///
        /// This is the part that keeps the grace period from being an exploit. A window in which
        /// the body cannot be hurt would be a free minute of invulnerability available to anybody
        /// willing to pull a cable at the right moment.
        /// </summary>
        private static bool SomebodyFindsTheBodyFirst(UnseenBootstrap boot, OfflineNetworkService net)
        {
            SimContext ctx = boot.Context;

            AgentEntity player = ctx.Entities.ByConnection(boot.Network.LocalConnectionId);
            if (player == null || !player.IsAlive) return Fail("the local player should start alive");

            AgentEntity killer = null;
            foreach (AgentEntity a in ctx.Entities.All)
                if (a != null && a != player && a.IsAlive) { killer = a; break; }

            if (killer == null) return Fail("need somebody else alive to do the killing");

            net.Shutdown();
            Step(boot, 30);

            if (!player.IsAlive) return Fail("the body should still be standing at this point");

            ctx.Combat.ApplyDamage(new DamageInfo
            {
                Attacker = killer.Id,
                Victim = player.Id,
                Kind = DamageKind.Melee,
                Amount = 10000f,
                Point = player.TorsoPosition,
                Direction = player.Forward
            });

            Step(boot, 10);

            bool ok = Check("an abandoned body can be killed during the window", !player.IsAlive);

            ok &= Check("and it is an ordinary kill, not a disconnect",
                player.DeathCause == DamageKind.Melee);

            ok &= Check("with the killer credited", player.Killer == killer.Id);

            net.Start();
            Step(boot, 10);

            ok &= Check("coming back to a corpse is bad luck, not a new body",
                ctx.Entities.ByConnection(boot.Network.LocalConnectionId) == null);

            return ok;
        }

        // ---------------------------------------------------------------- harness

        private static bool Scenario(string name, Func<UnseenBootstrap, OfflineNetworkService, bool> body)
        {
            var host = new GameObject($"DisconnectTest-{name}");

            UnseenConfig config = UnseenConfig.Default;
            int roster = config.Match.TargetEntityCount;
            config.Match.TargetEntityCount = 8;

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260827;
                boot.Boot();

                // Well clear of the drop and into the hunt.
                Step(boot, 60 * 70);

                var net = boot.Network as OfflineNetworkService;
                if (boot.Context.Match == null || net == null)
                    return Fail("need a listen server on the offline transport");

                Debug.Log($"[disconnect] --- {name} ---");
                return body(boot, net);
            }
            finally
            {
                config.Match.TargetEntityCount = roster;
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        private static bool Check(string what, bool ok)
        {
            Debug.Log($"[disconnect] {what}: {(ok ? "PASS" : "FAIL")}");
            return ok;
        }

        private static bool Fail(string why)
        {
            Debug.LogError($"[disconnect] {why}");
            return false;
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
