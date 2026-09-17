using System;
using Unity.Mathematics;

namespace Unseen.Core
{
    /// <summary>Stable handle for a simulated entity. Never reused within a match.</summary>
    [Serializable]
    public readonly struct AgentId : IEquatable<AgentId>, IComparable<AgentId>
    {
        public static readonly AgentId None = default;

        public readonly int Value;

        public AgentId(int value)
        {
            Value = value;
        }

        public bool IsValid => Value != 0;

        public bool Equals(AgentId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is AgentId o && Equals(o);
        public override int GetHashCode() => Value;
        public int CompareTo(AgentId other) => Value.CompareTo(other.Value);
        public override string ToString() => Value == 0 ? "e:none" : "e:" + Value;

        public static bool operator ==(AgentId a, AgentId b) => a.Value == b.Value;
        public static bool operator !=(AgentId a, AgentId b) => a.Value != b.Value;
    }

    public enum AgentKind : byte
    {
        Player = 0,
        Bot = 1
    }

    public enum Stance : byte
    {
        Stand = 0,
        Crouch = 1,
        Prone = 2
    }

    public enum LocomotionState : byte
    {
        Grounded = 0,
        Airborne = 1,
        WallClimb = 2,
        WallRun = 3,
        LedgeHang = 4,
        RafterCrawl = 5,
        Grapple = 6,
        Slide = 7,

        /// <summary>Motion is owned by a server-authoritative animation (takedown, warped vault).</summary>
        Locked = 8
    }

    /// <summary>
    /// What a motion warp is portraying.
    ///
    /// Locked says the body is being carried by a fixed animation; it does not say which one, and
    /// the three read completely differently. Kept separate from LocomotionState rather than added
    /// to it because Locked already means something to the movement code - IsLocked, the dead-agent
    /// exception in MotionSystem, the early-out in the motor - and splitting it into three states
    /// would mean auditing every one of those for a change that is purely about what you see.
    /// </summary>
    public enum WarpStyle : byte
    {
        /// <summary>Climbing up onto something: the far side is higher than where you started.</summary>
        Mantle = 0,

        /// <summary>Going over something: a rail, a fence, a low wall, landing back at your level.</summary>
        Vault = 1,

        /// <summary>A silent takedown, which owns both bodies.</summary>
        Takedown = 2
    }

    public enum GuardZone : byte
    {
        High = 0,
        Mid = 1,
        Low = 2
    }

    [Flags]
    public enum AgentFlags : ushort
    {
        None = 0,
        Alive = 1 << 0,
        Crouched = 1 << 1,
        Sprinting = 1 << 2,
        InCombat = 1 << 3,
        InMist = 1 << 4,
        Takedown = 1 << 5,
        Grappling = 1 << 6,
        Guarding = 1 << 7,
        Staggered = 1 << 8,
        Deployed = 1 << 9,
        Bot = 1 << 10,
        Smoked = 1 << 11,

        /// <summary>
        /// Mid-throw. Appended, never renumbered: this goes over the wire.
        ///
        /// Needed because a proxy has none of the thrower's combat state - it sees flags and
        /// nothing else - and without this everybody else's throws would be invisible while the
        /// blade sailed out of an idle body.
        /// </summary>
        Throwing = 1 << 12,

        /// <summary>
        /// The katana is in hand rather than on the back. Appended, never renumbered: this goes
        /// over the wire.
        ///
        /// A bit in a field every contact already carries, rather than a byte of its own. The
        /// snapshot has a hard size limit now, and a per-contact byte is paid for by every player
        /// in view of every other - so a fact that is genuinely binary should cost a bit.
        ///
        /// Binary is enough for somebody else's blade: a proxy needs to show a sword on the back
        /// or in the hand. The owner gets the exact state, with its draw progress, from the self
        /// block, because their own blade is the one whose timing they are reading.
        /// </summary>
        BladeDrawn = 1 << 13
    }

    public enum SoundKind : byte
    {
        Footstep = 0,
        Landing = 1,
        Vault = 2,
        GrappleFire = 3,
        WeaponSwing = 4,
        WeaponClash = 5,
        ShojiSlice = 6,
        ShojiBreak = 7,
        LanternBreak = 8,
        Noisemaker = 9,
        SmokeBomb = 10,
        Death = 11,
        LootContainer = 12,

        /// <summary>Bamboo disturbed by a body pushing against it. Appended, never renumbered:
        /// SoundKind travels on the wire as a byte.</summary>
        BambooRustle = 13,

        /// <summary>A bird going up off a branch because somebody came past too fast.</summary>
        BirdFlush = 14,

        /// <summary>Something small bolting out of the undergrowth.</summary>
        AnimalScatter = 15,

        /// <summary>Somebody underwater and out of air.</summary>
        Choking = 16,

        /// <summary>A thrown blade turning over in the air.</summary>
        ShurikenWhistle = 17,

        /// <summary>Steel arriving, in a body or in a wall.</summary>
        ShurikenHit = 18
    }

    public enum DamageKind : byte
    {
        Melee = 0,
        Thrown = 1,
        Takedown = 2,
        Mist = 3,
        Fall = 4,

        /// <summary>Crushed by the spirit forest. Appended, never renumbered: this goes on the
        /// wire as a byte.</summary>
        SpiritForest = 5,

        /// <summary>Drowned. Held under, or held yourself under too long.</summary>
        Drowning = 6
    }

    /// <summary>One tick of intent produced by a human client or a bot brain. Always validated server-side.</summary>
    [Serializable]
    public struct MoveIntent
    {
        public uint Sequence;

        /// <summary>Desired planar direction relative to <see cref="Yaw"/>: x strafes, y drives forward. Magnitude 0..1.</summary>
        public float2 Move;

        public float Yaw;
        public float Pitch;
        public bool Sprint;
        public bool Crouch;

        /// <summary>Flat on the ground. Slower and quieter than a crouch, and much harder to see.</summary>
        public bool Prone;
        public bool Jump;
        public bool Grapple;

        /// <summary>Throw a shuriken. Edge-triggered by the server; holding it is one throw.</summary>
        public bool Throw;
        public bool Interact;
        public bool AttackLight;
        public bool AttackHeavy;
        public bool Guard;
        public GuardZone Zone;

        /// <summary>0 = nothing, 1..3 = throw or drink the item in that utility slot.</summary>
        public byte UseUtility;

        public static MoveIntent Idle => new MoveIntent { Move = float2.zero, Zone = GuardZone.Mid };
    }

    /// <summary>Server-side sound sphere. Consumed by clients (as pings) and by bots (as stimulus).</summary>
    public struct SoundEvent
    {
        public AgentId Source;
        public float3 Position;
        public SoundKind Kind;

        /// <summary>Loudness where 1.0 == one unmodified standing footstep.</summary>
        public float Loudness;

        /// <summary>Distance at which the event is inaudible in open air.</summary>
        public float Radius;

        public int Tick;
    }

    /// <summary>Result of propagating one <see cref="SoundEvent"/> to one listener.</summary>
    public struct HeardSound
    {
        public AgentId Source;
        public SoundKind Kind;

        /// <summary>0..1 perceived intensity after distance falloff and occlusion.</summary>
        public float Intensity;

        /// <summary>0..1 how muffled the path was. Drives UI ping fidelity and bot confidence.</summary>
        public float Occlusion;

        /// <summary>Unit vector toward the apparent origin.</summary>
        public float3 Direction;

        /// <summary>Where the listener believes the sound came from - deliberately imprecise when muffled.</summary>
        public float3 ApparentPosition;

        public int Tick;
    }

    public struct DamageInfo
    {
        public AgentId Attacker;
        public AgentId Victim;
        public DamageKind Kind;
        public float Amount;
        public float3 Point;
        public float3 Direction;
        public GuardZone Zone;
    }

    /// <summary>What one observer is allowed to know about one target this tick.</summary>
    [Flags]
    public enum VisibilityKind : byte
    {
        None = 0,

        /// <summary>Unobstructed line of sight inside the view frustum.</summary>
        Direct = 1 << 0,

        /// <summary>Seen only as a shoji silhouette - no gear, no health, no identity.</summary>
        Silhouette = 1 << 1,

        /// <summary>Inside the replication bubble but not visible. Sent at minimum fidelity, never rendered.</summary>
        Proximate = 1 << 2
    }

    public static class UnseenMath
    {
        public const float Deg2Rad = 0.0174532924f;

        public static float3 YawToForward(float yawDegrees)
        {
            float r = yawDegrees * Deg2Rad;
            return new float3(math.sin(r), 0f, math.cos(r));
        }

        public static float ForwardToYaw(float3 forward)
        {
            return math.degrees(math.atan2(forward.x, forward.z));
        }

        /// <summary>Shortest signed delta between two yaw angles, in degrees.</summary>
        public static float YawDelta(float from, float to)
        {
            return (to - from + 540f) % 360f - 180f;
        }

        /// <summary>Perceptual falloff: 1 at the source, 0 at the radius, weighted toward the near field.</summary>
        public static float Falloff(float distance, float radius)
        {
            if (distance <= 0.001f) return 1f;
            if (distance >= radius) return 0f;
            float t = distance / radius;
            return math.saturate((1f - t) / (1f + 6f * t * t));
        }

        public static float3 Horizontal(float3 v)
        {
            return new float3(v.x, 0f, v.z);
        }
    }
}
