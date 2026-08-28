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
    /// Checks that raked gravel remembers who crossed it.
    ///
    /// A trail is evidence, so what matters is not that marks exist but that they say something: a
    /// line of them along the way somebody walked, spaced like strides, pointing the direction of
    /// travel, and gone again after long enough that the information has a shelf life.
    ///
    /// Walked and crawled separately, because they are different marks and the crawl is the one
    /// that costs a player something - going prone hides you from eyes and writes down exactly
    /// where you went.
    /// </summary>
    public static class UnseenFootprintProbe
    {
        [MenuItem("Unseen/Probe Footprints", priority = 67)]
        public static void Run()
        {
            var host = new GameObject("FootprintProbe");

            UnseenConfig config = UnseenConfig.Default;
            int roster = config.Match.TargetEntityCount;
            config.Match.TargetEntityCount = 4;

            try
            {
                Footprints.ClearAll();

                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.BuildNavMesh = false;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260824;
                boot.Boot();

                Step(boot, 60 * 60);

                var system = boot.Simulation.GetSystem<FootprintSystem>();

                if (system == null)
                {
                    Debug.LogError("[prints] no footprint system in the loop");
                    return;
                }

                bool anyBeds = GravelBed.All.Count > 0;
                Debug.Log($"[prints] {GravelBed.All.Count} raked bed(s) registered");
                Debug.Log($"[prints] the gardens are raked: {(anyBeds ? "PASS" : "FAIL")}");

                if (!anyBeds) { Debug.LogError("[prints] FAILED"); return; }

                GravelBed bed = null;
                foreach (GravelBed candidate in GravelBed.All)
                    if (candidate != null) { bed = candidate; break; }

                AgentEntity walker = null;
                foreach (AgentEntity agent in boot.Context.Entities.All)
                    if (agent != null && agent.IsAlive) { walker = agent; break; }

                if (walker == null) { Debug.LogError("[prints] no live agent"); return; }
                if (walker.Brain != null) walker.Brain.enabled = false;

                Vector3 centre = bed.transform.position;

                // ---------------------------------------------------------- walking leaves a line
                Footprints.ClearAll();
                int before = system.Pressed;

                var from = new float3(centre.x - bed.HalfSize.x * 0.7f, bed.SurfaceY,
                    centre.z - bed.HalfSize.y * 0.7f);

                var to = new float3(centre.x + bed.HalfSize.x * 0.7f, bed.SurfaceY,
                    centre.z + bed.HalfSize.y * 0.7f);

                int steps = Walk(boot, walker, from, to, Stance.Stand);
                int walked = system.Pressed - before;

                bool leavesTrail = walked >= 6;

                Debug.Log($"[prints] walking {math.distance(from, to):0} m across the bed left " +
                          $"{walked} marks over {steps} ticks; {Footprints.Count} in the ground");
                Debug.Log($"[prints] walking leaves a trail: {(leavesTrail ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- and it points somewhere
                //
                // The freshest mark near the far end should be heading the way the walker was, or
                // the trail tells a reader nothing beyond "somebody was here".
                float age = Footprints.FreshestNear(to, 4f, out float3 heading);

                float3 travelled = math.normalizesafe(UnseenMath.Horizontal(to - from));
                float agreement = math.dot(math.normalizesafe(UnseenMath.Horizontal(heading)),
                    travelled);

                bool pointsTheRightWay = age >= 0f && agreement > 0.6f;

                Debug.Log($"[prints] freshest mark at the far end is {age:0.0} s old, heading " +
                          $"agrees with travel by {agreement:0.00}");
                Debug.Log($"[prints] a trail says which way they went: " +
                          $"{(pointsTheRightWay ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- nothing off the bed
                //
                // The counterfactual. Marks appearing on ordinary street would mean the bed test is
                // not doing anything and every surface in the town records footprints.
                Footprints.ClearAll();
                int beforeStreet = system.Pressed;

                var street = new float3(centre.x + bed.HalfSize.x + 25f, 0.2f, centre.z);
                Walk(boot, walker, street, street + new float3(12f, 0f, 0f), Stance.Stand);

                int onStreet = system.Pressed - beforeStreet;
                bool gravelOnly = onStreet == 0;

                Debug.Log($"[prints] {onStreet} marks left walking the same distance on paving");
                Debug.Log($"[prints] only gravel records anything: " +
                          $"{(gravelOnly ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- crawling is worse
                //
                // Going prone hides a body from eyes and writes down exactly where it went. It must
                // leave MORE evidence than walking, not less, or the stealthy option is strictly
                // better and there is no decision in it.
                Footprints.ClearAll();
                int beforeCrawl = system.Pressed;

                Walk(boot, walker, from, to, Stance.Prone);
                int crawled = system.Pressed - beforeCrawl;

                bool crawlIsWorse = crawled > walked;

                Debug.Log($"[prints] crawling the same line left {crawled} marks against " +
                          $"{walked} walking");
                Debug.Log($"[prints] crawling leaves more evidence, not less: " +
                          $"{(crawlIsWorse ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- and they fill in
                int standing = Footprints.Count;
                for (int i = 0; i < 60 * 80; i++) boot.Simulation.Advance(1f / 60f);

                bool fades = Footprints.Count < standing;

                Debug.Log($"[prints] {standing} marks in the ground, {Footprints.Count} left after " +
                          $"eighty seconds");
                Debug.Log($"[prints] the gravel fills back in: {(fades ? "PASS" : "FAIL")}");

                if (leavesTrail && pointsTheRightWay && gravelOnly && crawlIsWorse && fades)
                    Debug.Log("[prints] PASSED");
                else
                    Debug.LogError("[prints] FAILED");
            }
            finally
            {
                config.Match.TargetEntityCount = roster;

                UnseenBootstrap boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();

                Object.DestroyImmediate(host);
                Footprints.ClearAll();
            }
        }

        /// <summary>Drags an agent along a line in a given stance, a step at a time.</summary>
        private static int Walk(UnseenBootstrap boot, AgentEntity agent, float3 from, float3 to,
            Stance stance)
        {
            // Walked at about three metres a second, not teleported across at ten.
            //
            // The first version covered the twenty-four metre bed in 150 ticks, which is 0.16 m a
            // tick - and the footprint system runs at the base rate, so it only looks every third
            // one. At 0.48 m per look, a 0.85 m stride and a 0.55 m crawl spacing BOTH round up to
            // two looks, and walking and crawling came out at exactly 25 marks each. The spacings
            // were fine; the test was moving faster than a sprint and could not resolve them.
            const int ticks = 480;

            for (int i = 0; i < ticks; i++)
            {
                float t = i / (float)(ticks - 1);
                agent.Motor.Teleport(math.lerp(from, to, t));

                var intent = new MoveIntent
                {
                    Sequence = (uint)i,
                    Move = new float2(0f, 1f),
                    Prone = stance == Stance.Prone,
                    Crouch = stance == Stance.Crouch
                };

                agent.Intent = intent;

                // The stance is set directly as well as asked for.
                //
                // A body teleported onto a mark every tick never completes the transition to
                // prone - the motor wants it grounded and settled first - so the intent alone left
                // the agent standing and the crawl case measured a walk. Both runs came out at
                // exactly 25 marks, which is what tipped me off.
                //
                // Legitimate here because what is under test is the MARK, not the stance machine:
                // the footprint system reads agent.Stance and nothing else, and the stance machine
                // has its own probe.
                agent.Stance = stance;

                boot.Network.Poll(1f / 60f);
                boot.Simulation.Advance(1f / 60f);

                agent.Intent = intent;
                agent.Stance = stance;
            }

            return ticks;
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
