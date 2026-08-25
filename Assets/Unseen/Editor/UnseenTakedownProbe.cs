using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Combat;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Sets up the exact situation a silent takedown is meant to reward - attacker directly behind
    /// an unaware victim, in range, clear line - and reports which gate rejects it.
    ///
    /// The smoke test has reported zero takedowns across every run since the feature was written,
    /// while the roadmap calls it done. One of those is wrong, and a staged encounter that prints
    /// each condition separately is the only way to find out which.
    /// </summary>
    public static class UnseenTakedownProbe
    {
        [MenuItem("Unseen/Probe Takedowns", priority = 82)]
        public static void Run()
        {
            var host = new GameObject("TakedownProbe");

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260824;
                boot.Boot();

                Step(boot, 240);

                AgentEntity attacker = null;
                AgentEntity victim = null;

                foreach (AgentEntity agent in boot.Context.Entities.All)
                {
                    if (!agent.IsAlive) continue;
                    if (attacker == null) { attacker = agent; continue; }
                    if (victim == null) { victim = agent; break; }
                }

                if (attacker == null || victim == null)
                {
                    Debug.LogError("[takedown] need two live agents");
                    return;
                }

                UnseenConfig.CombatSection cfg = boot.Context.Config.Combat;

                // Silence the victim's brain. A bot re-aims every tick, so a staged encounter
                // cannot hold one facing away long enough to test the rear-arc gate: the first
                // run of this probe reported the arc failing purely because the victim had turned
                // round while the sim settled.
                if (victim.Brain != null) victim.Brain.enabled = false;
                if (attacker.Brain != null) attacker.Brain.enabled = false;

                // Put them on open street, victim facing away, attacker a metre behind.
                var ground = new Vector3(23f, 30f, 0f);
                if (Physics.Raycast(ground, Vector3.down, out RaycastHit hit, 60f,
                        UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                    ground = hit.point + Vector3.up * 0.2f;

                victim.Motor.Teleport(ground);
                victim.Yaw = 0f;
                victim.Intent = new MoveIntent { Yaw = 0f };

                attacker.Motor.Teleport((float3)ground - new float3(0f, 0f, 1.1f));
                attacker.Yaw = 0f;

                Step(boot, 30);

                // Clear the memory that would make the victim "aware", then hold both still.
                victim.ResetForMatch();
                victim.Motor.Teleport(ground);
                victim.Yaw = 0f;

                for (int i = 0; i < 12; i++)
                {
                    victim.Intent = new MoveIntent { Yaw = 0f };
                    victim.Yaw = 0f;
                    boot.Network.Poll(1f / 60f);
                    boot.Simulation.Advance(1f / 60f);
                    victim.Yaw = 0f;
                    victim.Visible.Clear();
                }

                Report(boot, attacker, victim, cfg);

                // Now actually press attack and see whether a takedown starts.
                bool started = false;
                for (int i = 0; i < 120; i++)
                {
                    attacker.Intent = new MoveIntent
                    {
                        Sequence = (uint)i,
                        Yaw = attacker.Yaw,
                        AttackLight = i % 10 == 0
                    };
                    victim.Intent = new MoveIntent { Yaw = 0f };
                    victim.Yaw = 0f;

                    boot.Network.Poll(1f / 60f);
                    boot.Simulation.Advance(1f / 60f);
                    victim.Yaw = 0f;

                    attacker.Intent = new MoveIntent
                    {
                        Sequence = (uint)i,
                        Yaw = attacker.Yaw,
                        AttackLight = i % 10 == 0
                    };
                    victim.Intent = new MoveIntent { Yaw = 0f };

                    if ((attacker.Flags & AgentFlags.Takedown) != 0 ||
                        attacker.Melee.TakedownTarget.IsValid)
                    {
                        started = true;
                        break;
                    }
                }

                CombatDirector combat = boot.Simulation.GetSystem<CombatDirector>();
                Debug.Log($"[takedown] attempt: started={started}, " +
                          $"director tally={combat?.TotalTakedowns ?? -1}, " +
                          $"victim alive={victim.IsAlive}, victim health={victim.Vitals.Fraction:0.00}");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        private static void Report(UnseenBootstrap boot, AgentEntity attacker, AgentEntity victim,
            UnseenConfig.CombatSection cfg)
        {
            float now = boot.Simulation.Time;
            float3 delta = victim.Position - attacker.Position;
            float distance = math.length(delta);
            float3 toAttacker = math.normalizesafe(attacker.Position - victim.Position);
            float rearCos = math.cos(cfg.TakedownRearArc * 0.5f * UnseenMath.Deg2Rad);
            float dot = math.dot(toAttacker, victim.Forward);

            bool inRange = distance <= cfg.TakedownRange;
            bool fromRear = dot <= -rearCos;
            bool unaware = victim.IsUnawareOf(attacker.Id, now, cfg.AwarenessMemory);
            bool guarding = victim.Melee.Guarding;
            bool blocked = Physics.Linecast((Vector3)attacker.TorsoPosition, (Vector3)victim.TorsoPosition,
                out RaycastHit wall, (1 << UnseenLayers.Default) | (1 << UnseenLayers.Occluder),
                QueryTriggerInteraction.Ignore);

            Debug.Log($"[takedown] staged encounter, t={now:0.0}s");
            Debug.Log($"[takedown]   distance {distance:0.00} m (limit {cfg.TakedownRange:0.00}) -> " +
                      $"{(inRange ? "PASS" : "FAIL")}");
            Debug.Log($"[takedown]   rear dot {dot:0.00} (needs <= {-rearCos:0.00}) -> " +
                      $"{(fromRear ? "PASS" : "FAIL")}");
            Debug.Log($"[takedown]   victim unaware -> {(unaware ? "PASS" : "FAIL")}");
            Debug.Log($"[takedown]   victim not guarding -> {(!guarding ? "PASS" : "FAIL")}");
            Debug.Log($"[takedown]   line clear -> " +
                      $"{(blocked ? $"FAIL (blocked by '{wall.collider.name}')" : "PASS")}");
            Debug.Log($"[takedown]   attacker can act -> " +
                      $"{(attacker.Melee.CanAct(now) ? "PASS" : $"FAIL (phase {attacker.Melee.Phase})")}");
            Debug.Log($"[takedown]   attacker hot -> {(attacker.IsHot ? "PASS" : "FAIL (cold agents only tick on base ticks)")}");
        }

        private static void Step(UnseenBootstrap boot, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                boot.Network.Poll(1f / 60f);
                boot.Simulation.Advance(1f / 60f);
            }
        }
    }
}
