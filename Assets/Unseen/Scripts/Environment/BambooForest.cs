using System.Collections.Generic;
using UnityEngine;
using Unseen.Core;

namespace Unseen.Environment
{
    /// <summary>
    /// The spirit forest: a wall of bamboo that grows in from the rampart and takes the map away.
    ///
    /// Built once and then driven by a single number - how deep the forest has grown - so the
    /// growth costs nothing at runtime beyond moving a few transforms.
    ///
    /// The geometry is deliberately two things rather than one. A solid mass per side carries the
    /// collision and the bulk of the silhouette, because a player only ever sees the forest from
    /// the inside and a dense grove reads as solid anyway. A fixed pool of individual culms rides
    /// the growing inner face, which is the only part anyone looks at closely. Modelling the whole
    /// forest as culms would mean tens of thousands of them: the perimeter here is three kilometres.
    /// </summary>
    public sealed class BambooForest : MonoBehaviour
    {
        private struct Side
        {
            public Transform Mass;
            public BoxCollider Collider;
            public Vector3 Inward;
            public bool AlongX;
            public Transform[] Culms;
            public Transform[] Leaves;
        }

        [Tooltip("Metres between culms along the growing face.")]
        public float CulmSpacing = 2.3f;

        [Tooltip("Culms per side. Caps the cost on a very large map. Two hundred over a " +
                 "seven-hundred-metre side is one cane every three and a half metres, which " +
                 "reads as a picket fence rather than a grove.")]
        public int MaxCulmsPerSide = 320;

        [Tooltip("Sprays of leaf per culm. Two staggered sprays read as a feathered tip; one " +
                 "reads as a paddle on a stick.")]
        public int FrondsPerCulm = 2;

        private readonly List<Side> _sides = new List<Side>(4);
        private float _ring;
        private float _height;
        private float _maxDepth;

        /// <summary>How far the forest currently reaches in from the rampart, in metres.</summary>
        public float Depth { get; private set; }

        /// <summary>Distance from the map centre to the forest's inner face.</summary>
        public float InnerEdge => Mathf.Max(0f, _ring - Depth);

        public bool IsGrown => Depth > 0.01f;

        /// <summary>
        /// Lays out the forest against the inside of the rampart. Nothing is visible until
        /// <see cref="SetDepth"/> is called with something above zero.
        /// </summary>
        public void Build(float ringRadius, float wallHeight, float height, float maxDepth,
            Material culm, Material mass, Material foliage)
        {
            _ring = ringRadius;
            _height = height;
            _maxDepth = Mathf.Max(1f, maxDepth);

            float span = ringRadius * 2f + 4f;
            int culmCount = Mathf.Min(MaxCulmsPerSide,
                Mathf.Max(8, Mathf.RoundToInt(span / Mathf.Max(0.5f, CulmSpacing))));

            for (int side = 0; side < 4; side++)
            {
                bool alongX = side % 2 == 0;
                float sign = side < 2 ? 1f : -1f;
                Vector3 inward = alongX ? new Vector3(0f, 0f, -sign) : new Vector3(-sign, 0f, 0f);

                var host = new GameObject($"BambooMass_{side}");
                host.transform.SetParent(transform, false);

                var filter = host.AddComponent<MeshFilter>();
                filter.sharedMesh = BoxMeshFactory.Get(new Vector3(1f, 1f, 1f), 2.5f);

                var renderer = host.AddComponent<MeshRenderer>();
                if (mass != null) renderer.sharedMaterial = mass;

                var box = host.AddComponent<BoxCollider>();
                box.size = Vector3.one;

                host.layer = UnseenLayers.Occluder;
                host.SetActive(false);

                int fronds = Mathf.Max(1, FrondsPerCulm);
                var culms = new Transform[culmCount];
                var leaves = new Transform[culmCount * fronds];
                for (int i = 0; i < culmCount; i++)
                {
                    var stalk = new GameObject($"Culm_{side}_{i}");
                    stalk.transform.SetParent(transform, false);

                    // Culms share one mesh; the variation comes from scale and rotation, which
                    // costs nothing and keeps them batched.
                    stalk.AddComponent<MeshFilter>().sharedMesh =
                        BoxMeshFactory.Get(new Vector3(0.22f, 1f, 0.22f), 2.5f);

                    var stalkRenderer = stalk.AddComponent<MeshRenderer>();
                    if (culm != null) stalkRenderer.sharedMaterial = culm;

                    stalk.SetActive(false);
                    culms[i] = stalk.transform;

                    // Leaves. Bamboo is bare for most of its height and then all foliage at the
                    // top, and without that the culms read as fence posts.
                    //
                    // Sprays rather than one slab: a single wide box at the tip is a paddle from
                    // every angle, and a paddle on a pole is what the first pass looked like. Two
                    // narrow sprays at different heights, splayed apart, read as a feathered tip.
                    for (int f = 0; f < fronds; f++)
                    {
                        var spray = new GameObject($"Leaves_{side}_{i}_{f}");
                        spray.transform.SetParent(transform, false);
                        spray.AddComponent<MeshFilter>().sharedMesh =
                            BoxMeshFactory.Get(new Vector3(0.7f, 2.8f, 0.7f), 2.5f);

                        var sprayRenderer = spray.AddComponent<MeshRenderer>();
                        if (foliage != null) sprayRenderer.sharedMaterial = foliage;

                        spray.SetActive(false);
                        leaves[i * fronds + f] = spray.transform;
                    }
                }

                _sides.Add(new Side
                {
                    Mass = host.transform,
                    Collider = box,
                    Inward = inward,
                    AlongX = alongX,
                    Culms = culms,
                    Leaves = leaves
                });
            }

            Debug.Log($"[Unseen] spirit forest laid out: 4 sides, {culmCount} culms each, " +
                      $"ring {ringRadius:0} m, height {height:0} m");
        }

