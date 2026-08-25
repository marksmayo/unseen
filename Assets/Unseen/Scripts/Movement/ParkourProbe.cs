using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;

namespace Unseen.Movement
{
    public struct LedgeHit
    {
        public bool Found;

        /// <summary>Top surface point of the ledge.</summary>
        public float3 Top;

        /// <summary>Outward normal of the wall below the ledge.</summary>
        public float3 WallNormal;

        /// <summary>Where the hands go while hanging.</summary>
        public float3 GrabPoint;

        public float Height;
    }

    /// <summary>
    /// Geometry queries behind the parkour system. Deliberately stateless and allocation-free so
    /// it can run for 64 agents on the server every tick without touching the managed heap.
    /// </summary>
    public static class ParkourProbe
    {
        private static readonly RaycastHit[] Scratch = new RaycastHit[4];

        /// <summary>Finds a climbable surface directly ahead.</summary>
        public static bool FindWall(float3 chest, float3 forward, float distance, out RaycastHit hit)
        {
            return Physics.Raycast(chest, forward, out hit, distance, UnseenLayers.Climb, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Finds a mantle-able edge ahead: a wall within reach whose top surface is clear.
        /// Sweeps downward from above the wall to locate the lip.
        /// </summary>
        public static bool FindLedge(float3 feet, float3 forward, float reach, float minHeight, float maxHeight, out LedgeHit ledge)
        {
            ledge = default;

            float3 probeOrigin = feet + new float3(0f, minHeight, 0f);
            if (!Physics.Raycast(probeOrigin, forward, out RaycastHit wall, reach, UnseenLayers.Climb, QueryTriggerInteraction.Ignore))
                return false;

            float3 above = (float3)wall.point + forward * 0.25f + new float3(0f, maxHeight, 0f);
            if (!Physics.Raycast(above, Vector3.down, out RaycastHit top, maxHeight + 0.5f, UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                return false;

            float height = top.point.y - feet.y;
            if (height < minHeight || height > maxHeight) return false;
            if (Vector3.Dot(top.normal, Vector3.up) < 0.6f) return false;

            ledge.Found = true;
            ledge.Top = top.point;
            ledge.WallNormal = wall.normal;
            ledge.GrabPoint = (float3)top.point - forward * 0.3f;
            ledge.Height = height;
            return true;
        }

        /// <summary>Finds a rafter or beam overhead that can be grabbed and crawled along.</summary>
        public static bool FindRafter(float3 head, float reach, out RaycastHit hit)
        {
            return Physics.SphereCast(head, 0.25f, Vector3.up, out hit, reach,
                (1 << UnseenLayers.Rafter), QueryTriggerInteraction.Ignore);
        }

        /// <summary>True when a capsule of this size fits at this position - used before standing up or mantling.</summary>
        public static bool HasClearance(float3 feet, float radius, float height)
        {
            float3 bottom = feet + new float3(0f, radius + 0.02f, 0f);
            float3 top = feet + new float3(0f, math.max(height - radius, radius + 0.03f), 0f);
            return !Physics.CheckCapsule(bottom, top, radius * 0.95f, UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore);
        }

        /// <summary>Wall on either side suitable for a wall run. Returns the side as -1 (left) or +1 (right).</summary>
        public static int FindWallRunSide(float3 chest, float3 forward, float distance, out RaycastHit hit)
        {
            float3 right = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), forward));

            if (Physics.Raycast(chest, right, out hit, distance, UnseenLayers.Climb, QueryTriggerInteraction.Ignore))
                return 1;
            if (Physics.Raycast(chest, -right, out hit, distance, UnseenLayers.Climb, QueryTriggerInteraction.Ignore))
                return -1;

            hit = default;
            return 0;
        }

        /// <summary>Ground probe used for surface identification and fall detection.</summary>
        public static bool ProbeGround(float3 feet, float radius, float distance, out RaycastHit hit)
        {
            return Physics.SphereCast(feet + new float3(0f, radius + 0.05f, 0f), radius * 0.9f, Vector3.down,
                out hit, distance + 0.1f, UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore);
        }

        /// <summary>Number of solid surfaces between two points. Used by the grapple line check.</summary>
        public static int CountObstacles(float3 from, float3 to)
        {
            float3 delta = to - from;
            float len = math.length(delta);
            if (len < 0.01f) return 0;

            int count = Physics.RaycastNonAlloc(from, delta / len, Scratch, len,
                UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore);
            return count;
        }
    }
}
