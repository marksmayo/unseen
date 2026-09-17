using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Unseen.EditorTools
{
    public static class UnseenNinjaArtExport
    {
        public static void ValidateAndRender()
        {
            ValidateSkinning();
            UnseenScreenshot.Capture();
            UnseenGarmentTest.Run();
            UnseenPoseShot.Capture();
        }

        private static void ValidateSkinning()
        {
            var set = Unseen.Entities.AgentVisualSet.Load();
            var host = new GameObject("NinjaArtValidation");
            try
            {
                Mesh original = set.NinjaVisual.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh;
                var visual = set.Attach(host.transform, 0);
                Mesh refined = visual.Body.sharedMesh;
                if (!refined.name.EndsWith("_Refined") || original.vertexCount != refined.vertexCount)
                    throw new Exception("Refined ninja topology mismatch");
                BoneWeight[] before = original.boneWeights, after = refined.boneWeights;
                for (int i = 0; i < before.Length; i++)
                    if (!before[i].Equals(after[i])) throw new Exception("Refined ninja skin weights changed");
                Matrix4x4[] oldBind = original.bindposes, newBind = refined.bindposes;
                for (int i = 0; i < oldBind.Length; i++)
                    if (!oldBind[i].Equals(newBind[i])) throw new Exception("Refined ninja bind pose changed");
                var baked = new Mesh();
                visual.Body.BakeMesh(baked, true);
                float min = float.MaxValue, max = float.MinValue;
                foreach (Vector3 p in baked.vertices)
                {
                    float y = visual.Body.transform.TransformPoint(p).y;
                    min = Mathf.Min(min, y); max = Mathf.Max(max, y);
                }
                UnityEngine.Object.DestroyImmediate(baked);
                float expected = Unseen.Core.UnseenConfig.Default.Movement.StandHeight;
                if (Mathf.Abs(max - min - expected) > .025f)
                    throw new Exception($"Refined ninja height {max-min} differs from capsule {expected}");
                Debug.Log($"[ninja-art] PASS: {refined.vertexCount} vertices, skin weights and bind poses retained; height {max-min:0.000} m");
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        [Serializable] private sealed class Source
        {
            public Vector3[] vertices;
            public int[] triangles;
            public Vector2[] uv;
            public float[] weights;
            public Vector3 pivot, up, right, forward;
        }

        public static void Export()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Unseen/Art/Characters/characterMedium.fbx");
            var instance = UnityEngine.Object.Instantiate(prefab);
            try
            {
                var body = instance.GetComponentInChildren<SkinnedMeshRenderer>();
                Mesh mesh = body.sharedMesh;
                int head = Array.FindIndex(body.bones, b => b.name.EndsWith("Head", StringComparison.OrdinalIgnoreCase));
                if (head < 0) throw new Exception("Ninja head bone not found");
                var source = new Source { vertices = mesh.vertices, triangles = mesh.triangles, uv = mesh.uv,
                    weights = new float[mesh.vertexCount], pivot = mesh.bindposes[head].inverse.MultiplyPoint3x4(Vector3.zero),
                    up = body.transform.InverseTransformDirection(Vector3.up).normalized,
                    right = body.transform.InverseTransformDirection(Vector3.right).normalized,
                    forward = body.transform.InverseTransformDirection(Vector3.forward).normalized };
                BoneWeight[] weights = mesh.boneWeights;
                for (int i = 0; i < weights.Length; i++)
                {
                    BoneWeight w = weights[i];
                    source.weights[i] = (w.boneIndex0 == head ? w.weight0 : 0) + (w.boneIndex1 == head ? w.weight1 : 0) +
                        (w.boneIndex2 == head ? w.weight2 : 0) + (w.boneIndex3 == head ? w.weight3 : 0);
                }
                Directory.CreateDirectory("ArtSource");
                File.WriteAllText("ArtSource/ninja-mesh-source.json", JsonUtility.ToJson(source));
                Debug.Log($"[ninja-art] exported {mesh.vertexCount} vertices; head={body.bones[head].name}; up={source.up}");
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }
    }
}
