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
        [Tooltip("The visible rope, enabled while attached. Built on demand if nothing has " +
                 "assigned one - see BuildRope.")]
        public LineRenderer Rope;

        [Tooltip("Head on the end of the rope, parked at the anchor while it is attached.")]
        public Transform HookHead;

        [Tooltip("Thickness of the rope in metres.")]
        public float RopeWidth = 0.035f;

        /// <summary>Slack in the rope at rest, in metres. A rope under load is straight.</summary>
        private const float Sag = 0.55f;

        /// <summary>Points along the rope. Two would be a taut line; a rope hangs.</summary>
        private const int RopeSegments = 12;

        public bool Attached { get; private set; }
        public float3 Anchor { get; private set; }
        public float CooldownRemaining { get; private set; }

        /// <summary>True on the tick the hook attached. Consumed by the motor to emit the fire sound.</summary>
        public bool JustFired { get; private set; }

        private void Awake()
        {
            if (Rope == null) Rope = GetComponentInChildren<LineRenderer>();

            // And if there still is not one, make it. Nothing in the project ever assigned this,
            // which is why the rope was never once visible - see BuildRope.
            if (Rope == null) BuildRope();

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

            if (!Attached || Rope == null) return;

            // Drawn as a hanging curve rather than a straight line between two points.
            //
            // Two positions is a taut wire, and a taut wire is what this looked like when it was
            // visible at all: players appeared to be flying on nothing. A rope under tension is
            // nearly straight but not quite, and the small sag is most of what makes it read as
            // rope rather than as a debug line.
            float3 from = handPosition;
            float3 to = Anchor;

            for (int i = 0; i < RopeSegments; i++)
            {
                float t = i / (float)(RopeSegments - 1);
                float3 point = math.lerp(from, to, t);

                // A parabola, deepest in the middle, and less of it the more the rope is stretched.
                point.y -= math.sin(t * math.PI) * Sag;
                Rope.SetPosition(i, point);
            }

            if (HookHead == null) return;

            HookHead.position = Anchor;

            // Pointed back down the rope, so the head looks buried in what it is holding.
            float3 along = handPosition - Anchor;
            if (math.lengthsq(along) > 0.0001f)
                HookHead.rotation = Quaternion.LookRotation(math.normalize(along), Vector3.up);
        }

        /// <summary>
        /// Builds the rope and the hook head if nothing has supplied them.
        ///
        /// They were never supplied. The drawing code above has been here from the start and the
        /// LineRenderer it draws into was always null, so the rope has never once been visible and
        /// every grapple looked like a player flying on an invisible wire. Nothing assigned it
        /// because nothing was ever written to.
        /// </summary>
        private void BuildRope()
        {
            var host = new GameObject("GrappleRope");
            host.transform.SetParent(transform, false);

            Rope = host.AddComponent<LineRenderer>();
            Rope.useWorldSpace = true;
            Rope.positionCount = RopeSegments;
            Rope.numCapVertices = 2;
            Rope.alignment = LineAlignment.View;
            Rope.textureMode = LineTextureMode.Tile;
            Rope.startWidth = RopeWidth;
            Rope.endWidth = RopeWidth * 0.8f;
            Rope.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Rope.receiveShadows = false;
            Rope.enabled = false;

            Rope.material = RopeMaterial();

            // The head: a small dark wedge and two flukes, so there is something at the far end
            // rather than a line stopping in mid-air.
            var head = new GameObject("GrappleHead");
            head.transform.SetParent(transform, false);
            HookHead = head.transform;

            AddHeadPart(head.transform, "Shank", new Vector3(0f, 0f, 0.09f),
                new Vector3(0.05f, 0.05f, 0.18f));

            for (int i = -1; i <= 1; i += 2)
            {
                Transform fluke = AddHeadPart(head.transform, $"Fluke_{i}",
                    new Vector3(i * 0.055f, 0.02f, -0.03f), new Vector3(0.035f, 0.035f, 0.14f));
                fluke.localRotation = Quaternion.Euler(28f, i * 24f, 0f);
            }

            head.SetActive(false);
        }

        private Transform AddHeadPart(Transform parent, string name, Vector3 at, Vector3 size)
        {
            var part = new GameObject(name);
            part.transform.SetParent(parent, false);
            part.transform.localPosition = at;
            part.transform.localScale = size;

            part.AddComponent<MeshFilter>().sharedMesh = Unseen.Environment.BoxMeshFactory.Get(Vector3.one, 1f);

            var renderer = part.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = RopeMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            return part.transform;
        }

        private static Material _ropeMaterial;

        /// <summary>
        /// One shared unlit dark material for every rope in the match.
        ///
        /// Unlit on purpose: a lit line one centimetre wide, on a rope that swings through a dozen
        /// lantern pools a second, flickers between black and white. What a rope needs to be is
        /// consistently visible against both a night sky and a pale wall.
        /// </summary>
        private static Material RopeMaterial()
        {
            if (_ropeMaterial != null) return _ropeMaterial;

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");

            _ropeMaterial = new Material(shader) { name = "GrappleRope" };

            if (_ropeMaterial.HasProperty("_BaseColor"))
                _ropeMaterial.SetColor("_BaseColor", new Color(0.13f, 0.11f, 0.09f));
            if (_ropeMaterial.HasProperty("_Color"))
                _ropeMaterial.SetColor("_Color", new Color(0.13f, 0.11f, 0.09f));

            return _ropeMaterial;
        }

        private void SetRopeVisible(bool visible)
        {
            if (Rope != null)
            {
                Rope.positionCount = RopeSegments;
                Rope.enabled = visible;
            }

            if (HookHead != null) HookHead.gameObject.SetActive(visible);
        }
    }
}
