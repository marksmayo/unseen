using UnityEngine;
using Unseen.Environment;
using Unseen.Net;

namespace Unseen.Client
{
    /// <summary>
    /// Draws the mist wall at whatever radius the server last reported. Purely presentational: the
    /// damage boundary lives on the server, so a client that deletes this object gains nothing but
    /// a nasty surprise.
    /// </summary>
    public sealed class MistVisual : MonoBehaviour
    {
        public ClientNetworkView View;

        [Tooltip("Draw the mist wall. Off by default: a large ZWrite-off transparent cylinder drawn " +
                 "from the inside reads as black planes cutting through the town. The mist damage " +
                 "itself is server-side and unaffected by this.")]
        public bool DrawMistWall = true;

        [Tooltip("Height of the fog cylinder.")]
        public float Height = 60f;

        [Tooltip("How quickly the visual chases a closing circle.")]
        public float Smoothing = 3f;

        private Transform _cylinder;
        private float _radius;
        private Vector3 _center;

        /// <summary>
        /// A capless tube of unit radius and unit half-height, matching the cylinder primitive's
        /// dimensions so the existing scaling maths is unchanged.
        /// </summary>
        private static Mesh BuildTube()
        {
            const int segments = 48;

            var vertices = new Vector3[(segments + 1) * 2];
            var uvs = new Vector2[vertices.Length];
            var triangles = new int[segments * 6];

            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments;
                float angle = t * Mathf.PI * 2f;
                float x = Mathf.Sin(angle);
                float z = Mathf.Cos(angle);

                vertices[i * 2] = new Vector3(x, -1f, z);
                vertices[i * 2 + 1] = new Vector3(x, 1f, z);

                // U runs around the tube, which is what the shader drifts its noise along.
                uvs[i * 2] = new Vector2(t, 0f);
                uvs[i * 2 + 1] = new Vector2(t, 1f);
            }

            for (int i = 0; i < segments; i++)
            {
                int v = i * 2;
                int t = i * 6;

                triangles[t] = v;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;

                triangles[t + 3] = v + 2;
                triangles[t + 4] = v + 1;
                triangles[t + 5] = v + 3;
            }

            var mesh = new Mesh { name = "MistTube" };
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private void Awake()
        {
            if (!DrawMistWall) return;

            // An open tube, not Unity's cylinder primitive.
            //
            // The primitive has end caps, and the shader renders back faces so you can stand
            // inside the wall. Seen from above, the bottom cap is a solid purple disc lying across
            // the world at the base of the cylinder - which sat just above the river, so the water
            // had a sheet of mist over it that vanished the moment you dropped below the cap and
            // saw its culled front face instead. A wall of fog should have no floor.
            var go = new GameObject("MistWall");
            go.transform.SetParent(transform, false);

            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = BuildTube();

            go.AddComponent<MeshRenderer>();

            // Take the material from the asset set: Shader.Find only resolves shaders that some
            // asset already references, so building one here at runtime gives magenta in a player.
            var renderer = go.GetComponent<Renderer>();
            GreyboxMaterialSet set = GreyboxMaterialSet.Load();
            if (renderer != null && set != null && set.Mist != null)
            {
                renderer.sharedMaterial = set.Mist;
            }
            else if (renderer != null)
            {
                Debug.LogWarning("[Unseen] no mist material in the set; hiding the mist wall rather " +
                                 "than rendering it with a missing shader.");
                go.SetActive(false);
            }

            _cylinder = go.transform;
        }

        private void Start()
        {
            if (View != null) View.SnapshotApplied += OnSnapshot;
        }

        private void OnDestroy()
        {
            if (View != null) View.SnapshotApplied -= OnSnapshot;
        }

        private void OnSnapshot(SnapshotData snapshot)
        {
            _center = snapshot.ZoneCenter;
            _radius = snapshot.ZoneRadius;
        }

        private void LateUpdate()
        {
            if (_cylinder == null || _radius <= 0.1f)
            {
                if (_cylinder != null) _cylinder.gameObject.SetActive(false);
                return;
            }

            if (!_cylinder.gameObject.activeSelf) _cylinder.gameObject.SetActive(true);

            float t = 1f - Mathf.Exp(-Smoothing * Time.deltaTime);
            Vector3 targetScale = new Vector3(_radius * 2f, Height * 0.5f, _radius * 2f);
            _cylinder.localScale = Vector3.Lerp(_cylinder.localScale, targetScale, t);
            _cylinder.position = Vector3.Lerp(_cylinder.position,
                new Vector3(_center.x, Height * 0.5f - 4f, _center.z), t);
        }
    }
}
