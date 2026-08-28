using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Unseen.Environment
{
    /// <summary>
    /// A volume of smoke. Inside it agents gain a large stealth bonus, which in turn shortens the
    /// range at which anyone can resolve them - the same mechanism as standing in a dark room.
    /// </summary>
    public sealed class SmokeCloud : MonoBehaviour
    {
        private static readonly List<SmokeCloud> Clouds = new List<SmokeCloud>(32);

        public float Radius = 6f;
        public float Duration = 8f;

        [Tooltip("Seconds spent expanding to full radius.")]
        public float GrowDuration = 0.6f;

        private float _age;

        /// <summary>
        /// Whether a cloud builds itself something to look at.
        ///
        /// Turned off on a dedicated server, which needs the volume for its stealth maths and has
        /// nobody to show it to. Everywhere else this is on, because a smoke bomb you cannot see is
        /// a smoke bomb that does not work: the point of throwing one is that you and the person
        /// hunting you can both tell where the cover is.
        /// </summary>
        public static bool BuildVisuals = true;

        /// <summary>One quad, shared by every puff of every cloud.</summary>
        private static Mesh _quadMesh;

        /// <summary>Per cloud, because it fades - and the SRP batcher ignores property blocks.</summary>
        private Material _material;

        private Transform[] _puffs;

        public static IReadOnlyList<SmokeCloud> All => Clouds;
        public float CurrentRadius => Radius * Mathf.Clamp01(GrowDuration <= 0f ? 1f : _age / GrowDuration);

        private void OnEnable()
        {
            EnsureRegistered();
            EnsureVisual();
        }

        /// <summary>
        /// Joins the cloud registry. Safe to call more than once.
        ///
        /// Explicit rather than left to OnEnable, which is the same trap the lanterns and the loot
        /// containers already carry an EnsureRegistered for. A cloud built outside play mode gets
        /// no lifecycle callback, so Covers saw an empty list while two clouds sat in the scene -
        /// which is a test that cannot see a working mechanic, and a mechanic that quietly stops
        /// working anywhere else a cloud is made without Unity deciding to enable it for us.
        /// </summary>
        public void EnsureRegistered()
        {
            if (!Clouds.Contains(this)) Clouds.Add(this);
        }

        private void OnDisable()
        {
            Clouds.Remove(this);

            // The material is a per-cloud copy, so it has to go with the cloud.
            if (_material != null)
            {
                Destroy(_material);
                _material = null;
            }
        }

        private void Update()
        {
            Advance(Time.deltaTime);
        }

        /// <summary>
        /// Ages the cloud by one step, growing it, thinning it, and clearing it when it is spent.
        ///
        /// Separate from Update so that a test can drive it. Update does not run outside play mode,
        /// so a probe stepping the simulation by hand had no way to watch a cloud expire.
        /// </summary>
        public void Advance(float dt)
        {
            _age += dt;

            UpdateVisual(dt);

            if (_age < Duration) return;

            // Off the registry first, then destroyed.
            //
            // Destroy does not take the object away until the end of the frame, so a cloud that
            // only called Destroy went on being counted as cover for the rest of that frame - and
            // outside play mode it went on being cover for ever. Leaving the list on the tick it
            // expires makes the moment it stops working exact.
            Clouds.Remove(this);
            Destroy(gameObject);
        }

        /// <summary>
        /// Builds the cloud out of a handful of soft panels.
        ///
        /// There is no smoke prefab in the project and the field that would hold one is empty in
        /// the scene, so Spawn was falling back to a bare GameObject: the volume existed, agents
        /// inside it really were harder to see, and absolutely nothing appeared. From where the
        /// player stands that is indistinguishable from a broken item.
        ///
        /// Built here rather than as an asset because the whole town is built this way, and because
        /// a cloud that constructs itself cannot be shipped unassigned.
        ///
        /// Crossed panels rather than camera-facing billboards: the server spawns these too and has
        /// no camera to face, and a fixed cross of quads with a soft radial alpha reads as a volume
        /// from any angle without needing to know where anybody is standing.
        /// </summary>
        public void EnsureVisual()
        {
            if (!BuildVisuals || _puffs != null) return;

            // A prefab that came with its own renderers keeps them.
            if (GetComponentInChildren<Renderer>() != null) return;

            Shader shader = Shader.Find("Unseen/GroundMist");
            if (shader == null) return;

            _material = new Material(shader) { name = "SmokeCloud" };

            // Paler and denser than street mist. Mist is scenery you look through; smoke is cover
            // you hide behind, and it has to read as the thicker of the two at a glance.
            if (_material.HasProperty("_Tint"))
                _material.SetColor("_Tint", new Color(0.78f, 0.80f, 0.84f, 1f));
            if (_material.HasProperty("_Density")) _material.SetFloat("_Density", 1.15f);
            if (_material.HasProperty("_Speed")) _material.SetFloat("_Speed", 0.12f);
            if (_material.HasProperty("_Scale")) _material.SetFloat("_Scale", 0.14f);

            // No near fade. The mist version thins as you walk into a panel so it does not paint
            // the screen grey; smoke painting the screen grey when you are inside it is the effect.
            if (_material.HasProperty("_NearFade")) _material.SetFloat("_NearFade", 0f);

            const int puffs = 7;
            _puffs = new Transform[puffs];

            for (int i = 0; i < puffs; i++)
            {
                var host = new GameObject($"Puff_{i}");
                host.transform.SetParent(transform, false);

                // Scattered inside the sphere and turned every which way, seeded off the cloud
                // position so that two clouds are not the same cloud.
                float t = i / (float)puffs;
                float yaw = t * 360f + transform.position.x * 7f;
                float pitch = (i % 3) * 60f + transform.position.z * 3f;

                host.transform.localRotation = Quaternion.Euler(pitch, yaw, yaw * 0.5f);
                host.transform.localPosition = Quaternion.Euler(0f, yaw, 0f) *
                                               new Vector3(0f, (t - 0.5f) * 0.5f, t * 0.35f);

                host.AddComponent<MeshFilter>().sharedMesh = QuadMesh();
                MeshRenderer renderer = host.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = _material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                _puffs[i] = host.transform;
            }
        }

        private void UpdateVisual(float dt)
        {
            if (_puffs == null) return;

            // Grows with the volume, so what you see is where the cover actually is. A cloud drawn
            // at full size while its radius was still ramping would promise cover it did not give.
            float r = CurrentRadius;

            for (int i = 0; i < _puffs.Length; i++)
            {
                Transform puff = _puffs[i];
                if (puff == null) continue;

                puff.localScale = Vector3.one * (r * 2.1f);
                puff.Rotate(0f, 0f, 6f * dt, Space.Self);
            }

            // Thins out over the last third of its life rather than vanishing, so the moment cover
            // stops working is something you can watch happen instead of a surprise.
            if (_material == null || Duration <= 0f) return;

            float left = 1f - Mathf.Clamp01(_age / Duration);
            float fade = Mathf.Clamp01(left / 0.34f);

            if (_material.HasProperty("_Density")) _material.SetFloat("_Density", 1.15f * fade);
        }

        /// <summary>A two-sided unit quad. One mesh for every puff in the game.</summary>
        private static Mesh QuadMesh()
        {
            if (_quadMesh != null) return _quadMesh;

            _quadMesh = new Mesh { name = "SmokePuffQuad" };

            _quadMesh.SetVertices(new List<Vector3>
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f)
            });

            _quadMesh.SetUVs(0, new List<Vector2>
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)
            });

            // Both windings, so a panel is visible from either side without a two-pass shader.
            _quadMesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 }, 0);
            _quadMesh.RecalculateNormals();
            _quadMesh.RecalculateBounds();

            return _quadMesh;
        }

        public static bool Covers(float3 point)
        {
            for (int i = 0; i < Clouds.Count; i++)
            {
                SmokeCloud c = Clouds[i];
                if (c == null) continue;
                float r = c.CurrentRadius;
                if (math.distancesq(point, (float3)c.transform.position) <= r * r) return true;
            }

            return false;
        }

        public static SmokeCloud Spawn(GameObject prefab, float3 position, float radius, float duration)
        {
            // One bomb, one cloud.
            //
            // On a listen server the same throw arrives twice: CombatDirector spawns a cloud
            // server side, and the local client spawns another from the replicated SmokeSpawned
            // event. Both land on the same spot, so the result was two stacked clouds - twice the
            // panels and twice the opacity of the thing that was tuned, on the configuration most
            // people actually play. Coalescing here also absorbs a duplicated or replayed event.
            for (int i = 0; i < Clouds.Count; i++)
            {
                SmokeCloud existing = Clouds[i];
                if (existing == null) continue;
                if (math.distancesq(position, (float3)existing.transform.position) > 0.5625f) continue;

                existing.Radius = Mathf.Max(existing.Radius, radius);
                existing.Duration = Mathf.Max(existing.Duration, duration);
                return existing;
            }

            GameObject go = prefab != null
                ? Instantiate(prefab, position, Quaternion.identity)
                : new GameObject("SmokeCloud");

            go.transform.position = position;
            SmokeCloud cloud = go.GetComponent<SmokeCloud>();
            if (cloud == null) cloud = go.AddComponent<SmokeCloud>();
            cloud.Radius = radius;
            cloud.Duration = duration;

            // Registered and built here rather than trusting OnEnable to have fired. Radius and
            // Duration are also only correct now, so a visual built during AddComponent would have
            // been sized off the defaults.
            cloud.EnsureRegistered();
            cloud.EnsureVisual();

            return cloud;
        }
    }
}
