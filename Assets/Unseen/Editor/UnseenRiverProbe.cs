using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Audio;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks that the river is water rather than a blue floor.
    ///
    /// The surface used to be an ordinary collider, so a player walked across the channel with dry
    /// feet - which no screenshot reveals, because a ninja standing on water and a ninja standing
    /// in ankle-deep water look identical from above. The three things that distinguish them are
    /// measurable: where the feet come to rest relative to the surface, how fast you cross, and
    /// which footstep sound the surface underfoot selects.
    /// </summary>
    public static class UnseenRiverProbe
    {
        [MenuItem("Unseen/Test River", priority = 89)]
        public static void Run()
        {
            var host = new GameObject("RiverProbe");

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

                Transform water = GameObject.Find("Water") != null
                    ? GameObject.Find("Water").transform
                    : null;

                var volume = Object.FindAnyObjectByType<WaterVolume>();
                if (volume == null || water == null)
                {
                    Debug.LogError("[river] no water volume in the level");
                    return;
                }

                foreach (WaterVolume v in Object.FindObjectsByType<WaterVolume>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                    Debug.Log($"[river] volume '{v.name}' at {v.transform.position} " +
                              $"halfSize {v.HalfSize} surfaceY {v.SurfaceY} maxDepth {v.MaxDepth} " +
                              $"enabled={v.enabled} active={v.gameObject.activeInHierarchy}");

                Debug.Log($"[river] {WaterVolume.Registered} volume(s) registered for queries");

                float surface = volume.SurfaceY;
                float centreX = water.position.x;
                Debug.Log($"[river] surface at y {surface:0.00}, channel centred on x {centreX:0.0}");

                // ---------------------------------------------------------- you sink into it
                AgentEntity subject = null;
                foreach (AgentEntity agent in boot.Context.Entities.All)
                    if (agent.IsAlive)
                    {
                        subject = agent;
                        break;
                    }

                if (subject == null)
                {
                    Debug.LogError("[river] no live agent to test with");
                    return;
                }

                // Middle of the channel, dropped from just above the surface, and held still on
                // the way down: the subject is a bot, and left to itself BotDirector walks it out
                // of the river before the measurement is taken.
                Settle(boot, subject, new float3(centreX, surface + 3f, 40f));

                float feet = subject.Position.y;
                float depth = WaterVolume.DepthAt(subject.Position);

                Debug.Log($"[river] standing mid-channel: agent at {subject.Position} " +
                          $"feet {feet:0.00}, DepthAt says {depth:0.00} m");

                bool wadesDeep = depth > 0.9f;
                Debug.Log($"[river] the middle is more than knee deep: {(wadesDeep ? "PASS" : "FAIL")}");

                // The shelf along the bank should be crossable rather than a plunge.
                Settle(boot, subject, new float3(centreX + 6f, surface + 3f, 40f));
                float shelfDepth = WaterVolume.DepthAt(subject.Position);
                Debug.Log($"[river] standing on the shelf: {shelfDepth:0.00} m of water");

                bool shelfShallower = shelfDepth > 0.3f && shelfDepth < depth - 0.2f;
                Debug.Log($"[river] the banks are shallower than the middle: " +
                          $"{(shelfShallower ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- it slows you down
                //
                // The dry run needs somewhere genuinely dry, and it used to be written in as
                // "a hundred and twenty metres east of the channel". The castle lake was then built
                // across that spot - it spans a hundred and twenty metres of the middle of the map -
                // so the control run was wading too, both figures came out at exactly 4.8 m, and the
                // probe reported that wading is no slower than walking. The wade penalty was fine
                // the whole time; the dry ground had gone.
                if (!FindDryGround(out Vector3 dry))
                {
                    Debug.LogError("[river] found nowhere dry to run the control on");
                    return;
                }

                float dryRun = Traverse(boot, subject, new float3(dry.x, dry.y + 1f, dry.z));
                float wetRun = Traverse(boot, subject, new float3(centreX, surface + 3f, 40f));

                Debug.Log($"[river] control run on dry land at {dry}");
                Debug.Log($"[river] 3 s of running: {dryRun:0.0} m on the street, {wetRun:0.0} m in the water");
                bool slower = wetRun < dryRun * 0.75f;
                Debug.Log($"[river] wading is slower than walking: {(slower ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- it sounds wet
                //
                // The footstep is chosen from the acoustic material underfoot, so the question is
                // what the bed reports - not what the water does.
                bool splashes = false;
                string underfoot = "nothing";

                if (Physics.Raycast(new Vector3(centreX, surface + 1f, 40f), Vector3.down,
                        out RaycastHit hit, 8f, UnseenLayers.WorldGeometry,
                        QueryTriggerInteraction.Ignore))
                {
                    underfoot = hit.collider.name;
                    var mat = hit.collider.GetComponent<AcousticMaterial>();
                    AudioBank bank = AudioBank.Load();

                    if (mat != null && bank != null)
                    {
                        AudioClip clip = bank.FootstepFor(mat);
                        splashes = clip != null && clip.name.Contains("water");
                        Debug.Log($"[river] underfoot '{underfoot}' -> footstep clip " +
                                  $"'{(clip != null ? clip.name : "none")}'");
                    }
                    else
                    {
                        Debug.Log($"[river] underfoot '{underfoot}' has no acoustic material");
                    }
                }

                Debug.Log($"[river] footsteps in the channel splash: {(splashes ? "PASS" : "FAIL")}");

                if (wadesDeep && shelfShallower && slower && splashes) Debug.Log("[river] PASSED");
                else Debug.LogError("[river] FAILED");
            }
            finally
            {
                UnseenBootstrap boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// Drops an agent at a spot and holds it there until it has stopped falling.
        ///
        /// The idle intent is reapplied after each advance because ServerInputSystem overwrites
        /// Intent from the network every tick, and BotDirector fills it in for bots.
        /// </summary>
        /// <summary>
        /// Somewhere flat, open and with no water over it.
        ///
        /// Asked of the world rather than written down, so this cannot silently end up underneath
        /// the next body of water somebody adds to the middle of the map.
        /// </summary>
        private static bool FindDryGround(out Vector3 spot)
        {
            spot = Vector3.zero;

            for (int i = 0; i < 400; i++)
            {
                float angle = i * 41f * Mathf.Deg2Rad;
                float reach = 40f + i * 1.4f;
                var above = new Vector3(Mathf.Sin(angle) * reach, 80f, Mathf.Cos(angle) * reach);

                if (!Physics.Raycast(above, Vector3.down, out RaycastHit ground, 140f,
                        UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                    continue;

                if (ground.normal.y < 0.98f) continue;

                var feet = new float3(ground.point.x, ground.point.y + 0.1f, ground.point.z);
                if (WaterVolume.DepthAt(feet) > 0.01f) continue;

                // And clear ahead, or the control run measures a wall rather than a stride.
                if (Physics.Raycast(ground.point + Vector3.up * 1.2f, Vector3.forward, 14f,
                        UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                    continue;

                spot = ground.point;
                return true;
            }

            return false;
        }

        private static void Settle(UnseenBootstrap boot, AgentEntity agent, float3 at)
        {
            const float step = 1f / 60f;
            agent.Motor.Teleport(at);

            for (int i = 0; i < 150; i++)
            {
                var idle = new MoveIntent { Sequence = (uint)i, Yaw = agent.Yaw };
                agent.Intent = idle;

                boot.Network.Poll(step);
                boot.Simulation.Advance(step);

                agent.Intent = idle;
            }
        }

        /// <summary>Drops the agent at a spot, lets it settle, then runs it forward for 3 s.</summary>
        private static float Traverse(UnseenBootstrap boot, AgentEntity agent, float3 from)
        {
            Settle(boot, agent, from);

            float3 start = agent.Position;

            const float step = 1f / 60f;
            for (int i = 0; i < 180; i++)
            {
                var intent = new MoveIntent
                {
                    Sequence = (uint)i,
                    Move = new float2(0f, 1f),
                    Yaw = agent.Yaw,
                    Sprint = true
                };

                agent.Intent = intent;
                boot.Network.Poll(step);
                boot.Simulation.Advance(step);

                // ServerInputSystem overwrites Intent from the network each tick, so the scripted
                // value is reapplied after the input stage rather than before it.
                agent.Intent = intent;
            }

            float3 travelled = agent.Position - start;
            travelled.y = 0f;
            return math.length(travelled);
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
