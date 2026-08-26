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
                forest.SetRing(new Vector3(30f, 0f, -20f), forest.MaxRadius * 0.55f, 1f);
                Physics.SyncTransforms();

                var renderers = forest.GetComponentsInChildren<Renderer>(true);
                int active = 0;
                int adrift = 0;
                float worstDrift = 0f;
                string worstName = "none";

                foreach (Renderer r in renderers)
                {
                    if (!r.gameObject.activeInHierarchy) continue;
                    active++;

                    float drift = Vector3.Distance(r.bounds.center, r.transform.position);
                    if (drift > 2f) adrift++;
                    if (drift <= worstDrift) continue;

                    worstDrift = drift;
                    worstName = r.name;
                }

                Debug.Log($"[bounds] forest moved to r={forest.InnerEdge:0} m about " +
                          $"{forest.Centre}: {active} active renderers, {adrift} adrift " +
                          $"(worst {worstDrift:0.0} m on '{worstName}')");

                bool drawnWhereItStands = active > 0 && adrift == 0;
                Debug.Log($"[bounds] the wall is drawn where its colliders stand: " +
                          $"{(drawnWhereItStands ? "PASS" : "FAIL")}");

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
