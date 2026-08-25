using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;
using Unseen.Items;
using Unseen.Movement;

namespace Unseen.Combat
{
    public enum CombatEventKind : byte
    {
        Swing = 0,
        Hit = 1,
        Blocked = 2,
        Parried = 3,
        GuardBroken = 4,
        TakedownStart = 5,
        TakedownComplete = 6,
        Death = 7,
        Throw = 8
    }

    /// <summary>A combat beat worth showing to nearby clients. Purely presentational - state is server-side.</summary>
    public struct CombatEvent
    {
        public CombatEventKind Kind;
        public AgentId Attacker;
        public AgentId Victim;
        public float3 Position;
        public GuardZone Zone;
        public int Tick;
    }

    /// <summary>
    /// Resolves all melee. Frontal exchanges run through the three-zone clash: guard the zone the
    /// blade is coming for and you take almost nothing, guard the wrong one and you take extra.
    /// Timing the guard raise inside the parry window flips the exchange and staggers the attacker.
    /// The parry window is widened per connection by measured latency, so a distant player is not
    /// punished for their ping.
    /// </summary>
    public sealed class CombatDirector : SimSystem
    {
        private readonly List<CombatEvent> _events = new List<CombatEvent>(64);
        private readonly Dictionary<int, bool> _guardHeld = new Dictionary<int, bool>(64);
        private readonly Dictionary<int, bool> _attackHeld = new Dictionary<int, bool>(64);
        private readonly Dictionary<int, byte> _utilityHeld = new Dictionary<int, byte>(64);

        /// <summary>Optional smoke prefab. A bare gameplay volume is created when this is null.</summary>
        public GameObject SmokePrefab;

        public override int Order => SimOrder.Combat;
        public override SimRate Rate => SimRate.Combat;

        public IReadOnlyList<CombatEvent> Events => _events;

        /// <summary>Lifetime tallies, for diagnostics and soak-test reporting.</summary>
        public int TotalSwings { get; private set; }

        public int TotalHits { get; private set; }
        public int TotalParries { get; private set; }
        public int TotalTakedowns { get; private set; }
        public int TotalDeaths { get; private set; }

        protected override void OnInitialize()
        {
            Ctx.Combat = this;
        }

        public override void Tick(in SimFrame frame)
        {
            EntityRegistry registry = Ctx.Entities;

            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity agent = registry.BySlot(i);
                if (!agent.IsAlive) continue;

                // Cold agents only get a combat update on base ticks; a fight puts them hot.
                if (!agent.IsHot && !frame.IsBaseTick) continue;

                TickAgent(agent, frame);
            }
        }

        private void TickAgent(AgentEntity agent, in SimFrame frame)
        {
            AgentCombat melee = agent.Melee;
            MoveIntent intent = agent.Intent;
            float now = frame.Time;
            UnseenConfig.CombatSection cfg = Ctx.Config.Combat;

            // A takedown owns both participants until it finishes.
            if (melee.InTakedown(now)) return;
            if (melee.TakedownTarget.IsValid) CompleteTakedown(agent, frame);
            if (melee.IsTakedownVictim) return;

            AdvancePhase(agent, frame);
            UpdateStaggerFlag(agent, now);
            UpdateGuard(agent, intent, now, cfg);
            HandleInteract(agent, intent, frame);
            HandleUtility(agent, intent, frame);

            bool attackPressed = RisingEdge(_attackHeld, agent.Id.Value, intent.AttackLight || intent.AttackHeavy);
            if (!attackPressed || !melee.CanAct(now)) return;

            // A silent takedown always beats a swing when it is available.
            if (TryBeginTakedown(agent, frame)) return;

            if (agent.Inventory != null && agent.Inventory.WeaponClass == WeaponClass.Shuriken && intent.AttackHeavy)
            {
                ThrowShuriken(agent, frame);
                return;
            }

            BeginSwing(agent, intent.AttackHeavy, intent.Zone, now, cfg);
        }

        // ---------------------------------------------------------------- swings

