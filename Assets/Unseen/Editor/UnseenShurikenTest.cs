using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Combat;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks the four rules a thrown blade has to follow.
    ///
    /// A ranged attack is dangerous in a game built on not being seen, because a weapon you can use
    /// from cover without moving undoes most of what the stealth model is for. What keeps it honest
    /// is the cost attached to it, so the costs are what get asserted: one blade to start with, two
    /// seconds between throws, a whistle the whole way, and a miss that lands on the ground for
    /// anyone - including the target - to pick up.
    /// </summary>
    public static class UnseenShurikenTest
    {
        [MenuItem("Unseen/Test Shuriken", priority = 97)]
        public static void Run()
        {
            var host = new GameObject("ShurikenTest");

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

                var system = boot.Simulation.GetSystem<ShurikenSystem>();
                UnseenConfig.ShurikenSection cfg = config.Shuriken;

                AgentEntity thrower = null;
                AgentEntity target = null;

                foreach (AgentEntity agent in boot.Context.Entities.All)
                {
                    if (!agent.IsAlive) continue;
                    if (thrower == null && !agent.IsBot) { thrower = agent; continue; }
                    if (target == null && agent != thrower) target = agent;
                }

                if (system == null || thrower == null || target == null)
                {
                    Debug.LogError("[shuriken] no system, or not enough agents");
                    return;
                }

                Debug.Log($"[shuriken] thrower starts with {thrower.Shuriken}");
                bool startsArmed = thrower.Shuriken == cfg.StartingCount;
                Debug.Log($"[shuriken] everyone starts with {cfg.StartingCount}: " +
                          $"{(startsArmed ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- a hit
                //
                // Stood face to face at eight metres on open ground, so the only thing between them
                // is air and the test is of the blade rather than of the town.
                if (!FindOpenGround(out Vector3 spot))
                {
                    Debug.LogError("[shuriken] found nowhere open to throw across");
                    return;
                }

                var from = new float3(spot.x, spot.y, spot.z);
                var to = from + new float3(0f, 0f, 8f);

                thrower.Shuriken = 1;
                float targetHealth = target.Vitals.Fraction;

                Hold(boot, thrower, target, from, to, 30, throwing: false);
                int whistles = Hold(boot, thrower, target, from, to, 90, throwing: true);

                bool hit = target.Vitals.Fraction < targetHealth - 0.01f;
                bool spent = thrower.Shuriken == 0;

                Debug.Log($"[shuriken] target health {targetHealth:0.00} -> " +
                          $"{target.Vitals.Fraction:0.00}; thrower now holds {thrower.Shuriken}");
                Debug.Log($"[shuriken] a thrown blade hits and hurts: {(hit ? "PASS" : "FAIL")}");
                Debug.Log($"[shuriken] throwing spends it: {(spent ? "PASS" : "FAIL")}");
                Debug.Log($"[shuriken] {whistles} whistles heard in flight");
                Debug.Log($"[shuriken] it whistles on the way: {(whistles > 0 ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- the cooldown
                thrower.Shuriken = 3;

                // Let the previous throw's cooldown expire first, or this measures nothing: zero
                // throws from a spent cooldown passes a "no more than one" test for the wrong
                // reason.
                for (int i = 0; i < 60 * 3; i++) Drive(boot, thrower, from, throwing: false);

                int before = system.Thrown;

                // Half a second of hammering the button. At a two second floor that is one throw,
                // and it has to be exactly one - none would mean the throw is simply broken.
                for (int i = 0; i < 30; i++)
                {
                    Drive(boot, thrower, from, throwing: i % 2 == 0);
                }

                int burst = system.Thrown - before;
                bool paced = burst == 1;

                Debug.Log($"[shuriken] {burst} throws from half a second of mashing the button");
                Debug.Log($"[shuriken] no gatling: {(paced ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- a miss can be recovered
                ShurikenPickup.ClearAll();
                thrower.Shuriken = 1;

                // Aimed at the sky over open ground, so it lands rather than hitting anybody.
                int landed = 0;
                for (int i = 0; i < 60 * 6; i++)
                {
                    Drive(boot, thrower, from, throwing: i == 5, pitch: -35f);
                    if (ShurikenPickup.Count > landed) landed = ShurikenPickup.Count;
                }

                bool drops = landed > 0;
                Debug.Log($"[shuriken] {landed} blade(s) on the ground after a miss");
                Debug.Log($"[shuriken] a miss lands rather than vanishing: {(drops ? "PASS" : "FAIL")}");

                // And somebody can pick it up. The thrower is out, so recovering one re-arms them.
                bool recovered = false;

                if (drops && ShurikenPickup.TryPeek(out float3 lying))
                {
                    thrower.Shuriken = 0;

                    // Put the thrower beside the blade rather than walking them at it from wherever
                    // they happen to be. A lobbed throw can land thirty metres away in a direction
                    // nobody recorded, and "walk forward and hope" was testing pathing rather than
                    // pickup.
                    var beside = lying + new float3(1f, 0.2f, 0f);

                    for (int i = 0; i < 60 * 4 && !recovered; i++)
                    {
                        Drive(boot, thrower, beside, throwing: false);
                        if (thrower.Shuriken > 0) recovered = true;
                    }
                }

                Debug.Log($"[shuriken] a dropped blade can be picked up: " +
                          $"{(recovered ? "PASS" : "FAIL")} (holding {thrower.Shuriken})");

                if (startsArmed && hit && spent && whistles > 0 && paced && drops && recovered)
                    Debug.Log("[shuriken] PASSED");
                else
                    Debug.LogError("[shuriken] FAILED");
            }
            finally
            {
                config.Match.TargetEntityCount = roster;

                UnseenBootstrap boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>Somewhere with eight clear metres to throw across.</summary>
        private static bool FindOpenGround(out Vector3 spot)
        {
            spot = Vector3.zero;

            for (int i = 0; i < 300; i++)
            {
                float angle = i * 41f * Mathf.Deg2Rad;
                float reach = 20f + i * 1.4f;
                var from = new Vector3(Mathf.Sin(angle) * reach, 60f, Mathf.Cos(angle) * reach);

                if (!Physics.Raycast(from, Vector3.down, out RaycastHit ground, 90f,
                        UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                    continue;

                if (ground.normal.y < 0.95f) continue;

                var chest = ground.point + Vector3.up * 1.2f;
                if (Physics.Raycast(chest, Vector3.forward, 12f, UnseenLayers.WorldGeometry,
                        QueryTriggerInteraction.Ignore))
                    continue;

                spot = ground.point + Vector3.up * 0.1f;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Holds both agents in place for a number of ticks and counts the shuriken whistles that
        /// reach the sound bus, which only holds one tick at a time.
        /// </summary>
        private static int Hold(UnseenBootstrap boot, AgentEntity thrower, AgentEntity target,
            float3 from, float3 to, int ticks, bool throwing)
        {
            int whistles = 0;

            for (int i = 0; i < ticks; i++)
            {
                target.Motor.Teleport(to);
                Drive(boot, thrower, from, throwing && i == 5);

                foreach (SoundEvent e in boot.Context.Sound.LastTick)
                    if (e.Kind == SoundKind.ShurikenWhistle) whistles++;
            }

            return whistles;
        }

        /// <summary>One tick with a scripted intent, optionally pinning the thrower in place.</summary>
        private static void Drive(UnseenBootstrap boot, AgentEntity agent, float3 pin,
            bool throwing, float pitch = 0f, float2 move = default)
        {
            const float step = 1f / 60f;

            if (math.lengthsq(pin) > 0f) agent.Motor.Teleport(pin);

            var intent = new MoveIntent
            {
                Move = move,
                Yaw = 0f,
                Pitch = pitch,
                Throw = throwing
            };

            agent.Intent = intent;
            boot.Network.Poll(step);
            boot.Simulation.Advance(step);
            agent.Intent = intent;
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
