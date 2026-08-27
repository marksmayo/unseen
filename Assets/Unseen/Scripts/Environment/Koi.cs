using System.Collections.Generic;
using UnityEngine;

namespace Unseen.Environment
{
    /// <summary>
    /// A carp in the castle lake.
    ///
    /// Purely decorative, and the only moving thing in the town that nobody can interact with -
    /// which is the point. A lake with nothing in it is a puddle the size of a building; a lake
    /// with half a dozen fish turning slowly under the surface is somewhere people live.
    ///
    /// Kept off the simulation entirely. Koi are not perceived, cannot be heard, do not startle
    /// and do not block anything, so putting them on the server tick would be paying the
    /// authoritative clock for a fish. They run on the frame clock instead, and are advanced
    /// through a static so the tests can drive them without one.
    /// </summary>
    public sealed class Koi : MonoBehaviour
    {
        private static readonly List<Koi> Fish = new List<Koi>(32);

        /// <summary>How many are swimming. Diagnostics and tests.</summary>
        public static int Count => Fish.Count;

        [Tooltip("Centre of the water this fish is confined to.")]
        public Vector3 Centre;

        [Tooltip("Nearest and furthest the fish may swim from the centre, in metres. In a moat " +
                 "these are the island and the far bank.")]
        public Vector2 RadiusRange = new Vector2(6f, 12f);

        [Tooltip("Whether those bounds describe a SQUARE ring rather than a circular one. A moat " +
                 "round a castle is square, and a fish swimming a circle inside one crosses the " +
                 "corners of the island on dry land.")]
        public bool Square;

        [Tooltip("Water surface height. The fish swims below it and rises toward it to feed.")]
        public float SurfaceY;

        [Tooltip("Metres per second. Carp are unhurried and it matters that they look it.")]
        public float Speed = 0.55f;

        private float _angle;

        /// <summary>Where between the near bank and the far one this fish is swimming, 0 to 1.</summary>
        private float _lane;
        private float _laneTarget;
        private float _depth;
        private float _depthTarget;
        private float _clock;
        private float _phase;
        private int _direction = 1;

        /// <summary>
        /// Places a fish in a body of water and registers it.
        ///
        /// Explicit, not OnEnable: the town is generated in edit mode for every screenshot and
        /// probe in this project, and Unity runs no lifecycle callbacks there. A fish that
        /// registered itself in OnEnable would exist in a real game and nowhere else.
        /// </summary>
        public void Configure(Vector3 centre, Vector2 radiusRange, float surfaceY, int index,
            bool square = false)
        {
            Centre = centre;
            RadiusRange = radiusRange;
            SurfaceY = surfaceY;
            Square = square;

            _phase = index * 1.71f;
            _angle = index * 2.399f;
            _direction = (index & 1) == 0 ? 1 : -1;

            _lane = Frac(index * 0.618f);
            _laneTarget = _lane;

            _depth = 0.35f + Frac(index * 0.382f) * 0.5f;
            _depthTarget = _depth;

            // Placed straight away rather than waiting for the first tick, so a fish is in the
            // water in a screenshot taken before anything has been advanced.
            Place();

            if (!Fish.Contains(this)) Fish.Add(this);
        }

        private void OnDestroy() => Fish.Remove(this);

        private void Update()
        {
            Advance(Time.deltaTime);
        }

        /// <summary>Advances every fish. The tests call this; the game gets it from Update.</summary>
        public static void AdvanceAll(float dt)
        {
            for (int i = 0; i < Fish.Count; i++)
                if (Fish[i] != null) Fish[i].Advance(dt);
        }

        public static void ClearAll() => Fish.Clear();

        /// <summary>
        /// One step of swimming.
        ///
        /// A circling path with a drifting radius and depth rather than a wander with a heading.
        /// A carp in a pond does not pick destinations - it turns slowly and keeps turning, and a
        /// wandering fish reads as a fish looking for the exit.
        /// </summary>
        public void Advance(float dt)
        {
            if (dt <= 0f) return;

            _clock += dt;

            // New radius and depth to drift toward, every so often. Offset per fish so a shoal
            // does not breathe in and out together.
            if (_clock > 4f + _phase % 3f)
            {
                _clock = 0f;
                _laneTarget = Frac(_angle * 3.7f + _phase);
                _depthTarget = 0.16f + Frac(_angle * 5.3f + _phase) * 0.7f;
            }

            _lane = Mathf.MoveTowards(_lane, _laneTarget, dt * 0.06f);
            _depth = Mathf.MoveTowards(_depth, _depthTarget, dt * 0.22f);

            // Angular speed from linear speed, so a fish on the outside of the lake is not sprinting
            // to keep up with one near the middle.
            float safeRadius = Mathf.Max(0.5f, RadiusAt(_angle, _lane));
            _angle += _direction * (Speed / safeRadius) * dt;

            Place();
        }

        /// <summary>
        /// How far from the centre the fish is, at a given bearing and lane.
        ///
        /// In a square moat the banks are not at a constant distance: along a diagonal both the
        /// island and the outer wall are a factor of root two further away. Scaling the whole ring
        /// by that factor turns the fish's circle into a rounded square that follows the moat,
        /// which is the difference between a carp swimming a pond and a carp swimming through the
        /// corner of a castle.
        /// </summary>
        private float RadiusAt(float angle, float lane)
        {
            // A slow weave across the width, so the path is not a perfect track.
            float weave = Mathf.Sin(angle * 2.3f + _phase) * 0.12f;
            float across = Mathf.Clamp01(lane + weave);

            float radius = Mathf.Lerp(RadiusRange.x, RadiusRange.y, across);
            if (!Square) return radius;

            float reach = Mathf.Max(Mathf.Abs(Mathf.Cos(angle)), Mathf.Abs(Mathf.Sin(angle)));
            return radius / Mathf.Max(0.35f, reach);
        }

        private void Place()
        {
            float radius = Mathf.Max(0.4f, RadiusAt(_angle, _lane));

            var at = new Vector3(
                Centre.x + Mathf.Cos(_angle) * radius,
                SurfaceY - _depth,
                Centre.z + Mathf.Sin(_angle) * radius);

            transform.position = at;

            // Facing along the path, with a lazy roll into the turn.
            var heading = new Vector3(-Mathf.Sin(_angle) * _direction, 0f,
                Mathf.Cos(_angle) * _direction);

            transform.rotation = Quaternion.LookRotation(heading, Vector3.up) *
                                 Quaternion.Euler(0f, 0f, Mathf.Sin(_angle * 3f + _phase) * 9f);
        }

        private static float Frac(float value)
        {
            float f = value - Mathf.Floor(value);
            return f;
        }
    }
}
