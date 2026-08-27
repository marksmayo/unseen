using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Environment;

namespace Unseen.Combat
{
    /// <summary>
    /// A shuriken lying on the ground, and the star that gets drawn in flight.
    ///
    /// A missed throw does not disappear. It lands where it fell and anyone can pick it up,
    /// including the person it was aimed at - which is what stops a ranged attack from being free.
    /// Missing does not merely waste the blade, it may arm your target.
    ///
    /// Kept as a static pool rather than components on the agents, because a dropped blade belongs
    /// to nobody and outlives whoever threw it.
    /// </summary>
    public static class ShurikenPickup
    {
        private struct Dropped
        {
            public float3 Position;
            public float AvailableAt;
            public Transform Visual;
        }

        private static readonly List<Dropped> Lying = new List<Dropped>(64);
        private static Transform _root;
        private static Mesh _mesh;
        private static Material _material;

        /// <summary>How many blades are lying about. Watched by the tests.</summary>
        public static int Count => Lying.Count;

        /// <summary>Leaves a blade on the ground, pickable once the time has passed.</summary>
        public static void Drop(float3 at, float availableAt)
        {
            Transform visual = CreateBlade();
            if (visual != null)
            {
                visual.position = at;

                // Lying flat, tilted a little, as a thrown thing does.
                visual.rotation = Quaternion.Euler(88f, at.x * 37f % 360f, 0f);
            }

            Lying.Add(new Dropped { Position = at, AvailableAt = availableAt, Visual = visual });
        }

        /// <summary>
        /// Takes the nearest available blade within reach, if there is one. Returns false when
        /// there is nothing to pick up.
        /// </summary>
        public static bool TryTake(float3 from, float radius, float now)
        {
            int best = -1;
            float bestDistance = radius * radius;

            for (int i = 0; i < Lying.Count; i++)
            {
                if (now < Lying[i].AvailableAt) continue;

                // Horizontal only. A blade on the roof above your head is not within reach, and a
                // straight distance check makes it so.
                float3 offset = Lying[i].Position - from;
                if (math.abs(offset.y) > 2f) continue;

                offset.y = 0f;
                float distance = math.lengthsq(offset);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = i;
            }

            if (best < 0) return false;

            if (Lying[best].Visual != null) Object.Destroy(Lying[best].Visual.gameObject);
            Lying.RemoveAt(best);
            return true;
        }

        /// <summary>Where a dropped blade is lying. For tools and tests; the game does not need it.</summary>
        public static bool TryPeek(out float3 at)
        {
            at = default;
            if (Lying.Count == 0) return false;

            at = Lying[0].Position;
            return true;
        }

        public static void ClearAll()
        {
            for (int i = 0; i < Lying.Count; i++)
                if (Lying[i].Visual != null) Object.Destroy(Lying[i].Visual.gameObject);

            Lying.Clear();
        }

        /// <summary>
        /// The star itself: four points on a hub, built once and shared.
        ///
        /// Small enough that geometry beyond this would be invisible - a shuriken is fifteen
        /// centimetres across and is either flying past at thirty metres a second or lying in the
        /// gravel.
        /// </summary>
        public static Transform CreateBlade()
        {
            if (_root == null)
            {
                var host = new GameObject("Shuriken");
                _root = host.transform;
            }

            var blade = new GameObject("Blade");
            blade.transform.SetParent(_root, false);

            for (int i = 0; i < 4; i++)
            {
                var point = new GameObject($"Point_{i}");
                point.transform.SetParent(blade.transform, false);
                point.transform.localRotation = Quaternion.Euler(0f, 0f, i * 90f);
                point.transform.localScale = new Vector3(0.035f, 0.075f, 0.008f);
                point.transform.localPosition = point.transform.localRotation * new Vector3(0f, 0.05f, 0f);

                point.AddComponent<MeshFilter>().sharedMesh = BoxMeshFactory.Get(Vector3.one, 1f);

                var renderer = point.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = BladeMaterial();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            return blade.transform;
        }

        private static Material BladeMaterial()
        {
            if (_material != null) return _material;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");

            _material = new Material(shader) { name = "ShurikenSteel" };

            if (_material.HasProperty("_BaseColor"))
                _material.SetColor("_BaseColor", new Color(0.42f, 0.44f, 0.47f));
            if (_material.HasProperty("_Smoothness"))
                _material.SetFloat("_Smoothness", 0.72f);
            if (_material.HasProperty("_Metallic"))
                _material.SetFloat("_Metallic", 0.85f);

            return _material;
        }
    }
}
