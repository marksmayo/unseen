using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using Unseen.BattleRoyale;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks the spirit forest against the schedule it was specified with.
    ///
    /// A mechanic on a three minute timer is the easiest kind to ship broken: nobody plays that
    /// long while testing something else, and a forest that never grows looks exactly like a forest
    /// that has not started yet. So the clock is asserted directly - nothing before three minutes,
    /// full height and one metre deep a minute later, two metres fifteen seconds after that - along
    /// with the two rules that make it a mechanic rather than scenery: it cannot be entered, and it
    /// pushes out anyone it grows around.
    /// </summary>
    public static class UnseenBambooTest
    {
        [MenuItem("Unseen/Test Spirit Forest", priority = 87)]
        public static void Run()
        {
            var host = new GameObject("BambooTest");

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260824;
                boot.Boot();

                Step(boot, 120);

                var growth = boot.Simulation.GetSystem<BambooGrowthSystem>();
                BambooForest forest = Object.FindAnyObjectByType<BambooForest>();
                UnseenConfig.BambooSection cfg = boot.Context.Config.Bamboo;

                if (growth == null || forest == null)
                {
                    Debug.LogError("[bamboo] no growth system or no forest in the level");
                    return;
                }

                // Simulated seconds since the match began, not since boot.
                float start = boot.Simulation.Time;

                float beforeStart = DepthAt(boot, growth, start, cfg.FirstGrowth - 10f);
                float atFirstBand = DepthAt(boot, growth, start, cfg.FirstGrowth + cfg.FirstBandDuration + 1f);
                float afterSecond = DepthAt(boot, growth, start,
                    cfg.FirstGrowth + cfg.FirstBandDuration + cfg.BandDuration + 1f);

                Debug.Log($"[bamboo] depth at {cfg.FirstGrowth - 10f:0} s: {beforeStart:0.00} m");
                Debug.Log($"[bamboo] depth at {cfg.FirstGrowth + cfg.FirstBandDuration:0} s: {atFirstBand:0.00} m");
                Debug.Log($"[bamboo] depth at {cfg.FirstGrowth + cfg.FirstBandDuration + cfg.BandDuration:0} s: " +
                          $"{afterSecond:0.00} m");

                bool dormant = beforeStart <= 0.01f;
                bool firstBand = Mathf.Abs(atFirstBand - cfg.BandDepth) < cfg.BandDepth * 0.35f;
                bool secondBand = afterSecond > atFirstBand + cfg.BandDepth * 0.5f;

                Debug.Log($"[bamboo] dormant before {cfg.FirstGrowth:0} s:      {(dormant ? "PASS" : "FAIL")}");
                Debug.Log($"[bamboo] one band deep after the first minute: {(firstBand ? "PASS" : "FAIL")}");
                Debug.Log($"[bamboo] deeper again {cfg.BandDuration:0} s later:      {(secondBand ? "PASS" : "FAIL")}");

                // Height: twice the rampart, and solid.
                float wall = 7f; // bank 5.4 plus parapet 1.6, as the generator builds it
                float expected = wall * cfg.HeightMultiple;

                var mass = GameObject.Find("BambooMass_0");
                float actualHeight = mass != null ? mass.transform.localScale.y : 0f;
                bool tall = Mathf.Abs(actualHeight - expected) < expected * 0.2f;
                Debug.Log($"[bamboo] height {actualHeight:0.0} m against {expected:0.0} m expected: " +
                          $"{(tall ? "PASS" : "FAIL")}");

                // Solid: a capsule cast into the grown forest must hit something.
                bool solid = false;
                if (mass != null)
                {
                    Vector3 probe = mass.transform.position;
                    probe.y = 1f;
                    solid = Physics.OverlapSphere(probe, 0.4f, UnseenLayers.WorldGeometry,
                        QueryTriggerInteraction.Ignore).Length > 0;
                }

                Debug.Log($"[bamboo] the forest is solid: {(solid ? "PASS" : "FAIL")}");

                // And it holds people out. Drop an agent beyond the inner face and let it push.
                AgentEntity subject = null;
                foreach (AgentEntity agent in boot.Context.Entities.All)
                    if (agent.IsAlive)
                    {
                        subject = agent;
                        break;
                    }

                bool pushed = false;
                float before = 0f;
                float after = 0f;

                if (subject != null)
                {
                    float edge = growth.InnerEdge;
                    var outside = new Vector3(edge + 4f, 1f, 0f);
                    subject.Motor.Teleport(outside);
                    before = Mathf.Max(Mathf.Abs(subject.Position.x), Mathf.Abs(subject.Position.z));

                    Step(boot, 180);

                    after = Mathf.Max(Mathf.Abs(subject.Position.x), Mathf.Abs(subject.Position.z));
                    pushed = after < before - 0.5f;
                }

                Debug.Log($"[bamboo] agent at {before:0.0} m pushed to {after:0.0} m " +
                          $"(inner face {growth.InnerEdge:0.0} m): {(pushed ? "PASS" : "FAIL")}");

                if (dormant && firstBand && secondBand && tall && solid && pushed)
                    Debug.Log("[bamboo] PASSED");
                else
                    Debug.LogError("[bamboo] FAILED");
            }
            finally
            {
                UnseenBootstrap boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// Runs the simulation forward to a given point in the match and reports the depth.
        ///
        /// Stepping at sixty ticks a second through four minutes of match would be fourteen
        /// thousand ticks per sample; the growth is a pure function of elapsed time, so the clock
        /// is simply moved instead.
        /// </summary>
        private static float DepthAt(UnseenBootstrap boot, BambooGrowthSystem growth,
            float matchStart, float secondsIn)
        {
            growth.Begin(boot.Simulation.Time - secondsIn);
            Step(boot, 4);
            return growth.Depth;
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
