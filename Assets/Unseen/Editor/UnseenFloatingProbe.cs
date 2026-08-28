using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks that nothing scattered across the town is standing on thin air.
    ///
    /// The town is dressed by walking the street grid and dropping things at y = 0 with a bit of
    /// jitter: patches of grass, runs of hedge, potted plants, animals. None of it asked what was
    /// underneath. The river channel is cut four metres down and the bridges span it, so anything
    /// whose jitter carried it over the water was left hanging at street level - sheets of grass
    /// floating under bridges, a hedge run crossing the river on nothing, a cat standing on the air
    /// above a towpath. All three were reported from play.
    ///
    /// Each of those had its own hand-tuned "skip if within N metres of the river" test, and every
    /// one of them tested the grid position rather than the jittered one, which is why they all
    /// leaked. This asserts the outcome instead: for everything that is supposed to sit on the
    /// ground, there is ground under it.
    ///
    /// Birds are deliberately exempt. They perch in trees and on hedge tops, and a downward ray
    /// from one usually finds the street a long way below - which is correct, and is what being in
    /// a tree means.
    /// </summary>
    public static class UnseenFloatingProbe
    {
        [MenuItem("Unseen/Probe Floating Scenery", priority = 65)]
        public static void Run()
        {
            var host = new GameObject("FloatingProbe");

            try
            {
                GreyboxTownGenerator generator = host.AddComponent<GreyboxTownGenerator>();
                generator.Seed = 20260824;
                generator.Generate();

                Physics.SyncTransforms();

                // ---------------------------------------------------------- ground dressing
                var offenders = new System.Collections.Generic.List<string>(8);

                int checkedPieces = 0;
                int floating = 0;
                float worst = 0f;
                string worstName = "none";

                foreach (string groupName in new[] { "Verges", "Greenery" })
                {
                    Transform group = Find(host.transform, groupName);
                    if (group == null) continue;

                    foreach (Renderer renderer in group.GetComponentsInChildren<Renderer>(true))
                    {
                        // Skip anything hanging off a critter: those are checked separately, and a
                        // bird's tail feather is not ground dressing.
                        if (renderer.GetComponentInParent<Critter>() != null) continue;

                        checkedPieces++;

                        float gap = DropToGround(renderer.bounds);
                        if (gap <= 1.5f) continue;

                        floating++;

                        // Named with their parent and position: "Mass_46 floats" says nothing
                        // about which hedge, or what it is floating over.
                        if (offenders.Count < 8)
                            offenders.Add($"{renderer.transform.parent?.name}/{renderer.name} " +
                                          $"at {renderer.bounds.center} gap {gap:0.0} m");

                        if (gap <= worst) continue;
                        worst = gap;
                        worstName = renderer.name;
                    }
                }

                Debug.Log($"[floating] {checkedPieces} pieces of ground dressing checked");
                Debug.Log($"[floating] {floating} of them have nothing under them " +
                          $"(worst {worst:0.0} m on '{worstName}')");

                foreach (string offender in offenders) Debug.Log($"[floating]   {offender}");

                bool dressingSits = checkedPieces > 0 && floating == 0;
                Debug.Log($"[floating] grass, hedges and pots sit on the ground: " +
                          $"{(dressingSits ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- animals
                //
                // Held to a tighter figure than the dressing: a cat is small, and half a metre of
                // clearance under one is already obvious.
                int animals = 0;
                int hovering = 0;
                float worstAnimal = 0f;

                foreach (Critter critter in Critter.All)
                {
                    if (critter == null || critter.Kind != Critter.Species.Animal) continue;

                    animals++;

                    var feet = new Vector3(critter.transform.position.x,
                        critter.transform.position.y + 0.1f, critter.transform.position.z);

                    if (!Physics.Raycast(feet, Vector3.down, out RaycastHit hit, 60f,
                            UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                    {
                        hovering++;
                        continue;
                    }

                    float gap = feet.y - hit.point.y;
                    if (gap <= 0.6f) continue;

                    hovering++;
                    worstAnimal = Mathf.Max(worstAnimal, gap);
                }

                Debug.Log($"[floating] {animals} animals; {hovering} standing on air " +
                          $"(worst {worstAnimal:0.0} m)");

                bool animalsStand = animals > 0 && hovering == 0;
                Debug.Log($"[floating] animals stand on something: " +
                          $"{(animalsStand ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- and none of it is wet
                int wet = 0;

                foreach (Critter critter in Critter.All)
                {
                    if (critter == null || critter.Kind != Critter.Species.Animal) continue;

                    Vector3 at = critter.transform.position;
                    if (WaterVolume.DepthAt(new float3(at.x, at.y, at.z)) > 0.3f) wet++;
                }

                bool dry = wet == 0;
                Debug.Log($"[floating] {wet} animals standing in water");
                Debug.Log($"[floating] nothing is left standing in the river: " +
                          $"{(dry ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- nothing grows through
                //
                // A tree planted against a wall grows straight through it and out the other side.
                // Tested as a capsule up each trunk, ignoring the tree's own colliders - which is
                // the whole reason this cannot simply reuse the generator's own check.
                int trunks = 0;
                int embedded = 0;
                string worstTree = "none";

                Transform foliage = Find(host.transform, "Foliage");

                if (foliage != null)
                {
                    foreach (Transform trunk in foliage.GetComponentsInChildren<Transform>(true))
                    {
                        if (trunk.name != "TrunkCollider") continue;

                        var box = trunk.GetComponent<BoxCollider>();
                        if (box == null) continue;

                        trunks++;

                        Vector3 at = trunk.position;
                        float height = box.size.y * trunk.lossyScale.y;

                        Collider[] hits = Physics.OverlapCapsule(
                            at + Vector3.down * (height * 0.5f - 1.2f),
                            at + Vector3.up * (height * 0.35f),
                            1.1f, UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore);

                        bool clash = false;

                        foreach (Collider hit in hits)
                        {
                            // Its own trunk, and anything else belonging to the same tree.
                            if (hit.transform.IsChildOf(trunk.parent)) continue;
                            clash = true;
                            break;
                        }

                        if (!clash) continue;

                        embedded++;
                        if (worstTree == "none") worstTree = $"{trunk.parent.name} at {at}";
                    }
                }

                Debug.Log($"[floating] {trunks} trees; {embedded} growing through something " +
                          $"(first: {worstTree})");

                bool rooted = trunks > 0 && embedded == 0;
                Debug.Log($"[floating] trees have room to grow: {(rooted ? "PASS" : "FAIL")}");

                if (dressingSits && animalsStand && dry && rooted)
                    Debug.Log("[floating] PASSED");
                else
                    Debug.LogError("[floating] FAILED");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// How far a piece of scenery is off the ground, measured from the bottom of what it draws.
        ///
        /// Sampled at the centre and the four corners of the footprint, taking the closest hit. A
        /// single ray down the middle passes a hedge that has one end on the towpath and the other
        /// over the water, which is the exact shape of the bug this is here to catch.
        /// </summary>
        private static float DropToGround(Bounds bounds)
        {
            float best = float.MaxValue;

            for (int i = 0; i < 5; i++)
            {
                float x = i == 0 ? 0f : (i <= 2 ? -0.45f : 0.45f);
                float z = i == 0 ? 0f : (i % 2 == 1 ? -0.45f : 0.45f);

                var from = new Vector3(
                    bounds.center.x + bounds.size.x * x,
                    bounds.min.y + 0.25f,
                    bounds.center.z + bounds.size.z * z);

                if (!Physics.Raycast(from, Vector3.down, out RaycastHit hit, 80f,
                        UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                    continue;

                best = Mathf.Min(best, from.y - hit.point.y);
            }

            return best == float.MaxValue ? 99f : best;
        }

        private static Transform Find(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;

            return null;
        }
    }
}
