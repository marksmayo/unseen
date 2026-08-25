using Unity.Collections;
using Unity.Mathematics;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.Combat
{
    /// <summary>
    /// Decides who is "hot". Two hostile agents close enough to fight put each other into a combat
    /// pocket, and everything about them - motion, replication, bot thinking - steps up to the
    /// combat rate. Everyone else stays at the cheap roaming rate. This is what makes 64 entities
    /// affordable while still giving a 1v1 clash 60 Hz fidelity.
    /// </summary>
    public sealed class CombatPocketSystem : SimSystem
    {
        private NativeList<int> _query;

        public override int Order => SimOrder.CombatPockets;
        public override SimRate Rate => SimRate.Base;

        public int PocketCount { get; private set; }
        public int HotAgents { get; private set; }

        protected override void OnInitialize()
        {
            _query = new NativeList<int>(64, Allocator.Persistent);
        }

        public override void Tick(in SimFrame frame)
        {
            EntityRegistry registry = Ctx.Entities;
            UnseenConfig.NetworkSection cfg = Ctx.Config.Network;
            float radiusSq = cfg.CombatPocketRadius * cfg.CombatPocketRadius;

            int pockets = 0;
            int hot = 0;

            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity a = registry.BySlot(i);
                if (!a.IsAlive) continue;

                bool contact = false;

                // Proximity is not enough on its own: an agent that has neither seen nor been
                // damaged recently stays cold even with someone on the far side of a wall.
                Ctx.Grid.QueryRadius(a.Position, cfg.CombatPocketRadius, _query);
                for (int q = 0; q < _query.Length; q++)
                {
                    int slot = _query[q];
                    if (slot == i) continue;

                    AgentEntity b = registry.BySlot(slot);
                    if (b == null || !b.IsAlive) continue;
                    if (math.distancesq(a.Position, b.Position) > radiusSq) continue;

                    bool aware = a.TryGetVisible(b.Id, out _) || b.TryGetVisible(a.Id, out _);
                    bool recentlyHurt = frame.Time - a.Vitals.LastDamageTime < cfg.CombatPocketLinger;
                    bool fighting = a.Melee.Phase != AttackPhase.Idle || b.Melee.Phase != AttackPhase.Idle;

                    if (!aware && !recentlyHurt && !fighting) continue;

                    contact = true;
                    pockets++;
                    break;
                }

                if (contact)
                {
                    a.HotUntil = frame.Time + cfg.CombatPocketLinger;
                    a.Flags |= AgentFlags.InCombat;
                }
                else if (frame.Time >= a.HotUntil)
                {
                    a.Flags &= ~AgentFlags.InCombat;
                }

                // A human-controlled agent is always hot. At the base rate its motor integrates in
                // 1/20 s steps, which is fine for a distant bot but reads as stutter for the person
                // holding the controls - and no amount of camera smoothing hides a 20 Hz character.
                bool locallyDriven = a.ConnectionId >= 0;

                a.IsHot = locallyDriven || frame.Time < a.HotUntil || a.Melee.InTakedown(frame.Time);
                if (a.IsHot) hot++;
            }

            PocketCount = pockets;
            HotAgents = hot;
        }

        public override void Shutdown()
        {
            if (_query.IsCreated) _query.Dispose();
        }
    }
}
