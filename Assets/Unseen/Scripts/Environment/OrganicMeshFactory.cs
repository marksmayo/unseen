using System.Collections.Generic;
using UnityEngine;

namespace Unseen.Environment
{
    /// <summary>
    /// Round and irregular shapes, for the parts of the town that grew rather than being built.
    ///
    /// Everything in this world was a box. That is exactly right for a compound wall, a roof, a
    /// crate or a bridge deck - and exactly wrong for a tree, and the result read as Minecraft. A
    /// texture cannot fix a silhouette: a trunk made of a stretched cube is a stretched cube however
    /// convincing the bark on it is, because what the eye identifies a tree by at forty metres is
    /// its outline.
    ///
    /// So: tapered tubes with a bend in them for trunks and branches, deformed spheres for canopies
    /// and boulders, tapered strips for blades of grass and reeds.
    ///
    /// Meshes are cached by their parameters exactly as <see cref="BoxMeshFactory"/> caches boxes,
    /// and for the same reason - the town builds tens of thousands of these and they come in a
    /// handful of distinct shapes. Parameters are QUANTISED before they reach the cache: continuous
    /// random sizes would give one unique mesh per object, which is how a cache becomes a leak.
    /// Variety comes from scale and rotation on the transform, which is free.
    /// </summary>
    public static class OrganicMeshFactory
    {
        private static readonly Dictionary<long, Mesh> Cache = new Dictionary<long, Mesh>(128);

        /// <summary>Meshes currently cached. Watched by the generator's cost logging.</summary>
        public static int CachedMeshCount => Cache.Count;

        public static void Clear()
        {
            foreach (KeyValuePair<long, Mesh> entry in Cache)
                if (entry.Value != null) Object.DestroyImmediate(entry.Value);

            Cache.Clear();
        }

        /// <summary>
        /// A tapered, bent tube of unit height and unit base radius: a trunk, a limb, a cane.
        ///
        /// Unit-sized so the caller scales it, which is what keeps the cache small. The bend is
        /// baked into the mesh rather than applied by rotation because a trunk curves along its
        /// length - rotating a straight one just tilts it, and a tilted cylinder still reads as
        /// machinery.
        /// </summary>
        /// <param name="sides">Radial segments. Six is enough at the distance trees are seen.</param>
        /// <param name="rings">Segments up the length. More rings, smoother curve.</param>
        /// <param name="topScale">Radius at the top as a fraction of the base.</param>
        /// <param name="bend">Sideways lean of the tip, in units of the height.</param>
        /// <param name="wobble">Irregularity of the outline, 0 for a clean cylinder.</param>
        public static Mesh Tube(int sides, int rings, float topScale, float bend, float wobble)
        {
            sides = Mathf.Clamp(sides, 3, 12);
            rings = Mathf.Clamp(rings, 2, 8);

            // Quantised: a tenth on the taper and the bend, a twentieth on the wobble.
            int topKey = Mathf.RoundToInt(Mathf.Clamp01(topScale) * 10f);
            int bendKey = Mathf.RoundToInt(Mathf.Clamp(bend, -1f, 1f) * 10f);
            int wobbleKey = Mathf.RoundToInt(Mathf.Clamp01(wobble) * 20f);

            long key = Key(1, sides, rings, topKey, bendKey, wobbleKey);
            if (Cache.TryGetValue(key, out Mesh cached) && cached != null) return cached;

            var vertices = new List<Vector3>((sides + 1) * (rings + 1));
            var normals = new List<Vector3>(vertices.Capacity);
            var uvs = new List<Vector2>(vertices.Capacity);
            var triangles = new List<int>(sides * rings * 6);

            float top = Mathf.Clamp01(topScale) * 0.5f;

            for (int r = 0; r <= rings; r++)
            {
                float v = r / (float)rings;
                float radius = Mathf.Lerp(0.5f, top, v);

                // Deterministic irregularity from the ring index, so a trunk is not a perfect
                // cone but every trunk of the same parameters is identical and shares a mesh.
                float ringWobble = 1f + Mathf.Sin(r * 2.3f) * wobble * 0.35f;
                radius *= ringWobble;

                // The bend accelerates up the length: a tree leans away from the base, it does not
                // shear uniformly.
                float lean = bend * v * v;

                for (int s = 0; s <= sides; s++)
                {
                    float a = s / (float)sides * Mathf.PI * 2f;
                    float cos = Mathf.Cos(a);
                    float sin = Mathf.Sin(a);

                    float flute = 1f + Mathf.Sin(a * sides * 0.5f + r) * wobble * 0.2f;

                    vertices.Add(new Vector3(cos * radius * flute + lean, v, sin * radius * flute));
                    normals.Add(new Vector3(cos, wobble * 0.2f, sin).normalized);

                    // Wrapped around the circumference so bark runs the right way up.
                    uvs.Add(new Vector2(s / (float)sides * 2f, v * 3f));
                }
            }

            int stride = sides + 1;
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < sides; s++)
                {
                    int i0 = r * stride + s;
                    int i1 = i0 + 1;
                    int i2 = i0 + stride;
                    int i3 = i2 + 1;

                    triangles.Add(i0); triangles.Add(i2); triangles.Add(i1);
                    triangles.Add(i1); triangles.Add(i2); triangles.Add(i3);
                }
            }

