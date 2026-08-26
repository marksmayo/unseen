using UnityEditor;
using UnityEngine;
using Unseen.BattleRoyale;
using Unseen.Core;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks that the boundary keeps closing to the end of the match, and that the bamboo follows
    /// it all the way down.
    ///
    /// It did not. <see cref="MistZoneController.ZonePhase.Final"/> had no case in the tick, so the
    /// circle reached its last radius and then sat there for the rest of the match: two players
    /// willing to hide were never made to fight. Once the bamboo was moved onto the boundary the
    /// bug became visible as well as abstract - the wall of the world stopped closing in front of
    /// you, which reads as the mechanic breaking.
    ///
    /// The zone schedule is compressed for the test rather than waited out. At shipping values the
    /// collapse begins thirteen minutes into a match, which is nearly fifty thousand ticks of
    /// sixty-four agents for one assertion, and a roster small enough to simulate that quickly is
    /// a roster that has already won the match by then.
    /// </summary>
    public static class UnseenZoneCollapseTest
    {
        [MenuItem("Unseen/Test Zone Collapse", priority = 86)]
        public static void Run()
        {
            var host = new GameObject("ZoneCollapseTest");

            UnseenConfig config = UnseenConfig.Default;
            UnseenConfig.MatchSection m = config.Match;

            int roster = m.TargetEntityCount;
            float firstDelay = m.FirstZoneDelay;
            float hold = m.ZoneHoldDuration;
            float close = m.ZoneCloseDuration;
            int stages = m.ZoneStages;
            float collapseFor = m.FinalCollapseDuration;
            float bambooStart = config.Bamboo.FirstGrowth;
            float bambooRise = config.Bamboo.FirstBandDuration;

            m.TargetEntityCount = 8;
            m.FirstZoneDelay = 4f;
            m.ZoneHoldDuration = 2f;
            m.ZoneCloseDuration = 3f;
            m.ZoneStages = 3;
            m.FinalCollapseDuration = 20f;
            config.Bamboo.FirstGrowth = 3f;
            config.Bamboo.FirstBandDuration = 4f;

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260824;
                boot.Boot();

                var mist = boot.Simulation.GetSystem<MistZoneController>();
                BambooForest forest = Object.FindAnyObjectByType<BambooForest>();

                if (mist == null || forest == null)
                {
                    Debug.LogError("[collapse] no mist controller or no forest");
                    return;
                }

                // Run until the last stage has finished closing.
                float reachedFinal = -1f;
                float radiusAtFinal = 0f;

                for (int i = 0; i < 60 * 240; i++)
                {
                    boot.Network.Poll(1f / 60f);
                    boot.Simulation.Advance(1f / 60f);

                    if (mist.Phase != MistZoneController.ZonePhase.Final) continue;

                    reachedFinal = boot.Simulation.Time;
                    radiusAtFinal = mist.CurrentRadius;
                    break;
                }

                if (reachedFinal < 0f)
                {
                    Debug.LogError("[collapse] the match never reached the final zone phase");
                    return;
                }

                Debug.Log($"[collapse] final phase at t={reachedFinal:0} s, radius {radiusAtFinal:0.0} m " +
                          $"(final zone radius {m.FinalZoneRadius:0})");

                // Then keep going through the collapse, sampling as it tightens - and stopping
                // the moment the match is over.
                //
                // On a compressed schedule eight bots settle it inside a minute, and once the match
                // ends the director puts the forest away and starts a new lobby, which resets the
                // circle to the whole map. Sampling past that point measured a fresh match and
                // reported the boundary as having grown from 28 m to 375 m.
                float last = radiusAtFinal;
                float lowest = radiusAtFinal;
                float worstOffSchedule = 0f;
                bool everGrew = false;
                bool bambooTracked = true;
                int samples = 0;

                for (int block = 0; block < 8; block++)
                {
                    bool matchOver = false;

                    for (int i = 0; i < 60 * 5; i++)
                    {
                        boot.Network.Poll(1f / 60f);
                        boot.Simulation.Advance(1f / 60f);

                        // Endgame is still being played - it is the final-circles phase, which is
                        // exactly the window under test. Only PostMatch and a new Lobby mean stop.
                        MatchPhase phase = boot.Context.Match != null
                            ? boot.Context.Match.Phase
                            : MatchPhase.PostMatch;

                        if (phase == MatchPhase.Infiltration ||
                            phase == MatchPhase.Hunt ||
                            phase == MatchPhase.Endgame) continue;

                        matchOver = true;
                        break;
                    }

                    if (matchOver)
                    {
                        Debug.Log($"[collapse] match ended at t={boot.Simulation.Time:0} s " +
                                  $"(phase {boot.Context.Match?.Phase}); sampling stops here");
                        break;
                    }

                    float now = mist.CurrentRadius;
                    if (now > last + 0.01f) everGrew = true;
                    if (now < lowest) lowest = now;

                    // Checked against the schedule rather than waiting to watch it arrive. Eight
                    // bots settle a compressed match in half a minute, so no run ever survives to
                    // see the last metre - but if the radius is on the line from the final zone
                    // radius to the collapse radius at every sample, it gets there.
                    float into01 = Mathf.Clamp01((boot.Simulation.Time - reachedFinal) /
                                                 m.FinalCollapseDuration);
                    float want = Mathf.Lerp(m.FinalZoneRadius, m.FinalCollapseRadius, into01);
                    float off = Mathf.Abs(now - want);
                    if (off > worstOffSchedule) worstOffSchedule = off;

                    // The wall has to be on the boundary at every step of the collapse, not just
                    // at the ends of it.
                    if (forest.IsGrown && Mathf.Abs(forest.InnerEdge - now) > 1.5f)
                        bambooTracked = false;

                    Debug.Log($"[collapse] t={boot.Simulation.Time:0} s: mist {now:0.0} m " +
                              $"(schedule says {want:0.0} m), bamboo face {forest.InnerEdge:0.0} m, " +
                              $"phase {mist.Phase}");

                    last = now;
                    samples++;
                }

                float ended = lowest;
                bool shrank = ended < radiusAtFinal - 1f;
                bool monotonic = !everGrew;
                bool onSchedule = worstOffSchedule < 0.6f;

                Debug.Log($"[collapse] radius went {radiusAtFinal:0.0} m -> {ended:0.0} m " +
                          $"over {samples * 5} s of final phase ({samples} samples)");
                Debug.Log($"[collapse] the last circle keeps closing: {(shrank ? "PASS" : "FAIL")}");
                Debug.Log($"[collapse] it never reopens: {(monotonic ? "PASS" : "FAIL")}");
                Debug.Log($"[collapse] it is on course for {m.FinalCollapseRadius:0} m " +
                          $"(worst deviation {worstOffSchedule:0.00} m): " +
                          $"{(onSchedule ? "PASS" : "FAIL")}");

                // And the bamboo has to have come with it at every step, or the wall stops
                // closing in front of the player even though the damage does not.
                bool enoughSamples = samples >= 3;
                Debug.Log($"[collapse] the collapse was observed at all: " +
                          $"{(enoughSamples ? "PASS" : "FAIL")} ({samples} live samples)");
                Debug.Log($"[collapse] the bamboo followed it down: " +
                          $"{(bambooTracked ? "PASS" : "FAIL")}");

                if (shrank && monotonic && onSchedule && bambooTracked && enoughSamples)
                    Debug.Log("[collapse] PASSED");
                else
                    Debug.LogError("[collapse] FAILED");
            }
            finally
            {
                m.TargetEntityCount = roster;
                m.FirstZoneDelay = firstDelay;
                m.ZoneHoldDuration = hold;
                m.ZoneCloseDuration = close;
                m.ZoneStages = stages;
                m.FinalCollapseDuration = collapseFor;
                config.Bamboo.FirstGrowth = bambooStart;
                config.Bamboo.FirstBandDuration = bambooRise;

                UnseenBootstrap boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();
                Object.DestroyImmediate(host);
            }
        }
    }
}
