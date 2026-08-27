using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks that a roof's curved corners are a curve.
    ///
    /// Written after the first attempt shipped a flight of separate cubes climbing off the corner
    /// of every building and a row of blocks standing on top of the tiles like teeth. Both were
    /// obvious in a screenshot and invisible in the code, and both are trivially measurable:
    ///
    ///   - Consecutive pieces of a sweep must OVERLAP. Sampling a curve that climbs 1.4 m with
    ///     0.26 m thick blocks leaves half a metre of air between each pair.
    ///   - Nothing may stand above the roof plane. An eave is what the roof overhangs you with;
    ///     the moment any of it climbs above the tiles it is not an eave.
    ///   - The sweep must start ON the roof, not a step out from it with nothing bridging back.
    ///   - And it must not reach far. A tail longer than the roof is thick is a wing.
    /// </summary>
    public static class UnseenEaveProbe
    {
        [MenuItem("Unseen/Probe Roof Eaves", priority = 61)]
        public static void Run()
        {
            var host = new GameObject("EaveProbe");

            try
            {
                GreyboxTownGenerator generator = host.AddComponent<GreyboxTownGenerator>();
                generator.Seed = 20260824;
                generator.Generate();

                // Every bottom tier of every hip roof and every pagoda storey.
                var tiers = new List<(Transform slab, string tag)>();

                foreach (Transform t in host.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "Roof_0") tiers.Add((t, "Hip_0"));
                    else if (t.name.StartsWith("Roof_") && t.name.EndsWith("_0"))
                    {
                        // Roof_{storey}_0 on a pagoda.
                        string middle = t.name.Substring(5, t.name.Length - 7);
                        if (middle.Length > 0 && int.TryParse(middle, out int storey))
                            tiers.Add((t, $"Pagoda_{storey}_0"));
                    }
                }

                if (tiers.Count == 0)
                {
                    Debug.LogError("[eaves] found no roof tiers to check");
                    return;
                }

                Debug.Log($"[eaves] {tiers.Count} roof tiers with a swept corner");

                int checkedSweeps = 0;
                int gaps = 0;
                float worstGap = 0f;
                string worstGapAt = "-";

                int aboveRoof = 0;
                float worstAbove = 0f;
                string worstAboveAt = "-";

                int detached = 0;
                float worstDetach = 0f;

                float worstReach = 0f;

                // A sample rather than all of them: nineteen hundred roofs all built by the same
                // code proves nothing more than thirty do, and the probe has to finish.
                int stride = Mathf.Max(1, tiers.Count / 30);

                for (int index = 0; index < tiers.Count; index += stride)
                {
                    (Transform slab, string tag) tier = tiers[index];
                    Transform parent = tier.slab.parent;
                    if (parent == null) continue;

                    var collider = tier.slab.GetComponent<BoxCollider>();
                    if (collider == null) continue;

                    Bounds roof = collider.bounds;
                    float roofTop = roof.max.y;

                    // Grouped by which corner they belong to, in the order they were built.
                    var corners = new Dictionary<string, List<Renderer>>();

                    foreach (Transform child in parent)
                    {
                        string prefix = $"Sweep_{tier.tag}_";
                        if (!child.name.StartsWith(prefix)) continue;

                        // Sweep_{tag}_{sx}_{sz}_{i}
                        string rest = child.name.Substring(prefix.Length);
                        int lastUnderscore = rest.LastIndexOf('_');
                        if (lastUnderscore <= 0) continue;

                        string corner = rest.Substring(0, lastUnderscore);
                        if (!corners.TryGetValue(corner, out List<Renderer> pieces))
                        {
                            pieces = new List<Renderer>();
                            corners[corner] = pieces;
                        }

                        var renderer = child.GetComponent<Renderer>();
                        if (renderer != null) pieces.Add(renderer);
                    }

                    foreach (KeyValuePair<string, List<Renderer>> corner in corners)
                    {
                        List<Renderer> pieces = corner.Value;
                        if (pieces.Count < 2) continue;

                        checkedSweeps++;

                        // ------------------------------------------------ attached to the roof
                        //
                        // The first piece has to touch the slab, or the corner floats.
                        Bounds first = pieces[0].bounds;
                        first.Expand(0.06f);

                        if (!first.Intersects(roof))
                        {
                            detached++;
                            worstDetach = Mathf.Max(worstDetach,
                                Vector3.Distance(pieces[0].bounds.center, roof.ClosestPoint(
                                    pieces[0].bounds.center)));
                        }

                        for (int i = 0; i < pieces.Count; i++)
                        {
                            Bounds box = pieces[i].bounds;

                            // ------------------------------------------ never above the tiles
                            float over = box.max.y - roofTop;
                            if (over > 0.02f)
                            {
                                aboveRoof++;
                                if (over > worstAbove)
                                {
                                    worstAbove = over;
                                    worstAboveAt = pieces[i].name;
                                }
                            }

                            // ------------------------------------------ how far it reaches out
                            float outX = Mathf.Max(0f, Mathf.Abs(box.center.x - roof.center.x)
                                                       - roof.extents.x);
                            float outZ = Mathf.Max(0f, Mathf.Abs(box.center.z - roof.center.z)
                                                       - roof.extents.z);
                            worstReach = Mathf.Max(worstReach, Mathf.Max(outX, outZ));

                            if (i == 0) continue;

                            // ------------------------------------------ continuous, not a staircase
                            Bounds previous = pieces[i - 1].bounds;
                            Bounds grown = previous;
                            grown.Expand(0.02f);

                            if (grown.Intersects(box)) continue;

                            gaps++;
                            float gap = Vector3.Distance(previous.center, box.center)
                                        - (previous.extents.magnitude + box.extents.magnitude);

                            if (gap > worstGap)
                            {
                                worstGap = gap;
                                worstGapAt = pieces[i].name;
                            }
                        }
                    }
                }

                Debug.Log($"[eaves] {checkedSweeps} swept corners sampled");

                Debug.Log($"[eaves] {gaps} breaks between consecutive pieces " +
                          $"(worst {worstGap:0.00} m at {worstGapAt})");
                Debug.Log($"[eaves] the sweep is continuous: {(gaps == 0 ? "PASS" : "FAIL")}");

                Debug.Log($"[eaves] {aboveRoof} pieces standing above the tiles " +
                          $"(worst {worstAbove:0.00} m at {worstAboveAt})");
                Debug.Log($"[eaves] nothing climbs above the roof: " +
                          $"{(aboveRoof == 0 ? "PASS" : "FAIL")}");

                Debug.Log($"[eaves] {detached} corners not touching their roof " +
                          $"(worst {worstDetach:0.00} m)");
                Debug.Log($"[eaves] the corner is attached: {(detached == 0 ? "PASS" : "FAIL")}");

                bool modest = worstReach < 1.6f;
                Debug.Log($"[eaves] furthest reach past the slab {worstReach:0.00} m");
                Debug.Log($"[eaves] it is a corner, not a wing: {(modest ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- rafter ends
                //
                // Same rule, checked separately, because these were lifted by the same curve as
                // the sweep and pushed up through the slab.
                int rafterAbove = 0;
                float worstRafter = 0f;

                for (int index = 0; index < tiers.Count; index += stride)
                {
                    (Transform slab, string tag) tier = tiers[index];
                    Transform parent = tier.slab.parent;
                    var collider = tier.slab.GetComponent<BoxCollider>();
                    if (parent == null || collider == null) continue;

                    float roofTop = collider.bounds.max.y;

                    foreach (Transform child in parent)
                    {
                        if (!child.name.StartsWith($"Rafter_{tier.tag}_")) continue;

                        var renderer = child.GetComponent<Renderer>();
                        if (renderer == null) continue;

                        float over = renderer.bounds.max.y - roofTop;
                        if (over <= 0.02f) continue;

                        rafterAbove++;
                        worstRafter = Mathf.Max(worstRafter, over);
                    }
                }

                Debug.Log($"[eaves] {rafterAbove} rafter ends above the tiles " +
                          $"(worst {worstRafter:0.00} m)");
                Debug.Log($"[eaves] rafter ends hang below the eave: " +
                          $"{(rafterAbove == 0 ? "PASS" : "FAIL")}");

                if (gaps == 0 && aboveRoof == 0 && detached == 0 && modest && rafterAbove == 0 &&
                    checkedSweeps > 0)
                    Debug.Log("[eaves] PASSED");
                else
                    Debug.LogError("[eaves] FAILED");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }
    }
}