        private void BeginSwing(AgentEntity agent, bool heavy, GuardZone zone, float now, UnseenConfig.CombatSection cfg)
        {
            AgentCombat melee = agent.Melee;
            float windup = (heavy ? cfg.HeavyWindup : cfg.LightWindup) +
                           (agent.Inventory != null ? agent.Inventory.WindupBonus : 0f);

            melee.Phase = AttackPhase.Windup;
            melee.Heavy = heavy;
            melee.AttackZone = zone;
            melee.PhaseEnd = now + windup;
            melee.LastAttackTime = now;
            melee.Guarding = false;

            Raise(CombatEventKind.Swing, agent.Id, AgentId.None, agent.Position, zone);
        }

        /// <summary>
        /// Mirrors the stagger timer onto the replicated flags.
        ///
        /// AgentFlags.Staggered was declared with the rest of them and never written, so a flinch
        /// could not reach a client at all: the timer lives in AgentCombat, which is server-side
        /// state, and the flags are the only combat detail a proxy ever sees.
        /// </summary>
        private static void UpdateStaggerFlag(AgentEntity agent, float now)
        {
            if (agent.Melee.IsStaggered(now)) agent.Flags |= AgentFlags.Staggered;
            else agent.Flags &= ~AgentFlags.Staggered;
        }

        private void AdvancePhase(AgentEntity agent, in SimFrame frame)
        {
            AgentCombat melee = agent.Melee;
            float now = frame.Time;
            if (melee.Phase == AttackPhase.Idle || now < melee.PhaseEnd) return;

            UnseenConfig.CombatSection cfg = Ctx.Config.Combat;

            switch (melee.Phase)
            {
                case AttackPhase.Windup:
                    melee.Phase = AttackPhase.Strike;
                    melee.PhaseEnd = now + 0.05f;
                    ResolveStrike(agent, frame);
                    break;

                case AttackPhase.Strike:
                    melee.Phase = AttackPhase.Recovery;
                    melee.PhaseEnd = now + cfg.Recovery;
                    break;

                default:
                    melee.Phase = AttackPhase.Idle;
                    break;
            }
        }

        private void ResolveStrike(AgentEntity attacker, in SimFrame frame)
        {
            UnseenConfig.CombatSection cfg = Ctx.Config.Combat;
            AgentCombat melee = attacker.Melee;

            float reach = cfg.MeleeRange + (attacker.Inventory != null ? attacker.Inventory.ReachBonus : 0f);
            float swingLoudness = attacker.Inventory != null && attacker.Inventory.Weapon != null
                ? attacker.Inventory.Weapon.SwingLoudness
                : 1.4f;
            float swingRadius = attacker.Inventory != null && attacker.Inventory.Weapon != null
                ? attacker.Inventory.Weapon.SwingRadius
                : 18f;

            Ctx.Sound.Emit(attacker.Id, attacker.Position, SoundKind.WeaponSwing, swingLoudness, swingRadius, frame.Tick);

            // A swing that cuts through a paper wall opens it: the classic stealth entry.
            ShojiPanel panel = ShojiPanel.NearestIntact(attacker.TorsoPosition + attacker.Forward * 0.8f, 1.2f);
            if (panel != null) SlicePanel(attacker, panel, frame);

            Lantern lantern = Lantern.NearestLit(attacker.TorsoPosition + attacker.Forward * reach * 0.7f, reach * 0.8f);
            if (lantern != null) BreakLantern(attacker, lantern, frame);

            AgentEntity victim = FindMeleeTarget(attacker, reach, cfg.MeleeArcDegrees);
            if (victim == null) return;

            float damage = (melee.Heavy ? cfg.HeavyDamage : cfg.LightDamage) *
                           (attacker.Inventory != null ? attacker.Inventory.DamageScale : 1f);

            AgentCombat defence = victim.Melee;
            float now = frame.Time;
            bool zoneMatches = defence.GuardZoneHeld == melee.AttackZone;

            if (defence.ParryOpen(now) && zoneMatches)
            {
                // Parry: the attacker eats the stagger, the defender keeps their guard.
                melee.Phase = AttackPhase.Recovery;
                melee.PhaseEnd = now + cfg.Recovery;
                melee.StaggerEnd = now + cfg.StaggerDuration;
                defence.ParryWindowEnd = now;

                Ctx.Sound.Emit(victim.Id, victim.Position, SoundKind.WeaponClash, 2.2f, 34f, frame.Tick);
                Raise(CombatEventKind.Parried, attacker.Id, victim.Id, victim.Position, melee.AttackZone);
                return;
            }

            if (defence.Guarding && zoneMatches)
            {
                float blocked = damage * cfg.BlockedDamageScale;
                Ctx.Sound.Emit(victim.Id, victim.Position, SoundKind.WeaponClash, 1.9f, 30f, frame.Tick);

                // A heavy into a held guard breaks it open instead of chipping away.
                if (melee.Heavy)
                {
                    defence.GuardBreakEnd = now + cfg.GuardBreakDuration;
                    defence.Guarding = false;
                    Raise(CombatEventKind.GuardBroken, attacker.Id, victim.Id, victim.Position, melee.AttackZone);
                }
                else
                {
                    Raise(CombatEventKind.Blocked, attacker.Id, victim.Id, victim.Position, melee.AttackZone);
                }

                ApplyDamage(new DamageInfo
                {
                    Attacker = attacker.Id,
                    Victim = victim.Id,
                    Kind = DamageKind.Melee,
                    Amount = blocked,
                    Point = victim.TorsoPosition,
                    Direction = math.normalizesafe(victim.Position - attacker.Position),
                    Zone = melee.AttackZone
                });
                return;
            }

            if (defence.Guarding && !zoneMatches) damage *= cfg.WrongZoneDamageScale;

            ApplyDamage(new DamageInfo
            {
                Attacker = attacker.Id,
                Victim = victim.Id,
                Kind = DamageKind.Melee,
                Amount = damage,
                Point = victim.TorsoPosition,
                Direction = math.normalizesafe(victim.Position - attacker.Position),
                Zone = melee.AttackZone
            });

            Raise(CombatEventKind.Hit, attacker.Id, victim.Id, victim.TorsoPosition, melee.AttackZone);
        }

