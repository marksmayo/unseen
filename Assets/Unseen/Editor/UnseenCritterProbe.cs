using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;
using Unseen.Net;
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

                DeliveredToStartler = 0;
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

                bool ownEars = DeliveredToStartler > 0;
                Debug.Log($"[wildlife] {DeliveredToStartler} of them delivered to the ear of the " +
                          $"player who caused them");
                Debug.Log($"[wildlife] you hear the bird you flushed: " +
                          $"{(ownEars ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- never quite still
                //
                // A critter spends most of its life settled, and a settled critter that does not
                // move is an ornament. Measured as how far the head and the tail actually turn over
                // six seconds of resting, because "it has an idle animation" is a statement about
                // the code and this is a statement about the thing on screen.
                //
                // Also measured: that all this motion does not carry the body off its perch. An
                // idle that walks is a bug that would take a long time to notice.
                Critter resting = null;
                foreach (Critter candidate in Critter.All)
                {
                    if (candidate == null || candidate.Kind != Critter.Species.Animal) continue;
                    if (!candidate.IsSettled) continue;
                    resting = candidate;
                    break;
                }

                bool fidgets = false;

                if (resting == null)
                {
                    Debug.LogError("[wildlife] found no settled animal to watch");
                }
                else
                {
                    Transform head = FindPart(resting.transform, "Head");
                    Transform tail = FindPart(resting.transform, "Tail");

                    Quaternion headWas = head != null ? head.localRotation : Quaternion.identity;
                    Quaternion tailWas = tail != null ? tail.localRotation : Quaternion.identity;

                    float headSwing = 0f;
                    float tailSwing = 0f;
                    float drift = 0f;

                    // Drift is measured only across a single RESTING spell, re-anchored whenever
                    // one begins.
                    //
                    // Rests are two and a half to nine seconds now, so a six second window contains
                    // an outing - and the first version of this measured the stroll and reported
                    // 2.19 m of drift, which is the critter walking somewhere on purpose rather
                    // than an idle that cannot hold still.
                    Vector3 anchor = resting.transform.position;
                    bool wasResting = resting.IsResting;

                    for (int i = 0; i < 60 * 6; i++)
                    {
                        resting.Advance(1f / 60f);

                        // The head and tail should move whatever it is doing, so those accumulate
                        // across the whole window.
                        if (head != null)
                            headSwing = Mathf.Max(headSwing,
                                Quaternion.Angle(headWas, head.localRotation));

                        if (tail != null)
                            tailSwing = Mathf.Max(tailSwing,
                                Quaternion.Angle(tailWas, tail.localRotation));

                        if (!resting.IsResting) { wasResting = false; continue; }

                        if (!wasResting)
                        {
                            anchor = resting.transform.position;
                            wasResting = true;
                        }

                        drift = Mathf.Max(drift, Vector3.Distance(anchor, resting.transform.position));
                    }

                    // The head has to genuinely look about; the tail never stops. Both thresholds
                    // are well under what the idle actually produces, so this fails on the idle
                    // being absent rather than on it being retuned.
                    bool looks = head == null || headSwing > 12f;
                    bool wags = tail == null || tailSwing > 6f;
                    bool stays = drift < 0.25f;

                    fidgets = looks && wags && stays;

                    Debug.Log($"[wildlife] resting animal over six seconds: head turned " +
                              $"{headSwing:0} deg, tail moved {tailSwing:0} deg, body drifted " +
                              $"{drift:0.00} m");
                    Debug.Log($"[wildlife] a settled critter is never quite still: " +
                              $"{(fidgets ? "PASS" : "FAIL")}");
                }

                // ---------------------------------------------------------- nothing vanishes
                //
                // A bird can leave - it goes up and out of sight, and switching it off once it is a
                // dot against the sky costs nothing. Something on four legs cannot leave. It bolts
                // and stops somewhere else on the ground, and it used to switch itself off two
                // seconds into the run: a rabbit you disturbed blinked out of existence in front of
                // you and reappeared on its old spot twenty seconds later.
                Critter animal = null;
                foreach (Critter candidate in Critter.All)
                {
                    if (candidate == null || candidate.Kind == Critter.Species.Bird) continue;
                    if (!candidate.IsSettled) continue;
                    animal = candidate;
                    break;
                }

                bool bolts = false;

                if (animal == null)
                {
                    Debug.LogError("[wildlife] found no settled animal to startle");
                }
                else
                {
                    Vector3 stood = animal.transform.position;
                    animal.Flush(stood + Vector3.forward * 2f);

                    // Advanced only until the run ENDS, rather than for a fixed six seconds.
                    //
                    // A bolt is under two seconds and a rest is as little as one and a quarter after
                    // it, so a fixed window let the animal set off on an ordinary stroll before the
                    // assertions ran - and then reported it mid-stride, off the ground, as a
                    // failure to land. What is under test is where the bolt leaves it.
                    for (int i = 0; i < 60 * 8 && !animal.IsResting; i++) animal.Advance(1f / 60f);

                    Vector3 now = animal.transform.position;
                    float bolted = Vector3.Distance(stood, now);

                    bool present = animal.gameObject.activeSelf;
                    bool relocated = bolted > 0.6f;

                    // On the floor, not hanging where the bolt happened to end.
                    bool grounded = Physics.Raycast(now + Vector3.up * 1.5f, Vector3.down,
                        out RaycastHit under, 4f, UnseenLayers.WorldGeometry,
                        QueryTriggerInteraction.Ignore) && Mathf.Abs(now.y - under.point.y) < 0.4f;

                    bolts = present && relocated && grounded;

                    Debug.Log($"[wildlife] startled animal: still in the world {present}, " +
                              $"moved {bolted:0.0} m, feet on the ground {grounded}");
                    Debug.Log($"[wildlife] an animal runs somewhere instead of vanishing: " +
                              $"{(bolts ? "PASS" : "FAIL")}");
                }

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

                Critter.StrollsStarted = 0;
                Step(boot, 60 * 240);

                int strolls = Critter.StrollsStarted;

                int moved = 0;
                float furthest = 0f;

                foreach (System.Collections.Generic.KeyValuePair<Critter, Vector3> kv in homes)
                {
                    if (kv.Key == null || !kv.Key.gameObject.activeInHierarchy) continue;

                    float drift = Vector3.Distance(kv.Key.transform.position, kv.Value);
                    if (drift > 0.5f) moved++;
                    if (drift > furthest) furthest = drift;
                }

                // Counted as STROLLS BEGUN, not as bodies away from their start.
                //
                // Displacement at one moment is a bad measure of wandering: every target is picked
                // relative to the critter's home, so a critter that has pottered about twenty times
                // is no further from home than one that went once, and one caught mid-stroll on the
                // way back looks like it never moved. Judged on displacement, this read 149 of 289
                // one day and 68 the next with no change to any critter - which says the number was
                // measuring the sampling instant, not the behaviour.
                //
                // Over four minutes, with eighteen to fifty-five seconds between outings, a town
                // of this size should manage several outings apiece - four minutes is between four
                // and thirteen rest periods. Three per critter is a floor, not a target.
                bool wanders = strolls > homes.Count * 3;
                bool stayedLocal = furthest < 40f;

                Debug.Log($"[wildlife] {strolls} strolls begun by {homes.Count} critters over four " +
                          $"minutes; {moved} are away from their start right now " +
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

                if (populated && sprintFlushed && heard && ownEars && bolts && fidgets && crouchQuiet &&
                    wanders && stayedLocal &&
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

                // And separately, what the PLAYER was actually sent.
                //
                // Not the same question as whether the sound was emitted. The acoustic model
                // refuses to deliver a sound to the agent it names as the source - correct for
                // footsteps - so a flush credited to the player who caused it reached every ear in
                // the match except theirs. The player is the one who most needs to hear it: the
                // whole mechanic is that you know you have just announced yourself.
                //
                // Read off the decoded client snapshot rather than the agent's Heard list, because
                // replication CONSUMES that list on send - it is a one-shot ping, not state - so
                // by the time a test looks at it after the tick it is always empty.
                SnapshotData snapshot = boot.ClientView != null ? boot.ClientView.Latest : null;
                if (snapshot != null)
                {
                    foreach (HeardSound h in snapshot.Sounds)
                        if (h.Kind == SoundKind.BirdFlush || h.Kind == SoundKind.AnimalScatter)
                            DeliveredToStartler++;
                }
            }

            return sounds;
        }

        /// <summary>Flush sounds delivered to the ear of the agent who caused them.</summary>
        private static int DeliveredToStartler;

        /// <summary>A named child anywhere under a critter.</summary>
        private static Transform FindPart(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;

            return null;
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
