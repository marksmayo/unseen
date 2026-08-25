using System.Collections.Generic;
using UnityEngine;

namespace Unseen.Environment
{
    /// <summary>
    /// Builds the lathe-turned body of a paper lantern: narrow at both ends, bellied in the middle,
    /// the way a chochin is shaped by its bamboo frame.
    ///
    /// Procedural rather than an imported model, for the same reason the buildings are: the shape
    /// stays tied to the gameplay dimensions in config, and there is no asset to keep in sync.
    /// UVs run around the body horizontally so the ribbed paper texture wraps correctly.
    /// </summary>
    public static class LanternMeshFactory
    {
        // Radius profile from bottom (0) to top (1), as a fraction of the widest point. Both ends
        // roll in to a near-point: a wide mouth lets the camera see straight into the body past
        // the timber fitting, and the lamp inside makes whatever it finds there blow to white.
        private static readonly Vector2[] Profile =
        {
            new Vector2(0.00f, 0.10f),
            new Vector2(0.02f, 0.34f),
            new Vector2(0.06f, 0.62f),
            new Vector2(0.16f, 0.83f),
            new Vector2(0.30f, 0.95f),
            new Vector2(0.50f, 1.00f),
            new Vector2(0.70f, 0.95f),
            new Vector2(0.84f, 0.83f),
            new Vector2(0.94f, 0.62f),
            new Vector2(0.98f, 0.34f),
            new Vector2(1.00f, 0.10f)
        };

        private const int Segments = 14;

        private static readonly Dictionary<int, Mesh> Cache = new Dictionary<int, Mesh>(4);

        public static void ClearCache()
        {
            foreach (KeyValuePair<int, Mesh> kv in Cache)
                if (kv.Value != null)
                    Object.DestroyImmediate(kv.Value);
            Cache.Clear();
        }

        /// <summary>A lantern body of the given width and height, centred on its own origin.</summary>
        public static Mesh Get(float width, float height)
        {
            int key = Mathf.RoundToInt(width * 1000f) * 7919 + Mathf.RoundToInt(height * 1000f);
            if (Cache.TryGetValue(key, out Mesh cached) && cached != null) return cached;

            Mesh mesh = Build(width, height);
            Cache[key] = mesh;
            return mesh;
        }

        /// <summary>Fans a flat disc across the open mouth of one end of the lathe.</summary>
        private static void AddCap(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs,
            List<int> triangles, Vector3[] ring, int columns, int rowIndex, float height, bool top)
        {
            int rowStart = rowIndex * columns;
            float y = ring[rowStart].y;
            Vector3 normal = top ? Vector3.up : Vector3.down;

            int centre = vertices.Count;
            vertices.Add(new Vector3(0f, y, 0f));
            normals.Add(normal);
            uvs.Add(new Vector2(0.5f, top ? 1f : 0f));

            int first = vertices.Count;
            for (int c = 0; c < columns; c++)
            {
                vertices.Add(ring[rowStart + c]);
                normals.Add(normal);
                uvs.Add(new Vector2(c / (float)(columns - 1), top ? 1f : 0f));
            }

            for (int c = 0; c < columns - 1; c++)
            {
                int a = first + c;
                int b = first + c + 1;

                if (top)
                {
                    triangles.Add(centre);
                    triangles.Add(b);
                    triangles.Add(a);
                }
                else
                {
                    triangles.Add(centre);
                    triangles.Add(a);
                    triangles.Add(b);
                }
            }
        }

        private static Mesh Build(float width, float height)
        {
            int rings = Profile.Length;
            int columns = Segments + 1; // duplicate seam column so UVs wrap cleanly

            var vertices = new Vector3[rings * columns];
            var normals = new Vector3[rings * columns];
            var uvs = new Vector2[rings * columns];

            float radiusScale = width * 0.5f;

            for (int r = 0; r < rings; r++)
            {
                float t = Profile[r].x;
                float radius = Profile[r].y * radiusScale;
                float y = (t - 0.5f) * height;

                for (int c = 0; c < columns; c++)
                {
                    float angle = c / (float)Segments * Mathf.PI * 2f;
                    float sin = Mathf.Sin(angle);
                    float cos = Mathf.Cos(angle);
                    int index = r * columns + c;

                    vertices[index] = new Vector3(sin * radius, y, cos * radius);

                    // Normal follows the profile slope so the belly shades smoothly.
                    float slope = r > 0 && r < rings - 1
                        ? (Profile[r + 1].y - Profile[r - 1].y) * radiusScale /
                          Mathf.Max(0.0001f, (Profile[r + 1].x - Profile[r - 1].x) * height)
                        : 0f;

                    normals[index] = new Vector3(sin, -slope, cos).normalized;
                    uvs[index] = new Vector2(c / (float)Segments, t);
                }
            }

            var triangles = new List<int>((rings - 1) * Segments * 6 + Segments * 6);
            for (int r = 0; r < rings - 1; r++)
            {
                for (int c = 0; c < Segments; c++)
                {
                    int a = r * columns + c;
                    int b = a + 1;
                    int d = (r + 1) * columns + c;
                    int e = d + 1;

                    triangles.Add(a);
                    triangles.Add(d);
                    triangles.Add(b);

                    triangles.Add(b);
                    triangles.Add(d);
                    triangles.Add(e);
                }
            }

            // The lathe leaves an open mouth at each end. Left open you see straight through the
            // paper into the inside of the far wall, which reads as a hole rather than a lantern.
            var vertexList = new List<Vector3>(vertices);
            var normalList = new List<Vector3>(normals);
            var uvList = new List<Vector2>(uvs);

            AddCap(vertexList, normalList, uvList, triangles, vertices, columns, 0, height, false);
            AddCap(vertexList, normalList, uvList, triangles, vertices, columns, rings - 1, height, true);

            var mesh = new Mesh
            {
                name = $"Lantern_{width:0.##}x{height:0.##}",
                vertices = vertexList.ToArray(),
                normals = normalList.ToArray(),
                uv = uvList.ToArray()
            };

            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
