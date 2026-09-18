using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unseen.Entities
{
    /// <summary>Blender-sculpted positions on the original skinning topology.</summary>
    public static class NinjaBodyArt
    {
        [Serializable] private sealed class Shape { public Vector3[] vertices; public float scale; }
        private static Shape _shape;
        private static readonly Dictionary<Mesh, Mesh> Meshes = new Dictionary<Mesh, Mesh>();

        public static void Apply(AgentVisual visual)
        {
            if (visual.Body == null || visual.Body.sharedMesh == null) return;
            if (HeroNinjaAppearance.IsHero(visual)) return;
            if (_shape == null)
            {
                TextAsset asset = Resources.Load<TextAsset>("BlenderArt/NinjaBody");
                if (asset == null) return;
                _shape = JsonUtility.FromJson<Shape>(asset.text);
                Resources.UnloadAsset(asset);
            }
            Mesh source = visual.Body.sharedMesh;
            if (source.name.EndsWith("_Refined")) return;

            // A mesh imported without Read/Write cannot be modified, and neither can a copy of one.
            //
            // This failed exactly this way in a player build while working fine in the editor, which
            // keeps mesh data readable: the sculpt was silently dropped and the agent rendered with
            // the original body. Unity says so in three warnings about vertices, normals and
            // tangents, none of which mentions the ninja or this file. Better to say it once, here,
            // in terms of the thing that is actually wrong.
            if (!source.isReadable)
            {
                Debug.LogError($"[ninja-art] '{source.name}' is not readable, so the Blender " +
                               "refinement cannot be applied and the agent will use the unsculpted " +
                               "body. Enable Read/Write on the model's import settings.");
                return;
            }
            if (source.vertexCount != _shape.vertices.Length || _shape.scale < 1 || _shape.scale > 1.25f)
            {
                Debug.LogWarning("[ninja-art] source mesh changed; re-export the Blender refinement");
                return;
            }
            if (!Meshes.TryGetValue(source, out Mesh mesh) || mesh == null)
            {
                mesh = UnityEngine.Object.Instantiate(source);
                mesh.name = source.name + "_Refined";
                mesh.vertices = _shape.vertices;
                mesh.RecalculateNormals();
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                Meshes[source] = mesh;
            }
            visual.Body.sharedMesh = mesh;
            visual.ApplyArtScale(_shape.scale);
        }
    }
}
