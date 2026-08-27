using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;
using Unseen.Perception;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks the clock on holding your breath.
    ///
    /// Three things have to be true in order, and a mechanic that gets any of them wrong is worse
    /// than not having it: nothing at all for the first thirty seconds, choking loud enough to give
    /// a position away from thirty, and damage from forty-five. The middle one is the design - the
    /// noise is the warning and the penalty at once, so a player is given time to surface but only
    /// by announcing where they are.
    ///
    /// Also checks that surfacing clears the clock, because a player who bobs up and drowns anyway
    /// has been killed by a rule they appeared to satisfy.
    /// </summary>
    public static class UnseenDrowningTest
    {
        [MenuItem("Unseen/Test Drowning", priority = 94)]
        public static void Run()
        {
            var host = new GameObject("DrowningTest");

            UnseenConfig config = UnseenConfig.Default;
            int roster = config.Match.TargetEntityCount;
            config.Match.TargetEntityCount = 6;

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

                var drowning = boot.Simulation.GetSystem<DrowningSystem>();
                // The RIVER specifically, not whichever body of water happens to be first.
                //
                // There are two now - the river and the castle moat - and FindAnyObjectByType
                // returned the moat, whose surface is 1.35 m up on a raised plinth. Every reading
                // after that was taken against the wrong water: the test measured minus half a
                // metre of depth and reported three failures in a drowning system that was working.
                WaterVolume volume = null;

                foreach (WaterVolume candidate in Object.FindObjectsByType<WaterVolume>(
                    FindObjectsSortMode.None))
                {
                    // The river is the long one. Nothing else in this town is hundreds of metres
                    // of water in one direction and sixteen in the other.
                    if (volume == null || candidate.HalfSize.y > volume.HalfSize.y)
                        volume = candidate;
                }
                UnseenConfig.WaterSection cfg = config.Water;

                AgentEntity subject = null;
                foreach (AgentEntity agent in boot.Context.Entities.All)
                    if (agent.IsAlive) { subject = agent; break; }

                if (drowning == null || volume == null || subject == null)
                {
                    Debug.LogError("[drown] no drowning system, no water, or no live agent");
                    return;
                }

                float surface = volume.SurfaceY;
                float centreX = volume.transform.position.x;
                Debug.Log($"[drown] water surface at y {surface:0.00}, channel centre x {centreX:0.0}");

                // Into the deep middle, and PRONE.
                //
                // Standing does not submerge you and is not supposed to: the channel is 1.35 m deep
                // and a standing eye is 1.6 m up, so a ninja walks through the river chest-deep with
                // its head in the air. Lying down is what puts the head under, which is the whole
                // shape of the mechanic - the hiding place costs you your mobility as well as your
                // air. The first version of this test teleported a STANDING agent to the bed and
                // then reported that drowning did not work.
                var under = new float3(centreX, -100f, 30f);

                if (Physics.Raycast(new Vector3(centreX, surface + 5f, 30f), Vector3.down,
                        out RaycastHit bed, 20f, UnseenLayers.WorldGeometry,
                        QueryTriggerInteraction.Ignore))
                    under = new float3(centreX, bed.point.y + 0.05f, 30f);

                Debug.Log($"[drown] riverbed at y {under.y:0.00}, " +
                          $"{surface - under.y:0.00} m of water over it");

                // Let it lie down before the clock is expected to start.
                subject.Motor.Teleport(under);
                for (int i = 0; i < 120; i++)
                {
                    subject.Intent = new MoveIntent { Sequence = (uint)i, Prone = true };
                    boot.Network.Poll(1f / 60f);
                    boot.Simulation.Advance(1f / 60f);
                    subject.Intent = new MoveIntent { Sequence = (uint)i, Prone = true };
                }

                float eyeOffset = subject.EyePosition.y - subject.Position.y;
                Debug.Log($"[drown] stance={subject.Stance}, eye {eyeOffset:0.00} m above the feet, " +
                          $"eye at y {subject.EyePosition.y:0.00} against a surface at {surface:0.00}");

                float healthAtStart = subject.Vitals.Fraction;
                float healthAtChoke = healthAtStart;
                int chokesBeforeDamage = 0;
                int chokesBeforeThirty = 0;
                bool everSubmerged = false;

                for (int i = 0; i < 60 * 60; i++)
                {
                    // Held on the bed and held prone every tick: the point is the clock, not
                    // whether the motor can keep a body down against the bots wandering into it.
                    subject.Motor.Teleport(under);
                    subject.Intent = new MoveIntent { Sequence = (uint)i, Prone = true };

                    boot.Network.Poll(1f / 60f);
                    boot.Simulation.Advance(1f / 60f);

                    subject.Intent = new MoveIntent { Sequence = (uint)i, Prone = true };

                    if (drowning.Submerged > 0) everSubmerged = true;

                    float held = drowning.HeldBreath(subject.Id);

                    foreach (SoundEvent e in boot.Context.Sound.LastTick)
                    {
                        if (e.Kind != SoundKind.Choking) continue;
                        if (held < cfg.HoldBreathSeconds) chokesBeforeThirty++;
                        else chokesBeforeDamage++;
                    }

                    // Health at the moment the air runs out, so the damage window is measured from
                    // the right place.
                    if (held < cfg.DrownAfterSeconds) healthAtChoke = subject.Vitals.Fraction;

                    if (!subject.IsAlive) break;
                }

                float healthAtEnd = subject.Vitals.Fraction;
                float finalHeld = drowning.HeldBreath(subject.Id);

                Debug.Log($"[drown] held {finalHeld:0.0} s; health {healthAtStart:0.00} -> " +
                          $"{healthAtChoke:0.00} at {cfg.DrownAfterSeconds:0} s -> {healthAtEnd:0.00} at the end");
                Debug.Log($"[drown] choking sounds: {chokesBeforeThirty} before " +
                          $"{cfg.HoldBreathSeconds:0} s, {chokesBeforeDamage} after");

                bool submerges = everSubmerged;
                bool silentEarly = chokesBeforeThirty == 0;
                bool unharmedEarly = healthAtChoke >= healthAtStart - 0.001f;
                bool chokesAudibly = chokesBeforeDamage > 0;
                bool hurtsLate = healthAtEnd < healthAtChoke - 0.01f;

                Debug.Log($"[drown] going under is detected: {(submerges ? "PASS" : "FAIL")}");
                Debug.Log($"[drown] silent for the first {cfg.HoldBreathSeconds:0} s: " +
                          $"{(silentEarly ? "PASS" : "FAIL")}");
                Debug.Log($"[drown] unharmed before {cfg.DrownAfterSeconds:0} s: " +
                          $"{(unharmedEarly ? "PASS" : "FAIL")}");
                Debug.Log($"[drown] chokes audibly once the air is gone: " +
                          $"{(chokesAudibly ? "PASS" : "FAIL")}");
                Debug.Log($"[drown] drowns if it stays under: {(hurtsLate ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- surfacing saves you
                AgentEntity second = null;
                foreach (AgentEntity agent in boot.Context.Entities.All)
                    if (agent.IsAlive && agent != subject) { second = agent; break; }

                bool surfacingClears = false;

                if (second != null)
                {
                    var deep = new float3(centreX, under.y, 80f);
                    var air = new float3(centreX, surface + 2.5f, 80f);

                    // Pinned prone every tick, on BOTH sides of the advance.
                    //
                    // The only agent available here is a bot, and a bot's brain rewrites its intent
                    // every think - so "hold Prone" was honoured most of the time and dropped for
                    // about one tick in twenty. A standing eye in a 1.3 m channel is ABOVE the
                    // surface, so each of those ticks read as a breath and reset the clock: the
                    // body was under water for 1983 of 2100 ticks and never got past four seconds
                    // of held breath. The test was measuring the bot's stance machine, not drowning.
                    //
                    // Two other ways to hold it down were tried and are worse. Locking the
                    // locomotion state stops the eye anchor being updated, and the body then reads
                    // as submerged for five ticks in two thousand. Writing the Stance field
                    // directly desyncs it from the motor's own stance tracking, which is what
                    // actually moves the anchor, and drops it to 266. Switching the brain off and
                    // driving the intent is the only one of the three that leaves the body
                    // consistent.
                    if (second.Brain != null) second.Brain.enabled = false;

                    int submergedTicks = 0;

                    for (int i = 0; i < 60 * 35; i++)
                    {
                        second.Motor.Teleport(deep);
                        second.Intent = new MoveIntent { Sequence = (uint)i, Prone = true };
                        boot.Network.Poll(1f / 60f);
                        boot.Simulation.Advance(1f / 60f);
                        second.Intent = new MoveIntent { Sequence = (uint)i, Prone = true };

                        if (WaterVolume.IsUnder(second.EyePosition)) submergedTicks++;
                    }

                    float beforeSurfacing = drowning.HeldBreath(second.Id);

                    Debug.Log($"[drown] second agent held under for {submergedTicks} of " +
                              $"{60 * 35} ticks");

                    for (int i = 0; i < 60 * 2; i++)
                    {
                        second.Motor.Teleport(air);
                        boot.Network.Poll(1f / 60f);
                        boot.Simulation.Advance(1f / 60f);
                    }

                    float afterSurfacing = drowning.HeldBreath(second.Id);
                    surfacingClears = beforeSurfacing > 20f && afterSurfacing < 0.5f;

                    Debug.Log($"[drown] held {beforeSurfacing:0.0} s, then surfaced: " +
                              $"clock now {afterSurfacing:0.0} s");
                }

                Debug.Log($"[drown] surfacing clears the clock: {(surfacingClears ? "PASS" : "FAIL")}");

                if (submerges && silentEarly && unharmedEarly && chokesAudibly && hurtsLate &&
                    surfacingClears)
                    Debug.Log("[drown] PASSED");
                else
                    Debug.LogError("[drown] FAILED");
            }
            finally
            {
                config.Match.TargetEntityCount = roster;

                UnseenBootstrap boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();
                Object.DestroyImmediate(host);
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
