using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;

namespace Unseen.Client
{
    /// <summary>
    /// A remote ninja as the local client knows them. Proxies only exist while the server says the
    /// local player can perceive them, and a proxy built from a silhouette contact deliberately
    /// renders as an anonymous shape with no gear, facing or health.
    /// </summary>
    public sealed class EntityProxy : MonoBehaviour
    {
        public AgentId Id;
        public VisibilityKind Kind;

        [Tooltip("How quickly the proxy chases the last replicated position.")]
        public float PositionSmoothing = 16f;

        public float RotationSmoothing = 12f;

        private float3 _targetPosition;
        private float _targetYaw;
        private Renderer[] _renderers;
        private Transform _facing;
        private Entities.AgentVisual _visual;
        private float _lastUpdateTime;

        private static readonly Color DirectColour = new Color(0.82f, 0.24f, 0.22f);
        private static readonly Color SilhouetteColour = new Color(0.08f, 0.08f, 0.1f, 0.85f);

        public float LastUpdateTime => _lastUpdateTime;

        private void Awake()
        {
            _renderers = GetComponentsInChildren<Renderer>(true);
            _facing = transform.Find("Facing");
            _visual = GetComponentInChildren<Entities.AgentVisual>();
        }

        public void Apply(float3 position, float yaw, VisibilityKind kind, ushort flags, float now,
            bool snap = false)
        {
            _targetPosition = position;
            _targetYaw = yaw;
            _lastUpdateTime = now;

            // Hand the replicated flags to the body, which is all a proxy has to animate combat
            // from: guard, flinch and takedown are in there, the attack phase is not.
            if (_visual == null) _visual = GetComponentInChildren<Entities.AgentVisual>();
            if (_visual != null) _visual.ProxyFlags = flags;

            if (kind != Kind)
            {
                Kind = kind;
                ApplyAppearance();
            }

            if (!snap) return;
            transform.position = position;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        private void ApplyAppearance()
        {
            bool silhouette = (Kind & VisibilityKind.Direct) == 0;
            Color colour = silhouette ? SilhouetteColour : DirectColour;

            if (_renderers == null) return;
            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer r = _renderers[i];
                if (r == null || r.sharedMaterial == null) continue;
                r.material.color = colour;
            }

            // A silhouette gives away no facing, so the marker is hidden entirely.
            if (_facing != null) _facing.gameObject.SetActive(!silhouette);
        }

        private void Update()
        {
            float t = 1f - Mathf.Exp(-PositionSmoothing * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, _targetPosition, t);

            float rt = 1f - Mathf.Exp(-RotationSmoothing * Time.deltaTime);
            float yaw = Mathf.LerpAngle(transform.eulerAngles.y, _targetYaw, rt);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }
    }
}
