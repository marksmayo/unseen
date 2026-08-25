using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Kills a ninja and photographs the collapse frame by frame.
    ///
    /// The sequence is driven by hand rather than by LateUpdate, because MonoBehaviour callbacks do
    /// not run outside play mode. That is the same trap that hid the lying-down-ninja and the
    /// never-destroyed-placeholder bugs earlier in this project, so the death visual was written
    /// with a public step function specifically to be checkable this way.
    /// </summary>
    public static class UnseenDeathShot
    {
        private const int Width = 1280;
        private const int Height = 720;
        private const string OutputDir = "Server/out/shots";

        [MenuItem("Unseen/Art/Capture Death Scene", priority = 55)]
        public static void Capture()
        {
            var host = new GameObject("DeathShotTown");
            GameObject cameraHost = null;
            GameObject simHost = null;

            try
            {
                GreyboxTownGenerator generator = host.AddComponent<GreyboxTownGenerator>();
                generator.Seed = 20260824;
                generator.GridSize = 4;      // a small town: this shot is about one body
                generator.BuildRiver = false;
                generator.Generate();

                simHost = new GameObject("DeathShotSim");
                UnseenBootstrap boot = simHost.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = false;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260824;
                boot.Boot();

                for (int i = 0; i < 300; i++)
                {
                    boot.Network.Poll(1f / 60f);
                    boot.Simulation.Advance(1f / 60f);
                }

                foreach (Camera existing in simHost.GetComponentsInChildren<Camera>(true))
                    existing.enabled = false;

                // Pick a subject with a body, and stand it on open ground.
                AgentEntity subject = null;
                foreach (AgentEntity candidate in boot.Context.Entities.All)
                {
                    if (candidate.GetComponentInChildren<AgentVisual>() == null) continue;
                    subject = candidate;
                    break;
                }

                if (subject == null)
                {
                    Debug.LogError("[death] no agent with a visual body");
                    return;
                }

                var spot = new Vector3(23f, 30f, 0f);
                if (Physics.Raycast(spot, Vector3.down, out RaycastHit ground, 60f,
                        UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                    spot = ground.point + Vector3.up * 0.1f;

                subject.Motor.Teleport(spot);
                if (subject.Brain != null) subject.Brain.enabled = false;

                for (int i = 0; i < 30; i++)
                {
                    boot.Network.Poll(1f / 60f);
                    boot.Simulation.Advance(1f / 60f);
                }

                var death = subject.GetComponent<AgentDeathVisual>();
                if (death == null) death = subject.gameObject.AddComponent<AgentDeathVisual>();

                cameraHost = new GameObject("DeathCamera");
                Camera camera = cameraHost.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 400f;
                camera.allowHDR = true;
                camera.fieldOfView = 46f;
                camera.GetUniversalAdditionalCameraData().renderShadows = true;

                Vector3 eye = spot + new Vector3(3.2f, 1.6f, 3.2f);
                cameraHost.transform.position = eye;
                cameraHost.transform.rotation =
                    Quaternion.LookRotation(((spot + Vector3.up * 0.7f) - eye).normalized, Vector3.up);

                Directory.CreateDirectory(OutputDir);

                // Blow comes from the camera side, so the body falls away from us.
                death.Play(spot - eye);
                Debug.Log($"[death] playing at {spot}, subject {subject.DisplayName}");

                // Frames across the collapse, then one lying still.
                float[] marks = { 0f, 0.18f, 0.38f, 0.62f, 0.85f, 2.0f };
                float clock = 0f;

                for (int i = 0; i < marks.Length; i++)
                {
                    while (clock < marks[i])
                    {
                        float step = Mathf.Min(1f / 60f, marks[i] - clock);
                        death.Advance(step);
                        clock += step;
                    }

                    string path = Path.Combine(OutputDir, $"11-death-{i}.png");
                    Render(camera, path);
                    Debug.Log($"[death] t={clock:0.00}s -> {path}");
                }

                // Second scenario: killed off the ground. This is the case that was broken - the
                // agent transform stops where it died and the controller is off, so without a
                // ground search the corpse simply hangs at the height it was hit.
                death.Reset();
                Vector3 airborne = spot + Vector3.up * 7f;
                subject.Motor.Teleport(airborne);
                subject.transform.position = airborne;

                Vector3 airEye = airborne + new Vector3(5f, 0.5f, 5f) - Vector3.up * 3.5f;
                cameraHost.transform.position = airEye;
                cameraHost.transform.rotation =
                    Quaternion.LookRotation(((spot + Vector3.up * 1.5f) - airEye).normalized, Vector3.up);

                death.Play(airborne - airEye);
                Debug.Log($"[death] airborne test from y={airborne.y:0.0}, ground expected y={spot.y:0.0}");

                float[] airMarks = { 0f, 0.4f, 0.9f, 1.5f, 2.4f };
                float airClock = 0f;

                for (int i = 0; i < airMarks.Length; i++)
                {
                    while (airClock < airMarks[i])
                    {
                        float step = Mathf.Min(1f / 60f, airMarks[i] - airClock);
                        death.Advance(step);
                        airClock += step;
                    }

                    var body = subject.GetComponentInChildren<AgentVisual>();
                    Debug.Log($"[death]   air t={airClock:0.00}s body y={(body != null ? body.transform.position.y : 0f):0.00}");

                    Render(camera, Path.Combine(OutputDir, $"13-death-air-{i}.png"));
                }

                Debug.Log("[death] done");
            }
            finally
            {
                if (cameraHost != null) Object.DestroyImmediate(cameraHost);
                if (simHost != null)
                {
                    UnseenBootstrap boot = simHost.GetComponent<UnseenBootstrap>();
                    if (boot != null) boot.Shutdown();
                    Object.DestroyImmediate(simHost);
                }

                BoxMeshFactory.ClearCache();
                LanternMeshFactory.ClearCache();
                Object.DestroyImmediate(host);
            }
        }

        private static void Render(Camera camera, string path)
        {
            var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.DefaultHDR);
            var readback = new Texture2D(Width, Height, TextureFormat.RGB24, false);

            camera.targetTexture = target;
            camera.Render();

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
            readback.Apply();
            RenderTexture.active = previous;

            File.WriteAllBytes(path, readback.EncodeToPNG());

            camera.targetTexture = null;
            Object.DestroyImmediate(readback);
            target.Release();
            Object.DestroyImmediate(target);
        }
    }
}
