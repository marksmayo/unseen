using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using Unseen.AI;
using Unseen.Combat;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks the rules a thrown blade has to follow.
    ///
    /// A ranged attack is dangerous in a game built on not being seen, because a weapon you can use
    /// from cover without moving undoes most of what the stealth model is for. What keeps it honest
    /// is the cost attached to it - so the costs are what get asserted.
    ///
    /// The costs changed. Counting blades is gone: the supply is unlimited and the price of a throw
    /// is now the five seconds before the next one and the noise it makes, which is a decision a
    /// player can feel rather than an inventory they have to manage. So this asserts the cooldown
    /// hard, in both directions, and asserts that the blade arrives roughly where it was pointed -
    /// the old numbers put it 1.35 m below the crosshair at twenty-four metres, which read as the
    /// throw simply not working.
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

                // The target goes on the line the blade is actually thrown along, which is offset
                // to the right of the body by the same half metre the camera is.
                //
                // That offset IS the fix for "the shuriken always goes slightly left of centre":
                // the crosshair is rendered from half a metre right of the ninja, so a blade
                // leaving the body's centre line flew parallel to the aim ray and half a metre
                // left of it. Launching from the camera's lateral line makes the two coincide - so
                // a test that stands its target directly in front of the BODY is now testing the
                // wrong line, and it duly reported a miss.
                float aimRight = cfg.LaunchOffsetRight;

                var to = from + new float3(aimRight, 0f, 8f);

                int heldBefore = thrower.Shuriken;
                float targetHealth = target.Vitals.Fraction;

                Hold(boot, thrower, target, from, to, 30, throwing: false);
                int whistles = Hold(boot, thrower, target, from, to, 90, throwing: true);

                bool hit = target.Vitals.Fraction < targetHealth - 0.01f;

                // Throwing costs nothing but time now. Asserted as "the count did not move",
                // because the opposite - a silent decrement that nothing reads any more - would
                // leave a stale number on the agent for something else to trip over later.
                bool unlimited = cfg.Unlimited && thrower.Shuriken == heldBefore;

                Debug.Log($"[shuriken] target health {targetHealth:0.00} -> " +
                          $"{target.Vitals.Fraction:0.00}; thrower holds {heldBefore} -> " +
                          $"{thrower.Shuriken}");
                Debug.Log($"[shuriken] a thrown blade hits and hurts: {(hit ? "PASS" : "FAIL")}");
                Debug.Log($"[shuriken] the supply is unlimited and a throw does not spend it: " +
                          $"{(unlimited ? "PASS" : "FAIL")}");
                Debug.Log($"[shuriken] {whistles} whistles heard in flight");
                Debug.Log($"[shuriken] it whistles on the way: {(whistles > 0 ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- the cooldown
                //
                // Let the previous throw's cooldown expire first, or this measures nothing: zero
                // throws from a spent cooldown passes a "no more than one" test for the wrong
                // reason. Waited off the CONFIGURED cooldown rather than a hard-coded three
                // seconds, which is how this quietly started measuring nothing when the floor went
                // from two seconds to five.
                int settle = Mathf.CeilToInt(60f * (cfg.Cooldown + 1f));
                for (int i = 0; i < settle; i++) Drive(boot, thrower, from, throwing: false);

                int before = system.Thrown;

                // A second of hammering the button. At a five second floor that is one throw, and
                // it has to be exactly one - none would mean the throw is simply broken.
                for (int i = 0; i < 60; i++) Drive(boot, thrower, from, throwing: i % 2 == 0);

                int burst = system.Thrown - before;
                bool paced = burst == 1;

                Debug.Log($"[shuriken] {burst} throws from a second of mashing the button at a " +
                          $"{cfg.Cooldown:0} s floor");
                Debug.Log($"[shuriken] no gatling: {(paced ? "PASS" : "FAIL")}");

                // And the other direction: the cooldown has to END. A floor that never lifts is
                // indistinguishable from one throw per match, and both pass the test above.
                for (int i = 0; i < settle; i++) Drive(boot, thrower, from, throwing: false);

                int second = system.Thrown;
                for (int i = 0; i < 30; i++) Drive(boot, thrower, from, throwing: i % 2 == 0);

                bool recovers = system.Thrown - second == 1;

                Debug.Log($"[shuriken] {system.Thrown - second} throw(s) after waiting the " +
                          $"cooldown out again");
                Debug.Log($"[shuriken] the cooldown lifts: {(recovers ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- it goes where it points
                //
                // Thrown dead level at a target twenty-four metres away, which is a normal sightline
                // in this town. The old numbers - 34 m/s with 5.5 m/s^2 of drop - put the blade 1.35
                // m low over that distance, and a player aiming at a chest and hitting the ground
                // reasonably concluded the throw was broken rather than ballistic.
                ShurikenPickup.ClearAll();

                var farTo = from + new float3(aimRight, 0f, 24f);
                float farHealth = target.Vitals.Fraction;

                for (int i = 0; i < settle; i++) Drive(boot, thrower, from, throwing: false);
                Hold(boot, thrower, target, from, farTo, 120, throwing: true);

                bool reaches = target.Vitals.Fraction < farHealth - 0.01f;

                // What the drop actually costs over that distance, from the configured numbers.
                float flight = 24f / Mathf.Max(1f, cfg.Speed);
                float sag = 0.5f * cfg.Drop * flight * flight;

                Debug.Log($"[shuriken] at 24 m the blade sags {sag:0.00} m " +
                          $"({cfg.Speed:0} m/s, {cfg.Drop:0.0} m/s^2)");
                Debug.Log($"[shuriken] target health {farHealth:0.00} -> " +
                          $"{target.Vitals.Fraction:0.00}");
                Debug.Log($"[shuriken] it arrives where it was aimed at 24 m: " +
                          $"{(reaches && sag < 0.35f ? "PASS" : "FAIL")}");

                bool straight = reaches && sag < 0.35f;

                // ---------------------------------------------------------- a miss still lands
                ShurikenPickup.ClearAll();
                thrower.Shuriken = 1;

                // Wait the cooldown out FIRST. The straightness check above throws a blade, and at
                // a five second floor this section was asking for another one two frames later,
                // being refused, and then reporting that a missed blade does not land - a failure
                // in a mechanic that was working, caused entirely by the test's own previous throw.
                for (int i = 0; i < settle; i++) Drive(boot, thrower, from, throwing: false);

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

                // Blades on the ground are scenery now, not ammunition. Nothing should be
                // collecting them, and the litter must not grow without bound either - sixty-four
                // players throwing every five seconds for fifteen minutes is ten thousand blades,
                // and each one was a GameObject with four renderers on it.
                int lyingBefore = ShurikenPickup.Count;

                if (ShurikenPickup.TryPeek(out float3 lying))
                {
                    var beside = lying + new float3(1f, 0.2f, 0f);
                    for (int i = 0; i < 60 * 3; i++) Drive(boot, thrower, beside, throwing: false);
                }

                // Requires there to have been something lying there in the first place: "nothing
                // was picked up" is trivially true of an empty street.
                bool notAmmunition = lyingBefore > 0 && ShurikenPickup.Count == lyingBefore;

                Debug.Log($"[shuriken] {lyingBefore} blade(s) lying; still {ShurikenPickup.Count} " +
                          $"after standing on one for three seconds");
                Debug.Log($"[shuriken] a landed blade is scenery, not ammunition: " +
                          $"{(notAmmunition ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- the throw is visible
                //
                // A proxy has none of the thrower's combat state - it sees replicated flags and
                // nothing else - so the animation reads a flag rather than the timer. If the server
                // stops mirroring it, everybody else's throws become invisible and the blade sails
                // out of an idle body, which is the sort of thing that survives a long time.
                for (int i = 0; i < settle; i++) Drive(boot, thrower, from, throwing: false);

                bool flaggedDuring = false;

                for (int i = 0; i < 20; i++)
                {
                    Drive(boot, thrower, from, throwing: i == 2);
                    if ((thrower.Flags & AgentFlags.Throwing) != 0) flaggedDuring = true;
                }

                for (int i = 0; i < 60; i++) Drive(boot, thrower, from, throwing: false);

                bool flagCleared = (thrower.Flags & AgentFlags.Throwing) == 0;
                bool animates = flaggedDuring && flagCleared;

                Debug.Log($"[shuriken] throwing flag raised during the throw: {flaggedDuring}, " +
                          $"cleared afterwards: {flagCleared}");
                Debug.Log($"[shuriken] the throw is visible to other players: " +
                          $"{(animates ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- and bots use it
                //
                // They never had. There was no throw action in the HTN domain at all, so the
                // shuriken was a purely human weapon for as long as it has existed - which nothing
                // reported, because nothing was looking.
                //
                // Asked of the PLANNER rather than of a staged fight. Two attempts at the latter
                // proved only how hard it is to stage: five bots loose on a seven hundred metre map
                // do not meet inside a minute, and two held fifteen metres apart simply patrolled
                // past each other, never facing one another, and acquired a target on nought of
                // fifteen hundred ticks. Neither run told me anything about whether a bot that CAN
                // see somebody will throw - which is the thing that changed.
                //
                // The domain is a pure function of facts, so it can just be asked.
                var domain = NinjaDomain.Build();
                var planner = new HtnPlanner();
                var plan = new System.Collections.Generic.List<PrimitiveTask>(8);

                var canThrow = new BotFacts
                {
                    HasTarget = true,
                    TargetVisible = true,
                    TargetInApproachRange = true,
                    TargetInThrowRange = true,
                    CanThrow = true
                };

                bool planned = planner.Plan(domain, canThrow, plan) &&
                               plan.Count > 0 && plan[0].Action == BotAction.ThrowShuriken;

                Debug.Log($"[shuriken] with a visible target in range, the plan opens with " +
                          $"{(plan.Count > 0 ? plan[0].Action.ToString() : "nothing")}");
                Debug.Log($"[shuriken] bots throw when they can: {(planned ? "PASS" : "FAIL")}");

                // The counterfactual, twice over: on cooldown it must close instead, and in melee
                // it must swing. A plan that answered "throw" to everything would pass the check
                // above and be worse than no throw at all.
                var onCooldown = canThrow;
                onCooldown.CanThrow = false;

                plan.Clear();
                planner.Plan(domain, onCooldown, plan);
                BotAction cooling = plan.Count > 0 ? plan[0].Action : BotAction.Idle;

                var inMelee = canThrow;
                inMelee.TargetInMeleeRange = true;

                plan.Clear();
                planner.Plan(domain, inMelee, plan);
                BotAction close = plan.Count > 0 ? plan[0].Action : BotAction.Idle;

                bool discriminates = cooling != BotAction.ThrowShuriken &&
                                     close != BotAction.ThrowShuriken;

                Debug.Log($"[shuriken] on cooldown it plans {cooling}; in melee it plans {close}");
                Debug.Log($"[shuriken] and only when it should: " +
                          $"{(discriminates ? "PASS" : "FAIL")}");

                bool botsThrow = planned && discriminates;

                if (startsArmed && hit && unlimited && whistles > 0 && paced && recovers &&
                    straight && drops && notAmmunition && animates && botsThrow)
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

                // And DRY. The castle lake is a hundred and twenty metres across at the middle of
                // the map, and its bed is flat stone that satisfies every other test here - so this
                // search happily picked a spot chest deep in it, stood both agents in the water
                // among ninety rocks, and reported that a thrown blade does not hurt anybody.
                if (Unseen.Environment.WaterVolume.DepthAt(
                        new Unity.Mathematics.float3(ground.point.x, ground.point.y, ground.point.z))
                    > 0.01f)
                    continue;

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

            // The target's brain off for the duration.
            //
            // It is a bot, and a bot rewrites its own intent every think - so teleporting it onto
            // the mark each tick and then advancing the simulation let it walk straight back off
            // again. Over ninety ticks it wandered clear of the line of flight, the blade lived out
            // its full two seconds hitting nothing, and the test reported that a thrown blade does
            // not hurt anybody. What is under test here is the blade, not the bot.
            if (target.Brain != null) target.Brain.enabled = false;

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
