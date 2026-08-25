using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;

namespace Unseen.Movement
{
    /// <summary>
    /// Silent vertical traversal. The hook itself makes almost no noise, but reeling in near an
    /// enemy adds a loud rope-and-tile penalty, so it is fast escape and slow approach.
    /// </summary>
    public sealed class GrapplingHook : MonoBehaviour
    {
        [Tooltip("Rope renderer, enabled while attached.")]
        public LineRenderer Rope;

        public bool Attached { get; private set; }
        public float3 Anchor { get; private set; }
        public float CooldownRemaining { get; private set; }

        /// <summary>True on the tick the hook attached. Consumed by the motor to emit the fire sound.</summary>
        public bool JustFired { get; private set; }

        private void Awake()
        {
            if (Rope == null) Rope = GetComponentInChildren<LineRenderer>();
            SetRopeVisible(false);
        }

        /// <summary>
        /// Attempts to attach along the aim direction. Anchors must be on the GrappleAnchor layer,
        /// which keeps roof traversal an authored route rather than a way to climb any wall.
        /// </summary>
        [Tooltip("Aim forgiveness. A pin-thin ray demands pixel-perfect aim at a distant beam.")]
        public float AimRadius = 0.7f;

        [Tooltip("Half-angle of the fallback search cone, in degrees.")]
        public float AimConeDegrees = 14f;

        [Tooltip("How close counts as arrived. Generous, so a reel always terminates.")]
        public float ArrivalRadius = 1.5f;

        private static readonly Collider[] NearbyAnchors = new Collider[32];

        public bool TryFire(float3 origin, float3 direction, float range)
        {
            JustFired = false;
            if (Attached || CooldownRemaining > 0f) return false;

            // Thick cast rather than a ray: an anchor 30 m away subtends a couple of degrees, and
            // a hair-thin ray made the grapple feel broken rather than demanding.
            if (Physics.SphereCast(origin, AimRadius, direction, out RaycastHit hit, range,
                    UnseenLayers.Grapple, QueryTriggerInteraction.Ignore) &&
                RopeIsClear(origin, hit.point))
                return Attach(hit.point);

            // Fallback: the best anchor inside a cone around the aim. This is what makes the hook
            // usable from a street, where the only clear line to a beam is a few degrees wide.
            int found = Physics.OverlapSphereNonAlloc(origin, range, NearbyAnchors,
                UnseenLayers.Grapple, QueryTriggerInteraction.Ignore);
            if (found == 0) return false;

            float cone = math.cos(math.radians(AimConeDegrees));
            float3 aim = math.normalizesafe(direction);
            float bestScore = cone;
            float3 bestPoint = float3.zero;
            bool any = false;

            for (int i = 0; i < found; i++)
            {
                Collider anchor = NearbyAnchors[i];
                if (anchor == null) continue;

                float3 point = anchor.ClosestPoint(origin + aim * range);
                float3 to = point - origin;
                float distance = math.length(to);
                if (distance < 0.5f || distance > range) continue;

                float alignment = math.dot(to / distance, aim);
                if (alignment <= bestScore) continue;
                if (!RopeIsClear(origin, point)) continue;

                bestScore = alignment;
                bestPoint = point;
                any = true;
            }

            return any && Attach(bestPoint);
        }

        /// <summary>The rope must have a clear path; you cannot hook through a roof.</summary>
        private static bool RopeIsClear(float3 origin, float3 target)
        {
            if (!Physics.Linecast((Vector3)origin, (Vector3)target, out RaycastHit blocker,
                    (1 << UnseenLayers.Default) | (1 << UnseenLayers.Occluder),
                    QueryTriggerInteraction.Ignore))
                return true;

            // A metre of slack: a hook that catches the eave a hand's width from the bracket has
            // caught the bracket. Tighter than this and every shot at a roof edge is refused.
            return math.distance(blocker.point, target) <= 1.0f;
        }

        private bool Attach(float3 point)
        {
            Attached = true;
            JustFired = true;
            Anchor = point;
            SetRopeVisible(true);
            return true;
        }

        /// <summary>
        /// Whether a shot would connect right now, without firing. The HUD uses this to tell the
        /// player an anchor is in reach, which is the difference between a hook that feels precise
        /// and one that feels broken.
        /// </summary>
        public bool HasTarget(float3 origin, float3 direction, float range)
        {
            if (Attached || CooldownRemaining > 0f) return false;

            if (Physics.SphereCast(origin, AimRadius, direction, out RaycastHit hit, range,
                    UnseenLayers.Grapple, QueryTriggerInteraction.Ignore) &&
                RopeIsClear(origin, hit.point))
                return true;

            int found = Physics.OverlapSphereNonAlloc(origin, range, NearbyAnchors,
                UnseenLayers.Grapple, QueryTriggerInteraction.Ignore);
            float cone = math.cos(math.radians(AimConeDegrees));
            float3 aim = math.normalizesafe(direction);

            for (int i = 0; i < found; i++)
            {
                Collider anchor = NearbyAnchors[i];
                if (anchor == null) continue;

                float3 point = anchor.ClosestPoint(origin + aim * range);
                float3 to = point - origin;
                float distance = math.length(to);
                if (distance < 0.5f || distance > range) continue;
                if (math.dot(to / distance, aim) <= cone) continue;
                if (RopeIsClear(origin, point)) return true;
            }

            return false;
        }

        /// <summary>Velocity that pulls the agent toward the anchor, or zero once it has arrived.</summary>
        public float3 ReelVelocity(float3 position, float speed, out bool arrived)
        {
            arrived = false;
            if (!Attached) return float3.zero;

            float3 delta = Anchor - position;
            float distance = math.length(delta);
            if (distance < ArrivalRadius)
            {
                arrived = true;
                return float3.zero;
            }

            float3 dir = delta / distance;

            // Bias upward so the agent clears the eave instead of slamming into it - but ONLY
            // while still below the anchor.
            //
            // Forcing an upward component unconditionally pointed the reel away from the anchor
            // the moment the agent rose past it: the distance then grew every tick, "arrived"
            // never fired, and the agent climbed at reel speed inside the Grapple state where
            // gravity does not apply. It ended up pinned against the world-bounds ceiling. A
            // rising ninja who cannot come down is a worse bug than a ninja who clips an eave.
            if (dir.y > -0.05f) dir.y = math.max(dir.y, 0.35f);

            return math.normalize(dir) * speed;
        }

        public void Release(float cooldown)
        {
            Attached = false;
            JustFired = false;
            CooldownRemaining = math.max(CooldownRemaining, cooldown);
            SetRopeVisible(false);
        }

        public void Tick(float dt, float3 handPosition)
        {
            if (CooldownRemaining > 0f) CooldownRemaining = math.max(0f, CooldownRemaining - dt);
            JustFired = false;

            if (Attached && Rope != null)
            {
                Rope.SetPosition(0, handPosition);
                Rope.SetPosition(1, Anchor);
            }
        }

        private void SetRopeVisible(bool visible)
        {
            if (Rope == null) return;
            Rope.positionCount = 2;
            Rope.enabled = visible;
        }
    }
}
