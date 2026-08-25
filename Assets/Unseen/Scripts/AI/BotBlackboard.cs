using Unity.Mathematics;
using Unseen.Core;

namespace Unseen.AI
{
    /// <summary>
    /// The behaviour states from the design: patrol until something is perceived, investigate it,
    /// search the area when the trail goes cold, and fight or ambush on confirmed sight.
    /// </summary>
    public enum BotState : byte
    {
        Patrol = 0,
        Creep = 1,
        Investigate = 2,
        SearchArea = 3,
        Ambush = 4,
        Combat = 5,
        Flee = 6,
        Reposition = 7
    }

    /// <summary>One decision the planner can commit to this think.</summary>
    public enum BotAction : byte
    {
        Idle = 0,
        PatrolTo = 1,
        CreepTo = 2,
        MoveToNoise = 3,
        SearchNearby = 4,
        Approach = 5,
        HoldAmbush = 6,
        Strike = 7,
        Parry = 8,
        Retreat = 9,
        ThrowSmoke = 10,
        BreakLantern = 11,
        LootContainer = 12,
        MoveIntoZone = 13,
        TakeDownTarget = 14
    }

    /// <summary>
    /// Facts the planner reasons over. Every one of these is derived from information the bot is
    /// actually allowed to have: its own state, its interest set, and the sounds it heard. There is
    /// no direct read of another agent transform anywhere in this file.
    /// </summary>
    public struct BotFacts
    {
        public bool HasTarget;
        public bool TargetVisible;
        public bool TargetIsSilhouette;
        public bool TargetUnaware;
        public bool TargetInMeleeRange;
        public bool TargetInApproachRange;
        public bool UnderAttack;
        public bool Injured;
        public bool HasSmoke;
        public bool HasWeapon;
        public bool HeardSomething;
        public bool OutsideZone;
        public bool Concealed;
        public bool LootNearby;
        public bool LanternNearby;
        public bool EnemyIsSwinging;
    }

    /// <summary>Working memory for one bot. Persists between thinks; cleared between matches.</summary>
    public sealed class BotBlackboard
    {
        public BotState State = BotState.Patrol;
        public float StateEnteredAt;

        public AgentId Target;
        public float3 TargetLastSeen;
        public float TargetLastSeenAt = float.NegativeInfinity;
        public float TargetThreatScore;
        public bool TargetIsSilhouetteOnly;

        /// <summary>Where the most interesting sound came from, and how sure we are about it.</summary>
        public float3 NoisePosition;

        public float NoiseIntensity;
        public float NoiseHeardAt = float.NegativeInfinity;
        public SoundKind NoiseKind;

        public float3 PatrolDestination;
        public bool HasPatrolDestination;

        public float3 AmbushSpot;
        public bool HasAmbushSpot;

        /// <summary>Simulation time this bot is allowed to act again. Models reaction delay.</summary>
        public float ReactionReadyAt;

        public BotAction CurrentAction = BotAction.Idle;
        public float ActionExpiresAt;

        /// <summary>Per-bot skill jitter so a lobby of bots does not behave like one mind.</summary>
        public float SkillOffset;

        public void Reset()
        {
            State = BotState.Patrol;
            StateEnteredAt = 0f;
            Target = AgentId.None;
            TargetLastSeenAt = float.NegativeInfinity;
            TargetThreatScore = 0f;
            TargetIsSilhouetteOnly = false;
            NoiseIntensity = 0f;
            NoiseHeardAt = float.NegativeInfinity;
            HasPatrolDestination = false;
            HasAmbushSpot = false;
            ReactionReadyAt = 0f;
            CurrentAction = BotAction.Idle;
            ActionExpiresAt = 0f;
        }

        public void EnterState(BotState state, float now)
        {
            if (State == state) return;
            State = state;
            StateEnteredAt = now;
        }

        public float TimeInState(float now) => now - StateEnteredAt;
    }
}
