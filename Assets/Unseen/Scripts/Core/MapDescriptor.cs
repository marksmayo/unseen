using Unity.Mathematics;
using UnityEngine;

namespace Unseen.Core
{
    /// <summary>
    /// Describes the playable bounds of a level. The mist controller, bot patrol picker and glider
    /// flight line all read this rather than guessing from renderer bounds.
    /// </summary>
    public sealed class MapDescriptor : MonoBehaviour
    {
        [Tooltip("Centre of the playable area, and the first mist circle.")]
        public Vector3 Center = Vector3.zero;

        [Tooltip("Radius of the playable area in metres. The mist rings and the bot patrol " +
                 "picker are circular and read this.")]
        public float Radius = 320f;

        [Tooltip("Half-extent of the playable area if it is square, in metres - the distance from " +
                 "the centre to a wall along an axis. Zero means the area really is a circle.\n\n" +
                 "A square town cannot be fenced with a circle. The rampart here sits at the " +
                 "half-extent along each axis and at half-extent times root two in the corners, " +
                 "so a circular clamp at the axis distance cuts the corners off a hundred metres " +
                 "short of the wall - an invisible barrier standing in an open street.")]
        public float HalfExtent;

        [Tooltip("Lowest playable Y, e.g. the sewer floor. Used for spawn validation.")]
        public float FloorY = -12f;

        [Tooltip("Highest playable Y, e.g. the keep roof.")]
        public float CeilingY = 60f;

        public float3 CenterF3 => Center;

        private static MapDescriptor _cached;

        public static MapDescriptor Find()
        {
            if (_cached != null) return _cached;
#if UNITY_2023_1_OR_NEWER
            _cached = Object.FindFirstObjectByType<MapDescriptor>();
#else
            _cached = Object.FindObjectOfType<MapDescriptor>();
#endif
            return _cached;
        }

        private void OnEnable()
        {
            _cached = this;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.4f);
            Gizmos.DrawWireSphere(Center, Radius);

            if (HalfExtent <= 0f) return;

            Gizmos.color = new Color(1f, 0.8f, 0.3f, 0.5f);
            Gizmos.DrawWireCube(Center, new Vector3(HalfExtent * 2f, 2f, HalfExtent * 2f));
        }
#endif
    }
}
