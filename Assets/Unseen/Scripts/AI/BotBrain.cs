using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AI;
using Unseen.Combat;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;
using Unseen.Items;

namespace Unseen.AI
{
    /// <summary>
    /// One bot mind. It reads exactly the same perception products a human client receives - the
    /// interest set and the heard-sound queue - turns them into facts, asks the HTN domain what to
    /// do, and emits a MoveIntent. Nothing here touches another agent transform directly, so a bot
    /// is provably no better informed than a player standing in the same spot.
    /// </summary>
    public sealed class BotBrain : MonoBehaviour
    {
        private static CompoundTask _domain;

        private readonly BotBlackboard _bb = new BotBlackboard();
        private readonly HtnPlanner _planner = new HtnPlanner();
        private readonly List<PrimitiveTask> _plan = new List<PrimitiveTask>(8);
        private readonly BotNavigator _nav = new BotNavigator();

        private AgentEntity _agent;
        private System.Random _rng;
        private BotFacts _facts;
        private float _nextThinkAt;
        private float _nextGrappleAt;
        private float _nextLootAt;
        private int _lastLandmark = -1;
        private MapSketch _sketch;
        private bool _lookedForSketch;

        /// <summary>Threat score for the current target, written by the parallel scoring job.</summary>
        public float ThreatScore { get; internal set; }

        public BotState State => _bb.State;
        public BotAction Action => _bb.CurrentAction;
        public BotBlackboard Blackboard => _bb;
        public AgentId Target => _bb.Target;

        public void Bind(AgentEntity agent)
        {
            _agent = agent;
            _rng = new System.Random(agent.Id.Value * 7919 + 13);
            _bb.SkillOffset = (float)_rng.NextDouble();

            // Which way this bot goes round an obstacle. Fixed per bot so two of them meeting in an
            // alley do not mirror each other into a standoff.
            _nav.SetTurnPreference(agent.Id.Value);
            _domain ??= NinjaDomain.Build();
        }

        public void ResetBrain()
        {
            _bb.Reset();
            _nav.Clear();
            _plan.Clear();
            _nextThinkAt = 0f;
        }

        /// <summary>
        /// One decision cycle. Called by the bot director at a rate that depends on how close this
        /// bot is to the action: combat rate in a fight, a couple of hertz out in the mist.
        /// </summary>
        public void Think(SimContext ctx, float now, float dt)
        {
            if (_agent == null || !_agent.IsAlive) return;
            if (_agent.IsLocked || _agent.Melee.IsTakedownVictim) return;

            Perceive(ctx, now);
            UpdateState(ctx, now);
            BuildFacts(ctx, now);

            if (now >= _bb.ActionExpiresAt || _bb.CurrentAction == BotAction.Idle)
            {
                if (_planner.Plan(_domain, _facts, _plan) && _plan.Count > 0)
                {
                    PrimitiveTask chosen = _plan[0];
                    _bb.CurrentAction = chosen.Action;

                    // Jitter the commitment window so a lobby of bots does not replan in lockstep.
                    float jitter = 0.85f + 0.3f * _bb.SkillOffset;
                    _bb.ActionExpiresAt = now + chosen.Duration * jitter;
                }
                else
                {
                    _bb.CurrentAction = BotAction.PatrolTo;
                    _bb.ActionExpiresAt = now + 1.5f;
                }
            }

            Execute(ctx, now, dt);
        }

        // ---------------------------------------------------------------- perception

