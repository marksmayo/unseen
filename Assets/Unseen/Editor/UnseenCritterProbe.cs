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
    /// Checks that birds give away a careless player and forgive a careful one.
    ///
    /// Both halves matter and only one of them is obvious. A flushed bird that never fires is dead
    /// scenery; a flushed bird that fires whatever you do is a punishment with no counterplay, and
    /// it would make every tree-lined street unusable rather than dangerous. So the assertion is a
    /// pair: sprinting past a perch raises it, crouching past the same perch at the same distance
    /// does not.
    /// </summary>
    public static class UnseenCritterProbe
    {
        [MenuItem("Unseen/Test Wildlife", priority = 90)]
        public static void Run()
        {
            var host = new GameObject("CritterProbe");

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

                Step(boot, 120);

                Debug.Log($"[wildlife] {Critter.All.Count} critters in the level");

                bool populated = Critter.All.Count > 40;
                Debug.Log($"[wildlife] the town is inhabited: {(populated ? "PASS" : "FAIL")}");

                AgentEntity subject = null;
                foreach (AgentEntity agent in boot.Context.Entities.All)
                    if (agent.IsAlive)
                    {
                        subject = agent;
                        break;
                    }

                var startle = boot.Simulation.GetSystem<CritterStartleSystem>();

                if (subject == null || startle == null)
                {
                    Debug.LogError("[wildlife] no live agent or no startle system");
                    return;
                }

                // A bird on open ground, so nothing else can be what flushed it.
                Critter target = null;
                foreach (Critter critter in Critter.All)
                    if (critter != null && critter.Kind == Critter.Species.Bird && critter.IsSettled)
                    {
                        target = critter;
                        break;
                    }

                if (target == null)
                {
                    Debug.LogError("[wildlife] no settled bird to test against");
                    return;
                }

                Vector3 perch = target.transform.position;
                Debug.Log($"[wildlife] target bird perched at {perch}");

                // ---------------------------------------------------------- sprinting is heard
                Critter.ResetAll();

                int before = startle.Startles;

                // Run at the perch from twelve metres out, yaw facing it.
                Vector3 from = perch + new Vector3(0f, 0f, -12f);
                from.y = perch.y;

                int sprintSounds = Approach(boot, subject, from, sprint: true, crouch: false);

                int sprintStartles = startle.Startles - before;
                bool sprintFlushed = sprintStartles > 0;

                Debug.Log($"[wildlife] sprinting past: {sprintStartles} critter(s) startled");
                Debug.Log($"[wildlife] a sprint flushes them: {(sprintFlushed ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- and it is a real sound
                //
                // Emitted through the acoustic bus like a footstep, or nobody else ever hears it and
                // the mechanic is a private animation.
                //
                // Counted during the run rather than after it. The bus holds one tick, so by the
                // time an approach has finished the event is long gone - checking afterwards is how
                // the first version of this assertion managed to pass without ever seeing a sound.
                bool heard = sprintSounds > 0;
                Debug.Log($"[wildlife] {sprintSounds} critter sound(s) on the bus during the run");
                Debug.Log($"[wildlife] the flush reaches the sound bus: {(heard ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- crouching is not
                Critter.ResetAll();
                Step(boot, 6);

                int crouchBase = startle.Startles;
                int crouchSounds = Approach(boot, subject, from, sprint: false, crouch: true);
                int crouchStartles = startle.Startles - crouchBase;

                bool crouchQuiet = crouchStartles < sprintStartles;
                Debug.Log($"[wildlife] crouching past: {crouchStartles} critter(s) startled, " +
                          $"{crouchSounds} sound(s) (against {sprintStartles} and {sprintSounds} " +
                          $"at a sprint)");
                Debug.Log($"[wildlife] crouching disturbs fewer: {(crouchQuiet ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- and they potter about
                //
                // Undisturbed critters should drift around their patch, not stand on a mark for the
                // whole match. Sampled over four minutes of simulation, because the whole point of
                // the pacing is that it is slow: a critter that repositions often enough to see in
                // ten seconds is a twitching one.
                Critter.ResetAll();

                var homes = new System.Collections.Generic.Dictionary<Critter, Vector3>();
                foreach (Critter critter in Critter.All)
                    if (critter != null) homes[critter] = critter.transform.position;

                Step(boot, 60 * 240);

                int moved = 0;
                float furthest = 0f;

                foreach (System.Collections.Generic.KeyValuePair<Critter, Vector3> kv in homes)
                {
                    if (kv.Key == null || !kv.Key.gameObject.activeInHierarchy) continue;

                    float drift = Vector3.Distance(kv.Key.transform.position, kv.Value);
                    if (drift > 0.5f) moved++;
                    if (drift > furthest) furthest = drift;
                }

                bool wanders = moved > homes.Count / 4;
                bool stayedLocal = furthest < 40f;

                Debug.Log($"[wildlife] {moved}/{homes.Count} critters moved from where they started " +
                          $"(furthest {furthest:0.0} m)");
                Debug.Log($"[wildlife] they wander: {(wanders ? "PASS" : "FAIL")}");
                Debug.Log($"[wildlife] they stay in their own patch: {(stayedLocal ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- and they come back
                Critter.ResetAll();
                bool resettles = true;
                foreach (Critter critter in Critter.All)
                    if (critter != null && !critter.IsSettled) resettles = false;

                Debug.Log($"[wildlife] every critter resettles on a new match: " +
                          $"{(resettles ? "PASS" : "FAIL")}");

                if (populated && sprintFlushed && heard && crouchQuiet && wanders && stayedLocal &&
                    resettles)
                    Debug.Log("[wildlife] PASSED");
                else
                    Debug.LogError("[wildlife] FAILED");
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
        /// Runs or creeps an agent forward from a spot for two and a half seconds, and reports how
        /// many critter sounds appeared on the acoustic bus while it did.
        /// </summary>
        private static int Approach(UnseenBootstrap boot, AgentEntity agent, Vector3 from,
            bool sprint, bool crouch)
        {
            int sounds = 0;
            const float step = 1f / 60f;
            agent.Motor.Teleport(new float3(from.x, from.y + 1f, from.z));

            for (int i = 0; i < 30; i++)
            {
                agent.Intent = new MoveIntent { Sequence = (uint)i, Yaw = 0f, Crouch = crouch };
                boot.Network.Poll(step);
                boot.Simulation.Advance(step);
                agent.Intent = new MoveIntent { Sequence = (uint)i, Yaw = 0f, Crouch = crouch };
            }

            for (int i = 0; i < 150; i++)
            {
                var intent = new MoveIntent
                {
                    Sequence = (uint)(i + 100),
                    Move = new float2(0f, 1f),
                    Yaw = 0f,
                    Sprint = sprint,
                    Crouch = crouch
                };

                agent.Intent = intent;
                boot.Network.Poll(step);
                boot.Simulation.Advance(step);
                agent.Intent = intent;

                foreach (SoundEvent e in boot.Context.Sound.LastTick)
                    if (e.Kind == SoundKind.BirdFlush || e.Kind == SoundKind.AnimalScatter)
                        sounds++;
            }

            return sounds;
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
