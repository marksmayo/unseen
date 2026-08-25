using Unseen.BattleRoyale;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.Movement
{
    /// <summary>
    /// Drives every motor from the authoritative loop instead of from Unity Update, so ordering is
    /// deterministic and the tick LOD is explicit: agents inside a combat pocket integrate at the
    /// combat rate, everyone else integrates once per base tick with a proportionally larger step.
    /// </summary>
    public sealed class MotionSystem : SimSystem
    {
        private float _coldStep;

        public override int Order => SimOrder.Motion;
        public override SimRate Rate => SimRate.Combat;

        public int HotAgentsLastTick { get; private set; }

        protected override void OnInitialize()
        {
            _coldStep = Ctx.Config.BaseTickInterval;
        }

        public override void Tick(in SimFrame frame)
        {
            EntityRegistry registry = Ctx.Entities;
            int hot = 0;

            // While a glider is still open the deployment system owns the transform; letting the
            // motor integrate as well would bank 19 seconds of fall velocity and kill everyone on
            // landing.
            bool infiltrating = Ctx.Match != null && Ctx.Match.Phase == MatchPhase.Infiltration;

            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity agent = registry.BySlot(i);
                if (agent.Motor == null) continue;
                if (!agent.IsAlive && agent.Locomotion != LocomotionState.Locked) continue;
                if (infiltrating && (agent.Flags & AgentFlags.Deployed) == 0) continue;

                if (agent.IsHot)
                {
                    hot++;
                    agent.Motor.Simulate(Ctx, frame.Dt, frame.Tick, frame.Time);
                }
                else if (frame.IsBaseTick)
                {
                    agent.Motor.Simulate(Ctx, _coldStep, frame.Tick, frame.Time);
                }
            }

            HotAgentsLastTick = hot;
        }
    }
}
