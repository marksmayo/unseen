using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;

namespace Unseen.Environment
{
    /// <summary>
    /// Marks left in raked gravel: footprints from a walking body, a dragged trough from a crawling
    /// one.
    ///
    /// Pooled and capped. A sixty-four player match crossing gardens for fifteen minutes would
    /// otherwise be tens of thousands of GameObjects, and the oldest print is the least interesting
    /// one on the board - so when the pool is full the oldest is reused, which is also exactly what
    /// "the gardener has been round" looks like.
    ///
    /// Prints fade by SHRINKING rather than by going transparent. Alpha would need per-instance
    /// material data, and the SRP batcher silently ignores per-renderer overrides of anything
    /// declared in UnityPerMaterial - a lesson this project has already paid for once. Scale is on
    /// the transform, costs nothing, works with the opaque gravel material every bed already uses,
    /// and reads correctly: a print in sand does not go see-through, it fills in.
    /// </summary>
    public static class Footprints
    {
        private struct Mark
        {
            public Transform Visual;
            public float3 Position;
            public float3 Forward;
            public float Age;
            public float Life;
            public float Size;
            public bool Crawl;
            public AgentId Owner;
        }

        /// <summary>
        /// Most marks in the world at once.
        ///
        /// Sized for a garden's worth of traffic rather than a match's. Two hundred is about eight
        /// full crossings of a bed, which is more history than anybody can read anyway.
        /// </summary>
        public const int Capacity = 200;

        private static readonly List<Mark> Marks = new List<Mark>(Capacity);
        private static Transform _root;
        private static Material _material;
        private static Mesh _mesh;

        /// <summary>How many marks are currently in the ground. Diagnostics and tests.</summary>
        public static int Count => Marks.Count;

        /// <summary>
        /// Presses a mark into the gravel.
        ///
        /// A walking print is small and turned to face the way the body is going. A crawl mark is
        /// long, wide and shallow - a body being dragged along the ground moves far more gravel
        /// than a foot does, which is the whole reason going prone across a garden is a worse idea
        /// than walking across it, not a better one.
        /// </summary>
        public static void Press(float3 at, float3 forward, bool crawl, AgentId owner,
            float life, Material gravel)
        {
            EnsureRoot(gravel);

            var mark = new Mark
            {
                Position = at,
                Forward = math.normalizesafe(forward, new float3(0f, 0f, 1f)),
                Age = 0f,
                Life = math.max(1f, life),
                Size = crawl ? 1f : 0.34f,
                Crawl = crawl,
                Owner = owner
            };

            if (Marks.Count >= Capacity)
            {
                // Reuse the oldest rather than allocate. It is also the one nobody would have
                // learned anything from.
                mark.Visual = Marks[0].Visual;
                Marks.RemoveAt(0);
            }
            else
            {
                mark.Visual = Create();
            }

            Marks.Add(mark);
            Place(mark);
        }

        /// <summary>
        /// Ages every mark, filling them back in as they go.
        ///
        /// Driven from the simulation rather than from Update, for the same reason the critters
        /// are: Unity runs no lifecycle callbacks in edit mode, and a print that could only be
        /// advanced by the game loop could only be judged by playing the game and looking at it.
        /// </summary>
        public static void Advance(float dt)
        {
            if (dt <= 0f) return;

            for (int i = Marks.Count - 1; i >= 0; i--)
            {
                Mark mark = Marks[i];
                mark.Age += dt;

                if (mark.Age >= mark.Life)
                {
                    if (mark.Visual != null) mark.Visual.gameObject.SetActive(false);
                    Marks.RemoveAt(i);
                    continue;
                }

                Marks[i] = mark;
                Place(mark);
            }
        }

        /// <summary>
        /// The freshest mark near a point, in seconds since it was pressed, or -1 for none.
        ///
        /// Nothing reads this yet. It exists because a trail across a garden is information and the
        /// only reason a bot cannot follow one today is that nobody has wired it to the blackboard -
        /// not because the information is missing.
        /// </summary>
        public static float FreshestNear(float3 point, float radius, out float3 heading)
        {
            heading = new float3(0f, 0f, 1f);

            float best = -1f;
            float radiusSq = radius * radius;

            for (int i = 0; i < Marks.Count; i++)
            {
                if (math.distancesq(Marks[i].Position, point) > radiusSq) continue;

                float age = Marks[i].Age;
                if (best >= 0f && age >= best) continue;

                best = age;
                heading = Marks[i].Forward;
            }

            return best;
        }

