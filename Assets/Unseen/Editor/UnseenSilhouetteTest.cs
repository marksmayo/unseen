using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Unseen.Client;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Proves a silhouette actually prints on a shoji screen, by measuring pixels.
    ///
    /// This exists because the feature was marked done for months while being physically incapable
    /// of occurring: the server computed silhouette contacts, the feeder pushed them into global
    /// shader properties, and no material used the shader that reads them. Nothing in the codebase
    /// would have noticed. So the test asserts the outcome - the panel gets darker in the middle
    /// when a contact is behind it - rather than asserting that the parts exist.
    /// </summary>
    public static class UnseenSilhouetteTest
    {
        private const int Size = 512;
        private const string OutputDir = "Server/out/shots";

        [MenuItem("Unseen/Test Shoji Silhouettes", priority = 83)]
        public static void Run()
        {
            var host = new GameObject("SilhouetteTest");
            GameObject cameraHost = null;

            try
            {
                GreyboxMaterialSet set = GreyboxMaterialSet.Load();
                if (set == null || set.ShojiPaper == null)
                {
                    Debug.LogError("[silhouette] no ShojiPaper material in the set; " +
                                   "run Unseen > Art > Build Materials From Textures");
                    return;
                }

                Debug.Log($"[silhouette] material '{set.ShojiPaper.name}' " +
                          $"shader '{set.ShojiPaper.shader.name}'");

                if (set.ShojiPaper.shader.name != "Unseen/ShojiSilhouette")
                {
                    Debug.LogError("[silhouette] FAILED: the shoji material is not on the " +
                                   "silhouette shader, so no contact can ever print");
                    return;
                }

                // One panel, filling the view.
                var panel = GameObject.CreatePrimitive(PrimitiveType.Quad);
                panel.transform.SetParent(host.transform, false);
                panel.transform.position = Vector3.zero;
                panel.transform.rotation = Quaternion.identity;
                panel.transform.localScale = new Vector3(4f, 3f, 1f);
                panel.GetComponent<MeshRenderer>().sharedMaterial = set.ShojiPaper;
                Object.DestroyImmediate(panel.GetComponent<Collider>());

                var lightHost = new GameObject("Key");
                lightHost.transform.SetParent(host.transform, false);
                Light key = lightHost.AddComponent<Light>();
                key.type = LightType.Directional;
                key.intensity = 1.2f;
                lightHost.transform.rotation = Quaternion.Euler(20f, 160f, 0f);

                cameraHost = new GameObject("SilhouetteCamera");
                Camera camera = cameraHost.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.02f, 0.02f, 0.03f);
                camera.orthographic = true;
                camera.orthographicSize = 1.6f;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 20f;
                camera.transform.position = new Vector3(0f, 0f, -3f);
                camera.transform.rotation = Quaternion.identity;
                camera.GetUniversalAdditionalCameraData().renderShadows = false;

                Directory.CreateDirectory(OutputDir);

                // No contacts: this is the control.
                SetContacts(0, Vector3.zero);
                float clear = MeanCentre(camera, Path.Combine(OutputDir, "14-shoji-clear.png"));

                // One contact directly behind the middle of the panel.
                SetContacts(1, Vector3.zero);
                float printed = MeanCentre(camera, Path.Combine(OutputDir, "14-shoji-silhouette.png"));

                // And one off to the side, to prove the position is respected rather than the
                // whole panel simply darkening whenever the count is non-zero.
                SetContacts(1, new Vector3(1.6f, 0f, 0f));
                float offset = MeanCentre(camera, Path.Combine(OutputDir, "14-shoji-offset.png"));

                SetContacts(0, Vector3.zero);

                float drop = clear - printed;
                Debug.Log($"[silhouette] centre brightness: clear {clear:0.000}, " +
                          $"contact behind centre {printed:0.000} (drop {drop:0.000}), " +
                          $"contact offset {offset:0.000}");

                bool prints = drop > 0.04f;
                bool positional = offset > printed + 0.02f;

                Debug.Log($"[silhouette] prints a shape: {(prints ? "PASS" : "FAIL")}");
                Debug.Log($"[silhouette] respects position: {(positional ? "PASS" : "FAIL")}");

                if (prints && positional) Debug.Log("[silhouette] PASSED");
                else Debug.LogError("[silhouette] FAILED");
            }
            finally
            {
                if (cameraHost != null) Object.DestroyImmediate(cameraHost);
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>Writes the globals the way <see cref="ShojiSilhouetteFeeder"/> does.</summary>
        private static void SetContacts(int count, Vector3 position)
        {
            var entries = new Vector4[ShojiSilhouetteFeeder.MaxSilhouettes];
            if (count > 0) entries[0] = new Vector4(position.x, position.y, position.z, 1f);

            Shader.SetGlobalVectorArray("_UnseenSilhouettes", entries);
            Shader.SetGlobalFloat("_UnseenSilhouetteCount", count);
        }

        /// <summary>Mean luminance of the middle of the frame, and writes the frame out.</summary>
        private static float MeanCentre(Camera camera, string path)
        {
            var target = new RenderTexture(Size, Size, 24, RenderTextureFormat.DefaultHDR);
            var readback = new Texture2D(Size, Size, TextureFormat.RGB24, false);

            camera.targetTexture = target;
            camera.Render();

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0f, 0f, Size, Size), 0, 0);
            readback.Apply();
            RenderTexture.active = previous;

            File.WriteAllBytes(path, readback.EncodeToPNG());

            const int band = Size / 6;
            int from = Size / 2 - band;
            int to = Size / 2 + band;

            float total = 0f;
            int samples = 0;
            for (int y = from; y < to; y++)
            for (int x = from; x < to; x++)
            {
                Color c = readback.GetPixel(x, y);
                total += c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
                samples++;
            }

            camera.targetTexture = null;
            Object.DestroyImmediate(readback);
            target.Release();
            Object.DestroyImmediate(target);

            return samples > 0 ? total / samples : 0f;
        }
    }
}
