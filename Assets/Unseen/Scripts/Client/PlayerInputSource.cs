using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;

namespace Unseen.Client
{
    /// <summary>
    /// Turns local devices into a <see cref="MoveIntent"/>. This is the only place raw input is
    /// read; everything downstream - prediction, the server, even bots - consumes the same struct.
    ///
    /// Guard zone comes from where you are looking rather than a separate key: aim high to cover
    /// high, low to cover low. It reads naturally and keeps the clash to one button.
    /// </summary>
    public sealed class PlayerInputSource : MonoBehaviour
    {
        [Header("Look")]
        public float MouseSensitivity = 2.2f;
        public float PitchMin = -80f;
        public float PitchMax = 80f;
        public bool LockCursor = true;

        [Header("Bindings")]
        public KeyCode SprintKey = KeyCode.LeftShift;
        public KeyCode CrouchKey = KeyCode.LeftControl;
        public KeyCode JumpKey = KeyCode.Space;
        public KeyCode GrappleKey = KeyCode.F;
        public KeyCode InteractKey = KeyCode.E;
        public KeyCode Utility1 = KeyCode.Alpha1;
        public KeyCode Utility2 = KeyCode.Alpha2;
        public KeyCode Utility3 = KeyCode.Alpha3;

        [Tooltip("Pitch beyond which a raised guard covers the high or low zone.")]
        public float ZoneAngle = 22f;

        [Tooltip("Held alongside the attack button to swing heavy.")]
        public KeyCode HeavyModifier = KeyCode.LeftAlt;

        [Tooltip("Flips vertical look.")]
        public bool InvertY;

        private uint _sequence;

        public float Yaw { get; private set; }
        public float Pitch { get; private set; }
        public MoveIntent Current { get; private set; } = MoveIntent.Idle;

        /// <summary>Set false while a menu or the results screen is up.</summary>
        public bool AcceptInput = true;

        private void Start()
        {
            ApplyCursorState();
            ApplySettings(GameSettings.Current);
            GameSettings.Changed += ApplySettings;
        }

        private void OnDestroy()
        {
            GameSettings.Changed -= ApplySettings;
        }

        /// <summary>
        /// Bindings live in the settings file, not in the inspector. This is the single place they
        /// are turned back into KeyCodes, so a rebind takes effect the moment the menu closes.
        /// </summary>
        private void ApplySettings(GameSettings settings)
        {
            if (settings == null) return;

            MouseSensitivity = settings.MouseSensitivity;
            InvertY = settings.InvertY;
            SprintKey = settings.Key(settings.Sprint, KeyCode.LeftShift);
            CrouchKey = settings.Key(settings.Crouch, KeyCode.LeftControl);
            JumpKey = settings.Key(settings.Jump, KeyCode.Space);
            GrappleKey = settings.Key(settings.Grapple, KeyCode.F);
            InteractKey = settings.Key(settings.Interact, KeyCode.E);
            HeavyModifier = settings.Key(settings.Heavy, KeyCode.LeftAlt);
            Utility1 = settings.Key(settings.Utility1, KeyCode.Alpha1);
            Utility2 = settings.Key(settings.Utility2, KeyCode.Alpha2);
            Utility3 = settings.Key(settings.Utility3, KeyCode.Alpha3);
        }

        private void OnEnable()
        {
            ApplyCursorState();
        }

        private void ApplyCursorState()
        {
            if (!LockCursor) return;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void Update()
        {
            // Escape belongs to SettingsMenu, which gates AcceptInput and the cursor while open.
            if (!AcceptInput)
            {
                Current = new MoveIntent { Yaw = Yaw, Pitch = Pitch, Zone = GuardZone.Mid };
                return;
            }

            if (LockCursor)
            {
                Yaw += Input.GetAxisRaw("Mouse X") * MouseSensitivity;
                float look = Input.GetAxisRaw("Mouse Y") * MouseSensitivity;
                Pitch = Mathf.Clamp(InvertY ? Pitch + look : Pitch - look, PitchMin, PitchMax);
            }

            float strafe = Input.GetAxisRaw("Horizontal");
            float forward = Input.GetAxisRaw("Vertical");
            float2 move = new float2(strafe, forward);
            if (math.lengthsq(move) > 1f) move = math.normalize(move);

            byte utility = 0;
            if (Input.GetKey(Utility1)) utility = 1;
            else if (Input.GetKey(Utility2)) utility = 2;
            else if (Input.GetKey(Utility3)) utility = 3;

            bool guard = Input.GetMouseButton(1);

            Current = new MoveIntent
            {
                Sequence = ++_sequence,
                Move = move,
                Yaw = Yaw,
                Pitch = Pitch,
                Sprint = Input.GetKey(SprintKey),
                Crouch = Input.GetKey(CrouchKey),
                Jump = Input.GetKey(JumpKey),
                Grapple = Input.GetKey(GrappleKey),
                Interact = Input.GetKeyDown(InteractKey),
                AttackLight = Input.GetMouseButton(0) && !guard,
                AttackHeavy = Input.GetMouseButton(0) && Input.GetKey(HeavyModifier),
                Guard = guard,
                Zone = ZoneFromPitch(Pitch),
                UseUtility = utility
            };
        }

        private GuardZone ZoneFromPitch(float pitch)
        {
            if (pitch < -ZoneAngle) return GuardZone.High;
            if (pitch > ZoneAngle) return GuardZone.Low;
            return GuardZone.Mid;
        }

        public void SetLook(float yaw, float pitch)
        {
            Yaw = yaw;
            Pitch = Mathf.Clamp(pitch, PitchMin, PitchMax);
        }
    }
}
