using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Builds the vault clip and wires it into the Parkour layer.
    ///
    /// A vault used to look like nothing at all: the motion warp carried the body over the rail
    /// while the base locomotion layer kept running, so the ninja slid over a handrail standing
    /// bolt upright. The plumbing to ask for a vault animation now exists; this makes the animation
    /// it asks for.
    ///
    /// The poses are SAMPLED FROM THE EXISTING CLIPS rather than authored as bone rotations. That
    /// is the whole trick here. Writing quaternions per bone means guessing this rig's axis
    /// conventions, getting a sign wrong, and shipping a ninja who vaults backwards through his own
    /// pelvis - and it cannot be checked without eyes on it. Interpolating between a crouch, a jump
    /// and an idle uses poses an artist already made for this skeleton, so every frame is a pose
    /// the rig is known to hold correctly. The only thing added on top is a forward pitch of the
    /// hips, which is the one rotation a vault needs and a jump does not have.
    ///
    /// Generated as an asset rather than at runtime because that is how every other clip in the
    /// project exists, and because a generated asset can be opened and hand-corrected afterwards.
    /// </summary>
    public static class UnseenVaultAnimation
    {
        private const string ClipDirectory = "Assets/Unseen/Art/Characters/Clips";
        private const string ClipPath = ClipDirectory + "/ninja_vault.anim";
        private const string ControllerPath = "Assets/Unseen/Art/Characters/NinjaAnimator.controller";

        /// <summary>Matches Movement.MantleDuration, so the pose lands with the body.</summary>
        private const float Length = 0.45f;

        /// <summary>The Parkour value AgentVisual sends for a vault.</summary>
        private const int VaultParkourValue = 4;

        [MenuItem("Unseen/Art/Build Vault Animation", priority = 53)]
        public static void Build()
        {
            AnimationClip idle = Load("idle");
            AnimationClip crouch = Load("ninja_crouch");
            AnimationClip jump = Load("jump");

            if (idle == null || crouch == null || jump == null)
            {
                Debug.LogError("[vault] need idle, ninja_crouch and jump to build from");
                return;
            }

            // The shape of a vault, in four poses.
            //
            // Plant and tuck, knees up and over, legs reaching for the far side, stand. The times
            // are uneven on purpose: the tuck is quick and the recovery is not, which is what stops
            // it reading as a metronome.
            var stages = new[]
            {
                new Stage { Time = 0f, Source = idle, SourceTime = 0f, HipPitch = 6f },
                new Stage { Time = Length * 0.28f, Source = crouch, SourceTime = 0f, HipPitch = 38f },
                new Stage { Time = Length * 0.62f, Source = jump, SourceTime = 0f, HipPitch = 24f },
                new Stage { Time = Length, Source = idle, SourceTime = 0f, HipPitch = 0f }
            };

            var clip = new AnimationClip { name = "ninja_vault", frameRate = 60f };

            // Every binding any source clip animates, so no bone is left behind at its bind pose
            // while its neighbours move - which is what produces a dislocated shoulder.
            var bindings = new Dictionary<string, EditorCurveBinding>();
            foreach (Stage stage in stages)
                foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(stage.Source))
                    bindings[Key(binding)] = binding;

            int written = 0;

            foreach (EditorCurveBinding binding in bindings.Values)
            {
                var curve = new AnimationCurve();

                for (int i = 0; i < stages.Length; i++)
                {
                    Stage stage = stages[i];
                    float value = Sample(stage.Source, binding, stage.SourceTime);

                    // The hips carry the vault. Pitching them forward is what turns a jump in place
                    // into going over something, and it is the one rotation none of the source
                    // clips contains.
                    if (IsHipsRotation(binding))
                        value = PitchHips(stage.Source, stage.SourceTime, binding, stage.HipPitch);

                    curve.AddKey(new Keyframe(stage.Time, value));
                }

                for (int k = 0; k < curve.length; k++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(curve, k, AnimationUtility.TangentMode.ClampedAuto);
                    AnimationUtility.SetKeyRightTangentMode(curve, k, AnimationUtility.TangentMode.ClampedAuto);
                }

                AnimationUtility.SetEditorCurve(clip, binding, curve);
                written++;
            }

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = false;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            // Overwritten in place if it exists, so the controller keeps pointing at it and a
            // rebuild does not orphan the state that references it.
            AnimationClip existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);

            if (existing != null)
            {
                EditorUtility.CopySerialized(clip, existing);
                AssetDatabase.SaveAssets();
                clip = existing;
                Debug.Log($"[vault] rewrote {ClipPath} with {written} curves");
            }
            else
            {
                AssetDatabase.CreateAsset(clip, ClipPath);
                AssetDatabase.SaveAssets();
                Debug.Log($"[vault] created {ClipPath} with {written} curves");
            }

            Wire(clip);
        }

        /// <summary>Adds or repoints the Vault state on the Parkour layer.</summary>
        private static void Wire(AnimationClip clip)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);

            if (controller == null)
            {
                Debug.LogError($"[vault] no controller at {ControllerPath}");
                return;
            }

            AnimatorControllerLayer layer = null;
            foreach (AnimatorControllerLayer candidate in controller.layers)
                if (candidate.name == "Parkour") { layer = candidate; break; }

            if (layer == null) { Debug.LogError("[vault] no Parkour layer"); return; }

            AnimatorStateMachine machine = layer.stateMachine;

            AnimatorState vault = null;
            foreach (ChildAnimatorState child in machine.states)
                if (child.state != null && child.state.name == "Vault") { vault = child.state; break; }

            if (vault == null)
            {
                vault = machine.AddState("Vault");
                Debug.Log("[vault] added a Vault state to the Parkour layer");
            }

            vault.motion = clip;

            // Reached from Any State, like the other parkour actions: a vault can begin from a run,
            // a crouch or a landing, and enumerating those as transitions would miss one.
            bool wired = false;

            foreach (AnimatorStateTransition t in machine.anyStateTransitions)
                if (t.destinationState == vault) { wired = true; break; }

            if (!wired)
            {
                AnimatorStateTransition transition = machine.AddAnyStateTransition(vault);
                transition.AddCondition(AnimatorConditionMode.Equals, VaultParkourValue, "Parkour");
                transition.duration = 0.06f;
                transition.hasExitTime = false;

                // Without this a vault begun while a vault is playing restarts it, and mantling
                // along a run of railings turns into one long stutter.
                transition.canTransitionToSelf = false;

                Debug.Log($"[vault] wired Any State -> Vault on Parkour == {VaultParkourValue}");
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[vault] done");
        }

        private struct Stage
        {
            public float Time;
            public AnimationClip Source;
            public float SourceTime;

            /// <summary>Degrees of forward lean at the hips, added on top of the sampled pose.</summary>
            public float HipPitch;
        }

        private static string Key(EditorCurveBinding b) => b.path + "|" + b.propertyName;

        private static bool IsHipsRotation(EditorCurveBinding b) =>
            b.path == "HipsCtrl/Hips" && b.propertyName.StartsWith("m_LocalRotation");

        private static float Sample(AnimationClip clip, EditorCurveBinding binding, float time)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
            return curve != null ? curve.Evaluate(time) : 0f;
        }

        /// <summary>
        /// The sampled hips rotation with a forward pitch applied.
        ///
        /// Read as a whole quaternion and rotated, rather than nudging one component: the x
        /// component of a quaternion is not "pitch", and treating it as though it were is exactly
        /// how a generated animation ends up folding a character in half.
        /// </summary>
        private static float PitchHips(AnimationClip clip, float time, EditorCurveBinding binding,
            float degrees)
        {
            var q = new Quaternion(
                Sample(clip, Rebind(binding, "m_LocalRotation.x"), time),
                Sample(clip, Rebind(binding, "m_LocalRotation.y"), time),
                Sample(clip, Rebind(binding, "m_LocalRotation.z"), time),
                Sample(clip, Rebind(binding, "m_LocalRotation.w"), time));

            if (q == new Quaternion(0f, 0f, 0f, 0f)) q = Quaternion.identity;

            q.Normalize();
            Quaternion pitched = q * Quaternion.Euler(degrees, 0f, 0f);

            switch (binding.propertyName)
            {
                case "m_LocalRotation.x": return pitched.x;
                case "m_LocalRotation.y": return pitched.y;
                case "m_LocalRotation.z": return pitched.z;
                default: return pitched.w;
            }
        }

        private static EditorCurveBinding Rebind(EditorCurveBinding binding, string property)
        {
            binding.propertyName = property;
            return binding;
        }

        private static AnimationClip Load(string name) =>
            AssetDatabase.LoadAssetAtPath<AnimationClip>($"{ClipDirectory}/{name}.anim");
    }
}
