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
    /// Checks that nobody is ever shown standing in the town before the drop.
    ///
    /// Agents are spawned onto the ground, and the match does not start until the roster fills, so
    /// there was a window in which the player stood in the street and was then snatched up to the
    /// drop altitude. The reason it reads as a glitch is that it is one: the game was showing a
    /// position it had not decided on yet.
    ///
    /// Measured per tick rather than at the end, because the fault is a handful of frames long. A
    /// check that only looked at the final state would pass on a game that flashed the ground every
    /// single match.
    /// </summary>
    public static class UnseenSpawnFlashProbe
    {
        [MenuItem("Unseen/Probe Spawn Flash", priority = 71)]
        public static void Run()
        {
            Debug.Log("[flash] --- with infiltration on (the shipping path) ---");
            float lowest = LowestBeforeDrop(skipInfiltration: false, out float altitude,
                out int ticks, out bool everDropped);

            // Eighty metres, not "near the drop altitude".
            //
            // What matters is not that everybody is exactly at the drop height - a parked agent may
            // sag a little before it is re-parked - but that nobody is anywhere near a surface the
            // player could see themselves standing on. The tallest thing in the town is the keep at
            // well under fifty metres, so anything above eighty is unambiguously empty sky.
            const float floor = 80f;
            bool noFlash = ticks > 0 && lowest >= floor;

            Debug.Log($"[flash] over {ticks} pre-drop ticks the lowest agent sat at " +
                      $"{lowest:0.0} m, drop altitude {altitude:0.0} m, threshold {floor:0.0} m");
            Debug.Log($"[flash] nobody is shown on the ground before the drop: " +
                      $"{(noFlash ? "PASS" : "FAIL")}");

            // And the drop still has to happen, or the easiest way to pass the test above would be
            // to never let anybody land.
            Debug.Log($"[flash] the descent still delivers people to the ground: " +
                      $"{(everDropped ? "PASS" : "FAIL")}");

            int dropInWater = LastInWater;
            float dropSpread = LastSpread;

            // ------------------------------------------------------------------ counterfactual
            //
            // With infiltration off the ground IS where the match starts, so agents should be down
            // there. If this also came back high, the measurement would be reading something other
            // than where the bodies are and the test above would prove nothing.
            Debug.Log("[flash] --- with infiltration off (the control) ---");
            float control = LowestBeforeDrop(skipInfiltration: true, out _, out int controlTicks, out _);

            bool controlOnGround = controlTicks > 0 && control < floor;

            Debug.Log($"[flash] over {controlTicks} ticks the lowest agent sat at {control:0.0} m");
            Debug.Log($"[flash] the probe can tell the difference: " +
                      $"{(controlOnGround ? "PASS" : "FAIL")}");

            // Dry, and scattered. Both are what the drop is for: water costs a player the opening,
            // and everybody landing together is a lobby, not a battle royale.
            bool dry = dropInWater == 0;
            bool scattered = dropSpread > 150f;

            Debug.Log($"[flash] nobody lands in the water: {(dry ? "PASS" : "FAIL")} " +
                      $"({dropInWater} wet landings)");
            Debug.Log($"[flash] landings are spread across the map: {(scattered ? "PASS" : "FAIL")} " +
                      $"(furthest pair {dropSpread:0} m apart)");

            if (noFlash && everDropped && controlOnGround && dry && scattered)
                Debug.Log("[flash] PASSED");
            else Debug.LogError("[flash] FAILED");
        }

        /// <summary>
        /// Runs from boot until the match leaves its drop phases, returning the lowest any living
        /// agent got while it was still waiting or descending.
        /// </summary>
        /// <summary>Landings in water, from the most recent run. Read after LowestBeforeDrop.</summary>
        private static int LastInWater;

        /// <summary>Metres between the two furthest-apart landings, from the most recent run.</summary>
        private static float LastSpread;

        private static float LowestBeforeDrop(bool skipInfiltration, out float altitude,
            out int ticks, out bool everDropped)
        {
            var host = new GameObject("SpawnFlashProbe");

            UnseenConfig config = UnseenConfig.Default;
            int roster = config.Match.TargetEntityCount;
            bool skipped = config.Match.SkipInfiltration;

            config.Match.TargetEntityCount = 8;
            config.Match.SkipInfiltration = skipInfiltration;

            altitude = config.Match.GliderDeployAltitude;
            float lowest = float.MaxValue;
            float highest = 0f;
            int descentTicks = 0;
            int landed = 0, inWater = 0;
            float spread = 0f;
            ticks = 0;
            everDropped = false;

            try
            {
                var boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.BuildNavMesh = false;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260828;
                boot.Boot();

                MatchDirector match = boot.Simulation.GetSystem<MatchDirector>();

                if (match == null)
                {
                    Debug.LogError("[flash] no match director");
                    return 0f;
                }

                for (int i = 0; i < 60 * 90; i++)
                {
                    boot.Network.Poll(1f / 60f);
                    boot.Simulation.Advance(1f / 60f);

                    bool waiting = match.Phase == MatchPhase.Lobby ||
                                   match.Phase == MatchPhase.Infiltration;

                    if (match.Phase == MatchPhase.Infiltration) descentTicks++;

                    if (match.Phase == MatchPhase.Infiltration)
                        for (int s = 0; s < boot.Context.Entities.Count; s++)
                        {
                            AgentEntity a = boot.Context.Entities.BySlot(s);
                            if (a != null && a.IsAlive) highest = math.max(highest, a.Position.y);
                        }

                    // Only the lobby counts toward the flash. The descent legitimately passes
                    // through every altitude between the drop and the roofs, so measuring it would
                    // always report ground level and the test could never pass.
                    if (match.Phase == MatchPhase.Lobby)
                    {
                        ticks++;

                        float tickLow = float.MaxValue;
                        int alive = 0;

                        for (int s = 0; s < boot.Context.Entities.Count; s++)
                        {
                            AgentEntity a = boot.Context.Entities.BySlot(s);
                            if (a == null || !a.IsAlive) continue;
                            alive++;
                            tickLow = math.min(tickLow, a.Position.y);
                        }

                        if (alive > 0)
                        {
                            lowest = math.min(lowest, tickLow);
                            if (!skipInfiltration)
                                Debug.Log($"[flash]   lobby tick {ticks}: {alive} alive, " +
                                          $"lowest {tickLow:0.0} m");
                        }
                    }

                    if (!waiting)
                    {
                        // Landed and standing on something.
                        for (int s = 0; s < boot.Context.Entities.Count; s++)
                        {
                            AgentEntity a = boot.Context.Entities.BySlot(s);
                            if (a != null && a.IsAlive && a.Position.y < 80f)
                            {
                                everDropped = true;
                                break;
                            }
                        }

                        if (everDropped)
                        {
                            // Where people actually ended up, which is the only thing that counts.
                            // Rejecting wet targets is worthless if the glide slides them into the
                            // river on the way down.
                            for (int s = 0; s < boot.Context.Entities.Count; s++)
                            {
                                AgentEntity a = boot.Context.Entities.BySlot(s);
                                if (a == null || !a.IsAlive) continue;

                                landed++;
                                if (WaterVolume.OverWater(a.Position.x, a.Position.z)) inWater++;

                                spread = math.max(spread,
                                    math.distance(a.Position.xz, boot.Context.Entities
                                        .BySlot(0).Position.xz));
                            }

                            break;
                        }
                    }
                }
            }
            finally
            {
                config.Match.TargetEntityCount = roster;
                config.Match.SkipInfiltration = skipped;

                var boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();

                Object.DestroyImmediate(host);
            }

            LastInWater = inWater;
            LastSpread = spread;

            if (landed > 0)
                Debug.Log($"[flash] {landed} landed, {inWater} of them in water; furthest pair " +
                          $"{spread:0} m apart");

            Debug.Log($"[flash] the descent took {descentTicks / 60f:0.0} s of a " +
                      $"{config.Match.InfiltrationDuration:0} s phase budget");
            Debug.Log($"[flash] highest anybody reached during the descent: {highest:0.0} m " +
                      $"(GliderDeployAltitude is {altitude:0.0} m)");

            return lowest == float.MaxValue ? 0f : lowest;
        }
    }
}