        private void Perceive(SimContext ctx, float now)
        {
            UnseenConfig.BotSection cfg = ctx.Config.Bots;

            // Sight: pick the target that matters most, weighting a clear read over a vague shape.
            float bestScore = 0f;
            AgentId bestId = AgentId.None;
            float3 bestPosition = default;
            bool bestSilhouette = false;

            for (int i = 0; i < _agent.Visible.Count; i++)
            {
                VisibleTarget v = _agent.Visible[i];
                float distance = math.max(1f, math.distance(v.Position, _agent.Position));
                bool direct = (v.Kind & VisibilityKind.Direct) != 0;
                float score = v.Confidence * (direct ? 1f : 0.45f) * (40f / distance);

                if (score <= bestScore) continue;
                bestScore = score;
                bestId = v.Id;
                bestPosition = v.Position;
                bestSilhouette = !direct;
            }

            if (bestId.IsValid)
            {
                bool isNewContact = bestId != _bb.Target;
                _bb.Target = bestId;
                _bb.TargetLastSeen = bestPosition;
                _bb.TargetLastSeenAt = now;
                _bb.TargetThreatScore = bestScore;
                _bb.TargetIsSilhouetteOnly = bestSilhouette;

                if (isNewContact)
                    _bb.ReactionReadyAt = now + cfg.ReactionTime * (0.6f + _bb.SkillOffset * 0.8f);
            }
            else if (_bb.Target.IsValid && now - _bb.TargetLastSeenAt > cfg.InvestigateTimeout + cfg.SearchTimeout)
            {
                _bb.Target = AgentId.None;
            }

            // Hearing: the loudest thing above the interest threshold wins, and the bot only knows
            // the apparent position - the same degraded value a player gets on their compass.
            float loudest = cfg.NoiseInterestThreshold;
            for (int i = 0; i < _agent.Heard.Count; i++)
            {
                HeardSound s = _agent.Heard[i];
                if (s.Intensity < loudest) continue;

                loudest = s.Intensity;
                _bb.NoisePosition = s.ApparentPosition;
                _bb.NoiseIntensity = s.Intensity;
                _bb.NoiseKind = s.Kind;

                if (now - _bb.NoiseHeardAt > 1f)
                    _bb.ReactionReadyAt = math.max(_bb.ReactionReadyAt, now + cfg.ReactionTime);

                _bb.NoiseHeardAt = now;
            }

            // Bots consume their own perception queue; humans have theirs drained by replication.
            _agent.Heard.Clear();
        }

        private void UpdateState(SimContext ctx, float now)
        {
            UnseenConfig.BotSection cfg = ctx.Config.Bots;
            bool injured = _agent.Vitals.Fraction < cfg.FleeHealthFraction;
            bool underAttack = now - _agent.Vitals.LastDamageTime < 2f;
            bool visibleNow = _bb.Target.IsValid && now - _bb.TargetLastSeenAt < 0.4f;

            if (injured && (underAttack || visibleNow))
            {
                _bb.EnterState(BotState.Flee, now);
                return;
            }

            if (visibleNow)
            {
                bool concealed = _agent.StealthIndex >= ctx.Config.Stealth.ConcealedThreshold;
                float distance = math.distance(_bb.TargetLastSeen, _agent.Position);
                bool worthWaiting = concealed && distance > 6f && !underAttack;
                _bb.EnterState(worthWaiting ? BotState.Ambush : BotState.Combat, now);
                return;
            }

            bool hasTrail = _bb.Target.IsValid && now - _bb.TargetLastSeenAt < cfg.InvestigateTimeout;
            bool hasNoise = now - _bb.NoiseHeardAt < cfg.InvestigateTimeout * 0.5f;

            if (hasTrail || hasNoise)
            {
                _bb.EnterState(BotState.Investigate, now);
                return;
            }

            switch (_bb.State)
            {
                case BotState.Investigate:
                    _bb.EnterState(BotState.SearchArea, now);
                    break;

                case BotState.SearchArea:
                    if (_bb.TimeInState(now) > cfg.SearchTimeout) _bb.EnterState(BotState.Patrol, now);
                    break;

                case BotState.Combat:
                case BotState.Ambush:
                case BotState.Flee:
                    _bb.EnterState(BotState.SearchArea, now);
                    break;
            }
        }

