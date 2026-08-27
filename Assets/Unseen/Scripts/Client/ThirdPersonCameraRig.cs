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

        [Tooltip("Absolute closest the camera may sit to the pivot. " +
                 "Small on purpose. There used to be a 2.4 m minimum enforced even when the " +
                 "collision probe found a wall thirty centimetres away, so the camera was placed " +
                 "two and a half metres back - through the wall - and a player could swing the " +
                 "view around to see what was on the other side of it. In a game about not being " +
                 "seen that is not a camera artefact, it is a wallhack. Hugging the character is " +
                 "always better than sitting outside the room they are in.")]
        public float HardMinDistance = 0.28f;

        [Tooltip("Below this camera reach the local character is hidden, so the view is not filled " +
                 "by the inside of their own head.")]
        public float HideVisualBelow = 1.1f;

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
        private Renderer[] _localVisual;
        private Transform _visualOwner;
        private bool _visualHidden;

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

        /// <summary>
        /// How far back the camera may sit along a given offset before it meets something.
        ///
        /// The whole offset is tested, shoulder included. Casting straight back and then adding the
        /// shoulder afterwards - which is what this used to do - moves the camera half a metre
        /// sideways into geometry nothing ever looked at.
        /// </summary>
        private float AllowedReach(Vector3 pivot, Vector3 direction, float wanted)
        {
            if (!Physics.SphereCast(pivot, ProbeRadius, direction, out RaycastHit hit, wanted,
                    UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                return wanted;

            // A hit at ~zero distance means the probe began inside geometry. That carries no
            // information about how far back the camera can sit, and the old code responded by
            // holding the current distance - which is to say, by staying outside the wall. Hugging
            // the character is the safe answer: worst case you see the back of their head.
            if (hit.distance <= DegenerateHitDistance) return HardMinDistance;

            return Mathf.Max(HardMinDistance, hit.distance - ProbeRadius * 0.5f);
        }

        /// <summary>
        /// Hides the local ninja when the camera has been forced right up against them, and shows
        /// them again when it backs off. Only the local view is affected.
        /// </summary>
        private void UpdateLocalVisual(float reach)
        {
            if (Follow != _visualOwner)
            {
                _visualOwner = Follow;
                _localVisual = Follow != null ? Follow.GetComponentsInChildren<Renderer>(true) : null;
                _visualHidden = false;
            }

            if (_localVisual == null) return;

            bool hide = reach < HideVisualBelow;
            if (hide == _visualHidden) return;

            _visualHidden = hide;
            for (int i = 0; i < _localVisual.Length; i++)
                if (_localVisual[i] != null) _localVisual[i].enabled = !hide;
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

            // The full offset the camera would like to sit at, shoulder included, as one vector.
            // (Named for the offset, not "wanted": the pivot drop above already owns that word.)
            Vector3 desiredOffset = back * Distance + right * ShoulderOffset;
            float desiredReach = desiredOffset.magnitude;
            Vector3 direction = desiredReach > 0.0001f ? desiredOffset / desiredReach : back;

            float allowed = AllowedReach(pivot, direction, desiredReach);

            if (allowed < _currentDistance)
            {
                _currentDistance = allowed;                       // duck in at once
            }
            else
            {
                _currentDistance = Mathf.MoveTowards(_currentDistance, allowed, ExtendSpeed * Time.deltaTime);
            }

            // Placed exactly, not eased into place.
            //
            // The position used to be lerped toward its target, which meant that even when the
            // reach was computed correctly the camera spent the intervening frames somewhere
            // between - which, when the correct answer is "hard against this wall", is inside the
            // wall. Smoothing the reach gives smooth motion without ever putting the camera
            // somewhere the geometry says it may not be.
            transform.position = pivot + direction * _currentDistance;
            transform.rotation = rotation;

            UpdateLocalVisual(_currentDistance);
        }
    }
}
