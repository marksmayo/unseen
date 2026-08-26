using System.Collections.Generic;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Unseen.Combat;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;
using Unseen.Items;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Presses every control on a real local player and reports what each one actually changed.
    ///
    /// "Most of these do nothing" is a claim about behaviour, and the only way to answer it is to
    /// drive the input and watch the agent, rather than read the code and reason about what it
    /// ought to do. Each check states the observable it is looking for, so a pass means the button
    /// moved the world and not merely that a field was assigned.
    /// </summary>
    public static class UnseenControlAudit
    {
        [MenuItem("Unseen/Audit Controls", priority = 81)]
        public static void Run()
        {
            var host = new GameObject("ControlAudit");
            var report = new StringBuilder();

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.Seed = 20260824;
                boot.Boot();

                Settle(boot, 240);

                AgentEntity player = FindLocalPlayer(boot);
                if (player == null)
                {
                    Debug.LogError("[audit] no local player agent; nothing to test");
                    return;
                }

                Debug.Log($"[audit] local player '{player.DisplayName}' at {player.Position}, " +
                          $"connection {player.ConnectionId}");

                PlaceOnOpenGround(boot, player);
                Check(report, "W/A/S/D  move", Move(boot, player));
                Check(report, "Shift    sprint", Sprint(boot, player));
                Check(report, "Ctrl     crouch", Crouch(boot, player));
                Check(report, "C        prone", Prone(boot, player));
                Check(report, "Space    jump", Jump(boot, player));
                Check(report, "LMB      light attack", Attack(boot, player, heavy: false));
                Check(report, "Alt+LMB  heavy attack", Attack(boot, player, heavy: true));
                Check(report, "RMB      guard", Guard(boot, player));
                Check(report, "F        grapple", Grapple(boot, player));
                Check(report, "E        interact", Interact(boot, player));
                Check(report, "1/2/3    utility", Utility(boot, player));

                Debug.Log("[audit] result\n" + report);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        private static void Check(StringBuilder report, string label, string outcome)
        {
            report.AppendLine($"  {label,-24} {outcome}");
        }

        // ------------------------------------------------------------------ checks

        private static string Move(UnseenBootstrap boot, AgentEntity player)
        {
            float3 start = player.Position;
            Drive(boot, player, 60, i => new MoveIntent { Sequence = (uint)i, Move = new float2(0f, 1f) });
            float travelled = math.length(UnseenMath.Horizontal(player.Position - start));
            return travelled > 1f ? $"WORKS (moved {travelled:0.00} m)" : $"DEAD (moved {travelled:0.00} m)";
        }

        private static string Sprint(UnseenBootstrap boot, AgentEntity player)
        {
            PlaceOnOpenGround(boot, player);
            float walk = SpeedOver(boot, player, sprint: false);
            PlaceOnOpenGround(boot, player);
            float sprint = SpeedOver(boot, player, sprint: true);
            return sprint > walk * 1.3f
                ? $"WORKS (walk {walk:0.0} -> sprint {sprint:0.0} m/s)"
                : $"DEAD (walk {walk:0.0}, sprint {sprint:0.0} m/s)";
        }

        private static float SpeedOver(UnseenBootstrap boot, AgentEntity player, bool sprint)
        {
            Drive(boot, player, 40, i => new MoveIntent
            {
                Sequence = (uint)i, Move = new float2(0f, 1f), Sprint = sprint
            });

            _peakSpeed = 0f;
            Drive(boot, player, 30, i => new MoveIntent
            {
                Sequence = (uint)i, Move = new float2(0f, 1f), Sprint = sprint
            }, () =>
            {
                if (player.Motor != null)
                    _peakSpeed = math.max(_peakSpeed,
                        math.length(UnseenMath.Horizontal(player.Motor.Velocity)));
            });

            // Peak speed, not average displacement: a clip into a kerb halfway through the sample
            // would otherwise be reported as "sprint is slower than walking".
            return _peakSpeed;
        }

        private static string Crouch(UnseenBootstrap boot, AgentEntity player)
        {
            Drive(boot, player, 20, i => new MoveIntent { Sequence = (uint)i, Crouch = true });
            Stance crouched = player.Stance;
            float height = player.Controller != null ? player.Controller.height : -1f;

            Drive(boot, player, 20, i => new MoveIntent { Sequence = (uint)i });
            Stance stood = player.Stance;

            return crouched == Stance.Crouch && stood == Stance.Stand
                ? $"WORKS (height {height:0.00} m while crouched)"
                : $"DEAD (stance went {crouched} then {stood})";
        }

        private static string Prone(UnseenBootstrap boot, AgentEntity player)
        {
            PlaceOnOpenGround(boot, player);

            Drive(boot, player, 25, i => new MoveIntent { Sequence = (uint)i, Prone = true });
            Stance flat = player.Stance;
            float height = player.Controller != null ? player.Controller.height : -1f;

            // Prone must also beat crouch when both are held, or the deeper stance is unreachable
            // for anyone who rests a finger on control.
            Drive(boot, player, 20, i => new MoveIntent { Sequence = (uint)i, Prone = true, Crouch = true });
            Stance both = player.Stance;

            Drive(boot, player, 25, i => new MoveIntent { Sequence = (uint)i });
            Stance stood = player.Stance;

            if (flat != Stance.Prone) return $"DEAD (stance stayed {flat})";
            if (both != Stance.Prone) return $"DEAD (crouch overrode prone: {both})";
            if (stood != Stance.Stand) return $"DEAD (could not stand up again: {stood})";

            return $"WORKS (height {height:0.00} m while prone)";
        }

        private static string Jump(UnseenBootstrap boot, AgentEntity player)
        {
            Drive(boot, player, 30, i => new MoveIntent { Sequence = (uint)i });
            float groundY = player.Position.y;

            float peak = groundY;
            bool airborne = false;
            Drive(boot, player, 45, i => new MoveIntent { Sequence = (uint)i, Jump = true }, () =>
            {
                peak = math.max(peak, player.Position.y);
                airborne |= player.Locomotion == LocomotionState.Airborne;
            });

            float rise = peak - groundY;
            return rise > 0.4f
                ? $"WORKS (rose {rise:0.00} m, airborne={airborne})"
                : $"DEAD (rose {rise:0.00} m, airborne={airborne})";
        }

        private static string Attack(UnseenBootstrap boot, AgentEntity player, bool heavy)
        {
            // Let any previous swing finish rather than forcing the phase: AgentCombat owns it.
            Drive(boot, player, 45, i => new MoveIntent { Sequence = (uint)i });

            var phases = new HashSet<AttackPhase>();
            Drive(boot, player, 60, i => new MoveIntent
            {
                Sequence = (uint)i,
                AttackLight = !heavy,
                AttackHeavy = heavy
            }, () => phases.Add(player.Melee.Phase));

            phases.Remove(AttackPhase.Idle);
            return phases.Count > 0
                ? $"WORKS (phases {string.Join(">", phases)})"
                : "DEAD (attack phase never left Idle)";
        }

        private static string Guard(UnseenBootstrap boot, AgentEntity player)
        {
            bool guarded = false;
            Drive(boot, player, 30, i => new MoveIntent { Sequence = (uint)i, Guard = true },
                () => guarded |= player.Melee.Guarding);

            return guarded ? "WORKS (guard raised)" : "DEAD (guard never raised)";
        }

        private static string Grapple(UnseenBootstrap boot, AgentEntity player)
        {
            // Report whether an anchor is even reachable: a grapple that finds nothing is a very
            // different problem from a grapple that is not wired up.
            float range = boot.Context.Config.Movement.GrappleRange;
            Collider[] anchors = Physics.OverlapSphere(player.Position, range, UnseenLayers.Grapple,
                QueryTriggerInteraction.Collide);

            // Point at the nearest anchor first. A grapple you have to be aiming at is a fair
            // design; one you cannot aim at because nothing tells you where to look is not, and
            // these two cases have to be told apart.
            float pitch = 0f;
            if (anchors.Length > 0)
            {
                // Nearest anchor with a clear rope path, not just nearest. Aiming at a bracket
                // on the far side of a keep wall and calling the refusal a bug tests the wrong
                // thing: a player picks an anchor they can see.
                Collider nearest = null;
                float best = float.MaxValue;
                foreach (Collider c in anchors)
                {
                    Vector3 eyeNow = (Vector3)player.EyePosition;
                    float d = Vector3.Distance(c.bounds.center, eyeNow);
                    if (d >= best) continue;

                    if (Physics.Linecast(eyeNow, c.bounds.center, out RaycastHit wall,
                            (1 << UnseenLayers.Default) | (1 << UnseenLayers.Occluder),
                            QueryTriggerInteraction.Ignore) &&
                        Vector3.Distance(wall.point, c.bounds.center) > 1f)
                        continue;

                    best = d;
                    nearest = c;
                }

                if (nearest == null)
                    return $"NO LINE ({anchors.Length} anchor(s) in range, every one behind cover)";

                Vector3 to = nearest.bounds.center - (Vector3)player.EyePosition;
                player.Yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
                pitch = -Mathf.Asin(Mathf.Clamp(to.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
                Debug.Log($"[audit] aiming at '{nearest.name}' {best:0.0} m away, pitch {pitch:0.0} deg");

                // Report exactly why a shot is refused. Guessing at this has already cost two
                // build-and-look cycles.
                Vector3 eye = (Vector3)player.EyePosition;
                Vector3 dir = (nearest.bounds.center - eye).normalized;

                bool sphere = Physics.SphereCast(eye, 0.7f, dir, out RaycastHit sphereHit, range,
                    UnseenLayers.Grapple, QueryTriggerInteraction.Ignore);
                Debug.Log($"[audit]   spherecast: {(sphere ? sphereHit.collider.name : "MISS")}");

                // Test the rope against the point the spherecast actually returned, which is what
                // TryFire uses. The earlier version tested a different point and so measured a
                // blocker on a line the hook never traces.
                Vector3 target = sphere ? sphereHit.point : nearest.bounds.center;
                Debug.Log($"[audit]   eye {eye}, spherecast point {target}, " +
                          $"distance {Vector3.Distance(eye, target):0.0} m");
                bool blocked = Physics.Linecast(eye, target, out RaycastHit blocker,
                    (1 << UnseenLayers.Default) | (1 << UnseenLayers.Occluder),
                    QueryTriggerInteraction.Ignore);
                string ropeState = blocked
                    ? $"blocked by '{blocker.collider.name}' (layer {blocker.collider.gameObject.layer}) " +
                      $"{Vector3.Distance(blocker.point, target):0.00} m short of the anchor"
                    : "clear";
                Debug.Log($"[audit]   rope to {target}: {ropeState}");
                Debug.Log($"[audit]   hook component: {(player.Hook != null ? "present" : "MISSING")}, " +
                          $"cooldown {(player.Hook != null ? player.Hook.CooldownRemaining : -1f):0.00}, " +
                          $"attached {(player.Hook != null && player.Hook.Attached)}");
            }

            float aimPitch = pitch;
            bool grappling = false;
            Drive(boot, player, 90, i => new MoveIntent
                {
                    Sequence = (uint)i, Grapple = true, Pitch = aimPitch
                },
                () => grappling |= player.Locomotion == LocomotionState.Grapple);

            if (grappling)
            {
                // A grapple that lifts you is only half the feature. Keep simulating and prove the
                // ninja comes back down and ends up standing on something: the first working
                // version flew upward forever, because the reel forced an upward bias even after
                // passing the anchor, and the world-bounds ceiling was what eventually caught it.
                float peak = player.Position.y;
                float ceiling = boot.Map != null ? boot.Map.CeilingY : 100f;
                bool everGrounded = false;

                Drive(boot, player, 420, i => new MoveIntent { Sequence = (uint)i }, () =>
                {
                    peak = math.max(peak, player.Position.y);
                    everGrounded |= player.Locomotion == LocomotionState.Grounded;
                });

                string settled = everGrounded && player.Position.y < ceiling
                    ? "landed"
                    : $"STUCK at y={player.Position.y:0.0} ({player.Locomotion})";

                return $"WORKS ({anchors.Length} anchors, peak y {peak:0.0} m, {settled})";
            }
            return anchors.Length == 0
                ? $"NO TARGET (no grapple anchor within {range:0} m of the player)"
                : $"DEAD ({anchors.Length} anchor(s) in range, none taken)";
        }

        private static string Interact(UnseenBootstrap boot, AgentEntity player)
        {
            LootContainer nearest = null;
            float best = float.MaxValue;
            foreach (LootContainer container in LootContainer.All)
            {
                float d = Vector3.Distance(container.transform.position, (Vector3)player.Position);
                if (d >= best) continue;
                best = d;
                nearest = container;
            }

            if (nearest == null) return "NO TARGET (no loot container exists)";

            // Walk to it first: interact is range-gated, and testing it from wherever the player
            // happened to spawn would only ever report the range check.
            Teleport(player, (float3)nearest.transform.position + new float3(0f, 0f, 1.2f));
            Settle(boot, 20);

            int before = CountItems(player);
            LootContainer probe = LootContainer.NearestUnlooted(player.TorsoPosition, 2.2f);
            Debug.Log($"[audit] interact probe: torso {player.TorsoPosition}, container " +
                      $"{nearest.Position}, NearestUnlooted(2.2 m) -> " +
                      $"{(probe != null ? probe.name : "nothing")}, looted={nearest.Looted}");

            bool opened = false;
            Drive(boot, player, 40, i => new MoveIntent { Sequence = (uint)i, Interact = i % 8 == 0 },
                () => opened |= nearest.Looted);

            int after = CountItems(player);
            if (opened || after > before)
                return $"WORKS (container opened at {best:0.0} m, items {before} -> {after})";
            return $"DEAD (stood {Vector3.Distance(nearest.transform.position, (Vector3)player.Position):0.0} m away, nothing opened)";
        }

        private static string Utility(UnseenBootstrap boot, AgentEntity player)
        {
            int items = CountItems(player);
            if (items == 0) return "NO ITEM (player starts with an empty inventory)";

            bool used = false;
            Drive(boot, player, 40, i => new MoveIntent { Sequence = (uint)i, UseUtility = 1 },
                () => used |= CountItems(player) < items);

            return used ? $"WORKS (consumed one of {items})" : $"DEAD ({items} item(s) held, none consumed)";
        }

        // ------------------------------------------------------------------ plumbing

        private static float _peakSpeed;

        /// <summary>Drops the player onto the widest bit of flat street we can find.</summary>
        private static void PlaceOnOpenGround(UnseenBootstrap boot, AgentEntity player)
        {
            float pitch = 34f + 12f;
            var spot = new Vector3(pitch * 0.5f, 30f, 0f);

            if (Physics.Raycast(spot, Vector3.down, out RaycastHit hit, 60f,
                    UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                spot = hit.point + Vector3.up * 0.2f;

            player.Motor?.Teleport(spot);
            Settle(boot, 30);
        }

        private static int CountItems(AgentEntity player)
        {
            if (player.Inventory == null) return 0;
            int n = 0;
            foreach (ItemStack stack in player.Inventory.Utility)
                if (stack.Item != null)
                    n += stack.Count;
            return n;
        }

        private static void Teleport(AgentEntity player, float3 position)
        {
            player.Motor?.Teleport(position);
        }

        private static AgentEntity FindLocalPlayer(UnseenBootstrap boot)
        {
            foreach (AgentEntity agent in boot.Context.Entities.All)
                if (agent.ConnectionId >= 0)
                    return agent;
            return null;
        }

        /// <summary>Feeds one intent per tick straight onto the agent, then steps the simulation.</summary>
        private static void Drive(UnseenBootstrap boot, AgentEntity player, int ticks,
            System.Func<int, MoveIntent> build, System.Action after = null)
        {
            const float step = 1f / 60f;
            for (int i = 0; i < ticks; i++)
            {
                MoveIntent intent = build(i);
                intent.Yaw = player.Yaw;
                player.Intent = intent;

                boot.Network.Poll(step);
                boot.Simulation.Advance(step);

                // ServerInputSystem overwrites Intent from the network each tick, so the scripted
                // value is reapplied after the input stage rather than before it.
                player.Intent = intent;
                after?.Invoke();
            }
        }

        private static void Settle(UnseenBootstrap boot, int ticks)
        {
            const float step = 1f / 60f;
            for (int i = 0; i < ticks; i++)
            {
                boot.Network.Poll(step);
                boot.Simulation.Advance(step);
            }
        }
    }
}
