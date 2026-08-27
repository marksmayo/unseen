using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks that a wall with a lip on it can actually be got over.
    ///
    /// Reported from play: climbing a wall or grappling to just under a roof edge left the player
    /// apparently standing in mid-air against the wall, with no way up and nothing to do but crouch
    /// and fall. A traversal system that can reach a ledge and not pass it is worse than one that
    /// cannot reach it - the player has been shown the roof and then refused it.
    ///
    /// The assertion is the outcome and nothing else: start at the foot of a wall, hold forward and
    /// jump, and end up standing ON TOP of it. How that happens - climb, grab, mantle, or some
    /// sequence of the three - is the motor's business.
    /// </summary>
    public static class UnseenLedgeTest
    {
        [MenuItem("Unseen/Test Ledges", priority = 95)]
        public static void Run()
        {
            var host = new GameObject("LedgeTest");

            UnseenConfig config = UnseenConfig.Default;
            int roster = config.Match.TargetEntityCount;
            config.Match.TargetEntityCount = 4;

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260824;
                boot.Boot();

                Step(boot, 60 * 70);

                // The HUMAN slot, not a bot.
                //
                // BotDirector fills a bot's Intent during the tick, so a scripted intent set before
                // Advance is overwritten before the motor ever reads it and the test ends up
                // measuring whatever the bot felt like doing. Every scripted-input test in this
                // project has to drive the human.
                AgentEntity subject = null;
                foreach (AgentEntity agent in boot.Context.Entities.All)
                    if (agent.IsAlive && !agent.IsBot) { subject = agent; break; }

                if (subject == null)
                {
                    Debug.LogError("[ledge] no live agent");
                    return;
                }

                int attempted = 0;
                int topped = 0;
                int stranded = 0;
                float bestGain = 0f;

                // Several walls around the town rather than one, because the shape of what is on
                // top - a flat roof, a parapet, an eave - is exactly what decides whether the old
                // code found the lip, and one sample would hide that.
                for (int attempt = 0; attempt < 40 && attempted < 8; attempt++)
                {
                    if (!FindWallFace(attempt, out Vector3 stand, out Vector3 into, out float topY))
                        continue;

                    attempted++;

                    subject.Motor.Teleport(new float3(stand.x, stand.y, stand.z));
                    Drive(boot, subject, 30, Vector2.zero, jump: false, yaw: YawOf(into));

                    float startY = subject.Position.y;

                    // Forward and jump, held. That is what a player does at a wall.
                    //
                    // Traced on the first attempt only: which states it passes through and how high
                    // it gets in each is the whole diagnosis, and a summary of start and end height
                    // cannot distinguish "never climbed" from "climbed and fell off the top".
                    // A jump to get onto the wall, then forward to go up it.
                    //
                    // Pulsing jump for the whole attempt threw the subject off the top the moment
                    // it arrived: it climbed, stood on the wall, and the next scheduled jump sent
                    // it forward over the far edge. A player jumps at the wall once and then leans
                    // on the stick.
                    Drive(boot, subject, 45, new Vector2(0f, 1f), jump: true, yaw: YawOf(into),
                        trace: attempted == 1);
                    Drive(boot, subject, 170, new Vector2(0f, 1f), jump: false, yaw: YawOf(into),
                        trace: attempted == 1);

                    // Then let go and stand still.
                    //
                    // Holding jump for the whole attempt meant that the instant it arrived on the
                    // wall it jumped straight off again - forward, over the far edge, because
                    // forward was held too. That is the test walking its own subject off the roof,
                    // not the roof failing to hold it. A player stops pressing when they are up.
                    Drive(boot, subject, 90, Vector2.zero, jump: false, yaw: YawOf(into),
                        trace: attempted == 1);

                    float endY = subject.Position.y;
                    float gain = endY - startY;
                    if (gain > bestGain) bestGain = gain;

                    // On top: level with the roof surface, and standing on something.
                    bool onTop = endY > topY - 0.6f &&
                                 Physics.Raycast(subject.Position + new float3(0f, 0.3f, 0f),
                                     Vector3.down, 1.2f, UnseenLayers.WorldGeometry,
                                     QueryTriggerInteraction.Ignore);

                    // Stranded: gained real height but ended up neither on top nor back down.
                    bool inLimbo = !onTop && gain > 1.5f;

                    if (onTop) topped++;
                    if (inLimbo) stranded++;

                    Debug.Log($"[ledge] wall top {topY:0.0} m: started {startY:0.0}, " +
                              $"ended {endY:0.0} (gain {gain:0.0}), " +
                              $"{(onTop ? "ON TOP" : inLimbo ? "STRANDED" : "back down")}, " +
                              $"locomotion={subject.Locomotion}");
                }

                if (attempted == 0)
                {
                    Debug.LogError("[ledge] found no climbable wall face to test against");
                    return;
                }

                bool climbs = topped >= attempted / 2;
                bool noLimbo = stranded == 0;

                Debug.Log($"[ledge] {topped}/{attempted} walls topped, {stranded} stranded, " +
                          $"best height gained {bestGain:0.0} m");
                Debug.Log($"[ledge] a held forward-and-jump gets over a ledge: " +
                          $"{(climbs ? "PASS" : "FAIL")}");
                Debug.Log($"[ledge] nobody is left standing in mid-air: {(noLimbo ? "PASS" : "FAIL")}");

                if (climbs && noLimbo) Debug.Log("[ledge] PASSED");
                else Debug.LogError("[ledge] FAILED");
            }
            finally
            {
                config.Match.TargetEntityCount = roster;

                UnseenBootstrap boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// Finds somewhere to stand at the foot of a climbable wall, and reports how high its top
        /// is. Measured rather than hard-coded, so the test does not quietly stop testing anything
        /// when the layout moves.
        /// </summary>
        private static bool FindWallFace(int seed, out Vector3 stand, out Vector3 into, out float topY)
        {
            stand = Vector3.zero;
            into = Vector3.forward;
            topY = 0f;

            float angle = seed * 53f * Mathf.Deg2Rad;
            float reach = 30f + seed * 6f;
            var from = new Vector3(Mathf.Sin(angle) * reach, 50f, Mathf.Cos(angle) * reach);

            if (!Physics.Raycast(from, Vector3.down, out RaycastHit ground, 90f,
                    UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                return false;

            if (ground.normal.y < 0.9f) return false;

            var bearings = new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left };

            foreach (Vector3 dir in bearings)
            {
                var chest = ground.point + Vector3.up * 1.2f;

                // Something climbable within a stride.
                if (!Physics.Raycast(chest, dir, out RaycastHit wall, 3.5f,
                        UnseenLayers.Climb, QueryTriggerInteraction.Ignore))
                    continue;

                // And a top to it, between two and seven metres up - a wall worth climbing rather
                // than a kerb or a keep.
                var above = wall.point + dir * 0.3f + Vector3.up * 9f;
                if (!Physics.Raycast(above, Vector3.down, out RaycastHit top, 12f,
                        UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                    continue;

                float height = top.point.y - ground.point.y;
                if (height < 2f || height > 7f) continue;
                if (Vector3.Dot(top.normal, Vector3.up) < 0.6f) continue;

                stand = wall.point - dir * 0.6f;
                stand.y = ground.point.y;
                into = dir;
                topY = top.point.y;
                return true;
            }

            return false;
        }

        private static float YawOf(Vector3 forward)
        {
            return Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        }

        private static void Drive(UnseenBootstrap boot, AgentEntity agent, int ticks,
            Vector2 move, bool jump, float yaw, bool trace = false)
        {
            const float step = 1f / 60f;
            LocomotionState last = agent.Locomotion;
            if (trace) Debug.Log($"[ledge]   trace begins in {last} at y {agent.Position.y:0.00}");

            for (int i = 0; i < ticks; i++)
            {
                var intent = new MoveIntent
                {
                    Sequence = (uint)i,
                    Move = new float2(move.x, move.y),
                    Yaw = yaw,

                    // Pulsed rather than held: jump is a rising edge, and holding it down means it
                    // fires once and never again.
                    Jump = jump && i % 24 == 0
                };

                agent.Intent = intent;
                boot.Network.Poll(step);
                boot.Simulation.Advance(step);
                agent.Intent = intent;

                if (!trace || agent.Locomotion == last) continue;

                Debug.Log($"[ledge]   t={i * step:0.00}s {last} -> {agent.Locomotion} " +
                          $"at y {agent.Position.y:0.00}");
                last = agent.Locomotion;
            }
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
