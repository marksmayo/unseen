using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AI;
using Unseen.Core;

namespace Unseen.AI
{
    /// <summary>
    /// Path following for one bot. Uses the NavMesh when the level has one baked, and degrades to
    /// direct steering with whisker avoidance when it does not - which keeps bots functional in a
    /// procedurally generated greybox before anyone has run a bake.
    /// </summary>
    public sealed class BotNavigator
    {
        private const int MaxCorners = 16;

        private readonly Vector3[] _corners = new Vector3[MaxCorners];

        // Deliberately not a field initializer: Unity refuses to create a NavMeshPath from a
        // MonoBehaviour constructor or field initializer, and BotBrain owns one of these as a field.
        private NavMeshPath _path;

        private int _cornerCount;
        private int _cornerIndex;
        private float3 _destination;
        private float _nextRepathAt;

        public bool HasDestination { get; private set; }
        public bool UsingNavMesh { get; private set; }
        public float3 Destination => _destination;

        /// <summary>Seconds between repaths while chasing a moving destination.</summary>
        public float RepathInterval = 0.75f;

        public void SetDestination(float3 from, float3 to, float now, bool force = false)
        {
            bool sameTarget = HasDestination && math.distancesq(to, _destination) < 1.5f;
            if (!force && sameTarget && now < _nextRepathAt) return;

            _destination = to;
            HasDestination = true;
            _nextRepathAt = now + RepathInterval;
            _cornerIndex = 0;
            _cornerCount = 0;
            UsingNavMesh = false;

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
            _cornerCount = 0;
            _cornerIndex = 0;
        }

        /// <summary>Planar steering direction toward the destination, or zero when there is nothing to do.</summary>
        public float3 Steering(float3 position)
        {
            if (!HasDestination) return float3.zero;

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

            return Avoid(position, math.normalizesafe(delta));
        }

        /// <summary>
        /// Whisker avoidance. Without a NavMesh this is the only thing stopping a bot walking into a
        /// wall; with one it still helps around doorways and other agents.
        /// </summary>
        private static float3 Avoid(float3 position, float3 desired)
        {
            float3 origin = position + new float3(0f, 0.9f, 0f);
            const float probe = 1.6f;

            if (!Physics.Raycast(origin, desired, probe, UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                return desired;

            for (int i = 1; i <= 3; i++)
            {
                float angle = i * 30f;
                float3 left = Rotate(desired, -angle);
                if (!Physics.Raycast(origin, left, probe, UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                    return left;

                float3 right = Rotate(desired, angle);
                if (!Physics.Raycast(origin, right, probe, UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                    return right;
            }

            return -desired;
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
