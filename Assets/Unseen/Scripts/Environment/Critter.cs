using System.Collections.Generic;
using UnityEngine;
using Unseen.Core;

namespace Unseen.Environment
{
    /// <summary>
    /// A bird on a branch or a cat in an alley, and what it does when somebody blunders into it.
    ///
    /// This is not decoration. A flushed bird is the loudest thing in a quiet street and it is
    /// emitted through the same acoustic model as a footstep, which means it carries, it occludes,
    /// and somebody two rooftops away hears it and knows where you are without ever seeing you.
    /// The reward for moving carefully is that it does not happen: how close you can get before a
    /// critter bolts is scaled by the same stance loudness the footsteps use, so a crouched
    /// approach passes within a few metres and a sprint clears the whole courtyard.
    ///
    /// The geometry is a handful of boxes. At the distance these are seen from, a bird is a dark
    /// shape with a wing either side, and animating the wings costs two transform writes.
    /// </summary>
    public sealed class Critter : MonoBehaviour
    {
        public enum Species : byte
        {
            /// <summary>Perches high - branches, ridges, eaves - and leaves upward.</summary>
            Bird = 0,

            /// <summary>Stays on the ground and bolts along it.</summary>
            Animal = 1
        }

        /// <summary>Every critter in the level. The startle system walks this.</summary>
        public static readonly List<Critter> All = new List<Critter>(256);

        public Species Kind = Species.Bird;

        [Tooltip("How far a body at normal walking loudness has to come before this bolts.")]
        public float StartleRadius = 8f;

        [Tooltip("Seconds spent getting away before it is out of sight.")]
        public float FlightDuration = 2.2f;

        [Tooltip("Seconds before it comes back to its perch.")]
        public float ResettleDelay = 22f;

        private Vector3 _perch;
        private Quaternion _perchRotation;
        private Vector3 _flightDirection;
        private float _elapsed;
        private State _state = State.Settled;

        private Transform _leftWing;
        private Transform _rightWing;
        private Quaternion _leftRest;
        private Quaternion _rightRest;

        private enum State : byte
        {
            Settled = 0,
            Fleeing = 1,
            Gone = 2
        }

        /// <summary>True while it is on its perch and can be startled.</summary>
        public bool IsSettled => _state == State.Settled;

        /// <summary>Where it sits when undisturbed.</summary>
        public Vector3 Perch => _perch;

        /// <summary>Remembers the authored pose and registers for startling.</summary>
        public void Configure(Species kind, Transform leftWing, Transform rightWing)
        {
            Kind = kind;
            _perch = transform.localPosition;
            _perchRotation = transform.localRotation;

            _leftWing = leftWing;
            _rightWing = rightWing;
            if (_leftWing != null) _leftRest = _leftWing.localRotation;
            if (_rightWing != null) _rightRest = _rightWing.localRotation;

            // Registered here rather than in OnEnable. The town is generated in edit mode for every
            // screenshot and headless test in this project, and Unity does not run lifecycle
            // callbacks in edit mode - a critter that registered itself in OnEnable would exist in
            // a real game and be invisible to every test of it.
            if (!All.Contains(this)) All.Add(this);
        }

        private void OnDestroy()
        {
            All.Remove(this);
        }

        /// <summary>
        /// Sends it away from a point. Returns false if it was already gone, so the caller knows
        /// whether to make a noise about it.
        /// </summary>
        public bool Flush(Vector3 from)
        {
            if (_state != State.Settled) return false;

            _state = State.Fleeing;
            _elapsed = 0f;

            Vector3 away = transform.position - from;
            away.y = 0f;

            if (away.sqrMagnitude < 0.01f) away = transform.forward;
            away.Normalize();

            // A bird goes up and over; something on four legs stays low and takes a corner.
            _flightDirection = Kind == Species.Bird
                ? (away + Vector3.up * 1.6f).normalized
                : Quaternion.Euler(0f, Random.Range(-40f, 40f), 0f) * away;

            return true;
        }

        /// <summary>
        /// Moves it along. Driven by the startle system rather than by Update, so the headless
        /// tests see the same motion the game does.
        /// </summary>
        public void Advance(float dt)
        {
            if (_state == State.Settled) return;

            _elapsed += dt;

            if (_state == State.Fleeing)
            {
                float speed = Kind == Species.Bird ? 9f : 6.5f;

                // Slowing as it goes, so it reads as getting away rather than being fired out of a
                // cannon at constant velocity.
                float fade = Mathf.Clamp01(1f - _elapsed / Mathf.Max(0.1f, FlightDuration));
                transform.position += _flightDirection * (speed * fade * fade * dt);

                if (Kind == Species.Bird) BeatWings();
                else transform.Rotate(0f, 220f * dt, 0f, Space.Self);

                if (_elapsed < FlightDuration) return;

                _state = State.Gone;
                _elapsed = 0f;
                gameObject.SetActive(false);
                return;
            }

            if (_elapsed < ResettleDelay) return;

            // Back to the branch, wings folded, ready to be startled again.
            transform.localPosition = _perch;
            transform.localRotation = _perchRotation;

            if (_leftWing != null) _leftWing.localRotation = _leftRest;
            if (_rightWing != null) _rightWing.localRotation = _rightRest;

            gameObject.SetActive(true);
            _state = State.Settled;
            _elapsed = 0f;
        }

        /// <summary>Puts every critter back on its perch. Called when a match restarts.</summary>
        public static void ResetAll()
        {
            for (int i = 0; i < All.Count; i++)
            {
                Critter critter = All[i];
                if (critter == null) continue;

                critter.transform.localPosition = critter._perch;
                critter.transform.localRotation = critter._perchRotation;

                if (critter._leftWing != null) critter._leftWing.localRotation = critter._leftRest;
                if (critter._rightWing != null) critter._rightWing.localRotation = critter._rightRest;

                critter.gameObject.SetActive(true);
                critter._state = State.Settled;
                critter._elapsed = 0f;
            }
        }

        private void BeatWings()
        {
            if (_leftWing == null || _rightWing == null) return;

            // Roughly five beats a second, which is slow for a sparrow and fast enough to read.
            float flap = Mathf.Sin(_elapsed * 32f) * 55f;

            _leftWing.localRotation = _leftRest * Quaternion.Euler(0f, 0f, flap);
            _rightWing.localRotation = _rightRest * Quaternion.Euler(0f, 0f, -flap);
        }
    }
}
