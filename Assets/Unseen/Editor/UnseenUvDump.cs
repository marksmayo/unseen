using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Renders the ninja mesh's UV layout to a PNG and logs the bounds of each connected island.
    ///
    /// Authoring a costume texture blind - guessing which rectangle in the atlas is the hood and
    /// which is the shin - wastes a whole import-and-look cycle per guess. This turns that into
    /// measured fact before a single pixel is painted.
    /// </summary>
    public static class UnseenUvDump
    {
        private const string ModelPath = "Assets/Unseen/Art/Characters/characterMedium.fbx";
        private const string OutputPath = "Server/out/ninja-uv.png";
        private const int Size = 1024;

        [MenuItem("Unseen/Art/Dump Ninja UVs", priority = 53)]
        public static void Dump()
        {
            Mesh mesh = FindMesh();
            if (mesh == null)
            {
                Debug.LogError($"[uv] no mesh found in {ModelPath}");
                return;
            }

            Vector2[] uvs = mesh.uv;
            int[] triangles = mesh.triangles;
            Debug.Log($"[uv] {mesh.name}: {mesh.vertexCount} verts, {triangles.Length / 3} tris, " +
                      $"{mesh.subMeshCount} submeshes, uv count {uvs.Length}");

            if (uvs.Length == 0)
            {
                Debug.LogError("[uv] mesh has no UV0 channel");
                return;
            }

            var pixels = new Color32[Size * Size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(16, 16, 20, 255);

            // Fill each triangle faintly first, so islands read as solid patches, then draw edges.
            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vector2 a = uvs[triangles[t]];
                Vector2 b = uvs[triangles[t + 1]];
                Vector2 c = uvs[triangles[t + 2]];
                FillTriangle(pixels, a, b, c, new Color32(70, 80, 110, 255));
            }

            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vector2 a = uvs[triangles[t]];
                Vector2 b = uvs[triangles[t + 1]];
                Vector2 c = uvs[triangles[t + 2]];
                Line(pixels, a, b);
                Line(pixels, b, c);
                Line(pixels, c, a);
            }

            LogIslands(uvs, triangles);

            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            texture.SetPixels32(pixels);
            texture.Apply();

            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            File.WriteAllBytes(OutputPath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            Debug.Log($"[uv] wrote {OutputPath}");
        }

        private static Mesh FindMesh()
        {
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(ModelPath))
                if (asset is Mesh mesh)
                    return mesh;
            return null;
        }

        /// <summary>
        /// Groups triangles into connected islands by UV position, and reports each one's extent in
        /// pixels so it can be matched against the painted texture by eye.
        /// </summary>
        private static void LogIslands(Vector2[] uvs, int[] triangles)
        {
            int count = uvs.Length;
            var parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;

            // Weld by UV position, so vertices split for normals still share an island.
            var welded = new Dictionary<long, int>(count);
            for (int i = 0; i < count; i++)
            {
                long key = ((long)Mathf.RoundToInt(uvs[i].x * 4096f) << 20) ^
                           Mathf.RoundToInt(uvs[i].y * 4096f);
                if (welded.TryGetValue(key, out int first)) Union(parent, i, first);
                else welded[key] = i;
            }

            for (int t = 0; t < triangles.Length; t += 3)
            {
                Union(parent, triangles[t], triangles[t + 1]);
                Union(parent, triangles[t + 1], triangles[t + 2]);
            }

            var bounds = new Dictionary<int, Rect>();
            var tally = new Dictionary<int, int>();
            for (int i = 0; i < count; i++)
            {
                int root = Find(parent, i);
                Vector2 uv = uvs[i];
                if (bounds.TryGetValue(root, out Rect r))
                {
                    r.xMin = Mathf.Min(r.xMin, uv.x);
                    r.xMax = Mathf.Max(r.xMax, uv.x);
                    r.yMin = Mathf.Min(r.yMin, uv.y);
                    r.yMax = Mathf.Max(r.yMax, uv.y);
                    bounds[root] = r;
                    tally[root] = tally[root] + 1;
                }
                else
                {
                    bounds[root] = new Rect(uv.x, uv.y, 0f, 0f);
                    tally[root] = 1;
                }
            }

            var ordered = new List<KeyValuePair<int, Rect>>(bounds);
            ordered.Sort((l, r) => tally[r.Key].CompareTo(tally[l.Key]));

            Debug.Log($"[uv] {ordered.Count} islands (px on a {Size} texture, y measured from bottom):");
            for (int i = 0; i < ordered.Count && i < 24; i++)
            {
                Rect r = ordered[i].Value;
                Debug.Log($"[uv]  island {i,2}: {tally[ordered[i].Key],4} verts  " +
                          $"x {r.xMin * Size,6:0} to {r.xMax * Size,6:0}   " +
                          $"y {r.yMin * Size,6:0} to {r.yMax * Size,6:0}");
            }
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a);
            int rb = Find(parent, b);
            if (ra != rb) parent[rb] = ra;
        }

        private static void FillTriangle(Color32[] pixels, Vector2 a, Vector2 b, Vector2 c, Color32 colour)
        {
            Vector2 pa = a * Size, pb = b * Size, pc = c * Size;
            int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(pa.x, Mathf.Min(pb.x, pc.x))), 0, Size - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(pa.x, Mathf.Max(pb.x, pc.x))), 0, Size - 1);
            int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(pa.y, Mathf.Min(pb.y, pc.y))), 0, Size - 1);
            int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(pa.y, Mathf.Max(pb.y, pc.y))), 0, Size - 1);

            float area = Edge(pa, pb, pc);
            if (Mathf.Abs(area) < 1e-6f) return;

            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float w0 = Edge(pb, pc, p) / area;
                float w1 = Edge(pc, pa, p) / area;
                float w2 = Edge(pa, pb, p) / area;
                if (w0 < 0f || w1 < 0f || w2 < 0f) continue;
                pixels[y * Size + x] = colour;
            }
        }

        private static float Edge(Vector2 a, Vector2 b, Vector2 c) =>
            (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

        private static void Line(Color32[] pixels, Vector2 from, Vector2 to)
        {
            Vector2 a = from * Size;
            Vector2 b = to * Size;
            int steps = Mathf.CeilToInt(Vector2.Distance(a, b)) + 1;

            for (int i = 0; i <= steps; i++)
            {
                Vector2 p = Vector2.Lerp(a, b, i / (float)steps);
                int x = Mathf.Clamp(Mathf.RoundToInt(p.x), 0, Size - 1);
                int y = Mathf.Clamp(Mathf.RoundToInt(p.y), 0, Size - 1);
                pixels[y * Size + x] = new Color32(240, 220, 120, 255);
            }
        }
    }
}