        public static void ClearAll()
        {
            for (int i = 0; i < Marks.Count; i++)
                if (Marks[i].Visual != null) UnseenObject.Destroy(Marks[i].Visual.gameObject);

            Marks.Clear();
        }

        // ------------------------------------------------------------------ drawing

        private static void Place(in Mark mark)
        {
            if (mark.Visual == null) return;

            // Filling in over its life. Never quite to nothing before it is removed, so a print
            // does not spend its last second as an invisible sliver.
            float remaining = 1f - mark.Age / mark.Life;
            float fill = 0.25f + 0.75f * remaining;

            Transform t = mark.Visual;

            t.position = (Vector3)mark.Position + Vector3.up * 0.005f;
            t.rotation = Quaternion.LookRotation((Vector3)mark.Forward, Vector3.up);

            // A crawl mark is a trough: long, wide and shallow. A footprint is small and deeper
            // relative to its size, which is what makes a line of them read as steps rather than
            // as a smear.
            t.localScale = mark.Crawl
                ? new Vector3(0.72f * fill, 1f, 1.5f * fill)
                : new Vector3(0.17f * fill, 1f, 0.32f * fill);
        }

        private static Transform Create()
        {
            var mark = new GameObject("Print");
            mark.transform.SetParent(_root, false);
            mark.layer = UnseenLayers.Decoration;

            mark.AddComponent<MeshFilter>().sharedMesh = PrintMesh();

            var renderer = mark.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;

            // Never a shadow caster. A depression a centimetre deep casting a shadow would be
            // absurd, and there can be two hundred of them.
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            return mark.transform;
        }

        private static void EnsureRoot(Material gravel)
        {
            if (_root == null)
            {
                var host = new GameObject("Footprints");
                _root = host.transform;
            }

            if (_material != null || gravel == null) return;

            // The gravel's own material, darkened. A print is the same stone with the raked top
            // scuffed off it and a shadow in the hollow - not a different substance.
            _material = new Material(gravel) { name = "GravelScuff" };

            var tint = new Color(0.32f, 0.32f, 0.33f);

            if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", tint);
            if (_material.HasProperty("_Color")) _material.SetColor("_Color", tint);
            if (_material.HasProperty("_Smoothness")) _material.SetFloat("_Smoothness", 0.1f);
        }

        /// <summary>
        /// A shallow dish: a flat quad with its edges dropped, so the mark has a rim and reads as
        /// pressed into the surface rather than painted on top of it.
        /// </summary>
        private static Mesh PrintMesh()
        {
            if (_mesh != null) return _mesh;

            _mesh = new Mesh { name = "GravelPrint" };

            var vertices = new List<Vector3>(9);
            var uvs = new List<Vector2>(9);
            var triangles = new List<int>(24);

            // A three by three grid: the middle sits lower than the rim.
            for (int z = 0; z < 3; z++)
            for (int x = 0; x < 3; x++)
            {
                float u = x * 0.5f;
                float v = z * 0.5f;
                bool centre = x == 1 && z == 1;

                vertices.Add(new Vector3(u - 0.5f, centre ? -0.045f : 0f, v - 0.5f));
                uvs.Add(new Vector2(u, v));
            }

            for (int z = 0; z < 2; z++)
            for (int x = 0; x < 2; x++)
            {
                int i0 = z * 3 + x;
                int i1 = i0 + 1;
                int i2 = i0 + 3;
                int i3 = i2 + 1;

                triangles.Add(i0); triangles.Add(i2); triangles.Add(i1);
                triangles.Add(i1); triangles.Add(i2); triangles.Add(i3);
            }

            _mesh.SetVertices(vertices);
            _mesh.SetUVs(0, uvs);
            _mesh.SetTriangles(triangles, 0);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();

            return _mesh;
        }
    }
}
