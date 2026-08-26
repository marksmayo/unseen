using System.Collections.Generic;
using UnityEngine;
using Unseen.Core;

namespace Unseen.Environment
{
    /// <summary>
    /// The spirit forest: a ring of bamboo that closes on the survivors and takes the map away.
    ///
    /// It used to be four straight walls planted against the rampart, growing inward a metre at a
    /// time. That was wrong in two ways at once. It was square while everything else that shrinks
    /// the map - the mist ring, the safe zone, the bounds - is round, so the two boundaries only
    /// agreed at four points. And it moved on its own slow schedule while the mist raced ahead of
    /// it, so by the time anybody was near the edge the bamboo was a hundred metres behind the line
    /// that was actually killing them: a mechanic the player could neither see nor reach.
    ///
    /// So it is a ring now, and it rides the mist. The shrinking boundary is a wall of bamboo you
    /// can see coming instead of an invisible circle on a HUD.
    ///
    /// The geometry is deliberately two things rather than one. A ring of solid segments carries
    /// the collision and the bulk of the silhouette, because a player only ever sees the forest from
    /// the inside and a dense grove reads as solid anyway. A fixed pool of individual culms rides
    /// the inner face, which is the only part anyone looks at closely. Modelling the whole forest as
    /// culms would mean tens of thousands of them at the radius this starts from.
    ///
    /// Both pools are allocated once and reused. Nothing is created or destroyed as the ring closes,
    /// so a boundary move costs a few hundred transform writes and no allocation.
    /// </summary>
    public sealed class BambooForest : MonoBehaviour
    {
        [Tooltip("Segments in the ring. Each is one box: more segments is a rounder wall and a " +
                 "longer collision list.")]
        public int Segments = 72;

        [Tooltip("Culms riding the inner face. Fixed pool - as the ring closes they crowd together, " +
                 "which is what makes the forest thicken as it comes in.")]
        public int CulmCount = 900;

        [Tooltip("Sprays of leaf per culm. Two staggered sprays read as a feathered tip; one " +
                 "reads as a paddle on a stick.")]
        public int FrondsPerCulm = 2;

        [Tooltip("How deep the wall of bamboo is, in metres. Deep enough that you cannot see the " +
                 "world through it.")]
        public float Thickness = 10f;

        private readonly List<Transform> _segments = new List<Transform>(96);
        private readonly List<BoxCollider> _colliders = new List<BoxCollider>(96);
        private readonly List<Transform> _culms = new List<Transform>(1024);
        private readonly List<Transform> _leaves = new List<Transform>(2048);

        private float _maxRadius;
        private float _height;
        private bool _visible;

        // What the ring was last placed at. Between mist stages the boundary holds still for
        // upwards of a minute, and re-placing three thousand transforms twenty times a second to
        // put them back where they already are is pure waste - and it re-inserts seventy-two
        // colliders into the broadphase each time for nothing.
        private float _placedRadius = -1f;
        private float _placedHeight = -1f;
        private Vector3 _placedCentre = new Vector3(float.MaxValue, 0f, 0f);

        /// <summary>Distance from the ring's centre to its inner face. Where the wall stands.</summary>
        public float InnerEdge { get; private set; } = float.MaxValue;

        /// <summary>Widest the ring can stand: the inside of the rampart.</summary>
        public float MaxRadius => _maxRadius;

        /// <summary>Centre of the ring. Follows the mist, which drifts between stages.</summary>
        public Vector3 Centre { get; private set; }

        /// <summary>Height of the wall right now, in metres.</summary>
        public float CurrentHeight { get; private set; }

        public bool IsGrown => _visible && CurrentHeight > 0.5f;

