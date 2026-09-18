using UnityEngine;
using Unseen.Combat;
using Unseen.Environment;

namespace Unseen.Entities
{
    /// <summary>
    /// Draws the katana, and moves it between the back and the hand as
    /// <see cref="BladeCarry"/> says it should.
    ///
    /// Purely presentational. Whether the blade can strike is decided server-side; this only shows
    /// where it currently is, and a client that drew its own early would gain nothing but a picture
    /// disagreeing with what the server let it do.
    ///
    /// Parented to a bone but posed in world space, and scaled against the bone's lossyScale - the
    /// same trick <see cref="NinjaEquipmentArt"/> uses for the bracers and tabi. It is needed: the
    /// rig is Generic, its root carries the exporter's Z-up rotation, and its bone offsets run in
    /// thousandths, so a one-metre sword given localScale one comes out any size at all. Dividing
    /// by the accumulated scale is what makes Length mean metres.
    ///
    /// Parented rather than free-floating so it is culled, and destroyed, with the character it
    /// belongs to; posed every frame because it has to travel between two bones rather than sit
    /// on one.
    /// </summary>
    public sealed class BladeVisual : MonoBehaviour
    {
        /// <summary>Length of the blade in metres, grip butt to tip.</summary>
        public float Length = 1.02f;

        private Transform _root;
        private Transform _blade;
        private Transform _hand;
        private Transform _back;
        private Transform _hips;
        private bool _hero;

        private BladeState _state = BladeState.Sheathed;
        private float _inHand;

        /// <summary>Builds the blade and finds the two places it lives.</summary>
        public void Attach(Transform root, Material steel)
        {
            Mesh mesh = BlenderArt.Get("Katana");
            if (mesh == null) return;

            _root = root;
            _hero = HeroNinjaAppearance.IsHero(root.GetComponent<AgentVisual>());
            _hips = _hero ? Bone(root,"Hips") : null;
            _hand = Bone(root, "RightHand");

            // Highest point on the spine first: a sword worn across the back sits by the shoulders,
            // and hanging it off the hips would put the hilt somewhere nobody could reach.
            _back = Bone(root, "UpperChest") ?? Bone(root, "Chest")
                    ?? Bone(root, "Spine") ?? Bone(root, "Hips");

            // Nowhere to hang it. Better no blade than one at the world origin, which is what
            // following a null bone would give - visible to everybody, attached to nobody.
            if (_hand == null && _back == null) return;

            var go = new GameObject("Katana");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            if (steel != null) renderer.sharedMaterial = steel;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

            _blade = go.transform;
            _blade.SetParent(_back != null ? _back : _hand, false);
            go.layer = root.gameObject.layer;

            // Posed now, not left to the first LateUpdate.
            //
            // SetParent keeps a local scale of one, and this rig's bone chain multiplies up by
            // about fifty - so between construction and the first frame the blade is a
            // fifty-metre slab. In a player build that is one bad frame; in the editor, where
            // MonoBehaviour updates do not run at all, it is every frame, which is exactly how it
            // first appeared in a screenshot.
            Pose();
        }

        /// <summary>Tells the blade where the simulation says it should be.</summary>
        public void Show(BladeState state, float progress)
        {
            _state = state;

            // Drawing and sheathing are one journey in opposite directions, so both become a single
            // number: nought on the back, one in the hand.
            switch (state)
            {
                case BladeState.Drawn: _inHand = 1f; break;
                case BladeState.Sheathed: _inHand = 0f; break;
                case BladeState.Drawing: _inHand = progress; break;
                case BladeState.Sheathing: _inHand = 1f - progress; break;
            }

            // Moved now as well as next frame, so a state set from a tool or a test is visible
            // without waiting for an update loop that may never run.
            Pose();
        }

        /// <summary>
        /// Follows the bones after the animator has posed them.
        ///
        /// LateUpdate rather than Update: run before the animation evaluates and the blade trails a
        /// frame behind the hand holding it, which at speed is a sword visibly detached from its
        /// owner.
        /// </summary>
        private void LateUpdate()
        {
            Pose();
        }

