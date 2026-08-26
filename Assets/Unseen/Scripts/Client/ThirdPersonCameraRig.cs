using UnityEngine;
using Unseen.Core;

namespace Unseen.Client
{
    /// <summary>
    /// Third-person camera that orbits behind the ninja.
    ///
    /// The distance logic is asymmetric on purpose. A symmetric spring plus a collision cast that
    /// starts inside the character oscillates: one frame the cast overlaps a wall and reports zero
    /// distance, the next it does not, so the camera slams between minimum and full distance every
    /// frame and the whole view shakes. So: pull in immediately when something is in the way, ease
    /// back out slowly when it clears, and never let a degenerate (zero-distance) hit set the
    /// distance at all.
    /// </summary>
    public sealed class ThirdPersonCameraRig : MonoBehaviour
    {
        public Transform Follow;
        public PlayerInputSource Input;

        [Header("Framing")]
        [Tooltip("Distance behind the character with a clear line of sight.")]
        public float Distance = 4.5f;

        [Tooltip("Closest the camera may sit. Must exceed the character's own girth or the view ends up inside them.")]
        public float MinDistance = 2.4f;

        [Tooltip("Height of the orbit pivot above the character's feet.")]
        public float PivotHeight = 1.5f;

        [Tooltip("How far the pivot drops when crouched. Crouching has to be visible from the " +
                 "player's seat, and the capsule shrinking is not something you can see.")]
        public float CrouchPivotDrop = 0.42f;

        [Tooltip("How far the pivot drops when prone.")]
        public float PronePivotDrop = 0.95f;

        /// <summary>Set by the bootstrap from the local agent's stance.</summary>
        public bool Prone;

        [Tooltip("How quickly the pivot follows a stance change.")]
        public float StanceSmoothing = 8f;

        /// <summary>Set by the bootstrap each frame from the local agent's stance.</summary>
        public bool Crouched;

        [Tooltip("Sideways offset so the ninja does not sit dead centre.")]
        public float ShoulderOffset = 0.5f;

        [Header("Collision")]
        [Tooltip("Radius of the probe that looks for geometry between pivot and camera.")]
        public float ProbeRadius = 0.25f;

        [Tooltip("Hits closer than this are treated as the probe starting inside geometry, and ignored.")]
        public float DegenerateHitDistance = 0.05f;

        [Header("Smoothing")]
        [Tooltip("Metres per second the camera may move back out once the way is clear.")]
        public float ExtendSpeed = 2.5f;

        public float PositionSmoothing = 20f;

        private Camera _camera;
        private float _currentDistance;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            if (_camera == null) _camera = gameObject.AddComponent<Camera>();

            _camera.nearClipPlane = 0.08f;
            _camera.farClipPlane = 600f;
            _camera.fieldOfView = 62f;
            _currentDistance = Distance;
        }

        private float _pivotDrop;

        public void SetTarget(Transform target)
        {
            Follow = target;
            _currentDistance = Distance;

            // Start framed rather than easing in from wherever the rig happened to be.
            if (target != null && Input != null) SnapBehind();
        }

        private void SnapBehind()
        {
            Quaternion rotation = Quaternion.Euler(Input.Pitch, Input.Yaw, 0f);
            _pivotDrop = Prone ? PronePivotDrop : Crouched ? CrouchPivotDrop : 0f;
            Vector3 pivot = Follow.position + Vector3.up * (PivotHeight - _pivotDrop);
            transform.position = pivot + rotation * Vector3.back * _currentDistance + rotation * Vector3.right * ShoulderOffset;
            transform.rotation = rotation;
        }

        private void LateUpdate()
        {
            if (Follow == null || Input == null) return;

            Quaternion rotation = Quaternion.Euler(Input.Pitch, Input.Yaw, 0f);
            float wanted = Prone ? PronePivotDrop : Crouched ? CrouchPivotDrop : 0f;
            _pivotDrop = Mathf.Lerp(_pivotDrop, wanted,
                1f - Mathf.Exp(-StanceSmoothing * Time.deltaTime));

            Vector3 pivot = Follow.position + Vector3.up * (PivotHeight - _pivotDrop);
            Vector3 back = rotation * Vector3.back;
            Vector3 right = rotation * Vector3.right;

            float allowed = Distance;

            if (Physics.SphereCast(pivot, ProbeRadius, back, out RaycastHit hit, Distance,
                    UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
            {
                // A hit at ~zero distance means the probe began inside geometry, which carries no
                // information about how far back the camera can sit. Ignoring it is what stops the
                // oscillation; keeping the current distance is the stable choice.
                if (hit.distance > DegenerateHitDistance)
                    allowed = Mathf.Max(MinDistance, hit.distance - ProbeRadius);
                else
                    allowed = Mathf.Max(MinDistance, _currentDistance);
            }

            if (allowed < _currentDistance)
            {
                _currentDistance = allowed;                       // duck in at once
            }
            else
            {
                _currentDistance = Mathf.MoveTowards(_currentDistance, allowed, ExtendSpeed * Time.deltaTime);
            }

            Vector3 target = pivot + back * _currentDistance + right * ShoulderOffset;

            transform.position = Vector3.Lerp(transform.position, target,
                1f - Mathf.Exp(-PositionSmoothing * Time.deltaTime));
            transform.rotation = rotation;
        }
    }
}