            return Store(key, $"Tube_{sides}_{rings}_{topKey}_{bendKey}_{wobbleKey}",
                vertices, normals, uvs, triangles);
        }

        /// <summary>
        /// A deformed sphere of unit diameter: a canopy clump, a shrub, a boulder.
        ///
        /// The deformation is what matters. A clean sphere reads as a beach ball and is nearly as
        /// wrong as a cube; what a mass of foliage or a weathered rock has is a lumpy outline, and
        /// three low-frequency sine terms give that for the cost of nothing.
        /// </summary>
        /// <param name="rings">Latitude bands.</param>
        /// <param name="sides">Longitude segments.</param>
        /// <param name="lumpiness">How far the surface strays from a sphere, 0 to about 0.4.</param>
        /// <param name="variant">Selects a different lump pattern. A handful is plenty.</param>
        public static Mesh Blob(int rings, int sides, float lumpiness, int variant)
        {
            rings = Mathf.Clamp(rings, 3, 12);
            sides = Mathf.Clamp(sides, 4, 16);
            variant = Mathf.Abs(variant) % 8;

            int lumpKey = Mathf.RoundToInt(Mathf.Clamp01(lumpiness) * 20f);

            long key = Key(2, rings, sides, lumpKey, variant, 0);
            if (Cache.TryGetValue(key, out Mesh cached) && cached != null) return cached;

            var vertices = new List<Vector3>((sides + 1) * (rings + 1));
            var normals = new List<Vector3>(vertices.Capacity);
            var uvs = new List<Vector2>(vertices.Capacity);
            var triangles = new List<int>(sides * rings * 6);

            float phase = variant * 1.37f;

            for (int r = 0; r <= rings; r++)
            {
                float v = r / (float)rings;
                float polar = v * Mathf.PI;
                float y = Mathf.Cos(polar) * 0.5f;
                float ring = Mathf.Sin(polar) * 0.5f;

                for (int s = 0; s <= sides; s++)
                {
                    float u = s / (float)sides;
                    float azimuth = u * Mathf.PI * 2f;

                    float lump = 1f +
                                 Mathf.Sin(azimuth * 2f + phase) * 0.5f * lumpiness +
                                 Mathf.Sin(polar * 3f + phase * 1.7f) * 0.6f * lumpiness +
                                 Mathf.Sin(azimuth * 3f + polar * 2f + phase * 2.3f) * 0.4f * lumpiness;

                    var point = new Vector3(Mathf.Cos(azimuth) * ring, y, Mathf.Sin(azimuth) * ring) * lump;

                    vertices.Add(point);
                    normals.Add(point.normalized);
                    uvs.Add(new Vector2(u * 2f, v * 2f));
                }
            }

            int stride = sides + 1;
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < sides; s++)
                {
                    int i0 = r * stride + s;
                    int i1 = i0 + 1;
                    int i2 = i0 + stride;
                    int i3 = i2 + 1;

                    triangles.Add(i0); triangles.Add(i2); triangles.Add(i1);
                    triangles.Add(i1); triangles.Add(i2); triangles.Add(i3);
                }
            }

            return Store(key, $"Blob_{rings}_{sides}_{lumpKey}_{variant}",
                vertices, normals, uvs, triangles);
        }

        /// <summary>
        /// A tapered, curved strip standing on the origin: a blade of grass, a reed, a leaf.
        ///
        /// Two-sided, because a blade is seen from both faces and a single-sided one vanishes as you
        /// walk round it. Unit height and unit width at the base.
        /// </summary>
        /// <param name="segments">Segments up the blade. Four gives a readable curve.</param>
        /// <param name="curve">How far the tip bows over, in units of the height.</param>
        public static Mesh Blade(int segments, float curve)
        {
            segments = Mathf.Clamp(segments, 2, 8);
            int curveKey = Mathf.RoundToInt(Mathf.Clamp01(curve) * 10f);

            long key = Key(3, segments, curveKey, 0, 0, 0);
            if (Cache.TryGetValue(key, out Mesh cached) && cached != null) return cached;

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            for (int i = 0; i <= segments; i++)
            {
                float v = i / (float)segments;

                // Tapering to a point, and bowing over more the higher it goes.
                float halfWidth = Mathf.Lerp(0.5f, 0.02f, v * v);
                float bow = curve * v * v;

                vertices.Add(new Vector3(-halfWidth, v, bow));
                vertices.Add(new Vector3(halfWidth, v, bow));

                // Facing outward from the bow, so light catches the curve.
                var facing = new Vector3(0f, curve * v, -1f).normalized;
                normals.Add(facing);
                normals.Add(facing);

                uvs.Add(new Vector2(0f, v));
                uvs.Add(new Vector2(1f, v));
            }

            for (int i = 0; i < segments; i++)
            {
                int a = i * 2;
                int b = a + 1;
                int c = a + 2;
                int d = a + 3;

                triangles.Add(a); triangles.Add(c); triangles.Add(b);
                triangles.Add(b); triangles.Add(c); triangles.Add(d);

                // And the back face, wound the other way.
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
                triangles.Add(b); triangles.Add(d); triangles.Add(c);
            }

            return Store(key, $"Blade_{segments}_{curveKey}", vertices, normals, uvs, triangles);
        }

        private static Mesh Store(long key, string name, List<Vector3> vertices,
            List<Vector3> normals, List<Vector2> uvs, List<int> triangles)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();

            Cache[key] = mesh;
            return mesh;
        }

        private static long Key(int kind, int a, int b, int c, int d, int e)
        {
            long key = kind;
            key = key * 31 + a;
            key = key * 31 + b;
            key = key * 31 + c;
            key = key * 31 + d;
            key = key * 31 + e;
            return key;
        }
    }
}
