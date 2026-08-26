using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Samples each authored clip onto the ninja and photographs the result as a contact sheet.
    ///
    /// <see cref="AnimationClip.SampleAnimation"/> works outside play mode, which makes this the
    /// only way to check an authored pose without launching the game and trying to catch a swing
    /// mid-frame. A clip that imports cleanly and animates nothing looks identical to a clip that
    /// works, right up until you look at it.
    /// </summary>
    public static class UnseenPoseShot
    {
        private const string PrefabPath = "Assets/Unseen/Art/Characters/NinjaVisual.prefab";
        private const string ClipDir = "Assets/Unseen/Art/Characters/Clips";
        private const string OutputDir = "Server/out/shots";

        private const int Cell = 300;
        private const int Columns = 5;

        [MenuItem("Unseen/Art/Capture Animation Poses", priority = 58)]
        public static void Capture()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[pose] no prefab at {PrefabPath}");
                return;
            }

            var clips = new[]
            {
                "ninja_guard", "ninja_attack_light", "ninja_attack_heavy",
                "ninja_stagger", "ninja_takedown_attacker", "ninja_takedown_victim",
                "ninja_crouch", "ninja_prone", "ninja_climb", "ninja_wallrun", "ninja_hang"
            };

            GameObject subject = Object.Instantiate(prefab);
            GameObject rigHost = new GameObject("PoseRig");
            GameObject lightHost = null;
            GameObject cameraHost = null;

            try
            {
                subject.transform.SetParent(rigHost.transform, false);
                subject.transform.localPosition = Vector3.zero;
                subject.transform.localRotation = Quaternion.identity;

                // Curve paths are relative to the GameObject the Animator sits on, which is a
                // child of the prefab root. Sampling onto the prefab root instead resolves none of
                // them and renders a perfect, silent T-pose - which is what the first run of this
                // tool showed, and it was the tool that was wrong, not the clips.
                var animator = subject.GetComponentInChildren<Animator>();
                GameObject sampleTarget = animator != null ? animator.gameObject : subject;

                // The Animator has to be off. Left enabled it re-evaluates its own controller over
                // the sampled pose, and the result is a body that barely moves - which is exactly
                // what the second run of this tool showed.
                if (animator != null) animator.enabled = false;

                // Outside play mode nothing drives the skinning update, so the mesh renders from
                // whatever bind matrices it last cached however far the bones have actually moved.
                // The probe below reported a 116 degree swing while the picture stayed a T-pose
                // until this was set.
                foreach (SkinnedMeshRenderer skin in subject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    skin.forceMatrixRecalculationPerRender = true;
                    skin.updateWhenOffscreen = true;
                }

                Transform probe = sampleTarget.transform.Find(
                    "HipsCtrl/Hips/Spine/Chest/UpperChest/RightShoulder/RightArm");
                Quaternion probeRest = probe != null ? probe.rotation : Quaternion.identity;

                // Foot height matters for the crouch: the pose is rotation-only, so folding the
                // knees moves the feet. AgentVisual sinks the body to compensate, and the amount
                // it should sink is measurable rather than guessable.
                Transform foot = sampleTarget.transform.Find("HipsCtrl/Hips/LeftUpLeg/LeftLeg/LeftFoot");
                Transform head = sampleTarget.transform.Find(
                    "HipsCtrl/Hips/Spine/Chest/UpperChest/Neck/Head");

                var idleForProbe = AssetDatabase.LoadAssetAtPath<AnimationClip>($"{ClipDir}/idle.anim");
                if (idleForProbe != null) idleForProbe.SampleAnimation(sampleTarget, 0f);

                float footAtIdle = foot != null ? foot.position.y : 0f;
                float headAtIdle = head != null ? head.position.y : 0f;
                Debug.Log($"[pose] idle reference: foot y {footAtIdle:0.000}, head y {headAtIdle:0.000}");

                Debug.Log($"[pose] sampling onto '{sampleTarget.name}', animator disabled, " +
                          $"probe bone {(probe != null ? "RightArm" : "MISSING")}");

                // A plain key light: this shot is about silhouette, not mood.
                lightHost = new GameObject("PoseLight");
                Light key = lightHost.AddComponent<Light>();
                key.type = LightType.Directional;
                key.intensity = 1.6f;
                key.color = new Color(1f, 0.97f, 0.92f);
                lightHost.transform.rotation = Quaternion.Euler(38f, 150f, 0f);

                cameraHost = new GameObject("PoseCamera");
                Camera camera = cameraHost.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.1f, 0.11f, 0.14f);
                camera.orthographic = true;
                camera.orthographicSize = 1.25f;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 40f;
                camera.GetUniversalAdditionalCameraData().renderShadows = false;

                // Three-quarter view: a pure side-on shot hides a swing that crosses the body.
                cameraHost.transform.position = new Vector3(2.6f, 1.0f, 2.6f);
                cameraHost.transform.rotation =
                    Quaternion.LookRotation((new Vector3(0f, 0.9f, 0f) - cameraHost.transform.position).normalized);

                Directory.CreateDirectory(OutputDir);

                foreach (string name in clips)
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>($"{ClipDir}/{name}.anim");
                    if (clip == null)
                    {
                        Debug.LogWarning($"[pose] missing clip {name}");
                        continue;
                    }

                    var sheet = new Texture2D(Cell * Columns, Cell, TextureFormat.RGB24, false);

                    var idle = AssetDatabase.LoadAssetAtPath<AnimationClip>($"{ClipDir}/idle.anim");

                    for (int i = 0; i < Columns; i++)
                    {
                        float t = clip.length * i / (Columns - 1f);

                        // Idle first, then the combat clip over the top. That is exactly what the
                        // masked override layer does at runtime, so the preview shows the pose the
                        // player will actually see rather than the clip in isolation.
                        if (idle != null) idle.SampleAnimation(sampleTarget, 0f);
                        clip.SampleAnimation(sampleTarget, t);

                        // Report the angle the sample actually produced, so "the clip is subtle"
                        // and "the clip is not being applied" cannot be confused for each other.
                        if (probe != null)
                            Debug.Log($"[pose]   {name} t={t:0.00}s right-arm delta " +
                                      $"{Quaternion.Angle(probeRest, probe.rotation):0.0} deg" +
                                      (foot != null && head != null
                                          ? $" | foot {(foot.position.y - footAtIdle):+0.000} " +
                                            $"head {(head.position.y - headAtIdle):+0.000}"
                                          : string.Empty));

                        Texture2D frame = Render(camera);
                        sheet.SetPixels(i * Cell, 0, Cell, Cell, frame.GetPixels());
                        Object.DestroyImmediate(frame);
                    }

                    sheet.Apply();
                    string path = Path.Combine(OutputDir, $"12-{name}.png");
                    File.WriteAllBytes(path, sheet.EncodeToPNG());
                    Object.DestroyImmediate(sheet);

                    Debug.Log($"[pose] {name}: {Columns} samples over {clip.length:0.00}s -> {path}");
                }

                Debug.Log("[pose] done");
            }
            finally
            {
                if (cameraHost != null) Object.DestroyImmediate(cameraHost);
                if (lightHost != null) Object.DestroyImmediate(lightHost);
                Object.DestroyImmediate(rigHost);
            }
        }

        private static Texture2D Render(Camera camera)
        {
            var target = new RenderTexture(Cell, Cell, 24, RenderTextureFormat.DefaultHDR);
            var readback = new Texture2D(Cell, Cell, TextureFormat.RGB24, false);

            camera.targetTexture = target;
            camera.Render();

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0f, 0f, Cell, Cell), 0, 0);
            readback.Apply();
            RenderTexture.active = previous;

            camera.targetTexture = null;
            target.Release();
            Object.DestroyImmediate(target);
            return readback;
        }
    }
}
