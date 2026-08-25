using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;
using Unseen.Items;

namespace Unseen.BattleRoyale
{
    public enum MatchPhase : byte
    {
        /// <summary>Filling the lobby with players and backfill bots.</summary>
        Lobby = 0,

        /// <summary>Gliders and canopy drops. Nobody has landed yet.</summary>
        Infiltration = 1,

        /// <summary>The long middle: scavenge, stalk, avoid being the one who is seen first.</summary>
        Hunt = 2,

        /// <summary>Final circles. Interiors only, almost no darkness left.</summary>
        Endgame = 3,

        PostMatch = 4
    }

    /// <summary>
    /// Owns match flow: when the drop happens, when the mist starts, who placed where, and when to
    /// roll into the next match. Offline practice and a live 64-player server run the exact same
    /// state machine - only the transport underneath differs.
    /// </summary>
    public sealed class MatchDirector : SimSystem
    {
        private AgentSpawner _spawner;
        private float3 _mapCenter;
        private float _mapRadius = 400f;
        private float _phaseEnd;
        private float _phaseStart;
        private int _seed;

        public MatchPhase Phase { get; private set; } = MatchPhase.Lobby;
        public AgentSpawner Spawner => _spawner;
        public int MatchNumber { get; private set; }
        public AgentId Winner { get; private set; }
        public float3 MapCenter => _mapCenter;
        public float MapRadius => _mapRadius;
        public float SecondsInPhase { get; private set; }

        /// <summary>Seconds to wait in the lobby before starting anyway.</summary>
        public float LobbyTimeout = 10f;

        /// <summary>Seconds spent on the results screen before the next match begins.</summary>
        public float PostMatchDuration = 12f;

        /// <summary>Set false for a one-shot match; true keeps a soak test cycling.</summary>
        public bool LoopMatches = true;

        public event Action<int> MatchStarted;
        public event Action<AgentId> MatchEnded;
        public event Action<AgentEntity, AgentEntity> AgentDied;

        public override int Order => SimOrder.Match;
        public override SimRate Rate => SimRate.Base;

        protected override void OnInitialize()
        {
            Ctx.Match = this;
            EnterPhase(MatchPhase.Lobby, 0f, LobbyTimeout);
        }

        public void Configure(AgentSpawner spawner, float3 mapCenter, float mapRadius, int seed)
        {
            _spawner = spawner;
            _mapCenter = mapCenter;
            _mapRadius = math.max(50f, mapRadius);
            _seed = seed;
        }

        public override void Tick(in SimFrame frame)
        {
            SecondsInPhase = frame.Time - _phaseStart;

            switch (Phase)
            {
                case MatchPhase.Lobby:
                    TickLobby(frame);
                    break;

                case MatchPhase.Infiltration:
                    if (frame.Time >= _phaseEnd || AllDeployed()) EnterPhase(MatchPhase.Hunt, frame.Time, float.MaxValue);
                    break;

                case MatchPhase.Hunt:
                    if (Ctx.Mist != null && Ctx.Mist.Phase == MistZoneController.ZonePhase.Final)
                        EnterPhase(MatchPhase.Endgame, frame.Time, float.MaxValue);
                    CheckForWinner(frame);
                    break;

                case MatchPhase.Endgame:
                    CheckForWinner(frame);
                    break;

                case MatchPhase.PostMatch:
                    if (LoopMatches && frame.Time >= _phaseEnd) StartMatch(frame.Time);
                    break;
            }
        }

        private void TickLobby(in SimFrame frame)
        {
            int target = Ctx.Config.Match.TargetEntityCount;
            bool full = Ctx.Entities.Count >= target;
            if (full || frame.Time >= _phaseEnd) StartMatch(frame.Time);
        }