        private AgentEntity FindMeleeTarget(AgentEntity attacker, float reach, float arcDegrees)
        {
            EntityRegistry registry = Ctx.Entities;
            float cosArc = math.cos(arcDegrees * 0.5f * UnseenMath.Deg2Rad);
            float best = reach * reach;
            AgentEntity found = null;

            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity candidate = registry.BySlot(i);
                if (candidate == attacker || !candidate.IsAlive) continue;

                float3 delta = candidate.TorsoPosition - attacker.TorsoPosition;
                float distSq = math.lengthsq(delta);
                if (distSq > best) continue;

                float dist = math.sqrt(math.max(distSq, 1e-5f));
                if (math.dot(delta / dist, attacker.Forward) < cosArc) continue;

                // The blade still has to reach: a wall between the two stops the swing.
                if (Physics.Linecast(attacker.TorsoPosition, candidate.TorsoPosition,
                        (1 << UnseenLayers.Default) | (1 << UnseenLayers.Occluder), QueryTriggerInteraction.Ignore))
                    continue;

                best = distSq;
                found = candidate;
            }

            return found;
        }

        // ---------------------------------------------------------------- guard

        private void UpdateGuard(AgentEntity agent, in MoveIntent intent, float now, UnseenConfig.CombatSection cfg)
        {
            AgentCombat melee = agent.Melee;
            bool wants = intent.Guard && melee.Phase == AttackPhase.Idle && !melee.IsGuardBroken(now);
            bool raised = RisingEdge(_guardHeld, agent.Id.Value, wants);

            melee.GuardZoneHeld = intent.Zone;
            melee.Guarding = wants;
            if (wants) agent.Flags |= AgentFlags.Guarding;
            else agent.Flags &= ~AgentFlags.Guarding;

            if (!raised) return;

            // Latency compensation: the window a defender actually gets is their base window plus
            // half their measured round trip, capped so a laggy client cannot become unhittable.
            float rtt = agent.ConnectionId >= 0 ? Ctx.Net.RoundTripTime(agent.ConnectionId) : 0f;
            float window = math.clamp(
                cfg.ParryWindowBase + rtt * cfg.ParryLatencyCompensation,
                cfg.ParryWindowBase,
                cfg.ParryWindowMax);

            melee.ParryWindowEnd = now + window;
        }

        // ---------------------------------------------------------------- takedown

        private bool TryBeginTakedown(AgentEntity attacker, in SimFrame frame)
        {
            UnseenConfig.CombatSection cfg = Ctx.Config.Combat;
            AgentEntity victim = FindTakedownVictim(attacker, cfg, frame.Time);
            if (victim == null) return false;

            float3 behind = victim.Position - victim.Forward * 0.85f;
            behind.y = victim.Position.y;

            attacker.Melee.TakedownTarget = victim.Id;
            attacker.Melee.TakedownEnd = frame.Time + cfg.TakedownDuration;
            victim.Melee.IsTakedownVictim = true;
            victim.Melee.TakedownEnd = frame.Time + cfg.TakedownDuration;
            victim.Melee.Phase = AttackPhase.Idle;
            victim.Melee.Guarding = false;

            attacker.Flags |= AgentFlags.Takedown;
            victim.Flags |= AgentFlags.Takedown;

            // Motion warping: both actors are warped onto the exact marks the animation expects,
            // which is what keeps a 1.5 s lockstep animation from sliding on a client.
            attacker.Motor?.BeginMotionWarp(behind, victim.Yaw, cfg.TakedownDuration);
            victim.Motor?.BeginMotionWarp(victim.Position, victim.Yaw, cfg.TakedownDuration);

            Raise(CombatEventKind.TakedownStart, attacker.Id, victim.Id, victim.Position, GuardZone.Mid);
            return true;
        }

        private AgentEntity FindTakedownVictim(AgentEntity attacker, UnseenConfig.CombatSection cfg, float now)
        {
            EntityRegistry registry = Ctx.Entities;
            float rangeSq = cfg.TakedownRange * cfg.TakedownRange;
            float rearCos = math.cos(cfg.TakedownRearArc * 0.5f * UnseenMath.Deg2Rad);

            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity victim = registry.BySlot(i);
                if (victim == attacker || !victim.IsAlive) continue;
                if (victim.Melee.IsTakedownVictim) continue;

                float3 delta = victim.Position - attacker.Position;
                float verticalDrop = attacker.Position.y - victim.Position.y;
                bool fromAbove = verticalDrop >= cfg.TakedownAboveHeight &&
                                 math.lengthsq(UnseenMath.Horizontal(delta)) <= rangeSq;

                if (!fromAbove && math.lengthsq(delta) > rangeSq) continue;

                // The victim has to be genuinely unaware. Anyone who has laid eyes on the attacker
                // recently gets a fight instead of an execution.
                if (!victim.IsUnawareOf(attacker.Id, now, cfg.AwarenessMemory)) continue;
                if (victim.Melee.Guarding) continue;

                if (!fromAbove)
                {
                    float3 toAttacker = math.normalizesafe(attacker.Position - victim.Position);
                    if (math.dot(toAttacker, victim.Forward) > -rearCos) continue;
                }

                if (Physics.Linecast(attacker.TorsoPosition, victim.TorsoPosition,
                        (1 << UnseenLayers.Default) | (1 << UnseenLayers.Occluder), QueryTriggerInteraction.Ignore))
                    continue;

                return victim;
            }

            return null;
        }

        private void CompleteTakedown(AgentEntity attacker, in SimFrame frame)
        {
            AgentId targetId = attacker.Melee.TakedownTarget;
            attacker.Melee.TakedownTarget = AgentId.None;
            attacker.Flags &= ~AgentFlags.Takedown;

            if (!Ctx.Entities.TryGet(targetId, out AgentEntity victim)) return;

            victim.Melee.IsTakedownVictim = false;
            victim.Flags &= ~AgentFlags.Takedown;

            Raise(CombatEventKind.TakedownComplete, attacker.Id, victim.Id, victim.Position, GuardZone.Mid);

            ApplyDamage(new DamageInfo
            {
                Attacker = attacker.Id,
                Victim = victim.Id,
                Kind = DamageKind.Takedown,
                Amount = victim.Vitals.MaxHealth * 4f,
                Point = victim.TorsoPosition,
                Direction = victim.Forward
            });
        }

        // ---------------------------------------------------------------- interaction and utility

        private void HandleInteract(AgentEntity agent, in MoveIntent intent, in SimFrame frame)
        {
            if (!intent.Interact) return;

            LootContainer container = LootContainer.NearestUnlooted(agent.TorsoPosition, 2.2f);
            if (container != null)
            {
                container.TakeAll(agent.Inventory);
                Ctx.Destructibles.Raise(WorldEventKind.ContainerOpened, Ctx.Destructibles.IdOf(container),
                    container.Position, frame.Tick);
                Ctx.Sound.Emit(agent.Id, container.Position, SoundKind.LootContainer,
                    container.OpenLoudness, container.OpenRadius, frame.Tick);
                return;
            }

            ShojiPanel panel = ShojiPanel.NearestIntact(agent.TorsoPosition + agent.Forward * 0.9f, 1.4f);
            if (panel != null)
            {
                SlicePanel(agent, panel, frame);
                return;
            }

            Lantern lantern = Lantern.NearestLit(agent.TorsoPosition + agent.Forward * 1.2f, 1.8f);
            if (lantern != null) BreakLantern(agent, lantern, frame);
        }

        private void HandleUtility(AgentEntity agent, in MoveIntent intent, in SimFrame frame)
        {
            byte slot = intent.UseUtility;
            byte previous = _utilityHeld.TryGetValue(agent.Id.Value, out byte held) ? held : (byte)0;
            _utilityHeld[agent.Id.Value] = slot;

            if (slot == 0 || slot == previous || agent.Inventory == null) return;

            ItemDefinition item = agent.Inventory.ConsumeUtilitySlot(slot - 1);
            if (item == null) return;

            float3 landing = agent.EyePosition + agent.ViewDirection * math.min(item.ThrowSpeed * 0.6f, 12f);
            if (Physics.Linecast(agent.EyePosition, landing, out RaycastHit hit,
                    UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                landing = hit.point;

            switch (item.Effect)
            {
                case UtilityEffect.SmokeBomb:
                    SmokeCloud.Spawn(SmokePrefab, landing, item.EffectRadius, item.EffectDuration);
                    Ctx.Destructibles.Raise(new WorldEvent
                    {
                        Kind = WorldEventKind.SmokeSpawned,
                        Position = landing,
                        Radius = item.EffectRadius,
                        Duration = item.EffectDuration,
                        Tick = frame.Tick
                    });
                    Ctx.Sound.Emit(agent.Id, landing, SoundKind.SmokeBomb,
                        item.EffectLoudness, item.EffectSoundRadius, frame.Tick);
                    break;

                case UtilityEffect.Noisemaker:
                    // The point of a noisemaker is to be heard somewhere you are not.
                    Ctx.Sound.Emit(AgentId.None, landing, SoundKind.Noisemaker,
                        item.EffectLoudness, item.EffectSoundRadius, frame.Tick);
                    break;

                case UtilityEffect.NightVisionElixir:
                    agent.Inventory.ApplyNightVision(frame.Time, item.EffectDuration);
                    break;
            }

            Raise(CombatEventKind.Throw, agent.Id, AgentId.None, landing, GuardZone.Mid);
        }

        private void ThrowShuriken(AgentEntity agent, in SimFrame frame)
        {
            const float range = 32f;
            agent.Melee.Phase = AttackPhase.Recovery;
            agent.Melee.PhaseEnd = frame.Time + Ctx.Config.Combat.Recovery * 0.6f;
            agent.Melee.LastAttackTime = frame.Time;

            Raise(CombatEventKind.Throw, agent.Id, AgentId.None, agent.EyePosition, GuardZone.Mid);

            // Silent by design: a shuriken punishes someone who has not noticed you, and gives away
            // nothing if it misses.
            if (!Physics.Raycast(agent.EyePosition, agent.ViewDirection, out RaycastHit hit, range,
                    UnseenLayers.WorldGeometry | UnseenLayers.Agents | (1 << UnseenLayers.Interactable),
                    QueryTriggerInteraction.Ignore))
                return;

            Lantern lantern = hit.collider.GetComponentInParent<Lantern>();
            if (lantern != null)
            {
                BreakLantern(agent, lantern, frame);
                return;
            }

            AgentEntity victim = hit.collider.GetComponentInParent<AgentEntity>();
            if (victim == null || victim == agent || !victim.IsAlive) return;

            float damage = 22f * (agent.Inventory != null ? agent.Inventory.DamageScale : 1f);
            ApplyDamage(new DamageInfo
            {
                Attacker = agent.Id,
                Victim = victim.Id,
                Kind = DamageKind.Thrown,
                Amount = damage,
                Point = hit.point,
                Direction = agent.ViewDirection
            });

            Raise(CombatEventKind.Hit, agent.Id, victim.Id, hit.point, GuardZone.Mid);
        }

        private void SlicePanel(AgentEntity agent, ShojiPanel panel, in SimFrame frame)
        {
            if (!panel.Slice()) return;

            Ctx.Destructibles.Raise(WorldEventKind.ShojiSliced, Ctx.Destructibles.IdOf(panel),
                panel.Position, frame.Tick);
            Ctx.Sound.Emit(agent.Id, panel.Position, SoundKind.ShojiSlice,
                panel.SliceLoudness, panel.SliceRadius, frame.Tick);
        }

        private void BreakLantern(AgentEntity agent, Lantern lantern, in SimFrame frame)
        {
            if (!lantern.Extinguish()) return;

            Ctx.Destructibles.Raise(WorldEventKind.LanternExtinguished, Ctx.Destructibles.IdOf(lantern),
                lantern.Position, frame.Tick);
            Ctx.Sound.Emit(agent.Id, lantern.Position, SoundKind.LanternBreak,
                lantern.BreakLoudness, lantern.BreakRadius, frame.Tick);
        }

        // ---------------------------------------------------------------- damage

        /// <summary>The single entry point for every source of damage in the game.</summary>
        public void ApplyDamage(in DamageInfo info)
        {
            if (!Ctx.Entities.TryGet(info.Victim, out AgentEntity victim) || !victim.IsAlive) return;

            bool fatal = victim.Vitals.Apply(info, Ctx.Time);

            // Being hit makes you aware of your attacker whether you saw them or not, which closes
            // the takedown window on a second attempt.
            if (info.Attacker.IsValid) victim.NoteSaw(info.Attacker, Ctx.Time);

            if (!fatal) return;

            Kill(victim, info);
        }

        private void Kill(AgentEntity victim, in DamageInfo info)
        {
            victim.Flags &= ~AgentFlags.Alive;
            victim.Melee.ResetCombat();
            victim.Visible.Clear();
            victim.Heard.Clear();
            victim.IsHot = false;

            Ctx.Sound.Emit(victim.Id, victim.Position, SoundKind.Death, 2.6f, 38f, Ctx.Tick);
            Raise(CombatEventKind.Death, info.Attacker, victim.Id, victim.Position, info.Zone);

            AgentEntity killer = Ctx.Entities.Get(info.Attacker);
            if (killer != null && killer != victim) killer.Kills++;

            Ctx.Match?.NotifyDeath(victim, killer);
            Ctx.Sight?.Forget(victim.Id);
        }

        // ---------------------------------------------------------------- plumbing

        private static bool RisingEdge(Dictionary<int, bool> state, int key, bool value)
        {
            bool previous = state.TryGetValue(key, out bool held) && held;
            state[key] = value;
            return value && !previous;
        }

        private void Raise(CombatEventKind kind, AgentId attacker, AgentId victim, float3 position, GuardZone zone)
        {
            switch (kind)
            {
                case CombatEventKind.Swing: TotalSwings++; break;
                case CombatEventKind.Hit: TotalHits++; break;
                case CombatEventKind.Parried: TotalParries++; break;
                case CombatEventKind.TakedownComplete: TotalTakedowns++; break;
                case CombatEventKind.Death: TotalDeaths++; break;
            }

            _events.Add(new CombatEvent
            {
                Kind = kind,
                Attacker = attacker,
                Victim = victim,
                Position = position,
                Zone = zone,
                Tick = Ctx.Tick
            });
        }

        /// <summary>Called by the replication system once the queue has been fanned out to clients.</summary>
        public void ClearEvents()
        {
            _events.Clear();
        }
    }
}
