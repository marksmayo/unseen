using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unseen.Environment
{
    /// <summary>Shared meshes exported by Tools/build_blender_art.py in Unity coordinates.</summary>
    public static class BlenderArt
    {
        [Serializable]
        private sealed class Payload
        {
            public Vector3[] vertices;
            public Vector3[] normals;
            public Vector2[] uv;
            public int[] triangles;
        }

        private static readonly Dictionary<string, Mesh> Cache = new Dictionary<string, Mesh>();

        public static Mesh Get(string name)
        {
            if (Cache.TryGetValue(name, out Mesh cached)) return cached;
            TextAsset source = Resources.Load<TextAsset>("BlenderArt/" + name);
            if (source == null) return null;
            Payload data = JsonUtility.FromJson<Payload>(source.text);
            var mesh = new Mesh { name = "Blender_" + name };
            mesh.vertices = data.vertices;
            mesh.normals = data.normals;
            mesh.uv = data.uv;
            mesh.triangles = data.triangles;
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            // Vertex-displaced water needs a conservative culling bound at grazing angles.
            if (name == "WaterSurface") mesh.bounds = new Bounds(new Vector3(0, .5f, 0), new Vector3(1, 2, 1));
            // Runtime static batching needs CPU mesh data; meshes are shared and cached once.
            mesh.UploadMeshData(false);
            Cache.Add(name, mesh);
            Resources.UnloadAsset(source);
            return mesh;
        }
    }
}