        /// <summary>Puts the blade where it belongs, at the size it should be.</summary>
        private void Pose()
        {
            if (_blade == null) return;

            Vector3 handPos = Vector3.zero;
            Quaternion handRot = Quaternion.identity;
            Vector3 backPos = Vector3.zero;
            Quaternion backRot = Quaternion.identity;

            if (_hand != null) handPos = HandPose(out handRot);
            if (_back != null) backPos = BackPose(out backRot);

            // One bone missing means both ends of the journey are the one that exists, so the blade
            // sits still on the body rather than sliding toward the world origin.
            if (_hand == null) { handPos = backPos; handRot = backRot; }
            if (_back == null) { backPos = handPos; backRot = handRot; }

            // Interpolated rather than switched, so a draw is the blade travelling from shoulder to
            // fist instead of teleporting between two poses partway through.
            _blade.SetPositionAndRotation(
                Vector3.Lerp(backPos, handPos, _inHand),
                Quaternion.Slerp(backRot, handRot, _inHand));

            // Divided out of the parent's accumulated scale, so Length is metres rather than
            // whatever the rig's hierarchy happens to multiply up to.
            Vector3 scale = _blade.parent != null ? _blade.parent.lossyScale : Vector3.one;

            _blade.localScale = new Vector3(
                Length / Mathf.Max(0.0001f, Mathf.Abs(scale.x)),
                Length / Mathf.Max(0.0001f, Mathf.Abs(scale.y)),
                Length / Mathf.Max(0.0001f, Mathf.Abs(scale.z)));
        }

        /// <summary>
        /// Held: grip in the fist, blade out and slightly raised.
        ///
        /// Placed against the character's own axes rather than the bone's. Bone rotations in this
        /// rig carry the exporter's conventions - the tabi needed an explicit note that Blender's
        /// toe direction comes out as minus Z - so an offset expressed in bone space lands
        /// somewhere nobody can predict. The first attempt put the katana beside the ninja's head.
        /// </summary>
        private Vector3 HandPose(out Quaternion rotation)
        {
            Vector3 along = (_root.forward * 0.85f + _root.up * 0.52f).normalized;
            rotation = Aim(along, _root.right);
            return _hand.position + _root.up * 0.02f + _root.forward * 0.02f;
        }

        /// <summary>
        /// Sheathed: hilt above the right shoulder, blade running down across the back to the left.
        /// </summary>
        private Vector3 BackPose(out Quaternion rotation)
        {
            if (_hero && _hips != null)
            {
                Vector3 up=(_back.position-_hips.position).normalized;
                Vector3 right=Vector3.ProjectOnPlane(_root.right,up).normalized;
                Vector3 forward=Vector3.Cross(right,up).normalized;
                Vector3 direction=(-up*.87f-right*.48f).normalized;
                rotation=Aim(direction,forward);
                return _back.position+right*.11f+up*.13f-forward*.14f;
            }
            // Grip sits high on the right, behind the body; the blade travels down and across.
            Vector3 along = (-_root.up * 0.82f - _root.right * 0.48f - _root.forward * 0.18f).normalized;
            rotation = Aim(along, _root.forward);

            return _back.position
                   + _root.right * 0.11f
                   + _root.up * 0.13f
                   - _root.forward * 0.11f;
        }

        /// <summary>
        /// A rotation putting the mesh's own length along <paramref name="along"/>.
        ///
        /// The katana is authored standing up - grip at the origin, tip at plus Y - so aiming it is
        /// a matter of deciding where its Y should point. LookRotation's second argument is exactly
        /// that, and the first only decides which way the flat of the blade faces.
        /// </summary>
        private static Quaternion Aim(Vector3 along, Vector3 flatFacing)
        {
            Vector3 face = Vector3.ProjectOnPlane(flatFacing, along);
            if (face.sqrMagnitude < 0.001f) face = Vector3.ProjectOnPlane(Vector3.up, along);

            return Quaternion.LookRotation(face.normalized, along);
        }

        private static Transform Bone(Transform root, string name)
        {
            var all = root.GetComponentsInChildren<Transform>(true);

            for (int i = 0; i < all.Length; i++)
                if (all[i].name == name) return all[i];

            return null;
        }
    }
}
