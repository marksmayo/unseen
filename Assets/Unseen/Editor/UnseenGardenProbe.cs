using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Reports what the water gardens actually built: the moat and its island, the waterfalls,
    /// the raked gardens, and the fish.
    ///
    /// Written because a screenshot of a night-time town cannot tell a dark rock from a pale one
    /// lit badly, and reasoning about which of the two it was is how an afternoon goes.
    /// </summary>
    public static class UnseenGardenProbe
    {
        [MenuItem("Unseen/Probe Water Gardens", priority = 60)]
        public static void Run()
        {
            var host = new GameObject("GardenProbe");

            try
            {
                Koi.ClearAll();

                GreyboxTownGenerator generator = host.AddComponent<GreyboxTownGenerator>();
                generator.Seed = 20260824;
                generator.Generate();

                Transform lake = Find(host.transform, "CastleLake");
                if (lake == null) { Debug.LogError("[garden] no castle lake"); return; }

                Debug.Log($"[garden] lake at {lake.position}, {Koi.Count} koi swimming");

                // ---------------------------------------------------------- materials in use
                //
                // Grouped by material so a rock wearing the wrong one shows up as a count rather
                // than as a colour somebody has to judge from a screenshot.
                var byMaterial = new Dictionary<string, int>();
                var brightest = new Dictionary<string, Color>();

                foreach (Renderer r in lake.GetComponentsInChildren<Renderer>(true))
                {
                    Material m = r.sharedMaterial;
                    string name = m == null ? "<none>" : m.name;

                    byMaterial.TryGetValue(name, out int count);
                    byMaterial[name] = count + 1;

                    if (m != null && !brightest.ContainsKey(name))
                        brightest[name] = m.HasProperty("_BaseColor")
                            ? m.GetColor("_BaseColor")
                            : Color.magenta;
                }

                foreach (KeyValuePair<string, int> entry in byMaterial)
                {
                    Color tint = brightest.TryGetValue(entry.Key, out Color c) ? c : Color.magenta;
                    Debug.Log($"[garden] {entry.Value,4} x {entry.Key} " +
                              $"base=({tint.r:0.00},{tint.g:0.00},{tint.b:0.00})");
                }

                // ---------------------------------------------------------- the fall
                Transform fall = Find(lake, "Waterfall");
                if (fall != null)
                {
                    Bounds box = default;
                    bool any = false;

                    foreach (Renderer r in fall.GetComponentsInChildren<Renderer>(true))
                    {
                        if (!any) { box = r.bounds; any = true; continue; }
                        box.Encapsulate(r.bounds);
                    }

                    Debug.Log($"[garden] waterfall at {fall.position}, " +
                              $"spans {box.size.x:0.0} x {box.size.y:0.0} x {box.size.z:0.0} m");

                    Transform sheet = Find(fall, "Sheet");
                    if (sheet != null)
                        Debug.Log($"[garden] sheet faces {sheet.forward}, " +
                                  $"world size {sheet.GetComponent<Renderer>().bounds.size}");
                }

                // ---------------------------------------------------------- wading
                Debug.Log($"[garden] {WaterVolume.Registered} bodies of water registered");

                Vector3 centre = lake.position;

                // Read off the volume itself rather than written in as numbers. The lake was three
                // cells wide before these hardcoded offsets were, and the probe cheerfully
                // reported "no water in the moat, 1.45 m in the street" - both true of the sample
                // points, and both nonsense as a description of the lake.
                var body = lake.GetComponentInChildren<WaterVolume>(true);
                if (body == null) { Debug.LogError("[garden] the lake has no water volume"); return; }

                float islandHalf = body.InnerHalfSize.x;
                float lakeHalf = body.HalfSize.x;
                float band = (islandHalf + lakeHalf) * 0.5f;

                Debug.Log($"[garden] island half {islandHalf:0.0} m, lake half {lakeHalf:0.0} m, " +
                          $"{lakeHalf - islandHalf:0.0} m of water on every side");

                float onIsland = WaterVolume.DepthAt(centre);
                float inLake = WaterVolume.DepthAt(centre + new Vector3(band, 0f, 0f));
                float diagonal = WaterVolume.DepthAt(centre +
                    new Vector3(band * 0.72f, 0f, band * 0.72f));
                float outside = WaterVolume.DepthAt(centre + new Vector3(lakeHalf + 6f, 0f, 0f));

                Debug.Log($"[garden] depth at the keep {onIsland:0.00} m, " +
                          $"mid-lake {inLake:0.00} m, on the diagonal {diagonal:0.00} m, " +
                          $"in the street {outside:0.00} m");

                bool shaped = onIsland <= 0.01f && inLake > 0.8f && diagonal > 0.8f &&
                              outside <= 0.01f;

                Debug.Log($"[garden] the water is a ring round a dry island: " +
                          $"{(shaped ? "PASS" : "FAIL")}");

                // Wadeable but over your head prone. The crossing is the decision at the centre of
                // the map, and it only works if standing keeps your eyes out and prone does not.
                bool wadeable = inLake > 1.2f && inLake < 1.75f;
                Debug.Log($"[garden] chest deep, not lethal: {(wadeable ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- the fish stay wet
                //
                // Driven for two minutes and checked every step. A carp that swims through the
                // corner of the island is the failure a still screenshot cannot show.
                var koi = new List<Koi>(lake.GetComponentsInChildren<Koi>(true));
                int dry = 0;
                float nearest = 999f;

                for (int step = 0; step < 60 * 120; step++)
                {
                    Koi.AdvanceAll(1f / 60f);

                    if (step % 20 != 0) continue;

                    for (int i = 0; i < koi.Count; i++)
                    {
                        Vector3 at = koi[i].transform.position;
                        if (WaterVolume.DepthAt(at + Vector3.down * 0.4f) <= 0f) dry++;

                        // Clearance from the island, which for a square island is how far the
                        // furthest axis reaches past its edge. Taking the NEARER axis instead
                        // reports zero for a fish swimming happily down the middle of a side,
                        // because that axis passes the island's other face.
                        float reach = Mathf.Max(
                            Mathf.Abs(at.x - centre.x), Mathf.Abs(at.z - centre.z));

                        nearest = Mathf.Min(nearest, reach - islandHalf);
                    }
                }

                Debug.Log($"[garden] {dry} samples with a fish out of water over two minutes " +
                          $"(closest approach to the island wall {nearest:0.00} m)");

                Transform zen = Find(host.transform, "ZenGarden");
                Transform rocks = Find(host.transform, "RockGarden");
                Debug.Log($"[garden] zen garden={zen != null}, rock garden={rocks != null}");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Koi.ClearAll();
            }
        }

        private static Transform Find(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;

            return null;
        }
    }
}
