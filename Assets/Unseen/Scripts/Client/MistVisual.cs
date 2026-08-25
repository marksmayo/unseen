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

        private void Awake()
        {
            if (!DrawMistWall) return;

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "MistWall";
            go.transform.SetParent(transform, false);

            Destroy(go.GetComponent<Collider>());

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
