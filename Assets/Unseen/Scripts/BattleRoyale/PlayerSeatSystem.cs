using System.Collections.Generic;
using Unseen.AI;
using Unseen.Combat;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.BattleRoyale
{
    /// <summary>
    /// Who gets a body, and what becomes of one when its player goes away.
    ///
    /// This used to live in <see cref="BotDirector"/>, which was the wrong place from the moment a
    /// disconnect stopped producing a bot: a class for steering AI had become the one that decides
    /// whether a human is in the match.
    ///
    /// The rule it exists to hold is that leaving costs you the round. Handing an abandoned body to
    /// a bot rewarded the disconnect, made the results table claim a player who was never there,
    /// and left a ninja hunting people ten minutes after its player had gone. But ending the match
    /// the instant a packet is missed punishes a bad hotel connection exactly as hard as pulling the
    /// cable on purpose, and those are not the same act.
    ///
    /// So the body is abandoned rather than killed: it stands where it was left, for a minute,
    /// alive and visible and entirely killable. Come back inside that and it is yours again. Leave
    /// it and it dies. Let somebody find it first and it dies the way anything else does, with
    /// their name on it - which is what stops the window being a free minute of invulnerability
    /// available to anyone willing to pull a cable at the right moment.
    /// </summary>
    public sealed class PlayerSeatSystem : SimSystem
    {
        /// <summary>
        /// How long an abandoned body waits for its player.
        ///
        /// A minute covers what it is meant to cover - a lift, a dropped wifi handover, a router
        /// that reboots itself - without being long enough to hide in. It is deliberately not
        /// generous: the body is standing in the open the whole time, so a long window mostly means
        /// a longer wait before somebody else takes the kill.
        /// </summary>
        public const float GraceSeconds = 60f;

        /// <summary>A body waiting to see whether its player is coming back.</summary>
        private struct Abandoned
        {
            public AgentId Id;
            public float DeadlineAt;
        }

        // Keyed by the name the server granted, because that is the only identity this game has
        // before Steam authentication lands. It is weak: a name is released back to the roster when
        // its player drops, so somebody could in principle take it during the window and claim the
        // body. Recorded in TODO rather than papered over with something that looks stronger than
        // it is.
        private readonly Dictionary<string, Abandoned> _abandoned = new Dictionary<string, Abandoned>();
        private readonly List<string> _finished = new List<string>();

        private AgentSpawner _spawner;
        private BotDirector _bots;

        public override int Order => SimOrder.Backfill;
        public override SimRate Rate => SimRate.Base;

        /// <summary>How many bodies are currently standing about waiting for their players.</summary>
        public int AbandonedCount => _abandoned.Count;

        protected override void OnInitialize()
        {
            Ctx.Net.ClientConnected += OnClientConnected;
            Ctx.Net.ClientDisconnected += OnClientDisconnected;
        }

        public void Configure(AgentSpawner spawner, BotDirector bots)
        {
            _spawner = spawner;
            _bots = bots;
        }

        public override void Tick(in SimFrame frame)
        {
            if (!Ctx.Net.IsServer || _abandoned.Count == 0) return;

            _finished.Clear();

            foreach (KeyValuePair<string, Abandoned> kv in _abandoned)
            {
                AgentEntity body = Ctx.Entities.Get(kv.Value.Id);

                // Gone already - somebody found it, or the mist did. Bad luck, and nothing left to
                // do: the death it had is the real one, with whoever earned it credited.
                if (body == null || !body.IsAlive)
                {
                    _finished.Add(kv.Key);
                    continue;
                }

                if (frame.Time < kv.Value.DeadlineAt) continue;

                Kill(body);
                _finished.Add(kv.Key);
            }

            for (int i = 0; i < _finished.Count; i++) _abandoned.Remove(_finished[i]);
        }

        /// <summary>
        /// A player arrives: their own body back if one is waiting, a seat if the match has not
        /// started, and otherwise the spectator camera until the round ends.
        /// </summary>
        private void OnClientConnected(int connectionId)
        {
            if (Ctx.Entities.ByConnection(connectionId) != null) return;

            // What the player asked to be called, if the transport carried a name and the server
            // granted one. Falls back to the slot number: a transport with no names - offline
            // practice, or an adapter that does not carry one - still needs the player labelled,
            // and the fallback belongs here rather than being invented inside the transport.
            string granted = Ctx.Net?.NameOf(connectionId);
            string displayName = string.IsNullOrEmpty(granted) ? $"player-{connectionId}" : granted;

            if (Reclaim(connectionId, displayName)) return;

            // Nobody joins a round already under way. Somebody arriving mid-match watches until it
            // ends and plays the next one.
            //
            // This is the other half of leaving costing you the round: without it, dropping and
            // rejoining is the same escape by a longer route - out of a fight you were losing, back
            // in somewhere else with a fresh one. It also removes the question of where a late
            // arrival would be put, which has no good answer in a shrinking circle: the safe middle
            // is a gift, the edge is a sentence, and either way a stranger appears beside somebody
            // out of nothing.
            if (Ctx.Match != null && Ctx.Match.Phase != MatchPhase.Lobby)
            {
                UnseenLog.Info($"[Unseen] connection {connectionId} arrived mid-round; spectating until it ends");
                return;
            }

            Seat(connectionId, displayName);
        }

        /// <summary>
        /// Hands back a body its player left, if it is still standing.
        ///
        /// Returns true when the arrival has been dealt with either way - including when the body
        /// was killed while they were gone, because there is nothing further to give them then and
        /// falling through to a fresh seat is exactly the reward this whole rule exists to deny.
        /// </summary>
        private bool Reclaim(int connectionId, string displayName)
        {
            if (!_abandoned.TryGetValue(displayName, out Abandoned held)) return false;

            _abandoned.Remove(displayName);

            AgentEntity body = Ctx.Entities.Get(held.Id);

            if (body == null || !body.IsAlive)
            {
                UnseenLog.Info($"[Unseen] {displayName} came back to a body that had already been killed");
                return true;
            }

            Ctx.Entities.SetConnection(body, connectionId);
            body.Intent = MoveIntent.Idle;

            UnseenLog.Info($"[Unseen] {displayName} came back inside the window and has their body");
            return true;
        }

        /// <summary>
        /// A human arriving takes over a bot rather than joining a 63-entity match as a 64th. The
        /// bot body, its inventory and its position all carry over, so backfill is seamless.
        /// </summary>
        private void Seat(int connectionId, string displayName)
        {
            AgentEntity candidate = _bots != null ? _bots.PickBotToReplace() : null;

            if (candidate == null)
            {
                if (_spawner == null) return;

                candidate = _spawner.Spawn(AgentKind.Player, connectionId,
                    _bots != null ? _bots.PickSpawnPoint() : Unity.Mathematics.float3.zero, displayName);

                UnseenLog.Info($"[Unseen] connection {connectionId} spawned fresh as {candidate.DisplayName}");
                return;
            }

            candidate.Kind = AgentKind.Player;
            candidate.Flags &= ~AgentFlags.Bot;
            candidate.DisplayName = displayName;
            candidate.Intent = MoveIntent.Idle;
            if (candidate.Brain != null) candidate.Brain.enabled = false;
            Ctx.Entities.SetConnection(candidate, connectionId);

            UnseenLog.Info($"[Unseen] connection {connectionId} took over bot slot {candidate.Id}");
        }

        /// <summary>
        /// A player leaves: the body is unseated and left standing until the window closes.
        /// </summary>
        private void OnClientDisconnected(int connectionId)
        {
            AgentEntity agent = Ctx.Entities.ByConnection(connectionId);
            if (agent == null) return;

            // Unseated first, so nothing goes looking for a player behind this body again.
            Ctx.Entities.SetConnection(agent, -1);

            if (!agent.IsAlive)
            {
                UnseenLog.Info($"[Unseen] connection {connectionId} left; {agent.DisplayName} was already out");
                return;
            }

            // Idle rather than whatever they were doing when the connection went. Nothing updates
            // this while the body is unseated, so a last input of "sprint forward" would be held
            // for the whole minute - the body running off a roof on its player's behalf, which is
            // a strange thing to come back to and a stranger thing to be killed by.
            agent.Intent = MoveIntent.Idle;

            _abandoned[agent.DisplayName] = new Abandoned
            {
                Id = agent.Id,
                DeadlineAt = Ctx.Time + GraceSeconds
            };

            UnseenLog.Info($"[Unseen] connection {connectionId} left; {agent.DisplayName} " +
                      $"is standing for {GraceSeconds:0} s");
        }

        /// <summary>
        /// Kills through the ordinary damage path rather than deleting the agent.
        ///
        /// Placement, the kill feed, the standings row and the death other players watch all
        /// already know what a death is, and none of them know anything about an agent that simply
        /// stops existing.
        /// </summary>
        private void Kill(AgentEntity body)
        {
            if (Ctx.Combat == null) return;

            Ctx.Combat.ApplyDamage(new DamageInfo
            {
                Attacker = AgentId.None,
                Victim = body.Id,
                Kind = DamageKind.Disconnected,

                // Far past any health pool rather than exactly the remaining amount. This is not a
                // fight to be survived by a point, and reading the pool here would make it depend
                // on rules that belong to combat.
                Amount = 1e9f,
                Point = body.TorsoPosition,
                Direction = body.Forward
            });

            UnseenLog.Info($"[Unseen] {body.DisplayName} did not come back; their round is over");
        }

        public override void Shutdown()
        {
            if (Ctx?.Net != null)
            {
                Ctx.Net.ClientConnected -= OnClientConnected;
                Ctx.Net.ClientDisconnected -= OnClientDisconnected;
            }

            _abandoned.Clear();
        }
    }
}
