using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Unseen.Core;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    public static class UnseenVegetationProbe
    {
        [MenuItem("Unseen/Probe Vegetation Walls")]
        public static void Run()
        {
            var host = new GameObject("VegetationProbe");
            try
            {
                var generator = host.AddComponent<GreyboxTownGenerator>();
                generator.Seed = 20260824;
                generator.CombineStaticMeshes = false;
                generator.Generate();
                Check(host, "town");
                var forest = host.GetComponentInChildren<BambooForest>();
                forest.SetRing(Vector3.zero, forest.MaxRadius, 1);
                Check(forest.gameObject, "outer forest");
                forest.SetRing(new Vector3(12, 0, -8), 230, 1);
                Check(forest.gameObject, "closing forest");
            }
            finally { Object.DestroyImmediate(host); }
        }

        private static void Check(GameObject root, string label)
        {
            Physics.SyncTransforms();
            int checkedMeshes = 0, intersections = 0;
            var vertices = new Dictionary<Mesh, Vector3[]>();
            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>())
            {
                if (!renderer.enabled || renderer.name.StartsWith("BambooWall")) continue;
                Material mat = renderer.sharedMaterial;
                if (mat == null) continue;
                string materialName = mat.name.ToLowerInvariant();
                if (!materialName.Contains("bamboo") && !materialName.Contains("leaf") &&
                    !materialName.Contains("grass") && !materialName.Contains("reed")) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null || !filter.sharedMesh.isReadable) continue;
                checkedMeshes++;
                Bounds bounds = renderer.bounds;
                foreach (Collider collider in Physics.OverlapBox(bounds.center, bounds.extents,
                    Quaternion.identity, UnseenLayers.WorldGeometry | (1 << UnseenLayers.ShojiPaper),
                    QueryTriggerInteraction.Ignore))
                {
                    string name = collider.name.ToLowerInvariant();
                    if ((!name.Contains("wall") && !name.Contains("shoji")) || name.StartsWith("bamboowall")) continue;
                    if (!(collider is BoxCollider box)) continue;
                    if (!vertices.TryGetValue(filter.sharedMesh, out Vector3[] points))
                    {
                        points = filter.sharedMesh.vertices;
                        vertices.Add(filter.sharedMesh, points);
                    }
                    bool penetrates = false;
                    Vector3 half = box.size * .5f;
                    foreach (Vector3 point in points)
                    {
                        Vector3 local = box.transform.InverseTransformPoint(filter.transform.TransformPoint(point)) - box.center;
                        if (Mathf.Abs(local.x) < half.x - .01f && Mathf.Abs(local.y) < half.y - .01f &&
                            Mathf.Abs(local.z) < half.z - .01f) { penetrates = true; break; }
                    }
                    if (!penetrates) continue;
                    intersections++;
                    if (intersections <= 12)
                        Debug.Log($"[vegetation] {label}: {renderer.transform.parent.name}/{renderer.name} " +
                            $"at {renderer.transform.position} intersects {box.transform.parent.name}/{box.name}");
                }
            }
            Debug.Log($"[vegetation] {label}: {checkedMeshes} plant meshes, {intersections} wall intersections: " +
                (intersections == 0 && checkedMeshes > 0 ? "PASS" : "FAIL"));
            if (intersections > 0 || checkedMeshes == 0)
                throw new System.InvalidOperationException($"[vegetation] {label}: invalid plant clearance or empty scan");
        }
    }
}
