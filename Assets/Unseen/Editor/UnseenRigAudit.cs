using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks that every spawned ninja is actually wired to animate.
    ///
    /// Bots were seen standing in the bind pose - arms straight out - in a live match while others
    /// beside them moved normally. A body in its bind pose is one nothing is driving: either it has
    /// no Animator, or the Animator has no controller, or something switched it off and did not
    /// switch it back on.
    ///
    /// Animation itself cannot be exercised here, because Unity runs no MonoBehaviour callbacks and
    /// no Animator evaluation outside play mode. What CAN be checked outside play mode is whether
    /// every agent is set up identically, and that is the useful question: a fault that affects some
    /// agents and not others is almost always a difference in how they were built.
    /// </summary>
    public static class UnseenRigAudit
    {
        [MenuItem("Unseen/Diagnose Ninja Rigs", priority = 93)]
        public static void Run()
        {
            var host = new GameObject("RigAudit");

            UnseenConfig config = UnseenConfig.Default;
            int roster = config.Match.TargetEntityCount;
            config.Match.TargetEntityCount = 24;

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260824;
                boot.Boot();

                for (int i = 0; i < 60 * 70; i++)
                {
                    boot.Network.Poll(1f / 60f);
                    boot.Simulation.Advance(1f / 60f);
                }

                int total = 0;
                int noVisual = 0;
                int noRig = 0;
                int noController = 0;
                int rigDisabled = 0;
                int visualDisabled = 0;
                int noBody = 0;

                var cullingModes = new Dictionary<string, int>(4);
                var controllers = new Dictionary<string, int>(4);

                foreach (AgentEntity agent in boot.Context.Entities.All)
                {
                    if (agent == null) continue;
                    total++;

                    var visual = agent.GetComponentInChildren<AgentVisual>(true);
                    if (visual == null)
                    {
                        noVisual++;
                        Debug.Log($"[rig] {agent.Id} has no AgentVisual at all");
                        continue;
                    }

                    // A DEAD ninja is supposed to have its visual and animator switched off: the
                    // death visual takes the body over and poses it by hand. Only a living one with
                    // nothing driving it is a fault.
                    if (!visual.enabled && agent.IsAlive)
                    {
                        visualDisabled++;
                        Debug.Log($"[rig] {agent.Id} is ALIVE with its visual disabled: " +
                                  $"flags={agent.Flags}, locomotion={agent.Locomotion}");
                    }
                    if (visual.Body == null) noBody++;

                    Animator rig = visual.Rig != null
                        ? visual.Rig
                        : visual.GetComponentInChildren<Animator>(true);

                    if (rig == null)
                    {
                        noRig++;
                        Debug.Log($"[rig] {agent.Id} has no Animator");
                        continue;
                    }

                    if (!rig.enabled && agent.IsAlive) rigDisabled++;

                    string mode = rig.cullingMode.ToString();
                    cullingModes.TryGetValue(mode, out int mc);
                    cullingModes[mode] = mc + 1;

                    RuntimeAnimatorController rac = rig.runtimeAnimatorController;
                    if (rac == null)
                    {
                        noController++;
                        Debug.Log($"[rig] {agent.Id} has an Animator with no controller");
                        continue;
                    }

                    controllers.TryGetValue(rac.name, out int cc);
                    controllers[rac.name] = cc + 1;
                }

                Debug.Log($"[rig] {total} agents");
                Debug.Log($"[rig] without AgentVisual: {noVisual}, with the component disabled: {visualDisabled}");
                Debug.Log($"[rig] without a SkinnedMeshRenderer: {noBody}");
                Debug.Log($"[rig] without an Animator: {noRig}, with it disabled: {rigDisabled}");
                Debug.Log($"[rig] without an animator controller: {noController}");

                foreach (KeyValuePair<string, int> kv in cullingModes)
                    Debug.Log($"[rig] culling mode {kv.Key}: {kv.Value} agents");

                foreach (KeyValuePair<string, int> kv in controllers)
                    Debug.Log($"[rig] controller '{kv.Key}': {kv.Value} agents");

                bool allWired = noVisual == 0 && noRig == 0 && noController == 0 &&
                                rigDisabled == 0 && visualDisabled == 0 && noBody == 0;

                // ---------------------------------------------------------- the actual invariant
                //
                // Every masked override layer must have a clip in its resting state. A layer with
                // weight and no clip replaces its bones with the BIND pose - a T-pose - and the
                // weight is raised from code the moment a stance or action begins, before the state
                // machine has transitioned anywhere. This is the check that would have caught bots
                // standing in the street with their arms out.
                int emptyRestStates = 0;
                var controllerAsset = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(
                    "Assets/Unseen/Art/Characters/NinjaAnimator.controller");

                if (controllerAsset == null)
                {
                    Debug.Log("[rig] could not load the animator controller asset to inspect");
                }
                else
                {
                    foreach (UnityEditor.Animations.AnimatorControllerLayer l in controllerAsset.layers)
                    {
                        if (l.blendingMode != UnityEditor.Animations.AnimatorLayerBlendingMode.Override)
                            continue;

                        UnityEditor.Animations.AnimatorState def = l.stateMachine != null
                            ? l.stateMachine.defaultState
                            : null;

                        bool empty = def == null || def.motion == null;
                        if (empty) emptyRestStates++;

                        Debug.Log($"[rig] layer '{l.name}': default state " +
                                  $"'{(def != null ? def.name : "none")}' " +
                                  $"motion={(def != null && def.motion != null ? def.motion.name : "NONE")}");
                    }
                }

                bool restStatesPosed = controllerAsset != null && emptyRestStates == 0;
                Debug.Log($"[rig] every override layer rests on a real pose: " +
                          $"{(restStatesPosed ? "PASS" : "FAIL")} ({emptyRestStates} empty)");

                // Every ninja must animate whether or not anyone is looking. AlwaysAnimate costs
                // more than culling does, but a body that only starts moving once it is on screen
                // is a body that is in its bind pose on the frame you first see it - which is the
                // frame that matters in a game about spotting people.
                bool alwaysAnimating = cullingModes.Count == 1 &&
                                       cullingModes.ContainsKey("AlwaysAnimate");

                Debug.Log($"[rig] every agent is wired to animate: {(allWired ? "PASS" : "FAIL")}");
                Debug.Log($"[rig] none are culled out of animating: {(alwaysAnimating ? "PASS" : "FAIL")}");

                if (allWired && alwaysAnimating && restStatesPosed) Debug.Log("[rig] PASSED");
                else Debug.LogError("[rig] FAILED");
            }
            finally
            {
                config.Match.TargetEntityCount = roster;

                UnseenBootstrap boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();
                Object.DestroyImmediate(host);
            }
        }
    }
}
