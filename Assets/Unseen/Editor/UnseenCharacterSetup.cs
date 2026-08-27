using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Turns the imported CC0 character files into a usable ninja: rig import settings, skin
    /// materials, a small animator, and a prefab scaled to the gameplay capsule.
    ///
    /// The scaling step matters more than it looks. The capsule height comes from
    /// UnseenConfig.Movement.StandHeight and drives the character controller, the eye anchor and the
    /// torso anchor that line-of-sight samples. The art has to be fitted to that, never the reverse.
    /// </summary>
    public static class UnseenCharacterSetup
    {
        private const string Root = "Assets/Unseen/Art/Characters";
        private const string ModelPath = Root + "/characterMedium.fbx";
        private const string AnimationDir = Root + "/Animations";
        private const string ClipDir = Root + "/Clips";
        private const string MaterialDir = Root + "/Materials";
        private const string ControllerPath = Root + "/NinjaAnimator.controller";
        private const string PrefabPath = Root + "/NinjaVisual.prefab";
        private const string SetPath = "Assets/Unseen/Resources/AgentVisualSet.asset";

        private static readonly string[] Skins = { "NinjaCharcoal", "NinjaAsh" };

        /// <summary>
        /// Dumps what the clips actually contain and how their curve paths compare to the rig, so a
        /// clip that binds to nothing can be told apart from a clip that is simply not playing.
        /// </summary>
        [MenuItem("Unseen/Art/Diagnose Ninja Clips", priority = 54)]
        public static void DiagnoseClips()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[diag] no ninja prefab");
                return;
            }

            var animator = prefab.GetComponent<Animator>();
            Debug.Log($"[diag] prefab root='{prefab.name}' animator={(animator != null)} " +
                      $"avatar={(animator != null && animator.avatar != null ? animator.avatar.name : "none")} " +
                      $"avatarValid={(animator != null && animator.avatar != null && animator.avatar.isValid)} " +
                      $"controller={(animator != null && animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : "none")} " +
                      $"cullingMode={(animator != null ? animator.cullingMode.ToString() : "n/a")}");

            // Real transform paths in the rig, relative to the prefab root.
            var paths = new System.Collections.Generic.List<string>();
            CollectPaths(prefab.transform, "", paths);
            Debug.Log($"[diag] rig has {paths.Count} transforms; first few: " +
                      string.Join(" | ", paths.GetRange(0, Mathf.Min(6, paths.Count))));

            foreach (string name in new[] { "idle", "run", "jump" })
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>($"{ClipDir}/{name}.anim");
                if (clip == null)
                {
                    Debug.LogError($"[diag] clip '{name}' missing");
                    continue;
                }

                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
                int rootBindings = 0, matched = 0;
                var sample = new System.Collections.Generic.List<string>();

                foreach (EditorCurveBinding b in bindings)
                {
                    if (string.IsNullOrEmpty(b.path)) rootBindings++;
                    if (paths.Contains(b.path)) matched++;
                    if (sample.Count < 4) sample.Add($"'{b.path}'.{b.propertyName}");
                }

                Debug.Log($"[diag] clip '{name}': length={clip.length:0.00}s legacy={clip.legacy} " +
                          $"bindings={bindings.Length} rootPathBindings={rootBindings} " +
                          $"pathsMatchingRig={matched}/{bindings.Length} | sample: {string.Join(" , ", sample)}");

                // Compare against the untouched source, so a broken copy is distinguishable from a
                // source clip that was only ever one frame long.
                foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath($"{AnimationDir}/{name}.fbx"))
                {
                    if (!(asset is AnimationClip source) || source.name.StartsWith("__preview__")) continue;

                    EditorCurveBinding[] sourceBindings = AnimationUtility.GetCurveBindings(source);
                    var nonRoot = new System.Collections.Generic.List<string>();
                    foreach (EditorCurveBinding b in sourceBindings)
                    {
                        if (!string.IsNullOrEmpty(b.path) && nonRoot.Count < 3) nonRoot.Add($"'{b.path}'");
                    }

                    AnimationCurve firstCurve = sourceBindings.Length > 0
                        ? AnimationUtility.GetEditorCurve(source, sourceBindings[0])
                        : null;

                    Debug.Log($"[diag]   source '{source.name}': length={source.length:0.00}s " +
                              $"bindings={sourceBindings.Length} keysOnFirstCurve={(firstCurve != null ? firstCurve.length : -1)} " +
                              $"| non-root paths: {string.Join(" , ", nonRoot)}");
                    break;
                }
            }
        }

        /// <summary>
        /// Finds the transform that the controller's clips are authored against, by counting how many
        /// of their curve paths actually resolve from each candidate. Guessing this wrong binds every
        /// bone curve to nothing, and the character silently stays in its bind pose.
        /// </summary>
        private static Transform ResolveAnimatorNode(Transform root, AnimatorController controller)
        {
            var samplePaths = new System.Collections.Generic.List<string>();
            if (controller != null)
            {
                foreach (AnimationClip clip in controller.animationClips)
                {
                    foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
                    {
                        if (string.IsNullOrEmpty(binding.path)) continue;
                        if (!samplePaths.Contains(binding.path)) samplePaths.Add(binding.path);
                        if (samplePaths.Count >= 24) break;
                    }

                    if (samplePaths.Count >= 24) break;
                }
            }

            if (samplePaths.Count == 0) return root;

            Transform best = root;
            int bestScore = -1;

            foreach (Transform candidate in root.GetComponentsInChildren<Transform>(true))
            {
                int score = 0;
                foreach (string path in samplePaths)
                    if (candidate.Find(path) != null) score++;

                if (score <= bestScore) continue;
                bestScore = score;
                best = candidate;
            }

            Debug.Log($"[Unseen] animator node resolved to '{best.name}' " +
                      $"({bestScore}/{samplePaths.Count} sample curve paths resolve from it)");
            return best;
        }

        private static void CollectPaths(Transform root, string prefix, System.Collections.Generic.List<string> into)
        {
            foreach (Transform child in root)
            {
                string path = string.IsNullOrEmpty(prefix) ? child.name : prefix + "/" + child.name;
                into.Add(path);
                CollectPaths(child, path, into);
            }
        }

        [MenuItem("Unseen/Art/Build Ninja Character", priority = 53)]
        public static void BuildCharacter()
        {
            if (!File.Exists(ModelPath))
            {
                Debug.LogError($"[Unseen] no character model at {ModelPath}.");
                return;
            }

            Directory.CreateDirectory(MaterialDir);
            Directory.CreateDirectory(Path.GetDirectoryName(SetPath));

            Avatar avatar = ConfigureModel();
            ConfigureAnimations(avatar);

            Material[] skins = BuildSkinMaterials();
            AnimatorController controller = BuildAnimator();
            GameObject prefab = BuildPrefab(controller, avatar);

            AgentVisualSet set = AssetDatabase.LoadAssetAtPath<AgentVisualSet>(SetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<AgentVisualSet>();
                AssetDatabase.CreateAsset(set, SetPath);
            }

            set.NinjaVisual = prefab;
            set.Skins = skins;
            set.Cloth = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/Unseen/Art/Materials/Cloth.mat");
            EditorUtility.SetDirty(set);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[Unseen] ninja character built: prefab={prefab != null}, " +
                      $"skins={skins.Length}, animator={controller != null}");
        }

        private static Avatar ConfigureModel()
        {
            var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (importer == null) return null;

            importer.animationType = ModelImporterAnimationType.Generic;

            // importAnimation stays on and the avatar is requested explicitly: with it off, Unity
            // generates no Avatar sub-asset, and a Generic rig with no avatar binds no clips - the
            // model just sits in its bind pose while the animator appears to run.
            importer.importAnimation = true;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.materialImportMode = ModelImporterMaterialImportMode.None; // we supply the skins
            importer.importCameras = false;
            importer.importLights = false;
            importer.importBlendShapes = false;
            importer.SaveAndReimport();

            Avatar found = null;
            var types = new System.Collections.Generic.List<string>();
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(ModelPath))
            {
                types.Add(asset.GetType().Name);
                if (asset is Avatar avatar && found == null) found = avatar;
            }

            Debug.Log($"[Unseen] model sub-assets: {string.Join(", ", types)}");
            return found;
        }

        private static void ConfigureAnimations(Avatar avatar)
        {
            if (!Directory.Exists(AnimationDir)) return;

            foreach (string path in Directory.GetFiles(AnimationDir, "*.fbx"))
            {
                string assetPath = path.Replace('\\', '/');
                var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
                if (importer == null) continue;

                importer.animationType = ModelImporterAnimationType.Generic;
                importer.importAnimation = true;
                importer.materialImportMode = ModelImporterMaterialImportMode.None;

                // Retarget onto the model's skeleton, otherwise each clip drags in its own rig and
                // the animator silently animates nothing.
                if (avatar != null)
                {
                    importer.sourceAvatar = avatar;
                    importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
                }

                bool loop = assetPath.EndsWith("idle.fbx") || assetPath.EndsWith("run.fbx");
                ModelImporterClipAnimation[] clips = importer.defaultClipAnimations;
                for (int i = 0; i < clips.Length; i++)
                {
                    clips[i].loopTime = loop;
                    clips[i].lockRootHeightY = true;
                }

                if (clips.Length > 0) importer.clipAnimations = clips;
                importer.SaveAndReimport();
            }
        }

        private static Material[] BuildSkinMaterials()
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var built = new List<Material>();

            foreach (string skin in Skins)
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/{skin}.png");
                if (texture == null) continue;

                string path = $"{MaterialDir}/{skin}.mat";
                Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                {
                    material = new Material(lit);
                    AssetDatabase.CreateAsset(material, path);
                }

                material.shader = lit;
                material.SetTexture("_BaseMap", texture);
                material.SetTexture("_MainTex", texture);
                material.SetFloat("_Smoothness", 0.15f); // cloth, not plastic
                material.SetFloat("_Metallic", 0f);
                EditorUtility.SetDirty(material);
                built.Add(material);
            }

            return built.ToArray();
        }

        private static AnimatorController BuildAnimator()
        {
            AnimationClip idle = FindClip("idle");
            AnimationClip run = FindClip("run");
            AnimationClip jump = FindClip("jump");

            if (idle == null && run == null)
            {
                Debug.LogWarning("[Unseen] no animation clips found; the ninja will render unanimated.");
                return null;
            }

            if (File.Exists(ControllerPath)) AssetDatabase.DeleteAsset(ControllerPath);
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            controller.AddParameter("Airborne", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Crouched", AnimatorControllerParameterType.Bool);

            AnimatorStateMachine machine = controller.layers[0].stateMachine;

            AnimatorState idleState = machine.AddState("Idle");
            idleState.motion = idle;
            machine.defaultState = idleState;

            AnimatorState runState = machine.AddState("Run");
            runState.motion = run != null ? run : idle;

            AnimatorState jumpState = machine.AddState("Jump");
            jumpState.motion = jump != null ? jump : idle;

            // Idle <-> Run on planar speed. The threshold sits below the crouch speed so creeping
            // still animates rather than sliding.
            AnimatorStateTransition toRun = idleState.AddTransition(runState);
            toRun.AddCondition(AnimatorConditionMode.Greater, 0.6f, "Speed");
            toRun.hasExitTime = false;
            toRun.duration = 0.15f;

            AnimatorStateTransition toIdle = runState.AddTransition(idleState);
            toIdle.AddCondition(AnimatorConditionMode.Less, 0.5f, "Speed");
            toIdle.hasExitTime = false;
            toIdle.duration = 0.2f;

            // Airborne overrides both.
            foreach (AnimatorState from in new[] { idleState, runState })
            {
                AnimatorStateTransition toJump = from.AddTransition(jumpState);
                toJump.AddCondition(AnimatorConditionMode.If, 0f, "Airborne");
                toJump.hasExitTime = false;
                toJump.duration = 0.1f;
            }

            AnimatorStateTransition land = jumpState.AddTransition(idleState);
            land.AddCondition(AnimatorConditionMode.IfNot, 0f, "Airborne");
            land.hasExitTime = false;
            land.duration = 0.15f;

            EditorUtility.SetDirty(controller);
            return controller;
        }

        private static AnimationClip FindClip(string file)
        {
            string path = $"{AnimationDir}/{file}.fbx";
            if (!File.Exists(path)) return null;

            // An FBX take list usually contains rigging artefacts alongside the animation: the first
            // clip in this pack is a one-frame "Targeting Pose". Pick the longest real clip instead,
            // and log every candidate so a bad choice is visible rather than silent.
            AnimationClip best = null;
            var candidates = new System.Collections.Generic.List<string>();

            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (!(asset is AnimationClip clip) || clip.name.StartsWith("__preview__")) continue;

                candidates.Add($"'{clip.name}' {clip.length:0.00}s");
                if (best == null || clip.length > best.length) best = clip;
            }

            Debug.Log($"[Unseen] {file}.fbx clips: {string.Join(" , ", candidates)} -> chose " +
                      $"'{(best != null ? best.name : "none")}' ({(best != null ? best.length : 0f):0.00}s)");

            return best != null ? Sanitise(best, file) : null;
        }

        /// <summary>
        /// Copies a clip with its scale curves removed.
        ///
        /// The source clips animate the root's local scale in the model's native units. applyRootMotion
        /// only suppresses root *motion*, so the Animator was writing that scale every frame and
        /// overwriting the fit scale - inflating every ninja to roughly 100x, about 400 m tall, with
        /// the camera inside its own character. Clips inside an FBX are read-only, so the fix is a
        /// sanitised copy saved alongside them.
        /// </summary>
        private static AnimationClip Sanitise(AnimationClip source, string name)
        {
            Directory.CreateDirectory(ClipDir);
            string path = $"{ClipDir}/{name}.anim";

            var copy = new AnimationClip { name = name, frameRate = source.frameRate };
            int dropped = 0;
            int droppedRoot = 0;

            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(source))
            {
                if (binding.propertyName.StartsWith("m_LocalScale"))
                {
                    dropped++;
                    continue;
                }

                // Curves on the root itself fight the motor for the transform - and in these clips
                // they rotate the character flat onto its back. The animation drives bones; the
                // simulation drives the root.
                if (string.IsNullOrEmpty(binding.path))
                {
                    droppedRoot++;
                    continue;
                }

                AnimationUtility.SetEditorCurve(copy, binding, AnimationUtility.GetEditorCurve(source, binding));
            }

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(source);
            AnimationUtility.SetAnimationClipSettings(copy, settings);

            AnimationClip existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(copy, existing);
                Object.DestroyImmediate(copy);
                EditorUtility.SetDirty(existing);
                Debug.Log($"[Unseen] clip '{name}' refreshed, dropped {dropped} scale + {droppedRoot} root curve(s)");
                return existing;
            }

            AssetDatabase.CreateAsset(copy, path);
            Debug.Log($"[Unseen] clip '{name}' sanitised, dropped {dropped} scale + {droppedRoot} root curve(s)");
            return copy;
        }

        private static GameObject BuildPrefab(AnimatorController controller, Avatar avatar)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (model == null) return null;

            // Structure matters here. Clip curve paths are relative to the GameObject holding the
            // Animator, and these clips are authored relative to the model root. Renaming the model
            // root and animating from it put every path one level out, so all 322 bone curves bound
            // to nothing. So: an outer wrapper carries the fit scale and AgentVisual, and the model
            // keeps its own identity with the Animator on it.
            GameObject modelInstance = Object.Instantiate(model);
            var instance = new GameObject("NinjaVisual");
            modelInstance.transform.SetParent(instance.transform, false);

            try
            {
                // Art must never contribute colliders: they would alter line of sight and parkour.
                foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
                    Object.DestroyImmediate(collider);

                var body = instance.GetComponentInChildren<SkinnedMeshRenderer>();

                // Which node the Animator belongs on is dictated by the clips, not by convention:
                // curve paths are relative to it. These clips are authored relative to 'Root', not
                // the model root, so resolve it by testing which transform actually resolves the
                // most binding paths rather than assuming.
                Transform animatorNode = ResolveAnimatorNode(instance.transform, controller);
                foreach (Animator stray in instance.GetComponentsInChildren<Animator>(true))
                    if (stray.transform != animatorNode) Object.DestroyImmediate(stray);

                Animator animator = animatorNode.GetComponent<Animator>();
                if (animator == null) animator = animatorNode.gameObject.AddComponent<Animator>();
                if (controller != null) animator.runtimeAnimatorController = controller;

                // A Generic rig needs its avatar to bind clips to the skeleton. Because the model is
                // imported with importAnimation=false, Unity adds no Animator of its own, so the one
                // added here starts with no avatar - and every clip silently does nothing, leaving
                // the raw bind pose.
                if (avatar != null) animator.avatar = avatar;
                animator.applyRootMotion = false; // the motor owns movement, not the animation

                Debug.Log($"[Unseen] animator on '{animator.gameObject.name}' under wrapper " +
                          $"'{instance.name}': avatar={(avatar != null ? avatar.name : "MISSING")} " +
                          $"valid={(avatar != null && avatar.isValid)} " +
                          $"controller={(controller != null ? controller.name : "none")}");

                AgentVisual visual = instance.GetComponent<AgentVisual>();
                if (visual == null) visual = instance.AddComponent<AgentVisual>();
                visual.Rig = animator;
                visual.Body = body;

                // Fit the art to the gameplay capsule rather than the other way round.
                //
                // Measure the instantiated hierarchy's world bounds, not sharedMesh.bounds: for a
                // skinned mesh the latter is bind-pose data in the skeleton's own space and can be
                // wildly different from the size the thing actually renders at.
                float target = UnseenConfig.Default.Movement.StandHeight;
                Bounds worldBounds = default;
                bool measured = false;
                foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>())
                {
                    if (!measured)
                    {
                        worldBounds = renderer.bounds;
                        measured = true;
                    }
                    else
                    {
                        worldBounds.Encapsulate(renderer.bounds);
                    }
                }

                float rendered = measured ? worldBounds.size.y : 0f;
                float meshHeight = body != null && body.sharedMesh != null ? body.sharedMesh.bounds.size.y : 0f;

                if (rendered > 0.05f)
                {
                    float scale = target / rendered;
                    instance.transform.localScale = Vector3.one * scale;
                    Debug.Log($"[Unseen] ninja renders {rendered:0.00} m tall " +
                              $"(mesh bounds claim {meshHeight:0.000} m); scaled by {scale:0.000} " +
                              $"to match the {target:0.00} m capsule.");
                }
                else
                {
                    Debug.LogWarning($"[Unseen] could not measure the ninja ({rendered:0.000} m); " +
                                     "leaving it at authored scale.");
                }

                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }
    }
}
