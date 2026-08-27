using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks that a wall between two people actually muffles sound.
    ///
    /// This exists because of a number nobody could explain. The headless smoke run reported
    /// <c>paths traced 3542</c> and <c>sounds delivered 3542</c> - exactly equal - which means every
    /// single traced path survived to a listener and occlusion never once pushed a sound below the
    /// audibility floor. That is possible if agents only ever fight in open streets, and it is
    /// equally consistent with the acoustic raycasts missing the geometry entirely, in which case
    /// the whole stealth model is a distance falloff wearing a costume. It has been flagged as
    /// "verify before trusting" in the roadmap since the day it was written.
    ///
    /// So the model is put in a rig instead of watched in a crowd: two agents at a fixed distance
    /// on open ground, with walls placed between them by this test, and the delivered result read
    /// off the listener. The same distance is used every time, so distance falloff is held constant
    /// and the only thing changing is what is in the way.
    ///
    /// Both brains are switched off. The emitter's would walk it off its mark, and the listener's
    /// consumes its own Heard list every think - which is correct for a bot and would leave this
    /// test reading an empty list and concluding that nothing was ever delivered.
    /// </summary>
    public static class UnseenOcclusionProbe
    {
        private const float Gap = 12f;

        [MenuItem("Unseen/Probe Sound Occlusion", priority = 63)]
        public static void Run()
        {
            var host = new GameObject("OcclusionProbe");

            UnseenConfig config = UnseenConfig.Default;
            int roster = config.Match.TargetEntityCount;
            config.Match.TargetEntityCount = 4;

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.BuildNavMesh = false;   // nothing here paths; skip the bake
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260824;
                boot.Boot();

                Step(boot, 60 * 70);

                AgentEntity emitter = null;
                AgentEntity listener = null;

                foreach (AgentEntity agent in boot.Context.Entities.All)
                {
                    if (agent == null || !agent.IsAlive) continue;
                    if (emitter == null) { emitter = agent; continue; }
                    if (listener == null) { listener = agent; break; }
                }

                if (emitter == null || listener == null)
                {
                    Debug.LogError("[occlusion] need two live agents");
                    return;
                }

                if (emitter.Brain != null) emitter.Brain.enabled = false;
                if (listener.Brain != null) listener.Brain.enabled = false;

                if (!FindClearGround(out Vector3 from, out Vector3 to))
                {
                    Debug.LogError("[occlusion] found nowhere with twelve clear metres in it");
                    return;
                }

                Debug.Log($"[occlusion] rig on open ground: {from} to {to}, {Gap:0} m apart");

                // ---------------------------------------------------------- nothing in the way
                Measure(boot, emitter, listener, from, to, out float openIntensity,
                    out float openOcclusion, out int openHeard);

                bool carries = openHeard > 0 && openOcclusion < 0.05f && openIntensity > 0.2f;

                Debug.Log($"[occlusion] open ground: {openHeard} delivered, intensity " +
                          $"{openIntensity:0.000}, occlusion {openOcclusion:0.00}");
                Debug.Log($"[occlusion] sound carries across open ground: " +
                          $"{(carries ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- one solid wall
                //
                // The Occluder layer's default attenuation is 0.7, so the intensity that gets
                // through should be about a third of what it was.
                GameObject wall = Wall("StoneWall", from, to, UnseenLayers.Occluder, 0f);

                Measure(boot, emitter, listener, from, to, out float walledIntensity,
                    out float walledOcclusion, out int walledHeard);

                bool muffles = walledHeard > 0 &&
                               walledOcclusion > 0.5f &&
                               walledIntensity < openIntensity * 0.6f;

                Debug.Log($"[occlusion] one stone wall: an ordinary raycast on the same mask " +
                          $"finds {PlainHits(from, to)} blocker(s)");
                Debug.Log($"[occlusion] one stone wall: {walledHeard} delivered, intensity " +
                          $"{walledIntensity:0.000}, occlusion {walledOcclusion:0.00}");
                Debug.Log($"[occlusion] a wall muffles it: {(muffles ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- paper barely does
                //
                // A design claim worth pinning down rather than trusting: shoji hides sight and
                // barely touches sound, which is the whole reason the silhouette mechanic matters.
                // If paper muffled like stone, hiding behind a screen would be strictly better than
                // hiding behind a wall, and it is supposed to be strictly worse.
                Object.DestroyImmediate(wall);
                GameObject paper = Wall("ShojiScreen", from, to, UnseenLayers.ShojiPaper, 0f);

                Measure(boot, emitter, listener, from, to, out float paperIntensity,
                    out float paperOcclusion, out int paperHeard);

                bool paperIsThin = paperHeard > 0 && paperOcclusion < 0.3f &&
                                   paperIntensity > walledIntensity * 1.5f;

                Debug.Log($"[occlusion] one shoji screen: an ordinary raycast on the same mask " +
                          $"finds {PlainHits(from, to)} blocker(s)");
                Debug.Log($"[occlusion] one shoji screen: {paperHeard} delivered, intensity " +
                          $"{paperIntensity:0.000}, occlusion {paperOcclusion:0.00}");
                Debug.Log($"[occlusion] paper hides sight, not sound: " +
                          $"{(paperIsThin ? "PASS" : "FAIL")}");

                // ---------------------------------------------------------- enough of it silences
                //
                // THE point of this probe. Occlusion saturates at 1, which takes the delivered
                // intensity to zero and drops the sound below the audibility floor entirely - and
                // the smoke run's traced-equals-delivered says that has never once happened. Three
                // stone walls is 2.1 of attenuation, so if it does not happen here it cannot happen.
                Object.DestroyImmediate(paper);

                var walls = new GameObject[3];
                for (int i = 0; i < 3; i++)
                    walls[i] = Wall($"Thick_{i}", from, to, UnseenLayers.Occluder,
                        (i - 1) * 1.2f);

                Measure(boot, emitter, listener, from, to, out float deafIntensity,
                    out float deafOcclusion, out int deafHeard);

                bool silences = deafHeard == 0;

                Debug.Log($"[occlusion] three stone walls: an ordinary raycast on the same mask " +
                          $"finds {PlainHits(from, to)} blocker(s)");
                Debug.Log($"[occlusion] three stone walls: {deafHeard} delivered, intensity " +
                          $"{deafIntensity:0.000}, occlusion {deafOcclusion:0.00}");
                Debug.Log($"[occlusion] enough wall drops the sound entirely: " +
                          $"{(silences ? "PASS" : "FAIL")}");

                for (int i = 0; i < walls.Length; i++) Object.DestroyImmediate(walls[i]);

                if (carries && muffles && paperIsThin && silences)
                    Debug.Log("[occlusion] PASSED");
                else
                    Debug.LogError("[occlusion] FAILED");
            }
            finally
            {
                config.Match.TargetEntityCount = roster;

                UnseenBootstrap boot = host.GetComponent<UnseenBootstrap>();
                if (boot != null) boot.Shutdown();
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// Emits a sound from one agent for a few ticks and reports the loudest thing the other one
        /// was handed.
        ///
        /// Driven for several ticks and taking the maximum rather than reading once, because the
        /// sound bus holds a single tick and the order of the emit against the propagation system
        /// inside one Advance is not something this test should depend on.
        /// </summary>
        private static void Measure(UnseenBootstrap boot, AgentEntity emitter, AgentEntity listener,
            Vector3 from, Vector3 to, out float intensity, out float occlusion, out int delivered)
        {
            intensity = 0f;
            occlusion = 0f;
            delivered = 0;

            listener.Heard.Clear();

            for (int i = 0; i < 12; i++)
            {
                emitter.Motor.Teleport(new float3(from.x, from.y, from.z));
                listener.Motor.Teleport(new float3(to.x, to.y, to.z));
                Physics.SyncTransforms();

                // Loud, and with plenty of radius, so nothing here is a distance failure dressed
                // up as an occlusion result.
                boot.Context.Sound.Emit(emitter.Id, emitter.EyePosition, SoundKind.WeaponSwing,
                    1f, 60f, boot.Context.Tick);

                boot.Network.Poll(1f / 60f);
                boot.Simulation.Advance(1f / 60f);

                foreach (HeardSound heard in listener.Heard)
                {
                    if (heard.Kind != SoundKind.WeaponSwing) continue;

                    delivered++;
                    if (heard.Intensity <= intensity) continue;

                    intensity = heard.Intensity;
                    occlusion = heard.Occlusion;
                }

                listener.Heard.Clear();
            }
        }

        /// <summary>
        /// Whether an ORDINARY raycast, on the same mask the acoustic model uses, is blocked
        /// between the two ears.
        ///
        /// The control for the whole probe. If this says blocked and the model still reports zero
        /// occlusion, the fault is in the model rather than in the wall this test put there.
        /// </summary>
        private static int PlainHits(Vector3 from, Vector3 to)
        {
            Vector3 earA = from + Vector3.up * 1.6f;
            Vector3 earB = to + Vector3.up * 1.6f;

            var hits = new RaycastHit[8];
            Vector3 delta = earB - earA;

            return Physics.RaycastNonAlloc(earA, delta.normalized, hits, delta.magnitude,
                UnseenLayers.SoundBlockers, QueryTriggerInteraction.Ignore);
        }

        /// <summary>A wall across the line between the two marks, offset along it.</summary>
        private static GameObject Wall(string name, Vector3 from, Vector3 to, int layer,
            float offset)
        {
            Vector3 along = to - from;
            along.y = 0f;
            float span = along.magnitude;
            along = along.normalized;

            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = name;
            wall.layer = layer;

            wall.transform.position = from + along * (span * 0.5f + offset) + Vector3.up * 2f;
            wall.transform.rotation = Quaternion.LookRotation(along, Vector3.up);
            wall.transform.localScale = new Vector3(10f, 4f, 0.4f);

            // Push the new transform into the physics scene by hand.
            //
            // Unity does not auto-sync transforms in edit mode, so a collider created and then
            // positioned is still sitting at the origin as far as any raycast is concerned. The
            // first run of this probe reported zero occlusion through three stone walls and looked
            // exactly like the acoustic model being broken - the walls were simply not there yet.
            Physics.SyncTransforms();

            return wall;
        }

        /// <summary>
        /// Two marks twelve metres apart on dry, open, level ground with nothing between them.
        ///
        /// The baseline reading has to come from a genuinely clear line or every later comparison is
        /// against a number that already had a building in it.
        /// </summary>
        private static bool FindClearGround(out Vector3 from, out Vector3 to)
        {
            from = Vector3.zero;
            to = Vector3.zero;

            for (int i = 0; i < 400; i++)
            {
                float angle = i * 41f * Mathf.Deg2Rad;
                float reach = 25f + i * 1.3f;
                var above = new Vector3(Mathf.Sin(angle) * reach, 70f, Mathf.Cos(angle) * reach);

                if (!Physics.Raycast(above, Vector3.down, out RaycastHit ground, 110f,
                        UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                    continue;

                if (ground.normal.y < 0.98f) continue;

                var a = ground.point + Vector3.up * 0.1f;
                if (WaterVolume.DepthAt(new float3(a.x, a.y, a.z)) > 0.01f) continue;

                var b = a + new Vector3(0f, 0f, Gap);

                if (!Physics.Raycast(b + Vector3.up * 40f, Vector3.down, out RaycastHit far, 80f,
                        UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                    continue;

                if (Mathf.Abs(far.point.y - ground.point.y) > 0.3f) continue;
                if (WaterVolume.DepthAt(new float3(b.x, far.point.y, b.z)) > 0.01f) continue;

                // Clear at ear height, both ways, so neither a wall nor a lantern post is in it.
                Vector3 earA = a + Vector3.up * 1.6f;
                Vector3 earB = b + Vector3.up * 1.6f;

                if (Physics.Linecast(earA, earB, UnseenLayers.WorldGeometry,
                        QueryTriggerInteraction.Ignore))
                    continue;

                from = a;
                to = b;
                return true;
            }

            return false;
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