        /// <summary>
        /// Allocates the ring. Called once by the generator, before anyone connects: a wall of
        /// bamboo appearing as a few thousand new GameObjects mid-match is a hitch nobody needs,
        /// and the whole thing costs nothing while it is switched off.
        /// </summary>
        public void Build(float maxRadius, float height, Material culm, Material mass, Material foliage)
        {
            _maxRadius = Mathf.Max(1f, maxRadius);
            _height = Mathf.Max(1f, height);
            InnerEdge = _maxRadius;

            int segments = Mathf.Max(8, Segments);
            int fronds = Mathf.Max(1, FrondsPerCulm);

            for (int i = 0; i < segments; i++)
            {
                var host = new GameObject($"BambooWall_{i}");
                host.transform.SetParent(transform, false);

                host.AddComponent<MeshFilter>().sharedMesh =
                    BoxMeshFactory.Get(new Vector3(1f, 1f, 1f), 2.5f);

                var renderer = host.AddComponent<MeshRenderer>();
                if (mass != null) renderer.sharedMaterial = mass;

                var box = host.AddComponent<BoxCollider>();
                box.size = Vector3.one;

                host.layer = UnseenLayers.Occluder;
                host.SetActive(false);

                _segments.Add(host.transform);
                _colliders.Add(box);
            }

            for (int i = 0; i < Mathf.Max(16, CulmCount); i++)
            {
                var stalk = new GameObject($"Culm_{i}");
                stalk.transform.SetParent(transform, false);

                // Culms share one mesh; the variation comes from scale and rotation, which costs
                // nothing and keeps them batched.
                stalk.AddComponent<MeshFilter>().sharedMesh =
                    BoxMeshFactory.Get(new Vector3(0.22f, 1f, 0.22f), 2.5f);

                var stalkRenderer = stalk.AddComponent<MeshRenderer>();
                if (culm != null) stalkRenderer.sharedMaterial = culm;

                stalk.SetActive(false);
                _culms.Add(stalk.transform);

                // Bamboo is bare for most of its height and then all foliage at the top, and
                // without that the culms read as fence posts. Sprays rather than one slab: a single
                // wide box at the tip is a paddle from every angle.
                for (int f = 0; f < fronds; f++)
                {
                    var spray = new GameObject($"Leaves_{i}_{f}");
                    spray.transform.SetParent(transform, false);
                    spray.AddComponent<MeshFilter>().sharedMesh =
                        BoxMeshFactory.Get(new Vector3(0.7f, 2.8f, 0.7f), 2.5f);

                    var sprayRenderer = spray.AddComponent<MeshRenderer>();
                    if (foliage != null) sprayRenderer.sharedMaterial = foliage;

                    spray.SetActive(false);
                    _leaves.Add(spray.transform);
                }
            }

            Debug.Log($"[Unseen] spirit forest laid out: ring of {segments} segments, " +
                      $"{_culms.Count} culms, max radius {maxRadius:0} m, full height {height:0} m");
        }

        /// <summary>Puts the forest away entirely. Called before it starts and when a match ends.</summary>
        public void Hide()
        {
            if (!_visible && CurrentHeight <= 0f) return;

            _visible = false;
            CurrentHeight = 0f;
            InnerEdge = _maxRadius;
            _placedRadius = -1f;
            _placedHeight = -1f;
            _placedCentre = new Vector3(float.MaxValue, 0f, 0f);

            for (int i = 0; i < _segments.Count; i++)
                if (_segments[i].gameObject.activeSelf) _segments[i].gameObject.SetActive(false);

            for (int i = 0; i < _culms.Count; i++)
                if (_culms[i].gameObject.activeSelf) _culms[i].gameObject.SetActive(false);

            for (int i = 0; i < _leaves.Count; i++)
                if (_leaves[i].gameObject.activeSelf) _leaves[i].gameObject.SetActive(false);
        }

