using UnityEngine;
using Unseen.Core;

namespace Unseen.Combat
{
    public enum AttackPhase : byte
    {
        Idle = 0,

        /// <summary>Telegraph. Visible to anyone watching, and the window a parry is timed against.</summary>
        Windup = 1,

        /// <summary>The single tick the blade is live.</summary>
        Strike = 2,

        Recovery = 3
    }

    /// <summary>
    /// Melee state for one agent: what they are swinging, what they are guarding, and how long
    /// their latency-compensated parry window has left.
    /// </summary>
    public sealed class AgentCombat : MonoBehaviour
    {
        public AttackPhase Phase { get; internal set; }
        public bool Heavy { get; internal set; }
        public GuardZone AttackZone { get; internal set; }
        public GuardZone GuardZoneHeld { get; internal set; } = GuardZone.Mid;

        /// <summary>Simulation time at which the current phase ends.</summary>
        public float PhaseEnd { get; internal set; }

        public bool Guarding { get; internal set; }

        /// <summary>Simulation time until which a parry will succeed. Set when the guard is raised.</summary>
        public float ParryWindowEnd { get; internal set; }

        public float StaggerEnd { get; internal set; }
        public float GuardBreakEnd { get; internal set; }

        /// <summary>Set while a takedown is running. The victim of a takedown cannot act.</summary>
        public AgentId TakedownTarget { get; internal set; }

        public float TakedownEnd { get; internal set; }
        public bool IsTakedownVictim { get; internal set; }

        /// <summary>Last time this agent swung, used to rate-limit attacks.</summary>
        public float LastAttackTime { get; internal set; } = float.NegativeInfinity;

        public bool IsStaggered(float now) => now < StaggerEnd;
        public bool IsGuardBroken(float now) => now < GuardBreakEnd;
        public bool CanAct(float now) => Phase == AttackPhase.Idle && !IsStaggered(now) && !IsGuardBroken(now);
        public bool ParryOpen(float now) => Guarding && now < ParryWindowEnd;
        public bool InTakedown(float now) => now < TakedownEnd;

        internal void ResetCombat()
        {
            Phase = AttackPhase.Idle;
            Heavy = false;
            Guarding = false;
            GuardZoneHeld = GuardZone.Mid;
            PhaseEnd = 0f;
            ParryWindowEnd = 0f;
            StaggerEnd = 0f;
            GuardBreakEnd = 0f;
            TakedownTarget = AgentId.None;
            TakedownEnd = 0f;
            IsTakedownVictim = false;
            LastAttackTime = float.NegativeInfinity;
        }
    }
}
