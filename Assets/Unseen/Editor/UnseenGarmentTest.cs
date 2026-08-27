using UnityEditor;
using UnityEngine;
using Unseen.Entities;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks the cloth a ninja is wearing.
    ///
    /// Four things can go wrong with garments bolted onto an imported rig, and all four are silent
    /// in a screenshot of a standing figure:
    ///
    ///   - The bones are not found, and the body comes out bare.
    ///   - The rig's baked scale is not cancelled, and a thirteen-centimetre sash is built thirteen
    ///     metres wide - or, worse, thirteen millimetres, which just looks like nothing at all.
    ///   - A collider comes along for the ride, and decoration starts changing line of sight,
    ///     parkour probes and physics.
    ///   - The hanging pieces never move, and the figure runs with a plank bolted to its hips.
    ///
    /// The last one is the reason the simulation takes its own clock instead of reading
    /// Time.deltaTime: Unity runs no LateUpdate in edit mode, so cloth that could only be advanced
    /// by the game loop could only be judged by playing the game and squinting at it.
    /// </summary>
    public static class UnseenGarmentTest
    {
        [MenuItem("Unseen/Test Ninja Garments", priority = 99)]
        public static void Run()
        {
            AgentVisualSet set = AgentVisualSet.Load();

            if (set == null || !set.IsUsable)
            {
                Debug.LogError("[garments] no agent visual set - run Unseen > Art > Build Ninja Character");
                return;
            }

            if (set.Cloth == null)
            {
                Debug.LogError("[garments] the visual set has no cloth material");
                return;
            }

            var host = new GameObject("GarmentTest");

            try
            {
                AgentVisual visual = set.Attach(host.transform, 3);

                if (visual == null)
                {
                    Debug.LogError("[garments] the body would not attach");
                    return;
                }

                var garments = visual.GetComponent<NinjaGarments>();

                bool dressed = garments != null && garments.StrandCount == 3;
                Debug.Log($"[garments] {(garments == null ? 0 : garments.StrandCount)} hanging " +
                          $"pieces (two sash tails and a scarf)");
                Debug.Log($"[garments] the body is dressed: {(dressed ? "PASS" : "FAIL")}");

                if (garments == null) { Debug.LogError("[garments] FAILED"); return; }

                // ---------------------------------------------------------- every limb dressed
                //
                // Checked bone by bone rather than by counting pieces. Every band is placed by a
                // name lookup on the rig, so a body whose shin bone is not called "LeftLeg" comes
                // out in a sash and bare legs - which a total would hide, and which is exactly the
                // failure an imported character is most likely to have.
                var renderers = visual.GetComponentsInChildren<MeshRenderer>(true);

                string[] dressed_bones =
                {
                    "Hips", "Neck", "LeftLeg", "RightLeg", "LeftForeArm", "RightForeArm"
                };

                bool complete = true;
                var report = new System.Text.StringBuilder();

                foreach (string bone in dressed_bones)
                {
                    int on = 0;
                    for (int i = 0; i < renderers.Length; i++)
                        if (Under(renderers[i].transform, bone)) on++;

                    if (on == 0) complete = false;
                    report.Append($"{bone}:{on} ");
                }

                Debug.Log($"[garments] pieces per bone - {report}");
                Debug.Log($"[garments] every limb was dressed: {(complete ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- the size of it
                //
                // The failure this catches is a rig with scale baked into its bones. A band
                // measured in metres and parented to a bone scaled by a hundred is a hundred times
                // too big, and every other check here would still pass.
                Bounds cloth = default;
                bool any = false;

                for (int i = 0; i < renderers.Length; i++)
                {
                    if (!any) { cloth = renderers[i].bounds; any = true; continue; }
                    cloth.Encapsulate(renderers[i].bounds);
                }

                Vector3 size = any ? cloth.size : Vector3.zero;

                // A dressed figure is under two metres tall and under two wide - the arms are out
                // in the bind pose, which is what the forearm wraps are measured across.
                bool human = any &&
                             size.y > 0.6f && size.y < 2.4f &&
                             size.x > 0.2f && size.x < 2.4f &&
                             size.z > 0.05f && size.z < 1.2f;

                Debug.Log($"[garments] cloth spans {size.x:0.00} x {size.y:0.00} x {size.z:0.00} m");
                Debug.Log($"[garments] it is the size of clothing: {(human ? "PASS" : "FAIL")}");

                // And it is ON the body rather than beside it.
                Bounds body = visual.Body != null ? visual.Body.bounds : default;
                bool worn = visual.Body != null && body.Intersects(cloth) &&
                            Vector3.Distance(body.center, cloth.center) < 0.6f;

                Debug.Log($"[garments] cloth centre is {(visual.Body == null ? -1f : Vector3.Distance(body.center, cloth.center)):0.00} m from the body's");
                Debug.Log($"[garments] it is worn, not dropped nearby: {(worn ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- decoration only
                var colliders = visual.GetComponentsInChildren<Collider>(true);
                bool inert = colliders.Length == 0;

                Debug.Log($"[garments] {colliders.Length} colliders on the dressed body");
                Debug.Log($"[garments] cloth cannot change how the game plays: " +
                          $"{(inert ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- it actually moves
                //
                // Standing still first, so the tails settle to their resting hang, then sprinting
                // forward. The scarf is the last strand and the lightest, so it is the one that
                // should swing furthest.
                const float dt = 1f / 60f;
                float clock = 0f;

                for (int i = 0; i < 240; i++, clock += dt)
                    garments.Step(dt, Vector3.zero, clock);

                Vector3 resting = garments.TipDirection(2);
                float restAngle = Vector3.Angle(resting, Vector3.down);

                for (int i = 0; i < 120; i++, clock += dt)
                    garments.Step(dt, host.transform.forward * 7f, clock);

                Vector3 running = garments.TipDirection(2);
                float runAngle = Vector3.Angle(running, Vector3.down);

                // Hanging within a few degrees of straight down when still, and thrown well off it
                // when sprinting. A rigid tail scores the same angle twice.
                bool hangs = restAngle < 25f;
                bool streams = runAngle - restAngle > 35f;

                Debug.Log($"[garments] scarf tip: {restAngle:0}deg from vertical standing, " +
                          $"{runAngle:0}deg at a sprint");
                Debug.Log($"[garments] it hangs when still: {(hangs ? "PASS" : "FAIL")}");
                Debug.Log($"[garments] it streams when running: {(streams ? "PASS" : "FAIL")}");

                // And it trails BEHIND, not in front. A sign error here would look deliberate and
                // be completely wrong.
                float behind = Vector3.Dot(running.normalized, -host.transform.forward);
                bool trails = behind > 0.2f;

                Debug.Log($"[garments] trailing component {behind:0.00}");
                Debug.Log($"[garments] it trails behind the runner: {(trails ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- and it settles back
                for (int i = 0; i < 240; i++, clock += dt)
                    garments.Step(dt, Vector3.zero, clock);

                float settled = Vector3.Angle(garments.TipDirection(2), Vector3.down);
                bool settles = settled < 25f;

                Debug.Log($"[garments] back to {settled:0}deg after stopping");
                Debug.Log($"[garments] it settles rather than staying flung out: " +
                          $"{(settles ? "PASS" : "FAIL")}");

                if (dressed && complete && human && worn && inert && hangs && streams && trails &&
                    settles)
                    Debug.Log("[garments] PASSED");
                else
                    Debug.LogError("[garments] FAILED");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>Whether a piece hangs somewhere below a named bone.</summary>
        private static bool Under(Transform piece, string bone)
        {
            for (Transform t = piece; t != null; t = t.parent)
                if (t.name == bone) return true;

            return false;
        }
    }
}
