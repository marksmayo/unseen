using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Prints the ninja's bone hierarchy, the humanoid mapping, and each bone's rest pose.
    ///
    /// Authoring animation curves means naming transform paths exactly, and those paths are
    /// relative to the GameObject the Animator sits on. Getting one wrong produces a clip that
    /// imports, plays, and does nothing - which is the single most expensive failure mode in this
    /// project so far. So the paths get measured before a curve is written.
    /// </summary>
    public static class UnseenRigDump
    {
        private const string PrefabPath = "Assets/Unseen/Art/Characters/NinjaVisual.prefab";

        [MenuItem("Unseen/Art/Dump Ninja Rig", priority = 56)]
        public static void Dump()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[rig] no prefab at {PrefabPath}");
                return;
            }

            GameObject instance = Object.Instantiate(prefab);

            try
            {
                var animator = instance.GetComponentInChildren<Animator>();
                if (animator == null)
                {
                    Debug.LogError("[rig] no Animator in the prefab");
                    return;
                }

                Transform root = animator.transform;
                Debug.Log($"[rig] animator on '{Path(instance.transform, root)}', " +
                          $"avatar={(animator.avatar != null ? animator.avatar.name : "none")}, " +
                          $"human={(animator.avatar != null && animator.avatar.isHuman)}, " +
                          $"valid={(animator.avatar != null && animator.avatar.isValid)}");

                // Curve paths are relative to the animator's own transform.
                var lines = new List<string>();
                Walk(root, root, 0, lines);
                Debug.Log($"[rig] {lines.Count} transforms, paths relative to the animator:\n" +
                          string.Join("\n", lines));

                if (animator.avatar != null && animator.avatar.isHuman)
                    DumpHumanoid(animator, root);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        private static void Walk(Transform node, Transform root, int depth, List<string> lines)
        {
            string path = Path(node, root);
            Vector3 p = node.localPosition;
            Vector3 e = node.localEulerAngles;

            lines.Add($"  {new string(' ', depth * 2)}{node.name}" +
                      $"{(path.Length > 0 ? $"   [{path}]" : "   [<animator root>]")}" +
                      $"   pos({p.x:0.###},{p.y:0.###},{p.z:0.###})" +
                      $" rot({e.x:0.#},{e.y:0.#},{e.z:0.#})");

            for (int i = 0; i < node.childCount; i++)
                Walk(node.GetChild(i), root, depth + 1, lines);
        }

        /// <summary>Path of a transform relative to the animator, in the form a curve binding wants.</summary>
        private static string Path(Transform node, Transform root)
        {
            if (node == root) return string.Empty;

            var stack = new List<string>();
            Transform cursor = node;
            while (cursor != null && cursor != root)
            {
                stack.Add(cursor.name);
                cursor = cursor.parent;
            }

            stack.Reverse();
            return string.Join("/", stack);
        }

        private static void DumpHumanoid(Animator animator, Transform root)
        {
            var report = new StringBuilder();
            report.AppendLine("[rig] humanoid bones that resolve:");

            HumanBodyBones[] wanted =
            {
                HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest,
                HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head,
                HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm,
                HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm,
                HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
                HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot
            };

            foreach (HumanBodyBones bone in wanted)
            {
                Transform t = animator.GetBoneTransform(bone);
                report.AppendLine(t != null
                    ? $"  {bone,-18} -> {Path(t, root)}"
                    : $"  {bone,-18} -> (absent)");
            }

            Debug.Log(report.ToString());
        }
    }
}