        /// <summary>Resets every agent, rolls loot from the match seed and launches the drop.</summary>
        public void StartMatch(float now)
        {
            MatchNumber++;
            Winner = AgentId.None;

            var random = new System.Random(_seed + MatchNumber * 7919);

            Ctx.Destructibles.BuildIndex();
            RestoreWorld();
            PopulateLoot(random);

            IReadOnlyList<AgentEntity> agents = Ctx.Entities.All;
            for (int i = 0; i < agents.Count; i++)
            {
                agents[i].ResetForMatch();
                agents[i].Brain?.ResetBrain();

                // Bodies come back too. ResetForMatch restores the simulation state; the death
                // scene owns the transform and the renderer, and it had nobody telling it a new
                // match had started.
                agents[i].GetComponent<AgentDeathVisual>()?.Reset();
            }

            Ctx.Mist?.Begin(_mapCenter, now);

            DeploymentSystem deployment = Ctx.Get<DeploymentSystem>();
            if (Ctx.Config.Match.SkipInfiltration)
            {
                deployment?.PlaceOnGround(_mapCenter, _mapRadius, random);
                EnterPhase(MatchPhase.Hunt, now, float.MaxValue);
            }
            else
            {
                deployment?.Begin(_mapCenter, _mapRadius, random);
                EnterPhase(MatchPhase.Infiltration, now, Ctx.Config.Match.InfiltrationDuration);
            }
            MatchStarted?.Invoke(MatchNumber);
            Debug.Log($"[Unseen] match {MatchNumber} starting with {Ctx.Entities.Count} entities " +
                      $"({Ctx.Entities.Count - Ctx.Entities.BotCount} human)");
        }

        private void RestoreWorld()
        {
            IReadOnlyList<ShojiPanel> panels = ShojiPanel.All;
            for (int i = 0; i < panels.Count; i++) panels[i].Restore();

            IReadOnlyList<Lantern> lanterns = Lantern.All;
            for (int i = 0; i < lanterns.Count; i++) lanterns[i].Relight();
        }

        private void PopulateLoot(System.Random random)
        {
            IReadOnlyList<LootContainer> containers = LootContainer.All;
            for (int i = 0; i < containers.Count; i++) containers[i].Populate(random, 0);
        }

        private bool AllDeployed()
        {
            IReadOnlyList<AgentEntity> agents = Ctx.Entities.All;
            for (int i = 0; i < agents.Count; i++)
            {
                AgentEntity a = agents[i];
                if (a.IsAlive && (a.Flags & AgentFlags.Deployed) == 0) return false;
            }

            return true;
        }

        private void CheckForWinner(in SimFrame frame)
        {
            int alive = Ctx.Entities.AliveCount;
            if (alive > 1) return;

            AgentEntity survivor = null;
            IReadOnlyList<AgentEntity> agents = Ctx.Entities.All;
            for (int i = 0; i < agents.Count; i++)
            {
                if (!agents[i].IsAlive) continue;
                survivor = agents[i];
                break;
            }

            if (survivor != null)
            {
                survivor.Placement = 1;
                Winner = survivor.Id;
            }

            EnterPhase(MatchPhase.PostMatch, frame.Time, PostMatchDuration);
            MatchEnded?.Invoke(Winner);
            Debug.Log($"[Unseen] match {MatchNumber} won by {(survivor != null ? survivor.DisplayName : "nobody")}");
        }

        /// <summary>Called by the combat director whenever an agent dies.</summary>
        public void NotifyDeath(AgentEntity victim, AgentEntity killer)
        {
            if (victim.Placement == 0) victim.Placement = Ctx.Entities.AliveCount + 1;
            AgentDied?.Invoke(victim, killer);
        }

        private void EnterPhase(MatchPhase phase, float now, float duration)
        {
            Phase = phase;
            _phaseStart = now;
            _phaseEnd = duration >= float.MaxValue ? float.MaxValue : now + duration;
        }

        /// <summary>Human-readable status line for the server console.</summary>
        public string StatusLine()
        {
            return $"match {MatchNumber} {Phase} alive {Ctx.Entities.AliveCount}/{Ctx.Entities.Count} " +
                   $"zone {(Ctx.Mist != null ? Ctx.Mist.Stage : 0)} r={(Ctx.Mist != null ? Ctx.Mist.CurrentRadius : 0f):0}";
        }
    }
}
