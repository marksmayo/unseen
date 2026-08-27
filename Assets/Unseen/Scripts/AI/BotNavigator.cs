using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AI;
using Unseen.Core;

namespace Unseen.AI
{
    /// <summary>
    /// Path following for one bot. Uses the NavMesh when the level has one baked, and degrades to
    /// direct steering with whisker avoidance when it does not - which is always, here: the town is
    /// generated at runtime and nobody has ever run a bake on it, so the fallback IS the navigator.
    ///
    /// The fallback used to reverse. When every whisker was blocked it steered along -desired, which
    /// on the next tick put the obstacle far enough away that the whiskers came clear, so it steered
    /// forward again, and back, and forward - the metre-wide shuffle bots were observed doing for
    /// entire matches. Measured before the fix: a mean net-displacement-to-path-length ratio of
    /// 0.06, meaning sixty metres walked to finish four metres from the start.
    ///
    /// Two things fix it, and both are about refusing to change your mind every tick. An avoidance
    /// heading is committed to for a fraction of a second rather than re-derived continuously, and
    /// when boxed in the bot turns rather than reverses - a body that turns along a wall gets around
    /// it, a body that backs up re-approaches it. On top of that the navigator reports when it has
    /// stopped making progress at all, so the brain can abandon an unreachable destination instead
    /// of grinding against it forever.
    /// </summary>
    public sealed class BotNavigator
    {
        private const int MaxCorners = 16;

        /// <summary>Seconds of no progress toward the destination before it is declared hopeless.</summary>
        private const float StuckAfter = 2.5f;

        /// <summary>Metres of closing that count as progress. Above navigation jitter.</summary>
        private const float ProgressEpsilon = 0.4f;

        /// <summary>How long an avoidance heading is held before it is reconsidered.</summary>
        private const float CommitFor = 0.7f;

        private readonly Vector3[] _corners = new Vector3[MaxCorners];

        // Deliberately not a field initializer: Unity refuses to create a NavMeshPath from a
        // MonoBehaviour constructor or field initializer, and BotBrain owns one of these as a field.
        private NavMeshPath _path;

        private int _cornerCount;
        private int _cornerIndex;
        private float3 _destination;
        private float _nextRepathAt;

        private float3 _committed;
        private float _committedUntil;
        private int _turnSide = 1;

        private float _bestDistance = float.MaxValue;
        private float _lastProgressAt;

        public bool HasDestination { get; private set; }
        public bool UsingNavMesh { get; private set; }
        public float3 Destination => _destination;

        /// <summary>
        /// True when the bot has been unable to close on its destination for a while. The brain is
        /// expected to pick somewhere else rather than keep pushing.
        /// </summary>
        public bool Stuck { get; private set; }

        /// <summary>Seconds between repaths while chasing a moving destination.</summary>
        public float RepathInterval = 0.75f;

        /// <summary>
        /// Which way this bot prefers to turn around an obstacle. Set once per bot from its id so
        /// that two bots meeting in an alley do not mirror each other forever.
        /// </summary>
        public void SetTurnPreference(int seed)
        {
            _turnSide = (seed & 1) == 0 ? 1 : -1;
        }

        public void SetDestination(float3 from, float3 to, float now, bool force = false)
        {
            bool sameTarget = HasDestination && math.distancesq(to, _destination) < 1.5f;
            if (!force && sameTarget && now < _nextRepathAt) return;

            bool newTarget = !sameTarget;

            _destination = to;
            HasDestination = true;
            _nextRepathAt = now + RepathInterval;
            _cornerIndex = 0;
            _cornerCount = 0;
            UsingNavMesh = false;

            if (newTarget)
            {
                // A new destination gets a fresh patience budget. Without this, arriving somewhere
                // after a long walk and immediately setting off again would inherit the old clock
                // and be declared stuck on the first tick.
                Stuck = false;
                _bestDistance = math.distance(UnseenMath.Horizontal(from), UnseenMath.Horizontal(to));
                _lastProgressAt = now;
                _committedUntil = 0f;
            }

            if (!NavMesh.SamplePosition(from, out NavMeshHit fromHit, 3f, NavMesh.AllAreas)) return;
            if (!NavMesh.SamplePosition(to, out NavMeshHit toHit, 6f, NavMesh.AllAreas)) return;

            _path ??= new NavMeshPath();
            if (!NavMesh.CalculatePath(fromHit.position, toHit.position, NavMesh.AllAreas, _path)) return;
            if (_path.status == NavMeshPathStatus.PathInvalid) return;

            _cornerCount = _path.GetCornersNonAlloc(_corners);
            UsingNavMesh = _cornerCount > 1;
            _cornerIndex = UsingNavMesh ? 1 : 0;
        }

