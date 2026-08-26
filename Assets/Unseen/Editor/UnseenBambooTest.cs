using UnityEditor;
using UnityEngine;
using Unseen.BattleRoyale;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks the spirit forest against the two things it has to do: appear on time, and stand
    /// where the boundary is.
    ///
    /// A mechanic on a three minute timer is the easiest kind to ship broken: nobody plays that
    /// long while testing something else, and a forest that never grows looks exactly like a forest
    /// that has not started yet. So the clock is asserted directly.
    ///
    /// The second half of this exists because of a bug it would have caught. The forest used to
    /// close on its own schedule while the mist closed on a different one, so the two boundaries
    /// were never in the same place - the wall killing the player was an invisible circle and the
    /// bamboo was ninety metres behind it, out of sight and out of reach. Nothing asserted that
    /// they agreed, because nothing asserted anything about where the forest was at all.
    /// </summary>
    public static class UnseenBambooTest
    {
        [MenuItem("Unseen/Test Spirit Forest", priority = 87)]
        public static void Run()
        {
            var host = new GameObject("BambooTest");

            // This test has to simulate seven or eight minutes of match time, because the thing
            // being asserted is that the forest follows the mist across several of its stages and
            // the mist only moves when its own system ticks. Sixty-four agents of perception,
            // acoustics and bot thinking over half a million ticks is minutes of wall clock for a
            // property that has nothing to do with how many people are in the match, so the roster
            // is cut right back for the duration. Restored in the finally block: UnseenConfig is a
            // shared asset and leaving it modified would quietly change every later test.
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

                Step(boot, 120);

                var growth = boot.Simulation.GetSystem<BambooGrowthSystem>();
                var mist = boot.Simulation.GetSystem<MistZoneController>();
                BambooForest forest = Object.FindAnyObjectByType<BambooForest>();
                UnseenConfig.BambooSection cfg = boot.Context.Config.Bamboo;

                if (growth == null || forest == null || mist == null)
                {
                    Debug.LogError("[bamboo] no growth system, no mist, or no forest in the level");
                    return;
                }

                // Every time in the config is measured from the start of the match, not from
                // boot: the lobby takes about a minute to fill and drop, and testing against
                // absolute simulation time silently checks the wrong instants.
                float matchStart = RunToMatchStart(boot);
                Debug.Log($"[bamboo] match began at t={matchStart:0} s");

                // ---------------------------------------------------------- the clock
                RunTo(boot, matchStart + cfg.FirstGrowth - 10f);
                bool dormant = !forest.IsGrown;
                Debug.Log($"[bamboo] at {cfg.FirstGrowth - 10f:0} s: grown={forest.IsGrown} " +
                          $"height {forest.CurrentHeight:0.0} m");
                Debug.Log($"[bamboo] dormant before {cfg.FirstGrowth:0} s: {(dormant ? "PASS" : "FAIL")}");

                RunTo(boot, matchStart + cfg.FirstGrowth + cfg.FirstBandDuration * 0.15f);
                float shootHeight = forest.CurrentHeight;
                bool shoots = forest.IsGrown && shootHeight < 4f;
                Debug.Log($"[bamboo] shortly after it starts: {shootHeight:0.0} m of shoots");
                Debug.Log($"[bamboo] starts as shoots rather than a wall: {(shoots ? "PASS" : "FAIL")}");

                RunTo(boot, matchStart + cfg.FirstGrowth + cfg.FirstBandDuration + 5f);
                float fullHeight = forest.CurrentHeight;

                // Twice the rampart, as the spec asks: bank 5.4 plus parapet 1.6, doubled.
                float expected = 7f * cfg.HeightMultiple;
                bool tall = Mathf.Abs(fullHeight - expected) < expected * 0.2f;
                Debug.Log($"[bamboo] a minute later: {fullHeight:0.0} m against {expected:0.0} m expected");
                Debug.Log($"[bamboo] reaches twice the rampart's height: {(tall ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- it follows the boundary
                //
                // Sampled at several points as the mist closes, because agreeing once could be a
                // coincidence of the schedules rather than one boundary driving the other.
                int agreed = 0;
                int samples = 0;
                float worstGap = 0f;

                for (int i = 0; i < 3; i++)
                {
                    RunTo(boot, boot.Simulation.Time + 70f);
                    samples++;

                    float wanted = Mathf.Min(mist.CurrentRadius + cfg.MistMargin, forest.MaxRadius);
                    float gap = Mathf.Abs(forest.InnerEdge - wanted);
                    if (gap > worstGap) worstGap = gap;
                    if (gap < 1.5f) agreed++;

                    Debug.Log($"[bamboo] t={boot.Simulation.Time:0} s mist r={mist.CurrentRadius:0.0} " +
                              $"stage {mist.Stage} -> bamboo face {forest.InnerEdge:0.0} m " +
                              $"(wanted {wanted:0.0} m)");
                }

                bool tracks = agreed == samples;
                Debug.Log($"[bamboo] the wall stands on the mist line: {(tracks ? "PASS" : "FAIL")} " +
                          $"({agreed}/{samples}, worst gap {worstGap:0.00} m)");

                // ---------------------------------------------------------- it is solid all round
                //
                // The ring's colliders are moved by writing transforms, and in a batch run with no
                // physics step between the write and the query PhysX is still holding the previous
                // pose. Without this sync the sweep below asks about a wall that has not been put
                // there yet and finds an empty ring - which is a bug in the question, not the wall.
                Physics.SyncTransforms();

                float edge = forest.InnerEdge;
                Vector3 centre = forest.Centre;
                int blocked = 0;
                var bearings = new[] { 0f, 37f, 74f, 111f, 148f, 185f, 222f, 259f, 296f, 333f };

                // Asked as "is there bamboo in the ring on this bearing" rather than "does a sweep
                // from inside hit bamboo first". The forest closes THROUGH the town, so by the time
                // it is halfway in there are houses standing inside it, and a sweep outward finds a
                // wall or a roof before it ever reaches a culm. That says nothing about whether the
                // bamboo is there.
                foreach (float bearing in bearings)
                {
                    float rad = bearing * Mathf.Deg2Rad;
                    var dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));

                    // A body's width into the wall from its inner face, at chest height.
                    Vector3 probe = centre + dir * (edge + 1.5f);
                    probe.y = 1.2f;

                    bool found = false;
                    Collider[] hits = Physics.OverlapSphere(probe, 0.45f,
                        UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore);

                    foreach (Collider c in hits)
                        if (c.name.StartsWith("BambooWall")) { found = true; break; }

                    if (found) blocked++;
                    else
                        Debug.Log($"[bamboo] bearing {bearing:000}deg has no bamboo at " +
                                  $"{edge + 1.5f:0.0} m ({hits.Length} other colliders there)");
                }

                bool solid = blocked == bearings.Length;
                Debug.Log($"[bamboo] closed on every bearing: {(solid ? "PASS" : "FAIL")} " +
                          $"({blocked}/{bearings.Length})");

                // ------------------------------------------- and at the rampart, where it starts
                //
                // Checked separately at the far radius because the physics broadphase has a world
                // bounds box, and it used to be 256 m on a map 375 m to the wall. Multibox pruning
                // handles colliders outside its bounds far less reliably than inside them, and the
                // ring spends the first part of every match right out there - which is precisely
                // where a wall of bamboo was reported as something you could walk through.
                forest.SetRing(Vector3.zero, forest.MaxRadius, 1f);
                Physics.SyncTransforms();

                int farBlocked = 0;
                foreach (float bearing in bearings)
                {
                    float rad = bearing * Mathf.Deg2Rad;
                    var dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));

                    Vector3 probe = dir * (forest.InnerEdge + 1.5f);
                    probe.y = 1.2f;

                    foreach (Collider c in Physics.OverlapSphere(probe, 0.45f,
                                 UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                        if (c.name.StartsWith("BambooWall")) { farBlocked++; break; }
                }

                bool solidFarOut = farBlocked == bearings.Length;
                Debug.Log($"[bamboo] solid at the rampart ({forest.InnerEdge:0} m): " +
                          $"{(solidFarOut ? "PASS" : "FAIL")} ({farBlocked}/{bearings.Length})");

                // ---------------------------------------------------------- and it holds people out
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
                    Vector3 outside = centre + new Vector3(forest.InnerEdge + 4f, 1f, 0f);
                    subject.Motor.Teleport(outside);
                    before = Vector3.Distance(new Vector3(subject.Position.x, 0f, subject.Position.z),
                        new Vector3(centre.x, 0f, centre.z));

                    Step(boot, 180);

                    after = Vector3.Distance(new Vector3(subject.Position.x, 0f, subject.Position.z),
                        new Vector3(centre.x, 0f, centre.z));
                    pushed = after < before - 0.5f;
                }

                Debug.Log($"[bamboo] agent at {before:0.0} m pushed to {after:0.0} m " +
                          $"(face {forest.InnerEdge:0.0} m): {(pushed ? "PASS" : "FAIL")}");

                // ------------------------------------- a body cannot walk out through it
                //
                // The push above is computed in code from the ring's radius, so it would pass even
                // if the colliders were missing entirely. This drives a CharacterController at the
                // wall instead, which is the only thing that actually answers the player's
                // complaint.
                bool held = false;
                bool controlMoved = false;

                if (subject != null)
                {
                    // Control first. An agent that cannot move at all would pass the wall test for
                    // the wrong reason, and the first version of this check did exactly that: it
                    // reported travelling zero metres and called the wall solid.
                    //
                    // Yaw zero faces +Z, so the run goes outward along +Z and the subject is placed
                    // on that bearing.
                    Vector3 open = centre + new Vector3(0f, 1f, forest.InnerEdge * 0.4f);
                    subject.Motor.Teleport(open);
                    Drive(boot, subject, 60, Vector2.zero);

                    float controlStart = Radial(subject.Position, centre);
                    Drive(boot, subject, 120, new Vector2(0f, 1f));
                    float controlTravel = Radial(subject.Position, centre) - controlStart;

                    controlMoved = controlTravel > 4f;
                    Debug.Log($"[bamboo] control run in the open: {controlTravel:0.0} m outward " +
                              $"({(controlMoved ? "moves" : "DID NOT MOVE")})");

                    // Now the same run, started three metres short of the wall.
                    Vector3 inside = centre + new Vector3(0f, 1f, forest.InnerEdge - 3f);
                    subject.Motor.Teleport(inside);
                    Drive(boot, subject, 30, Vector2.zero);

                    float startOut = Radial(subject.Position, centre);
                    Drive(boot, subject, 180, new Vector2(0f, 1f));
                    float endOut = Radial(subject.Position, centre);

                    held = controlMoved && endOut < forest.InnerEdge + 0.6f;

                    Debug.Log($"[bamboo] ran at the wall from {startOut:0.0} m, reached " +
                              $"{endOut:0.0} m (face {forest.InnerEdge:0.0} m)");
                }

                Debug.Log($"[bamboo] a body cannot run out through it: {(held ? "PASS" : "FAIL")}");

                if (dormant && shoots && tall && tracks && solid && solidFarOut && pushed && held)
                    Debug.Log("[bamboo] PASSED");
                else
                    Debug.LogError("[bamboo] FAILED");
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
        /// Runs the simulation forward to a point in the match.
        ///
        /// Really runs it, rather than moving the clock: the forest now follows the mist, and the
        /// mist only moves if its own system ticks. Skipping time would leave the boundary parked
        /// at its opening radius and the tracking assertion would pass against a stationary target.
        /// </summary>
        /// <summary>Horizontal distance from a centre.</summary>
        private static float Radial(Unity.Mathematics.float3 at, Vector3 centre)
        {
            return Vector2.Distance(new Vector2(at.x, at.z), new Vector2(centre.x, centre.z));
        }

        /// <summary>Holds a scripted move intent on an agent for a number of ticks.</summary>
        private static void Drive(UnseenBootstrap boot, AgentEntity agent, int ticks, Vector2 move)
        {
            const float step = 1f / 60f;

            for (int i = 0; i < ticks; i++)
            {
                var intent = new MoveIntent
                {
                    Sequence = (uint)i,
                    Move = new Unity.Mathematics.float2(move.x, move.y),
                    Yaw = 0f,
                    Sprint = true
                };

                agent.Intent = intent;
                boot.Network.Poll(step);
                boot.Simulation.Advance(step);

                // ServerInputSystem overwrites Intent from the network each tick, and BotDirector
                // fills it in for bots, so the scripted value is reapplied after the input stage.
                agent.Intent = intent;
            }
        }

        /// <summary>Runs until the lobby ends, and reports the simulation time the match began.</summary>
        private static float RunToMatchStart(UnseenBootstrap boot)
        {
            const float step = 1f / 60f;
            int guard = 0;

            while (boot.Context.Match != null &&
                   boot.Context.Match.Phase == MatchPhase.Lobby &&
                   guard++ < 60 * 60 * 5)
            {
                boot.Network.Poll(step);
                boot.Simulation.Advance(step);
            }

            return boot.Simulation.Time;
        }

        private static void RunTo(UnseenBootstrap boot, float matchTime)
        {
            const float step = 1f / 60f;
            int guard = 0;

            while (boot.Simulation.Time < matchTime && guard++ < 200000)
            {
                boot.Network.Poll(step);
                boot.Simulation.Advance(step);
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