        private void BuildFacts(SimContext ctx, float now)
        {
            UnseenConfig cfg = ctx.Config;
            AgentEntity target = _bb.Target.IsValid ? ctx.Entities.Get(_bb.Target) : null;

            float distance = _bb.Target.IsValid
                ? math.distance(_bb.TargetLastSeen, _agent.Position)
                : float.MaxValue;

            bool ready = now >= _bb.ReactionReadyAt;
            bool visibleNow = _bb.Target.IsValid && now - _bb.TargetLastSeenAt < 0.4f;

            _facts.HasTarget = _bb.Target.IsValid;
            _facts.TargetVisible = visibleNow && ready && !_bb.TargetIsSilhouetteOnly;
            _facts.TargetIsSilhouette = visibleNow && _bb.TargetIsSilhouetteOnly;
            _facts.TargetInMeleeRange = ready && distance <= cfg.Combat.MeleeRange * 0.95f;
            _facts.TargetInApproachRange = distance <= cfg.Bots.ActiveRange;
            _facts.UnderAttack = now - _agent.Vitals.LastDamageTime < 2f;
            _facts.Injured = _agent.Vitals.Fraction < cfg.Bots.FleeHealthFraction;
            _facts.Concealed = _agent.StealthIndex >= cfg.Stealth.ConcealedThreshold;
            _facts.HeardSomething = now - _bb.NoiseHeardAt < cfg.Bots.InvestigateTimeout * 0.5f;
            _facts.OutsideZone = (_agent.Flags & AgentFlags.InMist) != 0;
            _facts.HasSmoke = _agent.Inventory != null && _agent.Inventory.HasUtility(UtilityEffect.SmokeBomb);
            _facts.HasWeapon = _agent.Inventory != null && _agent.Inventory.Weapon != null;

            // A victim only counts as unaware if they have not looked at us recently - the same test
            // the combat director will apply when the takedown is attempted.
            _facts.TargetUnaware = target != null &&
                                   target.IsUnawareOf(_agent.Id, now, cfg.Combat.AwarenessMemory);

            // Reading a wind-up is fair play: the telegraph is visible to a human too.
            _facts.EnemyIsSwinging = target != null && visibleNow &&
                                     target.Melee.Phase == AttackPhase.Windup &&
                                     distance < cfg.Combat.MeleeRange * 2f;

            // Loot is a detour, not a career.
            //
            // This used to look twenty-four metres for an unlooted container and prefer looting
            // over everything whenever it found one. There are nearly three hundred containers in
            // the town, so there is essentially always another within twenty-four metres: bots
            // chained from one to the next for the whole match and never went anywhere. Measured at
            // sixty-one per cent of all bot ticks spent on LootContainer against eight per cent
            // patrolling, walking a hundred and eighty metres inside a twenty metre box.
            //
            // Close by, still needed, and not straight after the last one.
            _facts.LootNearby = _agent.Inventory != null &&
                                now >= _nextLootAt &&
                                (_agent.Inventory.Weapon == null || _agent.Inventory.Gear.Count < Inventory.MaxGear) &&
                                LootContainer.NearestUnlooted(_agent.Position, LootReach) != null;

            _facts.LanternNearby = !_facts.Concealed && Lantern.NearestLit(_agent.Position, 4.5f) != null;
        }

        // ---------------------------------------------------------------- execution

