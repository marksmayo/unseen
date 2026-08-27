using System.IO;
using UnityEditor;
using UnityEngine;
using Unseen.Entities;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Measures how far the ninja's soles sit above or below its own origin in each stance, and
    /// reports the body drop that would plant them.
    ///
    /// Reported from play: prone sinks into the ground and a crouch levitates. Both come from the
    /// same place. The stance clips are rotation-only, so folding the knees or laying the body out
    /// moves the feet relative to the root, and AgentVisual sinks the whole body by a fixed amount
    /// to compensate. Those amounts were measured once and are now stale - every clip in the project
    /// was rewritten when the T-pose was fixed, and rewriting a pose moves the feet.
    ///
    /// The number that matters is the bottom of the SKINNED BOUNDS, not the foot bone. A boot has
    /// thickness and the sole sits below the ankle, so a drop measured from the bone leaves the
    /// ninja hovering by whatever that gap happens to be.
    /// </summary>
    public static class UnseenStanceProbe
    {
        private const string ClipDir = "Assets/Unseen/Art/Characters/Clips";

        [MenuItem("Unseen/Diagnose Stance Heights", priority = 96)]
        public static void Run()
        {
            AgentVisualSet set = AgentVisualSet.Load();
            if (set == null || set.NinjaVisual == null)
            {
                Debug.LogError("[stance] no ninja visual to measure");
                return;
            }

            GameObject subject = Object.Instantiate(set.NinjaVisual);

            try
            {
                subject.transform.position = Vector3.zero;
                subject.transform.rotation = Quaternion.identity;

                var animator = subject.GetComponentInChildren<Animator>();
                GameObject sampleTarget = animator != null ? animator.gameObject : subject;

                // The Animator has to be off, or it re-evaluates its controller over the sampled
                // pose and the measurement is of whatever it settled on instead.
                if (animator != null) animator.enabled = false;

                // Outside play mode nothing drives skinning, so the mesh keeps whatever bind
                // matrices it last cached however far the bones have moved.
                foreach (SkinnedMeshRenderer skin in subject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    skin.forceMatrixRecalculationPerRender = true;
                    skin.updateWhenOffscreen = true;
                }

                SkinnedMeshRenderer body = subject.GetComponentInChildren<SkinnedMeshRenderer>();
                if (body == null)
                {
                    Debug.LogError("[stance] no skinned mesh on the ninja");
                    return;
                }

                float idle = SoleHeight(body, sampleTarget, "idle");
                float crouch = SoleHeight(body, sampleTarget, "ninja_crouch");
                float prone = SoleHeight(body, sampleTarget, "ninja_prone");

                Debug.Log($"[stance] sole height above the root: idle {idle:0.000} m, " +
                          $"crouch {crouch:0.000} m, prone {prone:0.000} m");

                // The drop that plants each stance is how much HIGHER its soles sit than idle's.
                // Idle is the reference because that is the pose the root was authored against.
                float crouchDrop = crouch - idle;
                float proneDrop = prone - idle;

                // Read off the prefab rather than constructing an AgentVisual: `new` on a
                // MonoBehaviour is not a supported way to get at its defaults.
                var authored = set.NinjaVisual.GetComponentInChildren<AgentVisual>(true);
                float haveCrouch = authored != null ? authored.CrouchBodyDrop : 0f;
                float haveProne = authored != null ? authored.ProneBodyDrop : 0f;

                Debug.Log($"[stance] CrouchBodyDrop should be {crouchDrop:0.000} " +
                          $"(currently {haveCrouch:0.000})");
                Debug.Log($"[stance] ProneBodyDrop should be {proneDrop:0.000} " +
                          $"(currently {haveProne:0.000})");

                bool planted = Mathf.Abs(crouchDrop - haveCrouch) < 0.02f &&
                               Mathf.Abs(proneDrop - haveProne) < 0.02f;

                // Written back to the PREFAB, which is what the game actually reads.
                //
                // The C# field initialisers are not the source of truth here - a value serialised
                // on the prefab overrides them, and these two had drifted a long way from the
                // script defaults without anyone noticing. Editing the script alone changed
                // nothing in the running game.
                //
                // Applying rather than only reporting is deliberate. This drifts every time the
                // clips are rebuilt, because a rotation-only pose moves the feet - which is exactly
                // what happened when the animator's T-pose was fixed - so a tool that measures the
                // right answer and leaves somebody to type it in will be stale again by the next
                // clip change.
                if (!planted && authored != null)
                {
                    authored.CrouchBodyDrop = crouchDrop;
                    authored.ProneBodyDrop = proneDrop;

                    EditorUtility.SetDirty(authored);
                    PrefabUtility.SavePrefabAsset(set.NinjaVisual);
                    AssetDatabase.SaveAssets();

                    Debug.Log($"[stance] prefab updated: crouch {haveCrouch:0.000} -> " +
                              $"{crouchDrop:0.000}, prone {haveProne:0.000} -> {proneDrop:0.000}");
                    planted = true;
                }

                Debug.Log($"[stance] both stances are planted: {(planted ? "PASS" : "FAIL")}");
                if (!planted)
                    Debug.LogError("[stance] FAILED - the body drops do not match the clips");

                // A negative drop means the pose puts the soles BELOW the root, which is what
                // sinking into the ground looks like, and no amount of dropping the body fixes it -
                // it has to be lifted.
                if (crouchDrop < 0f || proneDrop < 0f)
                    Debug.Log("[stance] note: a negative figure means that pose sits BELOW the " +
                              "root and has to be lifted rather than dropped");
            }
            finally
            {
                Object.DestroyImmediate(subject);
            }
        }

        /// <summary>Lowest point of the baked mesh in a given clip's first frame, above the root.</summary>
        private static float SoleHeight(SkinnedMeshRenderer body, GameObject sampleTarget, string clipName)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>($"{ClipDir}/{clipName}.anim");
            if (clip == null)
            {
                Debug.LogWarning($"[stance] no clip {clipName}.anim");
                return 0f;
            }

            // Sampled onto the object the Animator sits on: curve paths are relative to it, and
            // sampling onto the prefab root resolves none of them and leaves a bind pose.
            clip.SampleAnimation(sampleTarget, clip.length * 0.5f);

            var baked = new Mesh();
            body.BakeMesh(baked, true);

            Vector3[] vertices = baked.vertices;
            float lowest = float.MaxValue;

            for (int i = 0; i < vertices.Length; i++)
            {
                // BakeMesh gives local space; the renderer's own transform puts it in the root's.
                float y = body.transform.TransformPoint(vertices[i]).y;
                if (y < lowest) lowest = y;
            }

            Object.DestroyImmediate(baked);
            return lowest;
        }
    }
}
