using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unseen.AI;
using Unseen.Combat;
using Unseen.Core;
using Unseen.Items;
using Unseen.Movement;

namespace Unseen.Entities
{
    /// <summary>What one observer currently knows about one target.</summary>
    public struct VisibleTarget
    {
        public AgentId Id;
        public VisibilityKind Kind;

        /// <summary>Last position the observer actually resolved.</summary>
        public float3 Position;

        /// <summary>0..1 how well resolved the target is. Silhouettes cap low.</summary>
        public float Confidence;

        public float LastSeenTime;
    }

    /// <summary>
    /// The authoritative record of one ninja - human or bot. Identical in both cases:
    /// bots receive exactly the perception a player would, and drive the same motor.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class AgentEntity : MonoBehaviour
    {
        [Header("Identity")]
        public AgentKind Kind = AgentKind.Bot;

        [Tooltip("Network connection that owns this agent. -1 for bots and unclaimed slots.")]
        public int ConnectionId = -1;

        public string DisplayName = "ninja";

        [Header("Rig")]
        [Tooltip("Eye/camera anchor. Falls back to a point near the top of the capsule.")]
        public Transform EyeAnchor;

        [Tooltip("Torso anchor used as the line-of-sight and stealth sample target.")]
        public Transform TorsoAnchor;

        public AgentId Id { get; internal set; }
        public int Slot { get; internal set; } = -1;

        public CharacterController Controller { get; private set; }
        public NinjaMotor Motor { get; private set; }
        public AgentVitals Vitals { get; private set; }
        public AgentCombat Melee { get; private set; }
        public Inventory Inventory { get; private set; }
        public GrapplingHook Hook { get; private set; }
        public BotBrain Brain { get; internal set; }

        public AgentFlags Flags;
        public Stance Stance = Stance.Stand;
        public LocomotionState Locomotion = LocomotionState.Grounded;

        /// <summary>0 = fully lit and exposed, 1 = swallowed by shadow.</summary>
        public float StealthIndex;

        public float Yaw;
        public float Pitch;

        public MoveIntent Intent = MoveIntent.Idle;

        /// <summary>Combat pocket membership. Hot agents simulate and replicate at the combat rate.</summary>
        public bool IsHot { get; internal set; }

        public float HotUntil { get; internal set; }

        /// <summary>Placement when this agent died, or 0 while alive.</summary>
        public int Placement { get; internal set; }

        public int Kills { get; internal set; }

        /// <summary>Targets resolved this tick. Rebuilt by the interest manager every base tick.</summary>
        public readonly List<VisibleTarget> Visible = new List<VisibleTarget>(24);

        /// <summary>Sounds this agent perceived since its last think. Consumed by the client HUD or the bot brain.</summary>
        public readonly List<HeardSound> Heard = new List<HeardSound>(16);

        private readonly Dictionary<int, float> _lastSawTime = new Dictionary<int, float>(16);

        public bool IsAlive => (Flags & AgentFlags.Alive) != 0;
        public bool IsBot => Kind == AgentKind.Bot;
        public bool IsLocked => Locomotion == LocomotionState.Locked;

        public float3 Position
        {
            get => transform.position;
            set => transform.position = value;
        }

        public float3 EyePosition =>
            EyeAnchor != null
                ? (float3)EyeAnchor.position
                : (float3)transform.position + new float3(0f, Controller != null ? Controller.height * 0.92f : 1.6f, 0f);

        public float3 TorsoPosition =>
            TorsoAnchor != null
                ? (float3)TorsoAnchor.position
                : (float3)transform.position + new float3(0f, Controller != null ? Controller.height * 0.55f : 0.95f, 0f);

        public float3 Forward => UnseenMath.YawToForward(Yaw);

        public float3 ViewDirection
        {
            get
            {
                float p = Pitch * UnseenMath.Deg2Rad;
                float3 flat = UnseenMath.YawToForward(Yaw);
                return math.normalize(new float3(flat.x * math.cos(p), -math.sin(p), flat.z * math.cos(p)));
            }
        }

        private void Awake()
        {
            CacheComponents();
        }

        /// <summary>
        /// Re-resolves the component cache. Called from Awake, and again by the spawner after it
        /// finishes assembling a rig at runtime.
        /// </summary>
        public void CacheComponents()
        {
            Controller = GetComponent<CharacterController>();
            Motor = GetComponent<NinjaMotor>();
            Vitals = GetComponent<AgentVitals>();
            Melee = GetComponent<AgentCombat>();
            Inventory = GetComponent<Inventory>();
            Hook = GetComponent<GrapplingHook>();
            if (Vitals == null) Vitals = gameObject.AddComponent<AgentVitals>();
            if (Melee == null) Melee = gameObject.AddComponent<AgentCombat>();
            if (Inventory == null) Inventory = gameObject.AddComponent<Inventory>();
            if (gameObject.layer == 0) gameObject.layer = UnseenLayers.Ninja;
        }

        /// <summary>Records that this agent resolved a target. Feeds takedown awareness checks.</summary>
        public void NoteSaw(AgentId target, float time)
        {
            _lastSawTime[target.Value] = time;
        }

        public float LastSawTime(AgentId target)
        {
            return _lastSawTime.TryGetValue(target.Value, out float t) ? t : float.NegativeInfinity;
        }

        /// <summary>True when this agent has no recent knowledge of <paramref name="other"/>.</summary>
        public bool IsUnawareOf(AgentId other, float now, float memory)
        {
            return now - LastSawTime(other) > memory;
        }

        public bool TryGetVisible(AgentId target, out VisibleTarget result)
        {
            for (int i = 0; i < Visible.Count; i++)
            {
                if (Visible[i].Id == target)
                {
                    result = Visible[i];
                    return true;
                }
            }

            result = default;
            return false;
        }

        public void ResetForMatch()
        {
            Flags = AgentFlags.Alive | (IsBot ? AgentFlags.Bot : AgentFlags.None);
            Stance = Stance.Stand;
            Locomotion = LocomotionState.Grounded;
            StealthIndex = 0f;
            Placement = 0;
            Kills = 0;
            IsHot = false;
            HotUntil = 0f;
            Intent = MoveIntent.Idle;
            Visible.Clear();
            Heard.Clear();
            _lastSawTime.Clear();
            Vitals.ResetVitals();
            Melee.ResetCombat();
            Inventory.Clear();

            // The controller is switched off by the glider descent and by the death scene, and
            // both hand it back when they finish normally. Dying halfway down a glide finishes
            // neither, so a match start restores it unconditionally rather than trusting that
            // every path out of those two states was taken.
            if (Controller != null) Controller.enabled = true;
        }

        public override string ToString() => $"{DisplayName}({Id})";
    }
}