        private void Execute(SimContext ctx, float now, float dt)
        {
            var intent = new MoveIntent
            {
                Sequence = (uint)ctx.Tick,
                Yaw = _agent.Yaw,
                Pitch = _agent.Pitch,
                Zone = GuardZone.Mid
            };

            // A destination the navigator cannot close on gets abandoned rather than ground
            // against. Without this a bot handed a point on the far side of a building pushed into
            // the wall for the rest of the match, which is what the metre-wide pacing looked like
            // from the outside.
            if (_nav.Stuck && IsRoaming(_bb.CurrentAction))
            {
                _nav.Clear();
                _bb.HasPatrolDestination = false;
                _bb.PatrolDestination = NextPatrolPoint(ctx, now);
                _bb.HasPatrolDestination = true;
            }

            // A container it cannot get to is worse than one it has already opened. The cooldown is
            // set on opening, so a bot grinding at a chest inside a sealed building never trips it
            // and stays on the loot action indefinitely - which was most of the pacing left after
            // the chain itself was broken.
            if (_nav.Stuck && _bb.CurrentAction == BotAction.LootContainer)
            {
                _nextLootAt = now + LootCooldown;
                _nav.Clear();
                _bb.CurrentAction = BotAction.PatrolTo;
                _bb.ActionExpiresAt = now + 2f;
            }

            switch (_bb.CurrentAction)
            {
                case BotAction.MoveIntoZone:
                    MoveTowards(ctx, ref intent, ctx.Mist != null ? ctx.Mist.NearestSafePoint(_agent.Position) : _agent.Position, now, sprint: true);
                    break;

                case BotAction.PatrolTo:
                    if (!_nav.HasDestination || !_bb.HasPatrolDestination) PickPatrolDestination(ctx, now);
                    MoveTowards(ctx, ref intent, _bb.PatrolDestination, now, sprint: false);
                    break;

                case BotAction.CreepTo:
                {
                    float3 destination = _bb.Target.IsValid ? _bb.TargetLastSeen : NextPatrolPoint(ctx, now);
                    MoveTowards(ctx, ref intent, destination, now, sprint: false);
                    intent.Crouch = true;
                    break;
                }

                case BotAction.MoveToNoise:
                    MoveTowards(ctx, ref intent, _bb.NoisePosition, now, sprint: false);
                    intent.Crouch = _bb.NoiseIntensity < 0.5f;
                    break;

                case BotAction.SearchNearby:
                {
                    if (!_nav.HasDestination)
                    {
                        float3 anchor = _bb.Target.IsValid ? _bb.TargetLastSeen : _agent.Position;
                        _bb.PatrolDestination = ScatterAround(anchor, 14f);
                        _bb.HasPatrolDestination = true;
                    }

                    MoveTowards(ctx, ref intent, _bb.PatrolDestination, now, sprint: false);
                    intent.Crouch = true;
                    break;
                }

                case BotAction.Approach:
                    MoveTowards(ctx, ref intent, _bb.TargetLastSeen, now, sprint: !_facts.TargetUnaware);
                    intent.Crouch = _facts.TargetUnaware;
                    FaceTarget(ref intent);
                    break;

                case BotAction.HoldAmbush:
                    intent.Move = float2.zero;
                    intent.Crouch = true;
                    FaceTarget(ref intent);
                    break;

                case BotAction.TakeDownTarget:
                    ApproachBehind(ctx, ref intent, now);
                    intent.AttackLight = true;
                    intent.Crouch = true;
                    break;

                case BotAction.Strike:
                    FaceTarget(ref intent);
                    intent.Zone = PickAttackZone();
                    bool heavy = ShouldSwingHeavy(ctx);
                    intent.AttackLight = !heavy;
                    intent.AttackHeavy = heavy;
                    break;

                case BotAction.Parry:
                    FaceTarget(ref intent);
                    intent.Guard = true;
                    intent.Zone = GuessGuardZone(ctx);
                    break;

                case BotAction.Retreat:
                {
                    float3 away = math.normalizesafe(_agent.Position - _bb.TargetLastSeen);
                    if (math.lengthsq(away) < 0.01f) away = _agent.Forward;
                    MoveTowards(ctx, ref intent, _agent.Position + away * 18f, now, sprint: true);

                    // Use the rope to break contact if it is off cooldown - the escape use is quiet.
                    if (_agent.Hook != null && _agent.Hook.CooldownRemaining <= 0f && _rng.NextDouble() < 0.25)
                    {
                        intent.Grapple = true;
                        intent.Pitch = -45f;
                    }

                    break;
                }

                case BotAction.ThrowSmoke:
                    intent.UseUtility = FindUtilitySlot(UtilityEffect.SmokeBomb);
                    intent.Pitch = 0f;
                    break;

                case BotAction.BreakLantern:
                {
                    Lantern lantern = Lantern.NearestLit(_agent.Position, 4.5f);
                    if (lantern != null)
                    {
                        FacePoint(ref intent, lantern.Position);
                        if (math.distance(lantern.Position, _agent.Position) < 1.9f) intent.Interact = true;
                        else MoveTowards(ctx, ref intent, lantern.Position, now, sprint: false);
                    }

                    break;
                }

                case BotAction.LootContainer:
                {
                    LootContainer container = LootContainer.NearestUnlooted(_agent.Position, LootReach);
                    if (container != null)
                    {
                        if (math.distance(container.Position, _agent.Position) < 1.9f)
                        {
                            FacePoint(ref intent, container.Position);
                            intent.Interact = true;

                            // Whatever was in it, that is enough looting for now. Without this the
                            // bot simply turns to the next container and the chain never breaks.
                            _nextLootAt = now + LootCooldown;
                        }
                        else
                        {
                            MoveTowards(ctx, ref intent, container.Position, now, sprint: false);
                        }
                    }

                    break;
                }
            }

            _agent.Intent = intent;
        }

