using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Unseen.Core;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// An inventory of what this town costs to draw, and what the simulation costs to run.
    ///
    /// Reports rather than asserts. There is no correct number for "how many renderers" - the
    /// answer depends on the target hardware and nobody has picked one - so this exists to put a
    /// ranked list in front of a decision rather than to pass or fail.
    ///
    /// Rendering cost is counted by MATERIAL, because that is what decides batching: a thousand
    /// renderers sharing one material is one SRP batch and a thousand renderers with a thousand
    /// materials is a thousand. Transparency is counted separately, since a transparent quad costs
    /// its own area in overdraw every frame whether or not anything is behind it, and this town has
    /// a great many of them lying in the streets.
    ///
    /// Simulation cost comes from ServerSimulation's own per-system timers, which are the only
    /// honest source: wall-clock for a whole run varies by a factor of three on this machine
    /// depending on what else is happening, but the SHARE each system takes of a run does not.
    /// </summary>
    public static class UnseenCostProbe
    {
        private sealed class Bucket
        {
            public string Material;
            public string Shader;
            public int Renderers;
            public long Triangles;
            public int ShadowCasters;
            public bool Transparent;
            public float TransparentArea;
        }

        [MenuItem("Unseen/Probe Cost", priority = 66)]
        public static void Run()
        {
            var host = new GameObject("CostProbe");

            try
            {
                GreyboxTownGenerator generator = host.AddComponent<GreyboxTownGenerator>();
                generator.Seed = 20260824;

                // Batching OFF for the measurement.
                //
                // StaticBatchingUtility.Combine repoints every renderer's shared mesh at one
                // combined mesh holding the whole group, so asking a renderer for its triangle
                // count afterwards returns the entire batch. The first run of this probe reported
                // 1.3 BILLION triangles and 92,000 per leaf, which is how I found out.
                generator.CombineStaticMeshes = false;
                generator.Generate();

                var buckets = new Dictionary<string, Bucket>(128);

                int renderers = 0;
                long triangles = 0;
                int shadowCasters = 0;

                foreach (Renderer renderer in host.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer.gameObject.activeInHierarchy || !renderer.enabled) continue;

                    renderers++;

                    Material material = renderer.sharedMaterial;
                    string name = material == null ? "<none>" : material.name;

                    if (!buckets.TryGetValue(name, out Bucket bucket))
                    {
                        bucket = new Bucket
                        {
                            Material = name,
                            Shader = material != null && material.shader != null
                                ? material.shader.name
                                : "<none>",

                            // Anything queued at or past 3000 is alpha blended, and pays for its
                            // area in overdraw every frame.
                            Transparent = material != null && material.renderQueue >= 3000
                        };

                        buckets[name] = bucket;
                    }

                    bucket.Renderers++;

                    Mesh mesh = MeshOf(renderer);
                    if (mesh != null) bucket.Triangles += mesh.triangles.Length / 3;

                    if (renderer.shadowCastingMode !=
                        UnityEngine.Rendering.ShadowCastingMode.Off)
                    {
                        bucket.ShadowCasters++;
                        shadowCasters++;
                    }

                    if (bucket.Transparent)
                    {
                        Vector3 size = renderer.bounds.size;
                        bucket.TransparentArea += size.x * size.z;
                    }
                }

                foreach (Bucket bucket in buckets.Values) triangles += bucket.Triangles;

                Light[] lights = host.GetComponentsInChildren<Light>(true);
                int realtime = lights.Count(l => l.enabled && l.gameObject.activeInHierarchy);

                Collider[] colliders = host.GetComponentsInChildren<Collider>(true);

                Debug.Log("[cost] ================ RENDERING ================");
                Debug.Log($"[cost] {renderers} active renderers, {triangles / 1000} k triangles, " +
                          $"{buckets.Count} distinct materials, {shadowCasters} shadow casters");
                Debug.Log($"[cost] mean {(renderers > 0 ? triangles / (float)renderers : 0f):0} " +
                          $"triangles per renderer");
                Debug.Log($"[cost] {colliders.Length} colliders, {lights.Length} lights " +
                          $"({realtime} enabled)");

                Debug.Log("[cost] top 20 by renderer count - each material is one SRP batch, so " +
                          "this column is roughly draw calls:");

                int rank = 1;

                foreach (Bucket bucket in buckets.Values
                             .OrderByDescending(b => b.Renderers)
                             .Take(20))
                {
                    Debug.Log($"[cost] {rank,2}. {bucket.Renderers,6} x {bucket.Material,-16} " +
                              $"{bucket.Triangles / 1000,5} k tris  " +
                              $"{(bucket.Transparent ? "TRANSPARENT" : "opaque")}  " +
                              $"{bucket.ShadowCasters} casters  [{bucket.Shader}]");
                    rank++;
                }

                Debug.Log("[cost] transparent surfaces, by ground area covered - this is overdraw, " +
                          "and it is paid every frame whether or not it is visible:");

                foreach (Bucket bucket in buckets.Values
                             .Where(b => b.Transparent)
                             .OrderByDescending(b => b.TransparentArea))
                {
                    Debug.Log($"[cost]     {bucket.Material,-16} {bucket.Renderers,5} quads, " +
                              $"{bucket.TransparentArea / 1000f:0} k m^2 of coverage");
                }

                Debug.Log("[cost] top 10 by triangle count:");

                rank = 1;

                foreach (Bucket bucket in buckets.Values
                             .OrderByDescending(b => b.Triangles)
                             .Take(10))
                {
                    Debug.Log($"[cost] {rank,2}. {bucket.Triangles / 1000,6} k tris  " +
                              $"{bucket.Material} ({bucket.Renderers} renderers)");
                    rank++;
                }
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        private static Mesh MeshOf(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;

            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }
    }
}