        /// <summary>
        /// Stands the ring at a radius around a centre, at a fraction of full height.
        /// </summary>
        /// <param name="centre">Where the ring is centred. The mist drifts, so this moves.</param>
        /// <param name="innerRadius">Distance from that centre to the inner face of the wall.</param>
        /// <param name="heightFraction">0 to 1 of full height. Shoots at 0.1, a wall at 1.</param>
        public void SetRing(Vector3 centre, float innerRadius, float heightFraction)
        {
            if (_segments.Count == 0) return;

            _visible = true;
            Centre = centre;
            InnerEdge = Mathf.Clamp(innerRadius, 2f, _maxRadius);
            CurrentHeight = Mathf.Max(0.4f, _height * Mathf.Clamp01(heightFraction));

            // Nothing has moved far enough to be worth redrawing. A tenth of a metre is well under
            // what anyone can see at the scale of a wall this size.
            if (Mathf.Abs(InnerEdge - _placedRadius) < 0.1f &&
                Mathf.Abs(CurrentHeight - _placedHeight) < 0.1f &&
                (centre - _placedCentre).sqrMagnitude < 0.01f)
                return;

            _placedRadius = InnerEdge;
            _placedHeight = CurrentHeight;
            _placedCentre = centre;

            // A ten metre wall of bamboo standing in a twelve metre final circle would be the whole
            // arena, so the wall thins as the ring closes.
            float thickness = Mathf.Min(Thickness, InnerEdge * 0.5f);
            float mid = InnerEdge + thickness * 0.5f;

            transform.position = new Vector3(centre.x, 0f, centre.z);

            int segments = _segments.Count;
            float step = 360f / segments;

            // Each segment is a chord of the circle at its midline. Overlapped slightly, or the
            // corners between segments are gaps you can shoot - and walk - through.
            float chord = 2f * mid * Mathf.Tan(Mathf.PI / segments) * 1.12f;

            for (int i = 0; i < segments; i++)
            {
                Transform seg = _segments[i];
                if (!seg.gameObject.activeSelf) seg.gameObject.SetActive(true);

                float angle = i * step;
                float rad = angle * Mathf.Deg2Rad;
                var outward = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));

                // The wall stands a little lower than the culms, so the stalks crown it instead of
                // the whole thing ending on one flat line.
                float wallHeight = CurrentHeight * 0.86f;

                seg.localPosition = outward * mid + new Vector3(0f, wallHeight * 0.5f, 0f);
                seg.localRotation = Quaternion.Euler(0f, angle, 0f);
                seg.localScale = new Vector3(chord, wallHeight, thickness);
            }

            PlaceCulms(thickness);
        }

        /// <summary>
        /// Rides the culms around the inner face. They are the only bamboo anyone sees up close, so
        /// they carry the detail; the wall behind them carries the bulk.
        /// </summary>
        private void PlaceCulms(float thickness)
        {
            int count = _culms.Count;
            int fronds = count > 0 ? _leaves.Count / count : 0;
            float golden = 137.507764f;

            for (int i = 0; i < count; i++)
            {
                Transform culm = _culms[i];
                if (!culm.gameObject.activeSelf) culm.gameObject.SetActive(true);

                // Deterministic jitter from the index: a hedge planted on a ruler is not a forest.
                // The golden angle spreads the culms evenly however many there are and however far
                // the ring has closed, without any of them lining up into visible spokes.
                float angle = i * golden;
                float rad = angle * Mathf.Deg2Rad;
                var outward = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));

                float wobble = Mathf.Sin(i * 12.9898f);
                float lean = Mathf.Sin(i * 4.1414f) * 6f;
                float scale = 0.75f + Mathf.Abs(Mathf.Sin(i * 7.233f)) * 0.5f;

                // In FRONT of the wall, on the town side of it. Planted behind, they were buried
                // and only the tallest tips showed over the top - which read as a flat green wall
                // with a few sticks over it rather than as a grove.
                float radius = InnerEdge - 0.45f - Mathf.Abs(Mathf.Cos(i * 3.77f)) * 0.7f
                               + wobble * 0.35f;

                float height = CurrentHeight * scale;

                culm.localPosition = outward * radius + new Vector3(0f, height * 0.5f, 0f);
                culm.localScale = new Vector3(1f, height, 1f);
                culm.localRotation = Quaternion.Euler(lean * 0.4f, angle, lean);

                for (int f = 0; f < fronds; f++)
                {
                    Transform head = _leaves[i * fronds + f];
                    if (!head.gameObject.activeSelf) head.gameObject.SetActive(true);

                    // Sprays staggered down from the tip and splayed to alternate sides, so the top
                    // of a culm has a silhouette instead of an outline.
                    float drop = f * 0.16f;
                    float splay = (f % 2 == 0 ? 1f : -1f) * (18f + f * 7f);

                    head.localPosition = culm.localPosition + new Vector3(0f, height * (0.42f - drop), 0f);
                    head.localScale = Vector3.one *
                                      (0.7f + Mathf.Abs(Mathf.Sin((i + f) * 2.11f)) * 0.45f);
                    head.localRotation = Quaternion.Euler(
                        splay * 0.5f, angle + f * 61f, lean * 0.5f + splay);
                }
            }
        }
    }
}
