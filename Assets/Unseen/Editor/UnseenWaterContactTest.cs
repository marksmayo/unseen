using UnityEditor;
using UnityEngine;
using Unity.Mathematics;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Crossing the waterline: the noise it makes, and the bubbles it leaves.
    ///
    /// Both exist for the same reason, which is that the river is cover from sight and the opposite
    /// of cover from sound. Wading across is meant to be a choice between being seen and being
    /// heard, and lying submerged is meant to cost air and give a little away - otherwise the
    /// channel is simply the best place in the town to hide and there is nothing to be done about
    /// somebody who is in it.
    ///
    /// Headless, because it needs a generated river to get wet in.
    /// </summary>
    public static class UnseenWaterContactTest
    {
        [MenuItem("Unseen/Test Water Contact", priority = 95)]
        public static void Run()
        {
            var host = new GameObject("WaterContactTest");
            bool passed = true;

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.BuildNavMesh = false;
                boot.Seed = 20260827;
                boot.Boot();

                Step(boot, 60 * 70);

                SimContext ctx = boot.Context;

                AgentEntity agent = ctx.Entities.ByConnection(boot.Network.LocalConnectionId);
                if (agent == null || !agent.IsAlive)
                {
                    Debug.LogError("[water] need a live local agent");
                    return;
                }

                // Somewhere genuinely deep. The shelves are ankle-to-knee and would not submerge a
                // standing body, which is the whole reason DepthAt and IsUnder are different tests.
                if (!FindDeepWater(out float3 deep))
                {
                    Debug.LogError("[water] found no water deep enough to stand in");
                    return;
                }

                float3 dry = deep + new float3(0f, 30f, 0f);

                Teleport(agent, dry);
                Step(boot, 20);

                int before = CountSplashes(ctx);

                // In.
                Teleport(agent, deep);
                Step(boot, 20);

                int afterEntry = CountSplashes(ctx);
                passed &= Check("going in makes a noise", afterEntry > before);

                // Lying down, not just standing in it.
                //
                // The channel is 1.35 m deep and a standing eye sits about 1.6 m up, so a body
                // standing on the bed has its head in the air - the controller resolves it up out
                // of the riverbed and there it stays. That is the design rather than a problem with
                // it: the river hides you if you lie in it and costs you air for doing so, and
                // wading across is meant to leave you visible.
                //
                // Driven through Intent rather than by setting the stance, so the controller
                // actually shrinks and the eye actually comes down. Writing the stance alone would
                // relabel the body without lowering anything.
                agent.Intent = new MoveIntent { Prone = true, Zone = GuardZone.Mid };
                Step(boot, 60);

                passed &= Check("lying in the channel puts the head under",
                    (agent.Flags & AgentFlags.Submerged) != 0);

                agent.Intent = MoveIntent.Idle;

                // Out. Rate limited, so the clock has to move past SplashInterval before the second
                // crossing can be heard at all - which is the behaviour, not a wait for convenience.
                Step(boot, 90);
                Teleport(agent, dry);
                Step(boot, 20);

                passed &= Check("coming out makes one too", CountSplashes(ctx) > afterEntry);

                passed &= Check("and the flag clears when the head is up",
                    (agent.Flags & AgentFlags.Submerged) == 0);

                Debug.Log(passed ? "[water] PASSED" : "[water] FAILED");
            }
            finally
            {
                Object.DestroyImmediate(host);
                foreach (MapDescriptor map in Object.FindObjectsByType<MapDescriptor>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (map != null) Object.DestroyImmediate(map.gameObject);

                GameObject town = GameObject.Find("GreyboxTown");
                if (town != null) Object.DestroyImmediate(town);
            }
        }

        /// <summary>
        /// Somewhere a body can be put with its head under the surface.
        ///
        /// Not the bed of the channel. The river is 1.35 m deep and a standing eye sits 1.6 m up,
        /// so a body standing on the bottom is emphatically not submerged - which is the design
        /// ("going prone in the deep channel does and crouching on the shelf does not") and was the
        /// first thing this probe got wrong. What IsUnder actually asks is whether a point is
        /// covered by a volume and below its surface, so the answer is to place the body low enough
        /// that its eye is, and let it clip the bed.
        ///
        /// Offsets tried around the centre because a volume can have an island cut out of it, and
        /// the middle of the river is exactly where the keep stands.
        /// </summary>
        private static bool FindDeepWater(out float3 point)
        {
            point = default;

            foreach (WaterVolume volume in WaterVolume.All)
            {
                if (volume == null || volume.MaxDepth <= 0.3f) continue;

                Vector3 centre = volume.transform.position;

                var offsets = new[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(0f, volume.HalfSize.y * 0.6f),
                    new Vector2(0f, -volume.HalfSize.y * 0.6f),
                    new Vector2(volume.HalfSize.x * 0.6f, 0f),
                    new Vector2(-volume.HalfSize.x * 0.6f, 0f)
                };

                foreach (Vector2 offset in offsets)
                {
                    float x = centre.x + offset.x;
                    float z = centre.z + offset.y;

                    if (!WaterVolume.OverWater(x, z)) continue;

                    // Low enough that the eye goes under, whatever the bed is doing.
                    var candidate = new float3(x, volume.SurfaceY - 2.0f, z);

                    if (!WaterVolume.IsUnder(candidate + new float3(0f, 1.6f, 0f))) continue;
                    if (WaterVolume.DepthAt(candidate) <= 0.25f) continue;

                    point = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Splashes that reached the sound bus.
        ///
        /// Counted off the bus rather than out of an agent's Heard list, which was the first
        /// attempt and always read zero: heard sounds are one-shot and the replication send clears
        /// them, and replication runs last. Anything looked for after Advance has already been
        /// consumed and cleared.
        ///
        /// The bus is still the right place rather than reaching into the drowning system, because
        /// a splash that never reaches the acoustic model is not a splash - it would occlude
        /// nothing, carry nowhere and give no position away, which is the entire point of it.
        /// </summary>
        private static int CountSplashes(SimContext ctx) => _splashesSeen;

        private static int _splashesSeen;

        private static void Teleport(AgentEntity agent, float3 to)
        {
            CharacterController controller = agent.Controller;

            if (controller != null) controller.enabled = false;
            agent.Position = to;
            if (controller != null) controller.enabled = true;
        }

        private static bool Check(string what, bool ok)
        {
            Debug.Log($"[water] {what}: {(ok ? "PASS" : "FAIL")}");
            return ok;
        }

        private static void Step(UnseenBootstrap boot, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                boot.Network.Poll(1f / 60f);
                boot.Simulation.Advance(1f / 60f);

                // Counted as they go past. The bus keeps one tick of history and the queue is
                // drained every tick, so anything not read now is gone.
                SimContext ctx = boot.Context;
                if (ctx.Sound == null) continue;

                foreach (SoundEvent e in ctx.Sound.LastTick)
                    if (e.Kind == SoundKind.Splash) _splashesSeen++;
            }
        }
    }
}
