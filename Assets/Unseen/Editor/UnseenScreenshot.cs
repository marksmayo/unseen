using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Generates the town and renders a few fixed viewpoints to PNG, so the look can be reviewed
    /// without opening the editor or entering play mode.
    ///
    /// Must run with graphics available - batch mode is fine, but not <c>-nographics</c>:
    ///   Unity -batchmode -quit -projectPath . \
    ///         -executeMethod Unseen.EditorTools.UnseenScreenshot.Capture
    /// </summary>
    public static class UnseenScreenshot
    {
        private const int Width = 1600;
        private const int Height = 900;
        private const string OutputDir = "Server/out/shots";

        private struct Shot
        {
            public string Name;
            public Vector3 Position;
            public Vector3 LookAt;
            public float Fov;
        }

        [MenuItem("Unseen/Art/Capture Screenshots", priority = 52)]
        public static void Capture()
        {
            // Street lines fall between compounds: pitch is BlockSize + StreetWidth.
            const float pitch = 34f + 12f;
            const float street = pitch * 0.5f;

            var shots = new[]
            {
                new Shot
                {
                    Name = "01-overview",
                    Position = new Vector3(-120f, 240f, -430f),
                    LookAt = new Vector3(0f, 6f, 0f),
                    Fov = 52f
                },
                new Shot
                {
                    Name = "02-street",
                    Position = new Vector3(0f, 1.7f, -60f),
                    LookAt = new Vector3(0f, 2.2f, 90f),
                    Fov = 68f
                },
                new Shot
                {
                    Name = "03-rooftops",
                    Position = new Vector3(6f, 12f, -pitch * 1.2f),
                    LookAt = new Vector3(23f, 8f, 40f),
                    Fov = 60f
                },
                new Shot
                {
                    Name = "04-keep",
                    Position = new Vector3(23f + 34f, 9f, 23f - 44f),
                    LookAt = new Vector3(23f, 12f, 23f),
                    Fov = 55f
                },
                new Shot
                {
                    Name = "07-rampart",
                    Position = new Vector3(30f, 40f, -300f),
                    LookAt = new Vector3(0f, 4f, -372f),
                    Fov = 58f
                }
            };

            var host = new GameObject("ScreenshotTown");
            GameObject cameraHost = null;
            GameObject simHost = null;

            try
            {
                GreyboxTownGenerator generator = host.AddComponent<GreyboxTownGenerator>();
                generator.Seed = 20260824;
                MapDescriptor map = generator.Generate();
                Debug.Log($"[shot] town built, radius {map.Radius:0} m");

                // Boot a real simulation so agents exist: the point of the close-up shot is to check
                // the ninja body against the capsule it is supposed to fit.
                simHost = new GameObject("ScreenshotSim");
                UnseenBootstrap boot = simHost.AddComponent<UnseenBootstrap>();
                // ListenServer, not DedicatedServer: a headless server deliberately builds no
                // meshes (AgentSpawner's createVisuals flag), so agents would be invisible.
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = false; // the town already exists
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260824;
                boot.Boot();

                // Let the lobby fill and the drop finish, so the agents are on the ground.
                for (int i = 0; i < 60 * 60; i++)
                {
                    boot.Network.Poll(1f / 60f);
                    boot.Simulation.Advance(1f / 60f);
                }

                // The listen-server rig brings its own camera; disable it so only the shot camera
                // renders.
                foreach (Camera existing in simHost.GetComponentsInChildren<Camera>(true))
                    existing.enabled = false;

                AgentEntity sample = null;
                foreach (AgentEntity candidate in boot.Context.Entities.All)
                {
                    if (candidate.GetComponentInChildren<AgentVisual>() != null)
                    {
                        sample = candidate;
                        break;
                    }
                }

                if (sample == null && boot.Context.Entities.Count > 0)
                    sample = boot.Context.Entities.BySlot(0);

                if (sample != null)
                {
                    var visual = sample.GetComponentInChildren<AgentVisual>();
                    var skinned = sample.GetComponentInChildren<SkinnedMeshRenderer>();
                    Debug.Log($"[shot] sample agent at {sample.Position} " +
                              $"controller {sample.Controller.height:0.00} m, " +
                              $"visual={visual != null}, skinned={skinned != null}, " +
                              $"renderedHeight={(skinned != null ? skinned.bounds.size.y : 0f):0.00} m");
                }

                cameraHost = new GameObject("ShotCamera");
                Camera camera = cameraHost.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 500f;
                camera.allowHDR = true;

                // URP needs its per-camera data; requesting it creates it if absent.
                camera.GetUniversalAdditionalCameraData().renderShadows = true;

                Directory.CreateDirectory(OutputDir);

                // Close-up on a real agent, framed from a few metres away at chest height.
                if (sample != null)
                {
                    Vector3 pos = sample.Position;
                    var closeUp = new Shot
                    {
                        Name = "05-ninja",
                        Position = pos + new Vector3(2.6f, 1.5f, 2.6f),
                        LookAt = pos + new Vector3(0f, 0.9f, 0f),
                        Fov = 50f
                    };

                    var list = new System.Collections.Generic.List<Shot>(shots) { closeUp };
                    shots = list.ToArray();
                }

                // Close-up on a lantern, to check the paper body and its texture rather than
                // judging it from a glowing dot at the end of an alley.
                // Grow the spirit forest so it can be photographed: it is dormant for the first
                // three minutes of a match, which no screenshot run will ever reach.
                var forest = host.GetComponentInChildren<Unseen.Environment.BambooForest>();
                if (forest != null)
                {
                    forest.SetDepth(4f, 1f);

                    var wallShot = new Shot
                    {
                        Name = "15-bamboo",
                        Position = new Vector3(0f, 6f, -336f),
                        LookAt = new Vector3(0f, 10f, -372f),
                        Fov = 62f
                    };

                    var list = new System.Collections.Generic.List<Shot>(shots) { wallShot };
                    shots = list.ToArray();
                    Debug.Log($"[shot] spirit forest grown to {forest.Depth:0.0} m for the capture");
                }

                // The deck is segmented into an arch now, so the first plank is the landmark.
                Transform bridge = FindNamed(host.transform, "Deck_7");
                if (bridge != null)
                {
                    Vector3 pos = bridge.position;
                    var onBridge = new Shot
                    {
                        Name = "08-river",
                        Position = pos + new Vector3(2f, 6f, 34f),
                        LookAt = pos + new Vector3(0f, -3f, -6f),
                        Fov = 66f
                    };

                    var underBridge = new Shot
                    {
                        Name = "09-underbridge",
                        Position = pos + new Vector3(0f, -2.6f, 15f),
                        LookAt = pos + new Vector3(0f, -2.4f, -4f),
                        Fov = 72f
                    };

                    var list = new System.Collections.Generic.List<Shot>(shots) { onBridge, underBridge };
                    shots = list.ToArray();
                    Debug.Log($"[shot] bridge at {pos}");
                }

                Transform pagoda = FindNamed(host.transform, "Podium");
                if (pagoda != null)
                {
                    Vector3 pos = pagoda.position;
                    var tower = new Shot
                    {
                        Name = "10-pagoda",
                        Position = pos + new Vector3(30f, 8f, 30f),
                        LookAt = pos + new Vector3(0f, 16f, 0f),
                        Fov = 58f
                    };

                    var list = new System.Collections.Generic.List<Shot>(shots) { tower };
                    shots = list.ToArray();
                    Debug.Log($"[shot] pagoda at {pos}");
                }

                Transform lantern = FindLantern(host.transform);
                if (lantern != null)
                {
                    Vector3 pos = lantern.position;
                    var lanternShot = new Shot
                    {
                        Name = "06-lantern",
                        Position = pos + new Vector3(1.1f, 0.25f, 1.1f),
                        LookAt = pos,
                        Fov = 42f
                    };

                    var list = new System.Collections.Generic.List<Shot>(shots) { lanternShot };
                    shots = list.ToArray();
                    Debug.Log($"[shot] lantern close-up at {pos}");
                }

                foreach (Shot shot in shots)
                {
                    cameraHost.transform.position = shot.Position;
                    cameraHost.transform.rotation =
                        Quaternion.LookRotation((shot.LookAt - shot.Position).normalized, Vector3.up);
                    camera.fieldOfView = shot.Fov;

                    string path = Path.Combine(OutputDir, shot.Name + ".png");
                    Render(camera, path);
                    Debug.Log($"[shot] wrote {path}");
                }
            }
            finally
            {
                if (cameraHost != null) Object.DestroyImmediate(cameraHost);
                if (simHost != null)
                {
                    simHost.GetComponent<UnseenBootstrap>()?.Shutdown();
                    Object.DestroyImmediate(simHost);
                }

                Object.DestroyImmediate(host);
                BoxMeshFactory.ClearCache();
                LanternMeshFactory.ClearCache();
            }

            Debug.Log("[shot] done");
        }

        /// <summary>First lantern in the town, for the close-up shot.</summary>
        private static Transform FindLantern(Transform root) => FindNamed(root, "Lantern");

        /// <summary>First transform with this exact name, so a shot can frame a real object.</summary>
        private static Transform FindNamed(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name)
                    return t;
            return null;
        }

        private static void Render(Camera camera, string path)
        {
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.DefaultHDR)
            {
                antiAliasing = 2
            };

            var readback = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;

            try
            {
                camera.targetTexture = rt;

                // Two passes: the first lets the pipeline warm up shadow maps and any deferred setup,
                // so the saved frame is not the one where half the lighting is still missing.
                camera.Render();
                camera.Render();

                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
                readback.Apply();

                File.WriteAllBytes(path, readback.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                camera.targetTexture = null;
                Object.DestroyImmediate(readback);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }
    }
}
