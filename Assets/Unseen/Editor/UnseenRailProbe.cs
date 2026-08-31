using UnityEditor;
using UnityEditor.Animations;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Movement;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks that a bridge rail stops you leaning on it and does not stop you leaving.
    ///
    /// Making the rails solid fixed one thing and risked another. A handrail you can walk straight
    /// through is scenery; a handrail you cannot get over turns every bridge into a corridor, and
    /// in a game about rooftops and vertical routes that is worse than the bug it replaced.
    ///
    /// So both halves are measured, and they have to disagree - walking into a rail should stop,
    /// jumping at it should not. A build where both came out the same would mean the rail is either
    /// a wall or a ghost, and the pair is what tells them apart.
    /// </summary>
    public static class UnseenRailProbe
    {
        [MenuItem("Unseen/Probe Rails", priority = 73)]
        public static void Run()
        {
            var host = new GameObject("RailProbe");

            UnseenConfig config = UnseenConfig.Default;
            int roster = config.Match.TargetEntityCount;
            bool skipped = config.Match.SkipInfiltration;

            config.Match.TargetEntityCount = 4;
            config.Match.SkipInfiltration = true;

            try
            {
                var boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.BuildNavMesh = false;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260831;
                boot.Boot();

                Step(boot, 120);
                Physics.SyncTransforms();

                float jump = config.Movement.JumpVelocity;
                float gravity = Mathf.Abs(config.Movement.Gravity);
                float apex = jump * jump / (2f * gravity);

                Debug.Log($"[rail] a standing jump lifts the feet {apex:0.00} m " +
                          $"(JumpVelocity {jump:0.0}, gravity {gravity:0.0})");

                // ------------------------------------------------------------------ find a rail
                // Searched across the scene, not under the probe host: the bootstrap parents the
                // generated town somewhere of its own choosing, and looking under the host found
                // nothing at all.
                Transform rail = null;
                var boxes = Object.FindObjectsByType<BoxCollider>(FindObjectsSortMode.None);

                for (int i = 0; i < boxes.Length; i++)
                {
                    if (boxes[i] == null || !boxes[i].name.StartsWith("Rail_")) continue;
                    rail = boxes[i].transform;
                    break;
                }

                if (rail == null) { Debug.LogError("[rail] no bridge rail found"); return; }

                var box = rail.GetComponent<BoxCollider>();
                Bounds bounds = rail.GetComponent<Collider>().bounds;

                Debug.Log($"[rail] testing {rail.name} at {bounds.center}, " +
                          $"{bounds.size.y:0.00} m tall, top {bounds.max.y:0.00}");

                AgentEntity agent = null;
                foreach (AgentEntity a in boot.Context.Entities.All)
                    if (a != null && a.IsAlive) { agent = a; break; }

                if (agent == null) { Debug.LogError("[rail] no live agent"); return; }
                if (agent.Brain != null) agent.Brain.enabled = false;

                // The deck is under the rail; stand a stride back from it on the bridge side.
                float deckY = FindDeck(bounds);

                if (float.IsNaN(deckY)) { Debug.LogError("[rail] no deck under the rail"); return; }

                Debug.Log($"[rail] deck at {deckY:0.00} m, so the rail stands " +
                          $"{bounds.max.y - deckY:0.00} m above it");

                // Which way is off the bridge: whichever side of the rail has no deck.
                float3 outward = OutwardFrom(bounds, deckY);

                // ------------------------------------------------------------------ 1. it stops you
                float walkedTo = Drive(boot, agent, bounds, deckY, outward, jumping: false);
                bool stopsAWalk = walkedTo < 0.35f;

                Debug.Log($"[rail] walking into it: crossed {walkedTo:0.00} m past the rail line");
                Debug.Log($"[rail] a rail stops you walking off: " +
                          $"{(stopsAWalk ? "PASS" : "FAIL")}");

                // ------------------------------------------------------------------ 2. you can leave
                float jumpedTo = Drive(boot, agent, bounds, deckY, outward, jumping: true);
                bool canGetOver = jumpedTo > 0.35f;

                Debug.Log($"[rail] jumping at it: crossed {jumpedTo:0.00} m past the rail line");
                Debug.Log($"[rail] you can still get over it on purpose: " +
                          $"{(canGetOver ? "PASS" : "FAIL")}");

                Debug.Log($"[rail] arrived within 0.9 m on {NearRailTicks} ticks, grounded on " +
                          $"{GroundedAtRail} of them; last state there {LastStateAtRail}");

                bool vaulted = SawVault;
                Debug.Log($"[rail] the motor asked for a vault while getting over: " +
                          $"{(vaulted ? "PASS" : "FAIL")}");

                bool animated = CheckVaultAnimation();

                if (stopsAWalk && canGetOver && vaulted && animated)
                    Debug.Log("[rail] PASSED");
                else
                    Debug.LogError("[rail] FAILED");
            }
            finally
            {
                config.Match.TargetEntityCount = roster;
                config.Match.SkipInfiltration = skipped;

                var boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();

                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// Runs an agent at the rail and returns how far past it the body got, in metres.
        ///
        /// Measured along the outward normal rather than as a position, so the answer means the
        /// same thing on a rail running north and a rail running east.
        /// </summary>
        private static float Drive(UnseenBootstrap boot, AgentEntity agent, Bounds bounds,
            float deckY, float3 outward, bool jumping)
        {
            // Standing back far enough to be walking at full speed when it arrives, and on the
            // deck rather than inside the rail.
            float3 start = (float3)bounds.center - outward * 2.2f;
            start.y = deckY + 0.1f;

            agent.Motor.Teleport(start);
            agent.Stance = Stance.Stand;
            Physics.SyncTransforms();
            Step(boot, 20);

            float best = float.MinValue;
            SawVault = false;
            NearRailTicks = 0;
            GroundedAtRail = 0;

            for (int i = 0; i < 180; i++)
            {
                // Jump AT the rail, not from across the deck.
                //
                // Holding the jump bit from the start made the agent leap the instant it began
                // walking, two metres out, and sail over the rail without the mantle probe ever
                // reaching it - so the vault never fired and this read as a missing feature. A
                // player presses jump when they arrive at the thing they are jumping.
                float distance = -math.dot((float3)agent.Position - (float3)bounds.center, outward);
                bool atTheRail = distance < 0.9f;

                var intent = new MoveIntent
                {
                    Sequence = (uint)(200 + i),
                    Move = new float2(0f, 1f),
                    Yaw = UnseenMath.ForwardToYaw(outward),
                    Jump = jumping && atTheRail
                };

                agent.Intent = intent;
                agent.Yaw = intent.Yaw;

                boot.Network.Poll(1f / 60f);
                boot.Simulation.Advance(1f / 60f);

                agent.Intent = intent;

                // Whether the motor ever asked for a vault. This is the hook the animation hangs
                // on, so a rail you get over by some other means would leave it false.
                // Either shape of vault counts: a warp onto the far side of a low wall, or the
                // vault pose laid over a jump when there is nothing to land on.
                if (agent.Motor != null &&
                    ((agent.Motor.IsWarping && agent.Motor.Warp == WarpStyle.Vault) ||
                     agent.Motor.IsVaulting)) SawVault = true;

                // What state the body was in when it arrived, which is the precondition the
                // mantle probe needs: TryMantle is only reached from TickGrounded.
                if (atTheRail && jumping)
                {
                    NearRailTicks++;
                    if (agent.Locomotion == LocomotionState.Grounded) GroundedAtRail++;
                    LastStateAtRail = agent.Locomotion;
                }

                float past = math.dot((float3)agent.Position - (float3)bounds.center, outward);
                best = math.max(best, past);
            }

            return best;
        }

        /// <summary>Set by Drive: whether a vault-styled motion warp ran during the attempt.</summary>
        private static bool SawVault;
        private static int NearRailTicks;
        private static int GroundedAtRail;
        private static LocomotionState LastStateAtRail;

        /// <summary>The deck height under a rail: the first surface below it that is not the rail.</summary>
        private static float FindDeck(Bounds bounds)
        {
            Vector3 from = bounds.center + Vector3.up * 0.5f;

            RaycastHit[] hits = Physics.RaycastAll(from, Vector3.down, 8f,
                UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore);

            float best = float.NaN;

            for (int i = 0; i < hits.Length; i++)
            {
                float y = hits[i].point.y;
                if (y > bounds.min.y - 0.05f) continue;
                if (float.IsNaN(best) || y > best) best = y;
            }

            return best;
        }

        /// <summary>
        /// Which way is off the bridge.
        ///
        /// Taken by looking for deck on each side rather than assumed from the rail's rotation: a
        /// rail runs along the bridge, so its own axes say nothing about which side the drop is on.
        /// </summary>
        private static float3 OutwardFrom(Bounds bounds, float deckY)
        {
            float3 alongX = new float3(1f, 0f, 0f);
            float3 alongZ = new float3(0f, 0f, 1f);

            // The rail is long in one axis; the drop is across the other.
            float3 across = bounds.size.x > bounds.size.z ? alongZ : alongX;

            Vector3 centre = bounds.center;
            centre.y = deckY + 0.6f;

            bool deckPositive = Physics.Raycast(centre + (Vector3)across * 1.2f, Vector3.down, 2f,
                UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore);

            return deckPositive ? -across : across;
        }

        /// <summary>
        /// Whether the Parkour layer can actually play a vault.
        ///
        /// Checked as an asset rather than by watching the Animator, because AgentVisual drives it
        /// from LateUpdate and no MonoBehaviour callback runs outside play mode - watching the live
        /// rig here would report nothing regardless of whether it works.
        /// </summary>
        private static bool CheckVaultAnimation()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(
                "Assets/Unseen/Art/Characters/NinjaAnimator.controller");

            if (controller == null) { Debug.LogError("[rail] no animator controller"); return false; }

            foreach (AnimatorControllerLayer layer in controller.layers)
            {
                if (layer.name != "Parkour") continue;

                foreach (AnimatorStateTransition t in layer.stateMachine.anyStateTransitions)
                {
                    if (t.destinationState == null || t.destinationState.name != "Vault") continue;

                    var clip = t.destinationState.motion as AnimationClip;
                    bool hasClip = clip != null && clip.length > 0.05f;

                    Debug.Log($"[rail] Parkour layer has a Vault state, clip " +
                              $"{(clip != null ? clip.name : "MISSING")} " +
                              $"{(clip != null ? clip.length : 0f):0.00} s");
                    Debug.Log($"[rail] a vault has an animation to play: " +
                              $"{(hasClip ? "PASS" : "FAIL")}");

                    return hasClip;
                }
            }

            Debug.LogError("[rail] no Any State -> Vault transition on the Parkour layer");
            return false;
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