        private void MoveTowards(SimContext ctx, ref MoveIntent intent, float3 destination, float now, bool sprint)
        {
            _nav.SetDestination(_agent.Position, destination, now);
            float3 steering = _nav.Steering(_agent.Position, now);

            // Somewhere above us. Grapple for it: the eaves and balconies all carry anchors and the
            // motor does the searching, so this is the whole of what a bot needs to reach a roof.
            // Without it no bot ever left ground level - measured at zero out of twenty-one gaining
            // even three metres of height over ninety seconds.
            float climb = destination.y - _agent.Position.y;
            float3 flatAway = UnseenMath.Horizontal(destination - _agent.Position);

            bool firing = climb > 2f &&
                          math.lengthsq(flatAway) < 32f * 32f &&
                          now >= _nextGrappleAt;

            if (math.lengthsq(steering) < 0.0001f)
            {
                intent.Move = float2.zero;
                if (firing) AimAndFireGrapple(ref intent, destination, now);
                return;
            }

            float yaw = UnseenMath.ForwardToYaw(steering);
            intent.Yaw = yaw;
            intent.Move = new float2(0f, 1f);
            intent.Sprint = sprint;

            // Aimed last, so the steering yaw does not overwrite it. The hook fires along the
            // agent's VIEW direction, and a patrolling bot looks flat ahead - which meant every
            // attempt at a roof went into the wall underneath it.
            if (firing) AimAndFireGrapple(ref intent, destination, now);

            // Vault or climb when the way ahead is blocked at chest height but open above.
            if (Physics.Raycast(_agent.Position + new float3(0f, 0.6f, 0f), steering, 1f,
                    UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore) &&
                !Physics.Raycast(_agent.Position + new float3(0f, 1.9f, 0f), steering, 1.4f,
                    UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
            {
                intent.Jump = true;
            }
        }

        /// <summary>Points the bot at something above it and pulls the trigger.</summary>
        private void AimAndFireGrapple(ref MoveIntent intent, float3 destination, float now)
        {
            // Aimed a little above the target surface: the anchors sit on the eaves and the ridge,
            // and a line straight at the middle of a roof passes through the eave in front of it.
            FacePoint(ref intent, destination + new float3(0f, 1.2f, 0f));
            intent.Grapple = true;

            // Retried briskly. A grapple that finds no anchor from where the bot happens to be
            // standing usually finds one a few steps later, and a long cooldown turns a roof into
            // somewhere the bot walks past the bottom of.
            _nextGrappleAt = now + 0.9f;
        }

        /// <summary>How far a bot will detour for a container.</summary>
        private const float LootReach = 12f;

        /// <summary>Seconds after opening one before another is worth crossing the street for.</summary>
        private const float LootCooldown = 25f;

        /// <summary>Actions whose destination is a suggestion, and may be swapped for another.</summary>
        private static bool IsRoaming(BotAction action)
        {
            return action == BotAction.PatrolTo ||
                   action == BotAction.SearchNearby ||
                   action == BotAction.CreepTo ||
                   action == BotAction.MoveToNoise;
        }

        private void ApproachBehind(SimContext ctx, ref MoveIntent intent, float now)
        {
            AgentEntity target = ctx.Entities.Get(_bb.Target);
            if (target == null)
            {
                MoveTowards(ctx, ref intent, _bb.TargetLastSeen, now, sprint: false);
                return;
            }

            // Aim for the spot behind the shoulder rather than the body: walking into the front arc
            // is how a takedown turns into a losing clash.
            float3 behind = _bb.TargetLastSeen - target.Forward * 1.1f;
            MoveTowards(ctx, ref intent, behind, now, sprint: false);
            FacePoint(ref intent, _bb.TargetLastSeen);
        }

        private void FaceTarget(ref MoveIntent intent)
        {
            if (!_bb.Target.IsValid) return;
            FacePoint(ref intent, _bb.TargetLastSeen);
        }

        private void FacePoint(ref MoveIntent intent, float3 point)
        {
            float3 delta = point - _agent.EyePosition;
            float3 flat = UnseenMath.Horizontal(delta);
            if (math.lengthsq(flat) > 0.0001f) intent.Yaw = UnseenMath.ForwardToYaw(math.normalize(flat));

            float horizontal = math.length(flat);
            if (horizontal > 0.01f) intent.Pitch = math.degrees(math.atan2(-delta.y, horizontal));
        }

        private GuardZone PickAttackZone()
        {
            double roll = _rng.NextDouble();
            if (roll < 0.5) return GuardZone.Mid;
            return roll < 0.78 ? GuardZone.High : GuardZone.Low;
        }

        private bool ShouldSwingHeavy(SimContext ctx)
        {
            // Heavies break a held guard, so favour them strongly against someone who is turtling.
            AgentEntity target = ctx.Entities.Get(_bb.Target);
            double bias = target != null && target.Melee.Guarding ? 0.75 : 0.3;
            return _rng.NextDouble() < bias + 0.15 * _bb.SkillOffset;
        }

        private GuardZone GuessGuardZone(SimContext ctx)
        {
            AgentEntity target = ctx.Entities.Get(_bb.Target);
            if (target == null) return GuardZone.Mid;

            // Reading the telegraph correctly is a skill check. A bot that fails it guards wrong,
            // exactly like a player who misread the wind-up.
            bool reads = _rng.NextDouble() < ctx.Config.Bots.ParryAptitude * (0.7f + 0.6f * _bb.SkillOffset);
            if (reads) return target.Melee.AttackZone;

            return (GuardZone)_rng.Next(0, 3);
        }

        private byte FindUtilitySlot(UtilityEffect effect)
        {
            if (_agent.Inventory == null) return 0;

            IReadOnlyList<ItemStack> utility = _agent.Inventory.Utility;
            for (int i = 0; i < utility.Count; i++)
                if (utility[i].Item != null && utility[i].Item.Effect == effect)
                    return (byte)(i + 1);

            return 0;
        }

        private void PickPatrolDestination(SimContext ctx, float now)
        {
            _bb.PatrolDestination = NextPatrolPoint(ctx, now);
            _bb.HasPatrolDestination = true;
        }

        /// <summary>
        /// Somewhere worth going.
        ///
        /// This used to be a uniformly random point inside the mist circle, which is a poor way to
        /// explore a town: most of those points are inside a building or on the far side of one, and
        /// with no NavMesh baked the bot cannot path to either. It would push at the nearest wall
        /// until the match ended.
        ///
        /// The generator already publishes what is in the town and where - blocks, keeps, pagodas,
        /// plazas, shrines, bridges, stores, gardens - so the bots read that instead. A landmark is
        /// a place with a reason to be there, and roughly a third of the time the chosen point is
        /// its ROOF, which is what sends bots up onto the skyline the town was built for.
        /// </summary>
        private float3 NextPatrolPoint(SimContext ctx, float now)
        {
            float3 center = ctx.Mist != null ? ctx.Mist.Center : _agent.Position;
            float radius = ctx.Mist != null ? math.max(20f, ctx.Mist.CurrentRadius * 0.9f) : 60f;

            if (!_lookedForSketch)
            {
                _sketch = MapSketch.Find();
                _lookedForSketch = true;
            }

            if (_sketch != null && _sketch.Landmarks.Count > 0)
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    int index = _rng.Next(_sketch.Landmarks.Count);

                    // Not the one we have just come from, or a bot ping-pongs between two
                    // neighbours and never sees the rest of the town.
                    if (index == _lastLandmark) continue;

                    MapSketch.Landmark mark = _sketch.Landmarks[index];
                    var at = new float3(mark.Center.x, 0f, mark.Center.y);

                    // Inside the circle, and far enough to be a journey rather than a step.
                    float3 fromCentre = at - center;
                    fromCentre.y = 0f;
                    if (math.length(fromCentre) > radius) continue;
                    if (math.distance(UnseenMath.Horizontal(at), UnseenMath.Horizontal(_agent.Position)) < 12f)
                        continue;

                    bool wantsRoof = _rng.NextDouble() < 0.5 && CanStandOn(mark.Kind);

                    // Offset off dead centre, so a dozen bots given the same landmark do not all
                    // converge on one point of it.
                    float jx = (float)(_rng.NextDouble() * 2.0 - 1.0) * mark.Extents.x * 0.6f;
                    float jz = (float)(_rng.NextDouble() * 2.0 - 1.0) * mark.Extents.y * 0.6f;
                    var spot = new float3(at.x + jx, 0f, at.z + jz);

                    if (!Physics.Raycast(spot + new float3(0f, 90f, 0f), Vector3.down,
                            out RaycastHit surface, 220f, UnseenLayers.WorldGeometry,
                            QueryTriggerInteraction.Ignore))
                        continue;

                    _lastLandmark = index;

                    // The first thing under a downward ray over a building IS its roof. If a roof
                    // was wanted, take it; otherwise aim for the street beside the landmark, which
                    // is somewhere a walking bot can actually arrive.
                    if (wantsRoof && surface.point.y > 3f)
                        return (float3)surface.point + new float3(0f, 0.4f, 0f);

                    float3 outward = math.normalizesafe(new float3(jx, 0f, jz), new float3(1f, 0f, 0f));
                    float3 street = at + outward * (math.max(mark.Extents.x, mark.Extents.y) + 5f);

                    if (Physics.Raycast(street + new float3(0f, 90f, 0f), Vector3.down,
                            out RaycastHit ground, 220f, UnseenLayers.WorldGeometry,
                            QueryTriggerInteraction.Ignore))
                        return (float3)ground.point + new float3(0f, 0.2f, 0f);

                    return (float3)surface.point + new float3(0f, 0.2f, 0f);
                }
            }

            // No sketch published, or nothing in range: fall back to the old scatter, but far
            // enough away to be a walk.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                float angle = (float)_rng.NextDouble() * math.PI * 2f;
                float distance = 25f + (float)_rng.NextDouble() * math.max(25f, radius - 25f);
                float3 candidate = center + new float3(math.cos(angle) * distance, 0f, math.sin(angle) * distance);

                if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 8f, NavMesh.AllAreas))
                    return hit.position;

                if (Physics.Raycast(candidate + new float3(0f, 80f, 0f), Vector3.down, out RaycastHit fallback, 200f,
                        UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                    return (float3)fallback.point + new float3(0f, 0.1f, 0f);
            }

            return ScatterAround(_agent.Position, 30f);
        }

        /// <summary>Landmarks with a roof worth being on. Water and plazas do not have one.</summary>
        private static bool CanStandOn(MapSketch.Feature kind)
        {
            switch (kind)
            {
                case MapSketch.Feature.Water:
                case MapSketch.Feature.Plaza:
                    return false;
                default:
                    return true;
            }
        }

        private float3 ScatterAround(float3 anchor, float radius)
        {
            float angle = (float)_rng.NextDouble() * math.PI * 2f;
            float distance = 3f + (float)_rng.NextDouble() * radius;
            return anchor + new float3(math.cos(angle) * distance, 0f, math.sin(angle) * distance);
        }

        /// <summary>Debug line for the AI overlay.</summary>
        public string Describe()
        {
            return $"{_bb.State}/{_bb.CurrentAction} target={_bb.Target} threat={_bb.TargetThreatScore:0.00}";
        }
    }
}
