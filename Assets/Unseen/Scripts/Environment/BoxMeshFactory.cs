using System.Collections.Generic;
using UnityEngine;

namespace Unseen.Environment
{
    /// <summary>
    /// Builds box meshes whose UVs are scaled to world size, so a texture keeps a constant real-world
    /// scale no matter how large the box is.
    ///
    /// Unity's primitive cube maps 0..1 across every face, which stretches one texture over a 34 m
    /// wall. The alternative - overriding _BaseMap_ST per renderer - fixes the scale but forces every
    /// object out of the SRP batcher and still cannot give differently-sized faces different tiling.
    /// Baking the UVs instead keeps one shared material per surface type, and gets every face right.
    ///
    /// Meshes are cached by size, so the couple of thousand boxes in a generated town resolve to a
    /// few dozen meshes.
    /// </summary>
    public static class BoxMeshFactory
    {
        private struct Key
        {
            public int X, Y, Z, Metres;

            public override int GetHashCode() => (X * 397 ^ Y) * 397 ^ (Z * 397 ^ Metres);
        }

        private static readonly Dictionary<Key, Mesh> Cache = new Dictionary<Key, Mesh>(64);

        /// <summary>Number of distinct meshes currently cached. Diagnostics only.</summary>
        public static int CachedMeshCount => Cache.Count;

        public static void ClearCache()
        {
            foreach (KeyValuePair<Key, Mesh> kv in Cache)
                if (kv.Value != null)
                    Object.DestroyImmediate(kv.Value);
            Cache.Clear();
        }

        /// <summary>
        /// A box of the given world size centred on the origin, with UVs repeating every
        /// <paramref name="textureMetres"/> metres. The transform using it must stay at unit scale.
        /// </summary>
        public static Mesh Get(Vector3 size, float textureMetres)
        {
            var key = new Key
            {
                X = Mathf.RoundToInt(size.x * 100f),
                Y = Mathf.RoundToInt(size.y * 100f),
                Z = Mathf.RoundToInt(size.z * 100f),
                Metres = Mathf.RoundToInt(Mathf.Max(0.05f, textureMetres) * 100f)
            };

            if (Cache.TryGetValue(key, out Mesh cached) && cached != null) return cached;

            Mesh mesh = Build(size, Mathf.Max(0.05f, textureMetres));
            Cache[key] = mesh;
            return mesh;
        }

        private static Mesh Build(Vector3 size, float textureMetres)
        {
            Vector3 h = size * 0.5f;
            float inv = 1f / textureMetres;

            var vertices = new Vector3[24];
            var normals = new Vector3[24];
            var uvs = new Vector2[24];
            var triangles = new int[36];

            // Six faces, each with its own UV scale taken from the two dimensions it spans.
            AddFace(vertices, normals, uvs, triangles, 0, // +X
                new Vector3(h.x, -h.y, -h.z), new Vector3(0f, 0f, size.z), new Vector3(0f, size.y, 0f),
                Vector3.right, size.z * inv, size.y * inv);

            AddFace(vertices, normals, uvs, triangles, 1, // -X
                new Vector3(-h.x, -h.y, h.z), new Vector3(0f, 0f, -size.z), new Vector3(0f, size.y, 0f),
                Vector3.left, size.z * inv, size.y * inv);

            AddFace(vertices, normals, uvs, triangles, 2, // +Y
                new Vector3(-h.x, h.y, -h.z), new Vector3(size.x, 0f, 0f), new Vector3(0f, 0f, size.z),
                Vector3.up, size.x * inv, size.z * inv);

            AddFace(vertices, normals, uvs, triangles, 3, // -Y
                new Vector3(-h.x, -h.y, h.z), new Vector3(size.x, 0f, 0f), new Vector3(0f, 0f, -size.z),
                Vector3.down, size.x * inv, size.z * inv);

            AddFace(vertices, normals, uvs, triangles, 4, // +Z
                new Vector3(h.x, -h.y, h.z), new Vector3(-size.x, 0f, 0f), new Vector3(0f, size.y, 0f),
                Vector3.forward, size.x * inv, size.y * inv);

            AddFace(vertices, normals, uvs, triangles, 5, // -Z
                new Vector3(-h.x, -h.y, -h.z), new Vector3(size.x, 0f, 0f), new Vector3(0f, size.y, 0f),
                Vector3.back, size.x * inv, size.y * inv);

            var mesh = new Mesh
            {
                name = $"Box_{size.x:0.##}x{size.y:0.##}x{size.z:0.##}",
                vertices = vertices,
                normals = normals,
                uv = uvs,
                triangles = triangles
            };

            mesh.RecalculateTangents(); // normal mapping needs tangents
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AddFace(
            Vector3[] vertices, Vector3[] normals, Vector2[] uvs, int[] triangles,
            int face, Vector3 origin, Vector3 across, Vector3 up, Vector3 normal, float tileU, float tileV)
        {
            int v = face * 4;
            int t = face * 6;

            vertices[v + 0] = origin;
            vertices[v + 1] = origin + across;
            vertices[v + 2] = origin + across + up;
            vertices[v + 3] = origin + up;

            for (int i = 0; i < 4; i++) normals[v + i] = normal;

            uvs[v + 0] = new Vector2(0f, 0f);
            uvs[v + 1] = new Vector2(tileU, 0f);
            uvs[v + 2] = new Vector2(tileU, tileV);
            uvs[v + 3] = new Vector2(0f, tileV);

            triangles[t + 0] = v + 0;
            triangles[t + 1] = v + 2;
            triangles[t + 2] = v + 1;
            triangles[t + 3] = v + 0;
            triangles[t + 4] = v + 3;
            triangles[t + 5] = v + 2;
        }
    }
}