        /// <summary>
        /// Sets how far the forest has grown, and how tall the growing edge currently stands.
        /// </summary>
        /// <param name="depth">Metres in from the rampart.</param>
        /// <param name="edgeHeight">Height of the newest band, 0 to 1 of full.</param>
        public void SetDepth(float depth, float edgeHeight)
        {
            Depth = Mathf.Clamp(depth, 0f, _maxDepth);
            bool visible = Depth > 0.01f;

            for (int i = 0; i < _sides.Count; i++)
            {
                Side side = _sides[i];

                if (side.Mass.gameObject.activeSelf != visible) side.Mass.gameObject.SetActive(visible);
                if (!visible)
                {
                    for (int c = 0; c < side.Culms.Length; c++)
                    {
                        if (side.Culms[c].gameObject.activeSelf)
                            side.Culms[c].gameObject.SetActive(false);
                    }

                    for (int c = 0; c < side.Leaves.Length; c++)
                    {
                        if (side.Leaves[c].gameObject.activeSelf)
                            side.Leaves[c].gameObject.SetActive(false);
                    }

                    continue;
                }

                // The mass fills everything behind the growing edge, so its centre sits half a
                // depth in from the rampart.
                float centreOffset = _ring - Depth * 0.5f;
                float span = _ring * 2f + 4f;

                Vector3 position = side.AlongX
                    ? new Vector3(0f, _height * 0.43f, centreOffset * Mathf.Sign(side.Inward.z) * -1f)
                    : new Vector3(centreOffset * Mathf.Sign(side.Inward.x) * -1f, _height * 0.43f, 0f);

                // The mass stands a little lower than the culms, so the stalks crown it instead
                // of the whole thing ending on one flat line.
                float massHeight = _height * 0.86f;

                Vector3 size = side.AlongX
                    ? new Vector3(span, massHeight, Mathf.Max(0.05f, Depth))
                    : new Vector3(Mathf.Max(0.05f, Depth), massHeight, span);

                side.Mass.localPosition = position;
                side.Mass.localScale = size;
                side.Collider.size = Vector3.one;

                PlaceCulms(side, span, edgeHeight);
            }
        }

        /// <summary>
        /// Rides the culms along the inner face. They are the only bamboo anyone sees up close, so
        /// they carry the growth: a new band comes up as shoots and thickens into the mass behind.
        /// </summary>
        private void PlaceCulms(Side side, float span, float edgeHeight)
        {
            float face = _ring - Depth;
            float height = Mathf.Max(0.4f, _height * Mathf.Clamp01(edgeHeight));

            for (int i = 0; i < side.Culms.Length; i++)
            {
                Transform culm = side.Culms[i];
                if (!culm.gameObject.activeSelf) culm.gameObject.SetActive(true);

                float t = side.Culms.Length <= 1 ? 0.5f : i / (side.Culms.Length - 1f);
                float along = Mathf.Lerp(-span * 0.5f, span * 0.5f, t);

                // Deterministic jitter from the index: a hedge planted on a ruler is not a forest.
                float wobble = Mathf.Sin(i * 12.9898f) * 0.45f;
                float lean = Mathf.Sin(i * 4.1414f) * 6f;
                float scale = 0.75f + Mathf.Abs(Mathf.Sin(i * 7.233f)) * 0.5f;

                // In FRONT of the mass, on the town side of the growing face. Planted behind it
                // they were buried, and only the tallest tips showed above the top - which read as
                // a flat green wall with a few sticks over it rather than a grove.
                float depth = face - 0.45f - Mathf.Abs(Mathf.Cos(i * 3.77f)) * 0.7f;

                culm.localPosition = side.AlongX
                    ? new Vector3(along + wobble, height * 0.5f * scale, depth * Mathf.Sign(side.Inward.z) * -1f)
                    : new Vector3(depth * Mathf.Sign(side.Inward.x) * -1f, height * 0.5f * scale, along + wobble);

                culm.localScale = new Vector3(1f, height * scale, 1f);
                culm.localRotation = Quaternion.Euler(lean * 0.4f, i * 37f, lean);

                int fronds = side.Leaves.Length / Mathf.Max(1, side.Culms.Length);
                for (int f = 0; f < fronds; f++)
                {
                    Transform head = side.Leaves[i * fronds + f];
                    if (!head.gameObject.activeSelf) head.gameObject.SetActive(true);

                    // Sprays staggered down from the tip and splayed to alternate sides, so the
                    // top of a culm has a silhouette instead of an outline.
                    float drop = f * 0.16f;
                    float splay = (f % 2 == 0 ? 1f : -1f) * (18f + f * 7f);

                    head.localPosition = culm.localPosition +
                        new Vector3(0f, height * scale * (0.42f - drop), 0f);
                    head.localScale = Vector3.one *
                        (0.7f + Mathf.Abs(Mathf.Sin((i + f) * 2.11f)) * 0.45f);
                    head.localRotation = Quaternion.Euler(
                        splay * 0.5f, i * 53f + f * 61f, lean * 0.5f + splay);
                }
            }
        }
    }
}