        public void Clear()
        {
            HasDestination = false;
            UsingNavMesh = false;
            Stuck = false;
            _cornerCount = 0;
            _cornerIndex = 0;
            _committedUntil = 0f;
            _bestDistance = float.MaxValue;
        }

        /// <summary>Planar steering direction toward the destination, or zero when there is nothing to do.</summary>
        public float3 Steering(float3 position, float now)
        {
            if (!HasDestination) return float3.zero;

            TrackProgress(position, now);

            float3 waypoint = _destination;

            if (UsingNavMesh && _cornerIndex < _cornerCount)
            {
                waypoint = _corners[_cornerIndex];
                if (math.lengthsq(UnseenMath.Horizontal(waypoint - position)) < 0.8f * 0.8f)
                {
                    _cornerIndex++;
                    if (_cornerIndex < _cornerCount) waypoint = _corners[_cornerIndex];
                }
            }

            float3 delta = UnseenMath.Horizontal(waypoint - position);
            if (math.lengthsq(delta) < 0.35f * 0.35f)
            {
                if (!UsingNavMesh || _cornerIndex >= _cornerCount - 1) HasDestination = false;
                return float3.zero;
            }

            return Avoid(position, math.normalizesafe(delta), now);
        }

        /// <summary>
        /// Watches the distance to the destination and gives up when it stops falling.
        ///
        /// Only closing counts. A bot squeezing along a wall is not making progress toward the far
        /// side of a building it cannot path around, and it is exactly that case - an unreachable
        /// destination held forever - that produced the pacing.
        /// </summary>
        private void TrackProgress(float3 position, float now)
        {
            float distance = math.distance(UnseenMath.Horizontal(position),
                UnseenMath.Horizontal(_destination));

            if (distance < _bestDistance - ProgressEpsilon)
            {
                _bestDistance = distance;
                _lastProgressAt = now;
                Stuck = false;
                return;
            }

            if (now - _lastProgressAt > StuckAfter) Stuck = true;
        }

        /// <summary>
        /// Whisker avoidance with commitment.
        ///
        /// Without a NavMesh this is the only thing stopping a bot walking into a wall; with one it
        /// still helps around doorways and other agents. The sweep tries this bot's preferred side
        /// first, so a body follows a wall consistently instead of picking a different way past the
        /// same corner every tick, and the chosen heading is held for a fraction of a second.
        /// </summary>
        private float3 Avoid(float3 position, float3 desired, float now)
        {
            float3 origin = position + new float3(0f, 0.9f, 0f);
            const float probe = 1.6f;

            if (!Blocked(origin, desired, probe))
            {
                // The way ahead is open. Drop any commitment: hugging a wall past the point where
                // the wall has ended is its own kind of stuck.
                _committedUntil = 0f;
                return desired;
            }

            // Still boxed in and the committed heading is still viable - keep going that way.
            if (now < _committedUntil && !Blocked(origin, _committed, probe)) return _committed;

            for (int i = 1; i <= 5; i++)
            {
                float angle = i * 30f;

                float3 preferred = Rotate(desired, angle * _turnSide);
                if (!Blocked(origin, preferred, probe)) return Commit(preferred, now);

                float3 other = Rotate(desired, -angle * _turnSide);
                if (!Blocked(origin, other, probe)) return Commit(other, now);
            }

            // Nothing within 150 degrees is open: a corner, or a doorway too tight to whisker
            // through. Stand still.
            //
            // The obvious move here is to turn ninety degrees and commit to it, and that is what
            // this did first - but that heading is not verified clear either, so a bot in a corner
            // walked into the wall and held there for the commitment window. The character
            // controller lost that argument often enough to wedge an agent 0.2 m into a compound
            // wall, which the drop test caught. Pushing at geometry is never better than waiting:
            // no progress is made either way, and after a couple of seconds the stuck detector
            // hands the brain a different destination.
            _turnSide = -_turnSide;
            _committedUntil = 0f;
            return float3.zero;
        }

        private float3 Commit(float3 heading, float now)
        {
            _committed = heading;
            _committedUntil = now + CommitFor;
            return heading;
        }

        private static bool Blocked(float3 origin, float3 direction, float distance)
        {
            return Physics.Raycast(origin, direction, distance, UnseenLayers.WorldGeometry,
                QueryTriggerInteraction.Ignore);
        }

        private static float3 Rotate(float3 direction, float degrees)
        {
            float r = degrees * UnseenMath.Deg2Rad;
            float c = math.cos(r);
            float s = math.sin(r);
            return new float3(direction.x * c + direction.z * s, direction.y, -direction.x * s + direction.z * c);
        }
    }
}
