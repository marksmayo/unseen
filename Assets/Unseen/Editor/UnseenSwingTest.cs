using UnityEditor;
using UnityEngine;
using Unity.Mathematics;
using Unseen.Combat;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Whether a click actually produces a sword swing that lands.
    ///
    /// Reported from play as "hitting with the sword isn't registering very often", and the first
    /// guess was the new character models — bounding boxes, collision geometry. It was not: melee
    /// resolution never touches a collider. It is a distance between two torso points, an arc, and
    /// one linecast for a wall in the way, and a new mesh cannot change any of those.
    ///
    /// The real cause was input. Wanting to strike is what starts the draw, the draw takes a third
    /// of a second, and the swing was gated on the blade being ready on the frame of the press —
    /// which it never is. A held button gives no second rising edge, so the attack was refused for
    /// ever. Every first click, and every click after a sprint, did nothing.
    ///
    /// Tested through a real match because the unit test around AttackBuffer proves the rule and
    /// not the wiring, and the wiring is where this lived for the whole life of the katana.
    /// </summary>
    public static class UnseenSwingTest
    {
        [MenuItem("Unseen/Test Sword Swing", priority = 93)]
        public static void Run()
        {
            var host = new GameObject("SwingTest");
            bool passed = true;

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.BuildNavMesh = false;
                boot.Seed = 20260827;
                boot.Boot();

                Step(boot, 60 * 70);

                SimContext ctx = boot.Context;
                CombatDirector combat = ctx.Combat;

                AgentEntity attacker = ctx.Entities.ByConnection(boot.Network.LocalConnectionId);
                if (attacker == null || !attacker.IsAlive)
                {
                    Debug.LogError("[swing] need a live local agent");
                    return;
                }

                AgentEntity victim = null;
                foreach (AgentEntity a in ctx.Entities.All)
                    if (a != null && a != attacker && a.IsAlive) { victim = a; break; }

                if (victim == null)
                {
                    Debug.LogError("[swing] need somebody to swing at");
                    return;
                }

                // Put them face to face, well inside reach, with nothing between them.
                Place(attacker, victim, ctx.Config.Combat.MeleeRange * 0.5f);
                Step(boot, 10);

                int hitsBefore = combat.TotalHits;
                float healthBefore = victim.Vitals.Fraction;

                // One click, held down, exactly as a player does it. The blade is sheathed because
                // nobody has asked for it in the seventy seconds since the match began - which is
                // the state every fight actually begins in.
                Hold(boot, attacker, victim, seconds: 1.0f);

                passed &= Check("one held click lands a hit", combat.TotalHits > hitsBefore);
                passed &= Check("and the victim actually took damage",
                    victim.Vitals.Fraction < healthBefore);

                // The other half of the report: after sprinting. Running puts the sword away, so
                // this is the same swallowed press by a different route, and it is the one that
                // happens in the middle of a chase.
                Place(attacker, victim, ctx.Config.Combat.MeleeRange * 0.5f);

                // Sprinting is what puts the sword away, so this drives it through the real path
                // rather than reaching in and setting the state.
                attacker.Intent = new MoveIntent { Move = new float2(0f, 1f), Sprint = true, Zone = GuardZone.Mid };
                Step(boot, 30);

                Place(attacker, victim, ctx.Config.Combat.MeleeRange * 0.5f);

                int hitsAfterSprint = combat.TotalHits;
                Hold(boot, attacker, victim, seconds: 1.0f);

                passed &= Check("and a click straight out of a sprint lands too",
                    combat.TotalHits > hitsAfterSprint);

                Debug.Log(passed ? "[swing] PASSED" : "[swing] FAILED");
            }
            finally
            {
                Object.DestroyImmediate(host);

                foreach (MapDescriptor map in Object.FindObjectsByType<MapDescriptor>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (map != null) Object.DestroyImmediate(map.gameObject);

                GameObject town = GameObject.Find("GreyboxTown");
                if (town != null) Object.DestroyImmediate(town);
            }
        }

        /// <summary>Holds the attack button down, the way a player does, and lets the sim run.</summary>
        private static void Hold(UnseenBootstrap boot, AgentEntity attacker, AgentEntity victim,
            float seconds)
        {
            int ticks = Mathf.RoundToInt(seconds * 60f);

            for (int i = 0; i < ticks; i++)
            {
                // Re-aimed every tick: the victim is a bot and will wander, and a test that failed
                // because the target stepped aside would be measuring the wrong thing.
                Face(attacker, victim);

                attacker.Intent = new MoveIntent
                {
                    AttackLight = true,
                    Zone = GuardZone.Mid
                };

                boot.Network.Poll(1f / 60f);
                boot.Simulation.Advance(1f / 60f);
            }
        }

        private static void Place(AgentEntity attacker, AgentEntity victim, float apart)
        {
            float3 behind = victim.Position - new float3(0f, 0f, apart);

            Teleport(attacker, behind);
            Face(attacker, victim);
        }

        private static void Face(AgentEntity attacker, AgentEntity victim)
        {
            float3 to = victim.Position - attacker.Position;
            attacker.Yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
        }

        private static void Teleport(AgentEntity agent, float3 to)
        {
            CharacterController controller = agent.Controller;

            if (controller != null) controller.enabled = false;
            agent.Position = to;
            if (controller != null) controller.enabled = true;
        }

        private static bool Check(string what, bool ok)
        {
            Debug.Log($"[swing] {what}: {(ok ? "PASS" : "FAIL")}");
            return ok;
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
