using System.Collections.Generic;
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
    /// Checks that the end-of-match table tells the truth.
    ///
    /// The table is the only account a player gets of a match they lost, so what it claims has to
    /// survive the round trip through the wire format rather than being read off the server's own
    /// objects. Every assertion here is made against a DECODED snapshot - the same bytes a real
    /// client would receive - because a table assembled from server state would pass while the
    /// protocol carried nothing at all.
    ///
    /// Four separate deaths are staged with four different causes and two different killers, since
    /// a table that reported "eliminated" for everybody would pass a test that only counted rows.
    /// </summary>
    public static class UnseenResultsTest
    {
        [MenuItem("Unseen/Test Results Table", priority = 98)]
        public static void Run()
        {
            var host = new GameObject("ResultsTest");

            UnseenConfig config = UnseenConfig.Default;
            int roster = config.Match.TargetEntityCount;
            // Eight on the roster to be sure of five alive.
            //
            // This asked for exactly five and then required all five to survive seventy seconds of
            // warm-up. They do not: bots roam, the map now has a lake at the middle of it deep
            // enough to drown in, and losing one before the test starts is normal behaviour rather
            // than a fault. A test that needs five bodies should ask for more than five.
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

                // Long enough to be well clear of the drop and into the hunt.
                Step(boot, 60 * 70);

                SimContext ctx = boot.Context;
                MatchDirector match = ctx.Match;

                var alive = new List<AgentEntity>();
                foreach (AgentEntity a in ctx.Entities.All)
                    if (a != null && a.IsAlive) alive.Add(a);

                if (match == null || alive.Count < 5)
                {
                    Debug.LogError($"[results] need five live agents, have {alive.Count}");
                    return;
                }

                // Only the first five are used, and the rest are killed off so the match can end.
                for (int i = 5; i < alive.Count; i++)
                    Finish(ctx, alive[i], null, DamageKind.Mist);

                alive.RemoveRange(5, alive.Count - 5);

                AgentEntity winner = alive[0];
                AgentEntity shot = alive[1];
                AgentEntity cut = alive[2];
                AgentEntity drowned = alive[3];
                AgentEntity fell = alive[4];

                // ---------------------------------------------------------- mid-match: no table
                //
                // The standings are a kilobyte for a full lobby. Sending them during the match
                // would be waste, and a client that rendered them would be showing a results
                // screen over a live game.
                SnapshotData live = RoundTrip(boot, winner);
                bool quietDuringMatch = live.Standings.Count == 0;

                Debug.Log($"[results] mid-match snapshot carries {live.Standings.Count} standings");
                Debug.Log($"[results] the table is not sent during a match: " +
                          $"{(quietDuringMatch ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- four ways to go out
                Finish(ctx, shot, winner, DamageKind.Thrown);
                Finish(ctx, cut, winner, DamageKind.Takedown);
                Finish(ctx, drowned, null, DamageKind.Drowning);
                Finish(ctx, fell, null, DamageKind.Fall);

                // The director notices the survivor on its own tick, which is what sets placement
                // one and moves the phase on.
                for (int i = 0; i < 60 * 4 && match.Phase != MatchPhase.PostMatch; i++)
                    Tick(boot);

                if (match.Phase != MatchPhase.PostMatch)
                {
                    Debug.LogError($"[results] match never ended (phase {match.Phase})");
                    return;
                }

                // ---------------------------------------------------------- the decoded table
                SnapshotData end = RoundTrip(boot, winner);

                int roll = ctx.Entities.Count;
                bool everyone = end.Standings.Count == roll;
                Debug.Log($"[results] {end.Standings.Count} rows for a roster of {roll}: " +
                          $"{(everyone ? "PASS" : "FAIL")}");

                bool won = Find(end, winner, out Standing top) &&
                           top.Placement == 1 && !top.Died && top.Kills == 2;

                Debug.Log($"[results] winner: #{top.Placement}, {top.Kills} kills, " +
                          $"{(top.Died ? "died" : "survived")}");
                Debug.Log($"[results] the survivor is #1 with both their kills: " +
                          $"{(won ? "PASS" : "FAIL")}");

                bool named = Find(end, shot, out Standing blade) &&
                             (DamageKind)blade.Cause == DamageKind.Thrown &&
                             blade.Killer == winner.Id;

                Debug.Log($"[results] {blade.Name}: cause {(DamageKind)blade.Cause}, " +
                          $"killer {blade.Killer.Value} (winner is {winner.Id.Value})");
                Debug.Log($"[results] a shuriken kill names the thrower: " +
                          $"{(named ? "PASS" : "FAIL")}");

                bool throat = Find(end, cut, out Standing quiet) &&
                              (DamageKind)quiet.Cause == DamageKind.Takedown &&
                              quiet.Killer == winner.Id;

                Debug.Log($"[results] a takedown is not reported as a plain kill: " +
                          $"{(throat ? "PASS" : "FAIL")}");

                // Nobody drowns you and nobody pushes you. These rows must carry the cause and no
                // killer at all - a table that credited the last person to touch them would be
                // inventing an execution.
                bool water = Find(end, drowned, out Standing under) &&
                             (DamageKind)under.Cause == DamageKind.Drowning &&
                             !under.Killer.IsValid;

                bool drop = Find(end, fell, out Standing gravity) &&
                            (DamageKind)gravity.Cause == DamageKind.Fall &&
                            !gravity.Killer.IsValid;

                Debug.Log($"[results] drowning: cause {(DamageKind)under.Cause}, " +
                          $"killer valid {under.Killer.IsValid}");
                Debug.Log($"[results] a fall and a drowning have no killer: " +
                          $"{(water && drop ? "PASS" : "FAIL")}");

                // Placements are handed out as people die, so four deaths must produce four
                // distinct places and not four copies of the same number.
                var places = new HashSet<int>();
                foreach (Standing r in end.Standings) places.Add(r.Placement);

                bool ordered = places.Count == roll;
                Debug.Log($"[results] {places.Count} distinct placements across {roll} rows: " +
                          $"{(ordered ? "PASS" : "FAIL")}");

                // Names survived the wire. Empty strings would render a table of blank rows that
                // still passed every check above.
                bool namesKept = true;
                foreach (Standing r in end.Standings)
                    if (string.IsNullOrEmpty(r.Name)) namesKept = false;

                Debug.Log($"[results] names survive the round trip: " +
                          $"{(namesKept ? "PASS" : "FAIL")}");

                if (quietDuringMatch && everyone && won && named && throat && water && drop &&
                    ordered && namesKept)
                    Debug.Log("[results] PASSED");
                else
                    Debug.LogError("[results] FAILED");
            }
            finally
            {
                config.Match.TargetEntityCount = roster;

                UnseenBootstrap boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>Kills an agent outright with a named cause, the way the game would.</summary>
        private static void Finish(SimContext ctx, AgentEntity victim, AgentEntity killer,
            DamageKind kind)
        {
            ctx.Combat.ApplyDamage(new DamageInfo
            {
                Attacker = killer != null ? killer.Id : AgentId.None,
                Victim = victim.Id,
                Kind = kind,
                Amount = 10000f,
                Point = victim.TorsoPosition,
                Direction = victim.Forward
            });
        }

        /// <summary>
        /// Encodes a snapshot for one player and decodes it again, so every assertion is made
        /// against the bytes rather than against the server's own objects.
        /// </summary>
        private static SnapshotData RoundTrip(UnseenBootstrap boot, AgentEntity self)
        {
            SimContext ctx = boot.Context;

            var writer = new NetWriter(4096);
            SnapshotProtocol.EncodeSnapshot(writer, ctx, self, ctx.Tick, ctx.Time,
                ctx.Combat.Events, ctx.Combat.Events.Count,
                ctx.Destructibles.PendingEvents, ctx.Destructibles.PendingEvents.Count);

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);

            var into = new SnapshotData();
            if (!SnapshotProtocol.DecodeSnapshot(reader, into,
                    UnseenConfig.Default.Network.PositionQuantum))
                Debug.LogError("[results] the snapshot would not decode");

            return into;
        }

        private static bool Find(SnapshotData snapshot, AgentEntity agent, out Standing row)
        {
            for (int i = 0; i < snapshot.Standings.Count; i++)
            {
                if (snapshot.Standings[i].Id != agent.Id) continue;
                row = snapshot.Standings[i];
                return true;
            }

            row = default;
            return false;
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
