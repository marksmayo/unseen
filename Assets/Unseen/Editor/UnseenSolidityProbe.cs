using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Unseen.Core;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Finds things that look solid and are not.
    ///
    /// The town is built from three kinds of object: Box, which has a collider; Detail, which is
    /// renderer-only trim; and Organic, which is renderer-only lumps. That split is deliberate and
    /// mostly right - a carved post modelled on the outside of a wall should not shift where a
    /// ninja stands - but it is easy to reach for the decorative helper when building something a
    /// player will walk into, and the result is a four metre boulder you can stand inside.
    ///
    /// So rather than hunting them one at a time, this walks every renderer in the generated town,
    /// keeps the ones big enough and low enough for a body to meet, and asks the physics scene
    /// whether anything is actually there. Grouped by name, because the interesting output is
    /// "every LakeRock is hollow", not a list of nine hundred objects.
    /// </summary>
    public static class UnseenSolidityProbe
    {
        /// <summary>Narrower than this and walking into it is not what anybody would call solid.</summary>
        private const float MinWidth = 0.8f;

        /// <summary>Shorter than this and you step over it rather than into it.</summary>
        private const float MinHeight = 0.5f;

        /// <summary>
        /// Above this is roof and eave trim, which nobody walks into from the street.
        ///
        /// Four and a half metres, not eight: a body is under two metres tall and can vault, so
        /// anything centred above this is something you land on rather than walk into. At eight it
        /// was collecting ridge ornaments and upper-storey window trim, which are real decoration
        /// and would never have been anything else.
        /// </summary>
        private const float MaxCentreHeight = 4.5f;

        /// <summary>
        /// Thinner than this in its smallest dimension and it is a facing, not an object.
        ///
        /// A wainscot band is twenty-eight metres long and two hundred millimetres thick; it is
        /// paint with depth, glued to a wall that carries the collision. Asking it to be solid in
        /// its own right would double the collider count of the town to no effect, because the wall
        /// behind it already stops you. What this probe is for is things with volume - a boulder, a
        /// crate, a post - that you can put your head inside.
        /// </summary>
        private const float MinThickness = 0.25f;

        /// <summary>
        /// Things that are meant to be walked through, and why.
        ///
        /// A tree canopy that blocked movement would be a ceiling over the street; foliage and
        /// grass are cover for the eyes rather than the body; mist and water are not objects at
        /// all. Everything else that shows up here is a bug.
        /// </summary>
        private static readonly string[] DeliberatelyHollow =
        {
            "Canopy", "Leaf", "Leaves", "Foliage", "Frond", "Bough", "Grass", "Tuft", "Blade",
            "Reed", "Moss", "Mist", "Water", "Fog", "Sprig", "Shrub", "Bush", "Hedge", "Petal",
            "Bamboo", "Culm", "Verge", "Lawn",

            // Cloth. A noren is a doorway curtain and a banner hangs off a pole; walking through
            // both is the point of them.
            "Noren", "Banner", "Flag", "Curtain",

            // Timber that grows. A branch or a bamboo cane is held up by a trunk that has its own
            // collider, and giving every limb one would fence the streets off at head height.
            "Branch", "Limb", "Cane", "Twig", "Root"
        };

        [MenuItem("Unseen/Probe Solidity", priority = 72)]
        public static void Run()
        {
            var host = new GameObject("SolidityProbe");

            try
            {
                var gen = host.AddComponent<GreyboxTownGenerator>();
                gen.Seed = 20260831;
                gen.Generate();

                // Colliders created and then positioned are still at the origin as far as physics
                // is concerned until this is called. Without it every object in the town reports as
                // hollow and the probe looks like a catastrophe.
                Physics.SyncTransforms();

                var renderers = host.GetComponentsInChildren<MeshRenderer>(true);

                var hollow = new Dictionary<string, (int count, float biggest)>();
                var solid = new Dictionary<string, int>();
                int considered = 0;

                for (int i = 0; i < renderers.Length; i++)
                {
                    MeshRenderer r = renderers[i];
                    if (r == null || !r.enabled) continue;

                    Bounds b = r.bounds;
                    float width = Mathf.Max(b.size.x, b.size.z);

                    if (width < MinWidth || b.size.y < MinHeight) continue;
                    if (b.center.y > MaxCentreHeight) continue;

                    float thickness = Mathf.Min(b.size.x, Mathf.Min(b.size.y, b.size.z));
                    if (thickness < MinThickness) continue;
                    if (IsDeliberatelyHollow(r.name) || IsDeliberatelyHollow(ParentName(r))) continue;

                    considered++;

                    // Is there a collider anywhere near this, on any layer?
                    //
                    // Two corrections to the obvious version. Every layer, not just WorldGeometry:
                    // a loot chest is solid but its collider is on the LootContainer layer, and
                    // masking it out reported six hundred and fifty nine chests as hollow.
                    //
                    // And a sphere reaching a little past the object rather than a box inside it.
                    // Most of the trim in this town is a facing on something that IS solid - a
                    // batter against a rampart, a ridge along a roof, a paper panel in its frame -
                    // and testing only the middle of the facing calls the whole castle wall hollow.
                    // What actually ruins the illusion is a FREESTANDING object with nothing behind
                    // it, which is what a reaching test finds and an inside test cannot.
                    // A metre of reach for thin things. Trim is modelled proud of the surface it
                    // decorates - a wainscot band stands off its wall, a window sits in its
                    // frame - and half a metre was not enough to find the wall behind it, which
                    // reported the compound walls of the whole town as hollow.
                    float reach = Mathf.Max(1f, Mathf.Min(b.extents.x,
                        Mathf.Min(b.extents.y, b.extents.z)) + 0.4f);

                    // Sampled along the object, not just at its middle.
                    //
                    // A compound wall has a doorway gap in it and the wainscot band runs the whole
                    // length of the wall, so the centre of a twenty-eight metre band lands in the
                    // opening - nothing behind it there, solid wall either side. Testing one point
                    // reported every compound in the town as hollow. An object is backed if any of
                    // it is backed; the parts spanning a doorway are supposed to have air behind
                    // them, because that is what a doorway is.
                    Vector3 axis = b.size.x > b.size.z ? Vector3.right : Vector3.forward;
                    float half = b.size.x > b.size.z ? b.extents.x : b.extents.z;

                    bool anything = false;
                    for (int t = -1; t <= 1 && !anything; t++)
                        anything = Physics.CheckSphere(b.center + axis * (half * 0.6f * t), reach,
                            ~0, QueryTriggerInteraction.Ignore);

                    string key = Family(r.name);

                    if (anything)
                    {
                        solid.TryGetValue(key, out int n);
                        solid[key] = n + 1;
                        continue;
                    }

                    hollow.TryGetValue(key, out (int count, float biggest) entry);
                    hollow[key] = (entry.count + 1, Mathf.Max(entry.biggest, width));
                }

                int hollowTotal = 0;
                foreach (var kv in hollow) hollowTotal += kv.Value.count;

                Debug.Log($"[solid] {considered} objects big enough and low enough to walk into; " +
                          $"{hollowTotal} of them have nothing there");

                // Worst first: a hundred hollow pebbles matter less than six hollow boulders.
                var ranked = new List<KeyValuePair<string, (int count, float biggest)>>(hollow);
                ranked.Sort((a, b) => b.Value.biggest.CompareTo(a.Value.biggest));

                for (int i = 0; i < ranked.Count && i < 20; i++)
                    Debug.Log($"[solid]   HOLLOW {ranked[i].Value.count,5} x {ranked[i].Key} " +
                              $"(widest {ranked[i].Value.biggest:0.0} m)");

                // The counterfactual. If nothing came back solid, the physics query is broken and
                // the empty list above would mean nothing at all.
                int solidTotal = 0;
                foreach (var kv in solid) solidTotal += kv.Value;

                Debug.Log($"[solid] {solidTotal} of them are backed by a collider");

                bool queryWorks = solidTotal > 0;
                bool nothingHollow = hollowTotal == 0;

                Debug.Log($"[solid] the probe can see colliders at all: " +
                          $"{(queryWorks ? "PASS" : "FAIL")}");
                Debug.Log($"[solid] nothing solid-looking is hollow: " +
                          $"{(nothingHollow ? "PASS" : "FAIL")}");

                if (queryWorks && nothingHollow) Debug.Log("[solid] PASSED");
                else Debug.LogError("[solid] FAILED");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        private static string ParentName(Renderer r) =>
            r.transform.parent != null ? r.transform.parent.name : string.Empty;

        private static bool IsDeliberatelyHollow(string name)
        {
            for (int i = 0; i < DeliberatelyHollow.Length; i++)
                if (name.IndexOf(DeliberatelyHollow[i], System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

            return false;
        }

        /// <summary>Strips the trailing indices, so Rock_3_1 and Rock_9_0 count as one family.</summary>
        private static string Family(string name) =>
            Regex.Replace(name, @"[_0-9]+$", string.Empty);
    }
}
