using UnityEditor;
using UnityEngine;
using Unseen.Client;
using Unseen.Core;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks that the third-person camera cannot be steered through a wall.
    ///
    /// Backed into a corner and swinging the view around, the camera used to end up outside the
    /// room - so a player could see who was waiting on the other side of a wall without exposing
    /// themselves. In a game whose entire currency is information that is not a camera artefact,
    /// it is a wallhack, and it is invisible in a screenshot because the frame it produces looks
    /// perfectly ordinary.
    ///
    /// The assertion is the one that matters and the only one that cannot be argued with: is there
    /// world geometry between the player's eye and the camera. It is swept over a full circle of
    /// yaw and a range of pitch at a spot chosen for being hard against a wall, because the bug
    /// only appeared at some angles.
    /// </summary>
    public static class UnseenCameraProbe
    {
        private static void Reach(ThirdPersonCameraRig rig, float distance)
        {
            typeof(ThirdPersonCameraRig)
                .GetField("_currentDistance", System.Reflection.BindingFlags.NonPublic |
                                              System.Reflection.BindingFlags.Instance)
                ?.SetValue(rig, distance);
        }

        private static void SetLook(PlayerInputSource input, float yaw, float pitch)
        {
            typeof(PlayerInputSource).GetProperty("Yaw")?.SetValue(input, yaw);
            typeof(PlayerInputSource).GetProperty("Pitch")?.SetValue(input, pitch);
        }

        [MenuItem("Unseen/Test Camera Collision", priority = 92)]
        public static void Run()
        {
            var host = new GameObject("CameraProbe");
            var rigHost = new GameObject("ProbeRig");
            var followHost = new GameObject("ProbeFollow");

            try
            {
                var generator = host.AddComponent<Unseen.Environment.GreyboxTownGenerator>();
                generator.Seed = 20260824;
                generator.Generate();

                var input = rigHost.AddComponent<PlayerInputSource>();
                ThirdPersonCameraRig rig = rigHost.AddComponent<ThirdPersonCameraRig>();
                rig.Input = input;
                rig.Follow = followHost.transform;

                // A spot hard against a TALL wall.
                //
                // The first version of this search accepted anything a chest-height ray hit within
                // twelve metres, which in this town is usually a barrel, a hedge or a gutter - the
                // camera sails over the top of those and the test passed against a wall that was
                // not there. A wall has to block at ankle, chest and head height to count.
                Vector3 stand = Vector3.zero;
                Vector3 into = Vector3.forward;
                bool found = false;

                var bearings = new[] { Vector3.forward, Vector3.back, Vector3.right, Vector3.left };

                for (int i = 0; i < 900 && !found; i++)
                {
                    float angle = i * 37f * Mathf.Deg2Rad;
                    float reach = 20f + i * 0.8f;
                    var from = new Vector3(Mathf.Sin(angle) * reach, 40f, Mathf.Cos(angle) * reach);

                    if (!Physics.Raycast(from, Vector3.down, out RaycastHit ground, 90f,
                            UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                        continue;

                    // Only flat ground: standing on a roof edge is a different test.
                    if (ground.normal.y < 0.9f) continue;

                    foreach (Vector3 dir in bearings)
                    {
                        var low = ground.point + Vector3.up * 0.5f;
                        var mid = ground.point + Vector3.up * 1.5f;
                        var high = ground.point + Vector3.up * 2.4f;

                        if (!Physics.Raycast(low, dir, out RaycastHit lowHit, 3f,
                                UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore)) continue;
                        if (!Physics.Raycast(mid, dir, out RaycastHit midHit, 3f,
                                UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore)) continue;
                        if (!Physics.Raycast(high, dir, 3f,
                                UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore)) continue;

                        // The three heights have to be the same surface, not three separate props.
                        if (Mathf.Abs(lowHit.distance - midHit.distance) > 0.4f) continue;

                        stand = ground.point + dir * Mathf.Max(0.1f, midHit.distance - 0.5f);
                        stand.y = ground.point.y;
                        into = dir;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    Debug.LogError("[camera] could not find a wall to stand against");
                    return;
                }

                Debug.Log($"[camera] standing at {stand}, facing a wall along {into}");
                followHost.transform.position = stand;

                int samples = Sweep(rig, rigHost, followHost, input, out int throughWall,
                    out float worstPenetration);

                Debug.Log($"[camera] {samples} view angles tested at a wall");
                Debug.Log($"[camera] angles with geometry between eye and camera: {throughWall} " +
                          $"(worst {worstPenetration:0.00} m past it)");

                // The counterfactual. Restoring the 2.4 m floor that used to be enforced regardless
                // of what the collision probe found should put the camera through the wall again -
                // if it does not, this test is not measuring what it claims to measure.
                rig.HardMinDistance = 2.4f;
                Sweep(rig, rigHost, followHost, input, out int oldThroughWall, out float oldWorst);
                rig.HardMinDistance = 0.28f;

                Debug.Log($"[camera] with the old 2.4 m floor: {oldThroughWall} angles through " +
                          $"geometry (worst {oldWorst:0.00} m past it)");

                bool testBites = oldThroughWall > 0;
                Debug.Log($"[camera] the check is capable of failing: {(testBites ? "PASS" : "FAIL")}");

                bool clean = throughWall == 0;
                Debug.Log($"[camera] the camera never sits through a wall: {(clean ? "PASS" : "FAIL")}");

                if (clean && testBites) Debug.Log("[camera] PASSED");
                else Debug.LogError("[camera] FAILED");
            }
            finally
            {
                Object.DestroyImmediate(followHost);
                Object.DestroyImmediate(rigHost);
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>Sweeps the view around and counts how often the camera ends up past geometry.</summary>
        private static int Sweep(ThirdPersonCameraRig rig, GameObject rigHost, GameObject followHost,
            PlayerInputSource input, out int throughWall, out float worstPenetration)
        {
                int samples = 0;
                throughWall = 0;
                worstPenetration = 0f;

                for (int yawStep = 0; yawStep < 36; yawStep++)
                {
                    for (int pitchStep = -2; pitchStep <= 2; pitchStep++)
                    {
                        // Yaw and Pitch are read-only to the rest of the game on purpose - only
                        // the input source may move the view - so the probe reaches in rather than
                        // widening that surface for a test's convenience.
                        SetLook(input, yawStep * 10f, pitchStep * 15f);

                        // Reset the reach to the full distance before every sample.
                        //
                        // Unity does not run Awake in edit mode, so the rig's reach starts at zero,
                        // and Time.deltaTime is ~0 here so its extend-outward smoothing never moves
                        // it. Left alone the camera sat exactly on the pivot for every sample and
                        // the test reported a clean sweep while measuring nothing at all - it
                        // passed identically with the bug reinstated, which is how it was caught.
                        //
                        // Forcing the reach makes each sample the real question: asked for the full
                        // distance at this angle, where does the rig actually put the camera?
                        Reach(rig, rig.Distance);
                        rig.SendMessage("LateUpdate", SendMessageOptions.DontRequireReceiver);

                        Vector3 pivot = followHost.transform.position + Vector3.up * rig.PivotHeight;
                        Vector3 camera = rigHost.transform.position;

                        samples++;

                        Vector3 delta = camera - pivot;
                        float reach = delta.magnitude;
                        if (reach < 0.01f) continue;

                        // The whole question: is there anything solid between the player's eye and
                        // where the camera has been put.
                        if (!Physics.Raycast(pivot, delta / reach, out RaycastHit blocking, reach,
                                UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                            continue;

                        float past = reach - blocking.distance;
                        if (past < 0.05f) continue;

                        throughWall++;
                        if (past > worstPenetration) worstPenetration = past;
                    }
                }

                return samples;
        }
    }
}
