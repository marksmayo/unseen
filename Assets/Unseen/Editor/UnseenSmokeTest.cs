using System;
using UnityEditor;
using UnityEngine;
using Unseen.AI;
using Unseen.Audio;
using Unseen.BattleRoyale;
using Unseen.Combat;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Movement;
using Unseen.Net;
using Unseen.Perception;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Boots a real 64-entity match and pumps the authoritative loop for a fixed number of simulated
    /// seconds, without a play-mode session. This is the cheapest way to find out whether the
    /// simulation actually runs, as opposed to merely compiling: every system, the greybox
    /// generator, agent spawning, perception, bots, motion, combat and replication all execute.
    ///
    /// Run it from the menu, or in batch mode:
    ///   Unity -batchmode -nographics -quit -projectPath . \
    ///         -executeMethod Unseen.EditorTools.UnseenSmokeTest.RunHeadlessMatch
    /// </summary>
    public static class UnseenSmokeTest
    {
        // Long enough to get past the ~19 s glider descent and into the hunt, where footsteps,
        // looting, fights and mist damage actually happen. Twenty seconds only tested the drop.
        private const float SimulatedSeconds = 120f;

        [MenuItem("Unseen/Run Headless Smoke Test", priority = 80)]
        public static void RunHeadlessMatch()
        {
            var host = new GameObject("SmokeTestBootstrap");
            int exceptions = 0;
            Application.LogCallback handler = (condition, trace, type) =>
            {
                if (type == LogType.Exception || type == LogType.Error) exceptions++;
            };

            Application.logMessageReceived += handler;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.DedicatedServer; // no client rig, no camera, no HUD
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = true;
                boot.Seed = 20260824;

                // Awake does not run in edit mode, so boot explicitly.
                boot.Boot();

                ServerSimulation sim = boot.Simulation;
                INetworkService net = boot.Network;
                if (sim == null)
                {
                    Debug.LogError("[smoke] bootstrap produced no simulation");
                    Fail();
                    return;
                }

                // Simulate one client joining, so the replication path is exercised too: without a
                // connection the server encodes nothing and snapshot encoding goes untested.
                (net as OfflineNetworkService)?.Start();

                float step = 1f / 60f;
                int steps = Mathf.RoundToInt(SimulatedSeconds / step);

                // Sample who is inside geometry as the match runs, and in what locomotion state.
                // "Bots clip through walls" has several possible causes with different fixes:
                // tunnelling at the cold 20 Hz step, the motion warps that move the transform with
                // the controller disabled (mantle, takedown), or simply walking through shoji that
                // have been sliced open - which is correct behaviour.
                var embeddedByState = new System.Collections.Generic.Dictionary<LocomotionState, int>();
                int embeddedSamples = 0, totalSamples = 0;
                float worstRange = 0f, lowestY = float.MaxValue;
                float worstDepth = 0f;
                string worstDetail = "none";

                for (int i = 0; i < steps; i++)
                {
                    net.Poll(step);
                    sim.Advance(step);

                    if (i % 30 != 0) continue; // sample twice a second

                    foreach (AgentEntity agent in boot.Context.Entities.All)
                    {
                        if (!agent.IsAlive) continue;
                        totalSamples++;

                        // Boundary: nobody should ever be outside the rampart or under the world.
                        Vector3 flat = (Vector3)agent.Position - boot.Map.Center;
                        flat.y = 0f;
                        worstRange = Mathf.Max(worstRange, flat.magnitude);
                        lowestY = Mathf.Min(lowestY, agent.Position.y);

                        float radius = boot.Context.Config.Movement.Radius;
                        float height = boot.Context.Config.StanceHeight(agent.Stance);
                        Vector3 bottom = (Vector3)agent.Position + Vector3.up * (radius + 0.05f);
                        Vector3 top = (Vector3)agent.Position + Vector3.up * Mathf.Max(height - radius, radius + 0.1f);

                        Collider[] hits = Physics.OverlapCapsule(bottom, top, radius * 0.9f,
                            UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore);
                        if (hits.Length == 0) continue;

                        embeddedSamples++;
                        embeddedByState.TryGetValue(agent.Locomotion, out int n);
                        embeddedByState[agent.Locomotion] = n + 1;

                        // Depth of the worst intrusion, to tell a graze from being fully inside.
                        foreach (Collider c in hits)
                        {
                            float depth = Vector3.Distance(c.ClosestPoint(agent.Position), (Vector3)agent.Position);
                            if (depth <= worstDepth) continue;
                            worstDepth = depth;
                            worstDetail = $"{agent.DisplayName} in '{c.name}' (layer {c.gameObject.layer}) " +
                                          $"while {agent.Locomotion}";
                        }
                    }
                }

                var breakdown = new System.Collections.Generic.List<string>();
                foreach (var kv in embeddedByState) breakdown.Add($"{kv.Key}={kv.Value}");

                Debug.Log($"[smoke] wall intrusion: {embeddedSamples}/{totalSamples} agent-samples inside " +
                          $"geometry | by state: {(breakdown.Count > 0 ? string.Join(", ", breakdown) : "none")} " +
                          $"| worst {worstDepth:0.00} m: {worstDetail}");

                var bounds = sim.GetSystem<WorldBoundsSystem>();
                float allowed = boot.Map != null ? boot.Map.Radius : 0f;
                string boundsVerdict = worstRange <= allowed + 0.5f ? "INSIDE" : "ESCAPED";
                Debug.Log($"[smoke] bounds: furthest agent {worstRange:0.0} m from centre " +
                          $"(playable radius {allowed:0.0} m) -> {boundsVerdict} | " +
                          $"lowest y {lowestY:0.0} m | clamp corrections " +
                          $"{(bounds != null ? bounds.Corrections : -1)}");
                if (boundsVerdict != "INSIDE") exceptions++;

                Debug.Log($"[smoke] system cost: {sim.DescribeSystemCost()}");

                stopwatch.Stop();
                Report(boot, sim, exceptions, stopwatch.Elapsed.TotalSeconds);
                ReportCollisionHealth(boot);
            }
            catch (Exception e)
            {
                Debug.LogError($"[smoke] threw: {e}");
                exceptions++;
            }
            finally
            {
                Application.logMessageReceived -= handler;
                host.GetComponent<UnseenBootstrap>()?.Shutdown();
                UnityEngine.Object.DestroyImmediate(host);
            }

            if (exceptions > 0)
            {
                Debug.LogError($"[smoke] FAILED with {exceptions} error(s)/exception(s)");
                Fail();
                return;
            }

            Debug.Log("[smoke] PASSED");
        }

        /// <summary>
        /// Counts agents standing inside solid geometry, and drives one agent hard into a wall to see
        /// whether it is actually stopped. "I can walk through walls" needs a measurement, not a
        /// theory - it could equally be spawning inside geometry, a layer matrix mistake, or a
        /// collider that does not match its mesh.
        /// </summary>
        private static void ReportCollisionHealth(UnseenBootstrap boot)
        {
            SimContext ctx = boot.Context;
            UnseenConfig cfg = ctx.Config;
            int embedded = 0;

            foreach (AgentEntity agent in ctx.Entities.All)
            {
                if (!agent.IsAlive) continue;

                float radius = cfg.Movement.Radius;
                Vector3 bottom = (Vector3)agent.Position + Vector3.up * (radius + 0.05f);
                Vector3 top = (Vector3)agent.Position + Vector3.up * Mathf.Max(cfg.StanceHeight(agent.Stance) - radius, radius + 0.1f);

                if (Physics.CheckCapsule(bottom, top, radius * 0.9f,
                        UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                    embedded++;
            }

            // Wall test: find a wall, stand an agent in front of it, and push straight at it.
            string wallVerdict = "no wall found to test";
            var walls = UnityEngine.Object.FindObjectsByType<BoxCollider>(FindObjectsSortMode.None);
            BoxCollider wall = null;
            foreach (BoxCollider candidate in walls)
            {
                if (candidate.gameObject.layer != UnseenLayers.Occluder) continue;
                if (candidate.size.y < 3f) continue;
                wall = candidate;
                break;
            }

            AgentEntity tester = ctx.Entities.Count > 0 ? ctx.Entities.BySlot(0) : null;
            if (wall != null && tester != null && tester.Motor != null)
            {
                Vector3 normal = wall.transform.forward;
                if (Mathf.Abs(wall.size.x) < Mathf.Abs(wall.size.z)) normal = wall.transform.right;

                Vector3 start = wall.bounds.center + normal * 3f;
                start.y = wall.bounds.min.y + 0.1f;

                tester.Motor.Teleport(start);
                float yaw = UnseenMath.ForwardToYaw(-normal);
                tester.Yaw = yaw;

                Vector3 before = tester.Position;
                for (int i = 0; i < 120; i++)
                {
                    tester.Intent = new MoveIntent
                    {
                        Sequence = (uint)i,
                        Move = new Unity.Mathematics.float2(0f, 1f),
                        Yaw = yaw,
                        Sprint = true,
                        Zone = GuardZone.Mid
                    };

                    tester.Motor.Simulate(ctx, 1f / 60f, i, i / 60f);
                }

                Vector3 after = tester.Position;
                float travelled = Vector3.Distance(before, after);
                float intoWall = Vector3.Dot(after - wall.bounds.center, normal);
                bool passedThrough = intoWall < 0f;

                wallVerdict = passedThrough
                    ? $"FAILED - passed through (travelled {travelled:0.00} m, now {intoWall:0.00} m past the surface)"
                    : $"held (travelled {travelled:0.00} m, stopped {intoWall:0.00} m from centre)";
            }

            // Shoji are walls too, as far as a player is concerned: an intact panel must block, and a
            // sliced one must not. Both halves of that matter - the quiet way in is cutting one open.
            string shojiVerdict = "no panel found to test";
            Environment.ShojiPanel panel = null;
            foreach (Environment.ShojiPanel candidate in Environment.ShojiPanel.All)
            {
                if (candidate.State == Environment.ShojiState.Intact && candidate.PaperCollider != null)
                {
                    panel = candidate;
                    break;
                }
            }

            if (panel != null && tester != null && tester.Motor != null)
            {
                shojiVerdict = PushThroughPanel(ctx, tester, panel, "intact");
                panel.Slice();
                shojiVerdict += "; after slicing: " + PushThroughPanel(ctx, tester, panel, "sliced");
            }

            Debug.Log($"[smoke] collision: {embedded} agent(s) embedded in geometry; wall test {wallVerdict}");
            Debug.Log($"[smoke] shoji: {shojiVerdict}");
        }

        /// <summary>Drives an agent straight at a panel and reports how far past it they ended up.</summary>
        private static string PushThroughPanel(SimContext ctx, AgentEntity tester, Environment.ShojiPanel panel, string label)
        {
            Transform t = panel.PaperCollider.transform;
            Vector3 normal = t.forward;
            Vector3 start = t.position + normal * 2.5f;
            start.y = panel.PaperCollider.bounds.min.y + 0.1f;

            tester.Motor.Teleport(start);
            float yaw = UnseenMath.ForwardToYaw(-normal);
            tester.Yaw = yaw;

            for (int i = 0; i < 120; i++)
            {
                tester.Intent = new MoveIntent
                {
                    Sequence = (uint)i,
                    Move = new Unity.Mathematics.float2(0f, 1f),
                    Yaw = yaw,
                    Sprint = true,
                    Zone = GuardZone.Mid
                };

                tester.Motor.Simulate(ctx, 1f / 60f, i, i / 60f);
            }

            float past = Vector3.Dot((Vector3)tester.Position - t.position, normal);
            bool through = past < -0.1f;
            return $"{label} {(through ? "PASSED THROUGH" : "blocked")} ({past:0.00} m from the panel)";
        }

        private static void Report(UnseenBootstrap boot, ServerSimulation sim, int exceptions, double wallSeconds)
        {
            SimContext ctx = boot.Context;
            MatchDirector match = boot.Match;

            var pockets = FindSystem<CombatPocketSystem>(sim);
            var motion = FindSystem<MotionSystem>(sim);
            var interest = FindSystem<InterestManager>(sim);
            var bots = FindSystem<BotDirector>(sim);
            var replication = FindSystem<ReplicationSystem>(sim);
            var acoustics = FindSystem<AcousticPropagation>(sim);

            Debug.Log(
                $"[smoke] simulated {SimulatedSeconds:0}s in {wallSeconds:0.0}s wall " +
                $"({sim.Tick} ticks, {sim.LastFrameMilliseconds:0.00} ms last tick)\n" +
                $"[smoke] entities {ctx.Entities.Count} alive {ctx.Entities.AliveCount} " +
                $"bots {ctx.Entities.BotCount}\n" +
                $"[smoke] match {match?.Phase} #{match?.MatchNumber} " +
                $"mist stage {ctx.Mist?.Stage} r={ctx.Mist?.CurrentRadius:0}\n" +
                $"[smoke] perception: {interest?.DescribeLoad()}\n" +
                $"[smoke] pockets {pockets?.PocketCount} hot {pockets?.HotAgents} " +
                $"motion-hot {motion?.HotAgentsLastTick}\n" +
                $"[smoke] bots: {bots?.Describe()}\n" +
                $"[smoke] acoustics: paths traced {acoustics?.TotalPathsTraced} " +
                $"sounds delivered {acoustics?.TotalSoundsDelivered}\n" +
                $"[smoke] combat: swings {ctx.Combat?.TotalSwings} hits {ctx.Combat?.TotalHits} " +
                $"parries {ctx.Combat?.TotalParries} takedowns {ctx.Combat?.TotalTakedowns} " +
                $"deaths {ctx.Combat?.TotalDeaths}\n" +
                $"[smoke] replication snapshots {replication?.SnapshotsLastTick} " +
                $"bytes {replication?.TotalBytesSent}\n" +
                $"[smoke] errors {exceptions}");
        }

        /// <summary>Reaches into the simulation for one system, for reporting only.</summary>
        private static T FindSystem<T>(ServerSimulation sim) where T : class, ISimSystem
        {
            return sim.GetSystem<T>();
        }

        private static void Fail()
        {
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
}
