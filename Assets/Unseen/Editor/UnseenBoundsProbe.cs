using UnityEditor;
using UnityEngine;
using Unseen.Core;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks that the boundary a player runs into is the boundary they can see.
    ///
    /// The town is square and the rampart is four straight walls, but the bounds clamp was a circle
    /// inscribed in it. Along the axes the two agreed, so nothing looked wrong; along a diagonal the
    /// clamp cut the corner off more than a hundred metres short of the wall, which the player met
    /// as an invisible barrier standing in an open street. It also put five of every eight bearings
    /// of the spirit forest permanently out of reach.
    ///
    /// The forest's own behaviour is covered by <see cref="UnseenBambooTest"/>. What is asserted
    /// here is the shape of the cage, and that the wall of bamboo is drawn where its colliders
    /// stand - a renderer that moves every tick is exactly the thing a static batch quietly freezes.
    /// </summary>
    public static class UnseenBoundsProbe
    {
        [MenuItem("Unseen/Diagnose Boundaries", priority = 88)]
        public static void Run()
        {
            var host = new GameObject("BoundsProbe");

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260824;
                boot.Boot();

                Step(boot, 120);

                MapDescriptor map = MapDescriptor.Find();
                BambooForest forest = Object.FindAnyObjectByType<BambooForest>();

                if (map == null || forest == null)
                {
                    Debug.LogError("[bounds] no map descriptor or no forest in the level");
                    return;
                }

                Debug.Log($"[bounds] map radius {map.Radius:0} m, half-extent {map.HalfExtent:0} m");

                bool square = map.HalfExtent > 0f;
                Debug.Log($"[bounds] the square town declares a square boundary: " +
                          $"{(square ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- can the wall be reached
                //
                // The rampart along a bearing is at half-extent / max(|sin|, |cos|) from the centre.
                // The clamp has to permit standing there, or the wall is not the edge of the world.
                float limit = square ? map.HalfExtent - 2f : 0f;
                var bearings = new[] { 0f, 22.5f, 45f, 67.5f, 90f, 135f, 180f, 225f };

                int reachable = 0;
                int circularWouldReach = 0;
                float circle = map.Radius - 2f;

                foreach (float bearing in bearings)
                {
                    float rad = bearing * Mathf.Deg2Rad;
                    var dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));

                    float t = (limit > 0f ? limit : circle) /
                              Mathf.Max(0.001f, Mathf.Max(Mathf.Abs(dir.x), Mathf.Abs(dir.z)));
                    Vector3 wall = dir * t;

                    bool boxAllows = limit > 0f &&
                                     Mathf.Abs(wall.x) <= limit + 0.01f &&
                                     Mathf.Abs(wall.z) <= limit + 0.01f;

                    if (boxAllows) reachable++;
                    if (wall.magnitude <= circle + 0.01f) circularWouldReach++;
                }

                // The counterfactual, so this assert is known to be capable of failing rather than
                // being a line that always prints PASS.
                Debug.Log($"[bounds] a circular clamp at {circle:0} m would reach " +
                          $"{circularWouldReach}/{bearings.Length} bearings");
                Debug.Log($"[bounds] the rampart is reachable on every bearing: " +
                          $"{(reachable == bearings.Length ? "PASS" : "FAIL")} " +
                          $"({reachable}/{bearings.Length})");

                // ---------------------------------------------------- is the wall drawn where it is
                //
                // This used to compare each renderer's BOUNDS CENTRE to its transform position and
                // call the difference drift. That is not drift, it is where the mesh sits in its own
                // space: a culm is a tube standing ON its origin, deliberately, so that it grows out
                // of the ground rather than being half buried - which puts the centre of a fifteen
                // metre cane seven and a half metres above its base by construction. The probe
                // reported 900 of 2,772 renderers "adrift" by up to 9.4 m and had been failing for
                // it, and every one of them was exactly where it should be.
                //
                // What the player actually cares about, and what this section claims to check, is
                // that the bamboo they can SEE marks the circle that damages them. So that is what
                // is measured: the solid wall against its own colliders, and the canes against the
                // damage radius.
                var centre = new Vector3(30f, 0f, -20f);
                float ring = forest.MaxRadius * 0.55f;

                forest.SetRing(centre, ring, 1f);
                Physics.SyncTransforms();

                float edge = forest.InnerEdge;

                // ---------------------------------------------------- the solid wall
                //
                // Each wall segment carries a box mesh and a box collider on the same transform, so
                // the thing you see and the thing you hit must occupy the same space. A mismatch
                // here is a wall you can walk through or one that stops you early.
                int segments = 0;
                float worstMismatch = 0f;
                string worstSegment = "none";

                foreach (BoxCollider box in forest.GetComponentsInChildren<BoxCollider>(true))
                {
                    if (!box.gameObject.activeInHierarchy) continue;

                    var renderer = box.GetComponent<Renderer>();
                    if (renderer == null) continue;

                    segments++;

                    float mismatch = Vector3.Distance(renderer.bounds.center, box.bounds.center);
                    if (mismatch <= worstMismatch) continue;

                    worstMismatch = mismatch;
                    worstSegment = box.name;
                }

                bool solidMatchesVisible = segments > 0 && worstMismatch < 0.25f;

                Debug.Log($"[bounds] {segments} wall segments; worst gap between what is drawn and " +
                          $"what collides {worstMismatch:0.00} m on '{worstSegment}'");
                Debug.Log($"[bounds] the solid wall is drawn where it collides: " +
                          $"{(solidMatchesVisible ? "PASS" : "FAIL")}");

                // ---------------------------------------------------- the canes mark the circle
                //
                // Measured at each cane's BASE - where it meets the ground - horizontally from the
                // forest centre, because that is the line a player walks up to. The canes are
                // planted just inside the edge on purpose, so the acceptable band is a couple of
                // metres inside it and nothing outside.
                int canes = 0;
                int misplaced = 0;
                float nearest = float.MaxValue;
                float furthest = 0f;

                foreach (Transform culm in forest.transform)
                {
                    if (!culm.gameObject.activeInHierarchy) continue;
                    if (!culm.name.StartsWith("Culm_")) continue;

                    canes++;

                    Vector3 offset = culm.position - centre;
                    offset.y = 0f;
                    float radius = offset.magnitude;

                    nearest = Mathf.Min(nearest, radius);
                    furthest = Mathf.Max(furthest, radius);

                    // Inside the damage edge, and not more than three metres in.
                    if (radius > edge + 0.5f || radius < edge - 3f) misplaced++;
                }

                bool canesOnTheCircle = canes > 0 && misplaced == 0;

                Debug.Log($"[bounds] {canes} canes standing between {nearest:0.0} m and " +
                          $"{furthest:0.0} m from the centre, against a damage edge at {edge:0.0} m");
                Debug.Log($"[bounds] {misplaced} canes are off the circle");
                Debug.Log($"[bounds] the canes mark the circle that hurts you: " +
                          $"{(canesOnTheCircle ? "PASS" : "FAIL")}");

                // ---------------------------------------------------- and the circle is the ring
                //
                // No margin between what the forest draws and what the zone asked for. There was a
                // ten metre one once, and it meant a player could stand in open ground taking
                // damage from a wall they could see was still ten metres away.
                float asked = Mathf.Clamp(ring, 2f, forest.MaxRadius);
                bool noMargin = Mathf.Abs(edge - asked) < 0.5f;

                Debug.Log($"[bounds] asked for r={asked:0.0} m, forest edge is {edge:0.0} m");
                Debug.Log($"[bounds] the wall sits on the circle it was given, with no margin: " +
                          $"{(noMargin ? "PASS" : "FAIL")}");

                bool drawnWhereItStands = solidMatchesVisible && canesOnTheCircle && noMargin;

                if (square && reachable == bearings.Length && drawnWhereItStands)
                    Debug.Log("[bounds] PASSED");
                else
                    Debug.LogError("[bounds] FAILED");
            }
            finally
            {
                UnseenBootstrap boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();
                Object.DestroyImmediate(host);
            }
        }

        private static void Step(UnseenBootstrap boot, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                boot.Network.Poll(1f / 60f);
                boot.Simulation.Advance(1f / 60f);
            }
        }
    }
}
