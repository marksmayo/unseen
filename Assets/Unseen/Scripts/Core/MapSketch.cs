using System.Collections.Generic;
using UnityEngine;

namespace Unseen.Core
{
    /// <summary>
    /// A flat description of the town's layout, for drawing a map.
    ///
    /// Published by the generator rather than captured with a second camera. A top-down camera
    /// render would cost a full culling pass over thirty-six thousand renderers every frame, and it
    /// would also draw whatever happened to be standing in the street - which in a game whose
    /// entire information model is "you know what you have earned" is a way of leaking positions
    /// through the minimap. A layout sketch cannot leak anything, because it only contains
    /// buildings, and buildings do not move.
    /// </summary>
    public sealed class MapSketch : MonoBehaviour
    {
        public enum Feature : byte
        {
            Block = 0,
            Keep = 1,
            Pagoda = 2,
            Water = 3,
            Bridge = 4
        }

        [System.Serializable]
        public struct Landmark
        {
            /// <summary>Centre on the ground plane.</summary>
            public Vector2 Center;

            /// <summary>Half-extents on the ground plane.</summary>
            public Vector2 Extents;

            public Feature Kind;
        }

        public readonly List<Landmark> Landmarks = new List<Landmark>(320);

        /// <summary>Half-width of the whole playable square, for framing a full-map view.</summary>
        public float Extent = 1f;

        public void Add(Feature kind, Vector3 centre, Vector2 extents)
        {
            Landmarks.Add(new Landmark
            {
                Center = new Vector2(centre.x, centre.z),
                Extents = extents,
                Kind = kind
            });
        }

        public void Clear()
        {
            Landmarks.Clear();
        }

        private static MapSketch _cached;

        public static MapSketch Find()
        {
            if (_cached != null) return _cached;
#if UNITY_2023_1_OR_NEWER
            _cached = Object.FindFirstObjectByType<MapSketch>();
#else
            _cached = Object.FindObjectOfType<MapSketch>();
#endif
            return _cached;
        }

        private void OnEnable()
        {
            _cached = this;
        }
    }
}
