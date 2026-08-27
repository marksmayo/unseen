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

        /// <summary>
        /// Strolls begun since boot. Diagnostics only.
        ///
        /// Exists because "how many critters are not where they started" measures the wrong thing:
        /// every stroll target is picked relative to the critter's HOME, so one that has wandered
        /// twenty times is no further from home than one that wandered once, and one caught
        /// mid-stroll back across its own patch looks like it never moved at all.
        /// </summary>
        public static int StrollsStarted;

        public Species Kind = Species.Bird;

        [Tooltip("How far a body at normal walking loudness has to come before this bolts.")]
        public float StartleRadius = 8f;

        [Tooltip("Seconds spent getting away before it is out of sight.")]
        public float FlightDuration = 2.2f;

        /// <summary>How long THIS flight lasts. Set when it starts, because a bird's is far longer
        /// than an animal's dash.</summary>
        private float _flightFor = 2.2f;

        [Tooltip("Seconds before it comes back to its perch.")]
        public float ResettleDelay = 22f;

        [Tooltip("How far from home it will wander while undisturbed, in metres.")]
        public float WanderRadius = 9f;

        [Tooltip("Shortest and longest it stands still between moves, in seconds.\n\n" +
                 "Long. A bird that repositions every couple of seconds reads as a glitch, and a " +
                 "street of them twitching in unison reads as a broken game. The point of this is " +
                 "that the town is alive, which is a thing you notice out of the corner of your " +
                 "eye rather than a thing that demands attention.")]
        public Vector2 RestSeconds = new Vector2(18f, 55f);

        [Tooltip("Metres per second while moving. An unhurried walk or a series of hops.")]
        public float StrollSpeed = 1.15f;

        private Vector3 _perch;
        private Quaternion _perchRotation;
        private Vector3 _flightDirection;
        private float _elapsed;
        private State _state = State.Settled;

        private Vector3 _home;
        private Vector3 _strollFrom;
        private Vector3 _strollTo;
        private float _strollDuration;
        private float _restUntil;
        private float _clock;

        private Transform _leftWing;
        private Transform _rightWing;
        private Quaternion _leftRest;
        private Quaternion _rightRest;

        private enum State : byte
        {
            Settled = 0,
            Fleeing = 1,
            Gone = 2,

            /// <summary>Undisturbed and moving to a new spot nearby.</summary>
            Strolling = 3
        }

        /// <summary>True while it is going about its business and can still be startled.</summary>
        public bool IsSettled => _state == State.Settled || _state == State.Strolling;

        /// <summary>Where it sits when undisturbed.</summary>
        public Vector3 Perch => _perch;

        /// <summary>Remembers the authored pose and registers for startling.</summary>
        public void Configure(Species kind, Transform leftWing, Transform rightWing)
        {
            Kind = kind;
            _perch = transform.localPosition;
            _perchRotation = transform.localRotation;
            _home = _perch;

            // Staggered, or every critter in the town sets off on the same tick.
            _restUntil = Random.Range(0f, RestSeconds.y);

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
            // Strolling counts. IsSettled includes it, so rejecting it here would have the
            // startle system pick a wandering bird as its victim and then quietly do nothing.
            if (_state != State.Settled && _state != State.Strolling) return false;

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

            // A bird climbs for long enough to actually get away. An animal's bolt is short and
            // sharp, because it is going to stop somewhere you can still see it and the run has to
            // read as a dash rather than a migration.
            _flightFor = Kind == Species.Bird ? FlightDuration * 2.2f : FlightDuration * 0.65f;

            return true;
        }

        /// <summary>
        /// Moves it along. Driven by the startle system rather than by Update, so the headless
        /// tests see the same motion the game does.
        /// </summary>
        public void Advance(float dt)
        {
            _clock += dt;

            if (_state == State.Settled)
            {
                Rest();
                return;
            }

            if (_state == State.Strolling)
            {
                Stroll(dt);
                return;
            }

            _elapsed += dt;

            if (_state == State.Fleeing)
            {
                float speed = Kind == Species.Bird ? 9f : 6.5f;

                // Slowing as it goes, so it reads as getting away rather than being fired out of a
                // cannon at constant velocity.
                float fade = Mathf.Clamp01(1f - _elapsed / Mathf.Max(0.1f, _flightFor));
                transform.position += _flightDirection * (speed * fade * fade * dt);

                if (Kind == Species.Bird) BeatWings();
                else transform.Rotate(0f, 220f * dt, 0f, Space.Self);

                if (_elapsed < _flightFor) return;

                // What happens at the end of the run depends on whether it can leave.
                //
                // A four-legged animal cannot. It bolts to somewhere else on the ground and stays
                // there, and it used to switch itself off instead - so a rabbit you disturbed
                // blinked out of existence two seconds later and reappeared on its old spot twenty
                // seconds after that. Vanishing is not a way of leaving a room.
                if (Kind != Species.Bird)
                {
                    Settle();
                    return;
                }

                // A bird can leave, but only once it is genuinely out of sight. Switched off at the
                // end of a two second climb it was still close enough to be a bird-shaped hole in
                // the air; it now keeps going until it is well up and well away.
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

        /// <summary>
        /// Stops where it is and calls that home.
        ///
        /// Used by an animal at the end of a bolt. The point on the ground under it becomes the new
        /// perch, so the next stroll starts from where it actually stands rather than from a spot
        /// it left twenty seconds ago - and so it can be startled again from here.
        /// </summary>
        private void Settle()
        {
            // Put its feet on whatever it has run onto. A bolt does not follow the ground, so a
            // rabbit that crossed a kerb ends its run a step above or below the floor.
            Vector3 at = transform.position;

            if (Physics.Raycast(at + Vector3.up * 2f, Vector3.down, out RaycastHit ground, 6f,
                    UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                at.y = ground.point.y + 0.06f;

            transform.position = at;

            _perch = transform.localPosition;
            _perchRotation = transform.localRotation;

            if (_leftWing != null) _leftWing.localRotation = _leftRest;
            if (_rightWing != null) _rightWing.localRotation = _rightRest;

            _state = State.Settled;
            _elapsed = 0f;

            // Wary for a while after being disturbed. It has just run for its life; it is not going
            // to start browsing again immediately.
            _restUntil = _clock + Random.Range(RestSeconds.x, RestSeconds.y) * 0.5f;
        }

        /// <summary>Puts every critter back on its perch. Called when a match restarts.</summary>
        public static void ResetAll()
        {
            for (int i = 0; i < All.Count; i++)
            {
                Critter critter = All[i];
                if (critter == null) continue;

                // Home, not wherever it had wandered to: a new match should look like the first
                // one, not like the leftovers of the last.
                critter._perch = critter._home;
                critter.transform.localPosition = critter._home;
                critter.transform.localRotation = critter._perchRotation;

                if (critter._leftWing != null) critter._leftWing.localRotation = critter._leftRest;
                if (critter._rightWing != null) critter._rightWing.localRotation = critter._rightRest;

                critter.gameObject.SetActive(true);
                critter._state = State.Settled;
                critter._elapsed = 0f;
                critter._restUntil = critter._clock + Random.Range(0f, critter.RestSeconds.y);
            }
        }

        /// <summary>
        /// Standing about. When the rest is over, picks somewhere nearby and sets off.
        ///
        /// Somewhere NEARBY, and not far: a third of the wander radius or so, so a critter drifts
        /// around its patch over a match rather than crossing the town. It is a bird on a street,
        /// not a migrating one.
        /// </summary>
        private void Rest()
        {
            if (_clock < _restUntil) return;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                float angle = Random.Range(0f, Mathf.PI * 2f);

                // Closer in on later attempts. A critter in a cramped spot - a bird on a hedge with
                // road on three sides, an animal in an alley - can reject every far target and
                // still have somewhere to go a metre away, and giving up because the first few
                // throws were long is how they end up standing still.
                float shrink = 1f - attempt / 10f;
                float reach = WanderRadius * Random.Range(0.2f, 0.45f) * shrink;
                Vector3 target = _home + new Vector3(Mathf.Sin(angle) * reach, 0f, Mathf.Cos(angle) * reach);

                // Ground it. A bird keeps roughly to the height it was perched at - it hops along a
                // wall or a branch - and an animal follows whatever it is walking on.
                Vector3 probe = transform.parent != null
                    ? transform.parent.TransformPoint(target)
                    : target;

                float wantY = Kind == Species.Bird ? _home.y : target.y;

                if (Physics.Raycast(probe + Vector3.up * 6f, Vector3.down, out RaycastHit ground, 14f,
                        UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                {
                    Vector3 local = transform.parent != null
                        ? transform.parent.InverseTransformPoint(ground.point)
                        : ground.point;

                    // Birds only accept a landing within a metre of their perch height, so they do
                    // not walk down off a hedge into the road.
                    if (Kind == Species.Bird && Mathf.Abs(local.y - _home.y) > 1f) continue;

                    wantY = Kind == Species.Bird ? _home.y : local.y;
                    target = new Vector3(target.x, wantY, target.z);
                }
                else if (Kind == Species.Animal)
                {
                    continue;
                }
                else
                {
                    target = new Vector3(target.x, wantY, target.z);
                }

                _strollFrom = transform.localPosition;
                _strollTo = target;
                StrollsStarted++;

                float distance = Vector3.Distance(_strollFrom, _strollTo);
                _strollDuration = Mathf.Max(0.35f, distance / Mathf.Max(0.1f, StrollSpeed));
                _elapsed = 0f;
                _state = State.Strolling;
                return;
            }

            // Nowhere to go this time. Try again SOON rather than after a full rest.
            //
            // A failed search used to cost the critter its whole next rest period - eighteen to
            // fifty-five seconds - so a critter in an awkward spot spent almost all of its time
            // waiting to fail again. Measured over four minutes, 289 critters managed 271 outings
            // between them, against the five or six apiece the rest interval implies: five chances
            // in six were being thrown away. The rest interval is a pacing decision about how often
            // a critter WANTS to move, and a search that found nowhere is not that.
            _restUntil = _clock + Random.Range(1.5f, 4f);
        }

        /// <summary>
        /// Moving to the chosen spot. Smoothed at both ends, so it is a walk rather than a slide,
        /// and it turns to face where it is going before it gets there.
        /// </summary>
        private void Stroll(float dt)
        {
            _elapsed += dt;

            float t = Mathf.Clamp01(_elapsed / _strollDuration);
            float eased = t * t * (3f - 2f * t);

            Vector3 at = Vector3.Lerp(_strollFrom, _strollTo, eased);

            // A bird hops: a little arc between the two points rather than a slide along the wall.
            // An animal gets a much smaller bob, from its legs rather than from flight.
            float bobHeight = Kind == Species.Bird ? 0.28f : 0.06f;
            at.y += Mathf.Sin(t * Mathf.PI) * bobHeight;

            transform.localPosition = at;

            Vector3 travel = _strollTo - _strollFrom;
            travel.y = 0f;
            if (travel.sqrMagnitude > 0.0004f)
            {
                Quaternion facing = Quaternion.LookRotation(travel.normalized, Vector3.up);
                transform.localRotation = Quaternion.Slerp(transform.localRotation, facing,
                    1f - Mathf.Exp(-6f * dt));
            }

            if (Kind == Species.Bird) BeatWings();

            if (t < 1f) return;

            transform.localPosition = _strollTo;
            _perch = _strollTo;
            _perchRotation = transform.localRotation;

            if (_leftWing != null) _leftWing.localRotation = _leftRest;
            if (_rightWing != null) _rightWing.localRotation = _rightRest;

            _state = State.Settled;
            _restUntil = _clock + Random.Range(RestSeconds.x, RestSeconds.y);
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
