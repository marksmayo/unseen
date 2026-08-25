using System.Collections.Generic;
using UnityEngine;
using Unseen.Core;
using Unseen.Net;

namespace Unseen.Client
{
    /// <summary>
    /// Pushes silhouette contacts into the shoji shader once per frame.
    ///
    /// The list can only ever contain contacts the server already granted, so the shader is
    /// physically unable to print a shape for someone the interest manager withheld. Combined with
    /// the snapshot format, that means the silhouette is a genuine information channel rather than
    /// a client-side effect that a modified client could widen.
    /// </summary>
    public sealed class ShojiSilhouetteFeeder : MonoBehaviour
    {
        public const int MaxSilhouettes = 8;

        public ClientNetworkView View;

        [Tooltip("How strongly a fresh contact prints. Confidence scales this down.")]
        [Range(0f, 1f)] public float Strength = 0.95f;

        [Tooltip("Seconds a silhouette keeps printing after its last update.")]
        public float Linger = 0.25f;

        private static readonly int SilhouettesProperty = Shader.PropertyToID("_UnseenSilhouettes");
        private static readonly int CountProperty = Shader.PropertyToID("_UnseenSilhouetteCount");

        private readonly Vector4[] _entries = new Vector4[MaxSilhouettes];
        private readonly List<VisibleEntity> _current = new List<VisibleEntity>(MaxSilhouettes);
        private float _lastUpdate;

        private void Start()
        {
            if (View != null) View.SnapshotApplied += OnSnapshot;
        }

        private void OnDestroy()
        {
            if (View != null) View.SnapshotApplied -= OnSnapshot;
            Clear();
        }

        private void OnSnapshot(SnapshotData snapshot)
        {
            _current.Clear();

            for (int i = 0; i < snapshot.Entities.Count && _current.Count < MaxSilhouettes; i++)
            {
                VisibleEntity entity = snapshot.Entities[i];
                if ((entity.Kind & VisibilityKind.Silhouette) == 0) continue;
                _current.Add(entity);
            }

            _lastUpdate = Time.time;
        }

        private void LateUpdate()
        {
            if (Time.time - _lastUpdate > Linger && _current.Count > 0) _current.Clear();

            int count = Mathf.Min(_current.Count, MaxSilhouettes);
            for (int i = 0; i < count; i++)
            {
                VisibleEntity entity = _current[i];
                Vector3 torso = (Vector3)entity.Position + Vector3.up * 1f;
                _entries[i] = new Vector4(torso.x, torso.y, torso.z,
                    Mathf.Clamp01(entity.Confidence * 2.4f) * Strength);
            }

            for (int i = count; i < MaxSilhouettes; i++) _entries[i] = Vector4.zero;

            Shader.SetGlobalVectorArray(SilhouettesProperty, _entries);
            Shader.SetGlobalFloat(CountProperty, count);
        }

        private void Clear()
        {
            for (int i = 0; i < MaxSilhouettes; i++) _entries[i] = Vector4.zero;
            Shader.SetGlobalVectorArray(SilhouettesProperty, _entries);
            Shader.SetGlobalFloat(CountProperty, 0f);
        }
    }
}
